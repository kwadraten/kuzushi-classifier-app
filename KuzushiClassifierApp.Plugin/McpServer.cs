using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace KuzushiClassifierApp.Plugin;

/// <summary>
/// Newline-delimited JSON-RPC 2.0 MCP server over stdio (no Content-Length
/// framing). stdout carries protocol messages only; everything else goes to
/// stderr. Requests are dispatched concurrently, correlated by JSON-RPC id,
/// and cancellable via notifications/cancelled.
/// </summary>
public sealed class McpServer
{
    public const int MaxRequestBytes = 16 * 1024 * 1024;   // fits 8 MiB image + base64 overhead + envelope
    public const int MaxResponseBytes = 16 * 1024 * 1024;
    private const int MaxConcurrentRequests = 8;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly PluginDispatcher _dispatcher;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _writerLock = new(1, 1);
    private readonly SemaphoreSlim _concurrencyLimit = new(MaxConcurrentRequests, MaxConcurrentRequests);
    private readonly Dictionary<string, CancellationTokenSource> _inFlight = new(StringComparer.Ordinal);
    private readonly object _inFlightGate = new();
    private readonly List<Task> _requestTasks = new();

    public McpServer(PluginDispatcher dispatcher, ILogger? logger = null)
    {
        _dispatcher = dispatcher;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    public async Task RunAsync(Stream input, Stream output, CancellationToken cancellationToken = default)
    {
        var reader = new BoundedLineReader(input, MaxRequestBytes);

        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break; // clean EOF: stop accepting work
            }

            if (line.Length == 0)
            {
                if (reader.OverranLimit)
                {
                    await WriteResponseAsync(output, ErrorResponse(
                        null, -32600, $"Request exceeds the {MaxRequestBytes}-byte limit."))
                        .ConfigureAwait(false);
                }

                continue;
            }

            JsonObject? message;
            try
            {
                message = JsonNode.Parse(line) as JsonObject;
            }
            catch (JsonException)
            {
                message = null;
            }

            if (message is null)
            {
                await WriteResponseAsync(output, ErrorResponse(
                    null, -32700, "Parse error: expected one JSON-RPC 2.0 object per line."))
                    .ConfigureAwait(false);
                continue;
            }

            var method = message["method"] is JsonValue methodNode
                && methodNode.TryGetValue<string>(out var m) ? m : null;
            var id = message["id"];

            if (method is null)
            {
                if (id is not null)
                {
                    await WriteResponseAsync(output, ErrorResponse(
                        id, -32600, "Invalid request: missing 'method'."))
                        .ConfigureAwait(false);
                }

                continue;
            }

            if (id is null)
            {
                HandleNotification(method, message["params"] as JsonObject);
                continue;
            }

            var requestId = id.ToJsonString();
            var requestCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            lock (_inFlightGate)
            {
                _inFlight[requestId] = requestCts;
            }

            await _concurrencyLimit.WaitAsync(cancellationToken).ConfigureAwait(false);

            var task = Task.Run(async () =>
            {
                try
                {
                    var response = await HandleRequestAsync(method, message, id, requestCts.Token)
                        .ConfigureAwait(false);
                    await WriteResponseAsync(output, response).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unhandled error while writing response for {RequestId}", requestId);
                }
                finally
                {
                    lock (_inFlightGate)
                    {
                        _inFlight.Remove(requestId);
                    }

                    requestCts.Dispose();
                    _concurrencyLimit.Release();
                }
            }, CancellationToken.None);

            lock (_requestTasks)
            {
                _requestTasks.Add(task);
            }
        }

        Task[] pending;
        lock (_requestTasks)
        {
            pending = _requestTasks.ToArray();
        }

