using System.Text.Json;
using System.Text.Json.Nodes;
using KuzushiClassifierApp.Plugin;
using Xunit;

namespace KuzushiClassifierApp.Plugin.Tests;

/// <summary>
/// Interactive MCP client over an in-memory duplex stream pair: one stream
/// feeds the server's stdin, another drains its stdout. Lets tests interleave
/// requests and notifications exactly like a host would.
/// </summary>
internal sealed class McpClientConnection : IDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly MemoryDuplexStream _clientToServer = new();
    private readonly MemoryDuplexStream _serverToClient = new();
    private readonly StreamReader _reader;
    private readonly Task _serverTask;

    public McpClientConnection(PluginDispatcher dispatcher)
    {
        _reader = new StreamReader(_serverToClient);
        _serverTask = new McpServer(dispatcher).RunAsync(_clientToServer, _serverToClient);
    }

    public Task ServerTask => _serverTask;

    public async Task SendAsync(JsonObject message)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(message.ToJsonString() + "\n");
        await _clientToServer.WriteAsync(bytes);
        await _clientToServer.FlushAsync();
    }

    public async Task SendRawAsync(string line)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(line + "\n");
        await _clientToServer.WriteAsync(bytes);
        await _clientToServer.FlushAsync();
    }

    public async Task SendBytesAsync(byte[] bytes)
    {
        await _clientToServer.WriteAsync(bytes);
        await _clientToServer.FlushAsync();
    }

    public async Task<JsonObject> ReadMessageAsync(TimeSpan? timeout = null)
    {
        var line = await _reader.ReadLineAsync()
            .WaitAsync(timeout ?? DefaultTimeout);
        Assert.NotNull(line);
        return Assert.IsAssignableFrom<JsonObject>(JsonNode.Parse(line!));
    }

    /// <summary>Send a request and read its correlated response.</summary>
    public async Task<JsonObject> RequestAsync(JsonObject message)
    {
        await SendAsync(message);
        var response = await ReadMessageAsync();
        Assert.Equal(message["id"]?.ToJsonString(), response["id"]?.ToJsonString());
        return response;
    }

    /// <summary>Close stdin; the server drains and exits cleanly at EOF.</summary>
    public async Task ShutdownAsync()
    {
        _clientToServer.CompleteWriting();
        await _serverTask.WaitAsync(DefaultTimeout);
    }

    public void Dispose()
    {
        _clientToServer.CompleteWriting();
        _clientToServer.Dispose();
        _serverToClient.Dispose();
        _reader.Dispose();
    }
}

internal static class McpMessages
{
    public static JsonObject Initialize(int id = 1) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["method"] = "initialize",
        ["params"] = new JsonObject
        {
            ["protocolVersion"] = "2025-06-18",
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "tests", ["version"] = "0" },
        },
    };

    public static JsonObject ToolCall(int id, string tool, JsonObject arguments) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["method"] = "tools/call",
        ["params"] = new JsonObject
        {
            ["name"] = tool,
            ["arguments"] = arguments,
        },
    };

    public static JsonObject Find(int id, string? scope = null, string? query = null,
        int? limit = null, string? cursor = null)
    {
        var args = new JsonObject();
        if (scope is not null) args["in"] = scope;
        if (query is not null) args["query"] = query;
        if (limit is not null) args["limit"] = limit;
        if (cursor is not null) args["cursor"] = cursor;
        return ToolCall(id, "patchouli.plugin.find", args);
    }

    public static JsonObject Fetch(int id, string uri) =>
        ToolCall(id, "patchouli.plugin.fetch", new JsonObject { ["uri"] = uri });

    public static JsonObject Send(int id, string action, JsonObject args,
        string? requestId = null, string endpoint = "control", string target = "")
    {
        var message = new JsonObject
        {
            ["endpoint"] = endpoint,
            ["action"] = action,
            ["target"] = target,
            ["args"] = args,
        };
        if (requestId is not null)
        {
            message["request_id"] = requestId;
        }

        return ToolCall(id, "patchouli.plugin.send", message);
    }

    public static JsonObject ImageArgs(string? imageBase64 = null,
        string? mediaType = null, int? topK = null, string? extraKey = null)
    {
        var args = new JsonObject
        {
            ["image_base64"] = imageBase64 ?? TestImages.PngBase64(),
        };
        if (mediaType is not null) args["media_type"] = mediaType;
        if (topK is not null) args["top_k"] = topK;
        if (extraKey is not null) args[extraKey] = "x";
        return args;
    }
}

internal static class McpAssertions
{
    /// <summary>Asserts a tool-level error result and returns its error code.</summary>
    public static string AssertToolError(JsonObject response)
    {
        var result = Assert.IsAssignableFrom<JsonObject>(response["result"]);
        Assert.True(result["isError"]!.GetValue<bool>());
        var error = Assert.IsAssignableFrom<JsonObject>(
            Assert.IsAssignableFrom<JsonObject>(result["structuredContent"])["error"]);
        return error["code"]!.GetValue<string>();
    }

    public static JsonObject AssertToolSuccess(JsonObject response)
    {
        var result = Assert.IsAssignableFrom<JsonObject>(response["result"]);
        Assert.False(result["isError"]!.GetValue<bool>());
        return Assert.IsAssignableFrom<JsonObject>(result["structuredContent"]);
    }

    public static int AssertProtocolError(JsonObject response)
    {
        var error = Assert.IsAssignableFrom<JsonObject>(response["error"]);
        return error["code"]!.GetValue<int>();
    }
}