        try
        {
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Request task failed during shutdown drain.");
        }
    }

    private async Task<JsonObject> HandleRequestAsync(
        string method,
        JsonObject message,
        JsonNode? id,
        CancellationToken cancellationToken)
    {
        try
        {
            return method switch
            {
                "initialize" => Result(id, InitializeResult(message)),
                "ping" => Result(id, new JsonObject()),
                "tools/list" => Result(id, ToolList()),
                "tools/call" => await HandleToolCallAsync(id, message["params"], cancellationToken)
                    .ConfigureAwait(false),
                _ => ErrorResponse(id, -32601, $"Method not found: '{method}'."),
            };
        }
        catch (OperationCanceledException)
        {
            return ErrorResponse(id, -32800, "Request cancelled.");
        }
    }

    private JsonObject InitializeResult(JsonObject request)
    {
        var requested = request["params"]?["protocolVersion"] is JsonValue v
            && v.TryGetValue<string>(out var version)
            ? version
            : "2025-06-18";

        return new JsonObject
        {
            ["protocolVersion"] = requested,
            ["capabilities"] = new JsonObject
            {
                ["tools"] = new JsonObject { ["listChanged"] = false },
            },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = PluginDispatcher.PluginId,
                ["title"] = "Kuzushi Classifier",
                ["version"] = PluginDispatcher.PluginVersion,
            },
        };
    }

    private async Task<JsonObject> HandleToolCallAsync(
        JsonNode? id,
        JsonNode? parameters,
        CancellationToken cancellationToken)
    {
        if (parameters is not JsonObject p
            || p["name"] is not JsonValue nameValue
            || !nameValue.TryGetValue<string>(out var name))
        {
            return ErrorResponse(id, -32602, "tools/call requires params.name (string).");
        }

        var arguments = p["arguments"] is null ? null : p["arguments"] as JsonObject;
        if (p["arguments"] is not null && arguments is null)
        {
            return ToolErrorResult(id, new ToolError(
                ToolErrorCodes.InvalidArgument,
                "tools/call params.arguments must be an object."));
        }

        try
        {
            JsonObject structured = name switch
            {
                "patchouli.plugin.find" => await _dispatcher
                    .FindAsync(arguments, cancellationToken).ConfigureAwait(false),
                "patchouli.plugin.fetch" => await _dispatcher
                    .FetchAsync(arguments, cancellationToken).ConfigureAwait(false),
                "patchouli.plugin.send" => await _dispatcher
                    .SendAsync(arguments, cancellationToken).ConfigureAwait(false),
                _ => throw new ToolErrorException(
                    ToolErrorCodes.UnsupportedOperation,
                    $"Unknown tool '{name}'. This server exposes patchouli.plugin.find, patchouli.plugin.fetch and patchouli.plugin.send."),
            };

            return ToolResult(id, structured);
        }
        catch (OperationCanceledException)
        {
            return ToolErrorResult(id, new ToolError(
                ToolErrorCodes.Cancelled,
                "The request was cancelled."));
        }
        catch (ToolErrorException ex)
        {
            return ToolErrorResult(id, ex.ToError());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected failure in tool {Tool}", name);
            return ToolErrorResult(id, new ToolError(
                ToolErrorCodes.Unavailable,
                $"Unexpected failure: {ex.Message}"));
        }
    }

    private void HandleNotification(string method, JsonObject? parameters)
    {
        if (method != "notifications/cancelled")
        {
            return; // initialized + all other notifications need no action
        }

        var requestId = parameters?["requestId"]?.ToJsonString();
        if (requestId is null)
        {
            return;
        }

        lock (_inFlightGate)
        {
            if (_inFlight.TryGetValue(requestId, out var cts))
            {
                try
                {
                    cts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // Request finished between lookup and cancel; nothing to do.
                }
            }
        }
    }

    // ----------------------------------------------------------- envelopes

    private static JsonObject Result(JsonNode? id, JsonObject result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["result"] = result,
    };

    private static JsonObject ToolResult(JsonNode? id, JsonObject structured) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["result"] = new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject
            {
                ["type"] = "text",
                ["text"] = structured.ToJsonString(SerializerOptions),
            }),
            ["structuredContent"] = structured,
            ["isError"] = false,
        },
    };

    private static JsonObject ToolErrorResult(JsonNode? id, ToolError error)
    {
        var structured = error.ToStructuredContent();
        return new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["result"] = new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = structured.ToJsonString(SerializerOptions),
                }),
                ["structuredContent"] = structured,
                ["isError"] = true,
            },
        };
    }

    private static JsonObject ErrorResponse(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["error"] = new JsonObject
        {
            ["code"] = code,
            ["message"] = message,
        },
    };

    private static JsonObject ToolList() => new()
    {
        ["tools"] = new JsonArray(
            new JsonObject
            {
                ["name"] = "patchouli.plugin.find",
                ["description"] = "Browse or search this plugin's declared virtual nodes.",
                ["inputSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = false,
                    ["properties"] = new JsonObject
                    {
                        ["in"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "\"\" (plugin root) or \"results/\".",
                            ["default"] = "",
                        },
                        ["query"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["default"] = "",
                        },
                        ["limit"] = new JsonObject
                        {
                            ["type"] = "integer",
                            ["minimum"] = 1,
                            ["maximum"] = 50,
                            ["default"] = 20,
                        },
                        ["cursor"] = new JsonObject
                        {
                            ["type"] = new JsonArray("string", "null"),
                            ["default"] = null,
                        },
                    },
                },
            },
            new JsonObject
            {
                ["name"] = "patchouli.plugin.fetch",
                ["description"] = "Fetch a declared plugin resource (\"\", status.json, results/{id}.json).",
                ["inputSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = false,
                    ["required"] = new JsonArray("uri"),
                    ["properties"] = new JsonObject
                    {
                        ["uri"] = new JsonObject { ["type"] = "string" },
                    },
                },
            },
            new JsonObject
            {
                ["name"] = "patchouli.plugin.send",
                ["description"] = "Run a declared control action: classify, similar or analyze.",
                ["inputSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = false,
                    ["required"] = new JsonArray("endpoint", "action", "target", "args"),
                    ["properties"] = new JsonObject
                    {
                        ["endpoint"] = new JsonObject { ["const"] = "control" },
                        ["action"] = new JsonObject
                        {
                            ["enum"] = new JsonArray("classify", "similar", "analyze"),
                        },
                        ["target"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "\"\" for this plugin's root.",
                        },
                        ["args"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["additionalProperties"] = false,
                            ["required"] = new JsonArray("image_base64"),
                            ["properties"] = new JsonObject
                            {
                                ["image_base64"] = new JsonObject
                                {
                                    ["type"] = "string",
                                    ["contentEncoding"] = "base64",
                                },
                                ["media_type"] = new JsonObject
                                {
                                    ["enum"] = new JsonArray("image/png", "image/jpeg", "image/webp"),
                                },
                                ["top_k"] = new JsonObject
                                {
                                    ["type"] = "integer",
                                    ["minimum"] = 1,
                                    ["maximum"] = 50,
                                },
                            },
                        },
                        ["request_id"] = new JsonObject
                        {
                            ["type"] = new JsonArray("string", "null"),
                            ["default"] = null,
                        },
                    },
                },
            }),
    };

    private async Task WriteResponseAsync(Stream output, JsonObject response)
    {
        var payload = Encoding.UTF8.GetBytes(response.ToJsonString(SerializerOptions) + "\n");
        if (payload.Length > MaxResponseBytes)
        {
            payload = Encoding.UTF8.GetBytes(
                ErrorResponse(response["id"], -32603, "Response exceeds the server byte limit.")
                    .ToJsonString(SerializerOptions) + "\n");
        }

        await _writerLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await output.WriteAsync(payload).ConfigureAwait(false);
            await output.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            _writerLock.Release();
        }
    }
}

/// <summary>
/// Reads newline-delimited messages with a hard byte cap per message.
/// Oversized messages are discarded up to the next newline.
/// </summary>
internal sealed class BoundedLineReader
{
    private Stream _stream;
    private readonly int _maxBytes;
    private readonly byte[] _chunk = new byte[64 * 1024];
    private readonly MemoryStream _buffer = new();
    private bool _discarding;

    public BoundedLineReader(Stream stream, int maxBytes)
    {
        _stream = stream;
        _maxBytes = maxBytes;
    }

    /// <summary>True when the last returned (empty) line was dropped for size.</summary>
    public bool OverranLimit { get; private set; }

    /// <summary>Returns the next line without the newline, or null at EOF.</summary>
    public async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        OverranLimit = false;

        while (true)
        {
            var read = await _stream
                .ReadAsync(_chunk, cancellationToken)
                .ConfigureAwait(false);

            if (read == 0)
            {
                return null;
            }

            var span = _chunk.AsSpan(0, read);
            var newline = span.IndexOf((byte)'\n');
            var take = newline < 0 ? span : span[..newline];

            if (!_discarding && _buffer.Length + take.Length <= _maxBytes)
            {
                _buffer.Write(take);
            }
            else if (!_discarding)
            {
                _discarding = true;
                OverranLimit = true;
            }

            if (newline >= 0)
            {
                // Preserve the unconsumed tail for the next line.
                var tail = span[(newline + 1)..];
                if (tail.Length > 0)
                {
                    _stream = new PrependStream(tail.ToArray(), _stream);
                }

                var lineBytes = _buffer.ToArray();
                _buffer.SetLength(0);
                var overran = _discarding;
                _discarding = false;

                if (overran)
                {
                    OverranLimit = true;
                    return string.Empty;
                }

                return Encoding.UTF8.GetString(lineBytes);
            }
        }
    }

    /// <summary>Streams buffered leftover bytes before the wrapped stream.</summary>
    private sealed class PrependStream : Stream
    {
        private readonly byte[] _prefix;
        private readonly Stream _inner;
        private int _offset;

        public PrependStream(byte[] prefix, Stream inner)
        {
            _prefix = prefix;
            _inner = inner;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_offset < _prefix.Length)
            {
                var count = Math.Min(buffer.Length, _prefix.Length - _offset);
                _prefix.AsSpan(_offset, count).CopyTo(buffer.Span);
                _offset += count;
                return count;
            }

            return await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
