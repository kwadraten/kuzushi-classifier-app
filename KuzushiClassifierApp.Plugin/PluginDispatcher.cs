using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KuzushiClassifierApp.Models;

namespace KuzushiClassifierApp.Plugin;

/// <summary>
/// Implements the three fixed plugin tools (find/fetch/send) over the
/// declared virtual tree. All validation happens here before any inference
/// is scheduled; errors map to the shared contract error codes.
/// </summary>
public sealed class PluginDispatcher
{
    public const string PluginId = "kwadraten.kuzushi-classifier";
    public const string PluginVersion = "0.1.0";
    public const string PluginRootUri = "patchouli://plugin/" + PluginId + "/";

    private const int DefaultLimit = 20;
    private const int MaxLimit = 50;
    private const string ResultsScope = "results/";

    private static readonly string[] Actions = { "classify", "similar", "analyze" };
    private static readonly string[] SendArgsKeys = { "image_base64", "media_type", "top_k" };

    private readonly PluginAssetProbe _assetProbe;
    private readonly PluginInference _inference;
    private readonly ResultStore _resultStore;
    private readonly DedupeStore _dedupeStore;

    public PluginDispatcher(
        PluginAssetProbe assetProbe,
        PluginInference inference,
        ResultStore resultStore,
        DedupeStore dedupeStore)
    {
        _assetProbe = assetProbe;
        _inference = inference;
        _resultStore = resultStore;
        _dedupeStore = dedupeStore;
    }

    // ----------------------------------------------------------------- find

    public Task<JsonObject> FindAsync(JsonObject? arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var scope = GetOptionalString(arguments, "in") ?? "";
        if (scope is not ("" or ResultsScope))
        {
            throw Invalid($"find 'in' must be \"\" or \"{ResultsScope}\"; got '{scope}'.");
        }

        var query = GetOptionalString(arguments, "query") ?? "";
        var limit = GetOptionalInt(arguments, "limit", DefaultLimit);
        if (limit < 1 || limit > MaxLimit)
        {
            throw Invalid($"find 'limit' must be an integer in 1..{MaxLimit}; got {limit}.");
        }

        if (scope == "" && query.Length > 0)
        {
            throw Invalid("find 'query' is only supported inside 'results/'; the root does not advertise search.");
        }

        var offset = DecodeCursor(GetOptionalString(arguments, "cursor"), scope, query);
        var entries = scope == "" ? RootEntries() : ResultEntries(query);

        var page = entries.Skip(offset).Take(limit).ToArray();
        var nextOffset = offset + page.Length;
        string? continuation = nextOffset < entries.Count
            ? EncodeCursor(scope, query, nextOffset)
            : null;

        var result = new JsonObject
        {
            ["entries"] = new JsonArray(page.Select(e => (JsonNode?)e).ToArray()),
            ["continuation"] = continuation is null ? null : JsonValue.Create(continuation),
        };

        return Task.FromResult(result);
    }

    private IReadOnlyList<JsonObject> RootEntries()
    {
        return new JsonObject[]
        {
            new()
            {
                ["uri"] = "status.json",
                ["title"] = "Plugin status and cached-asset readiness",
                ["type"] = "file",
            },
            new()
            {
                ["uri"] = ResultsScope,
                ["title"] = "Action results kept by this plugin process",
                ["type"] = "directory",
            },
        };
    }

    private IReadOnlyList<JsonObject> ResultEntries(string query)
    {
        return _resultStore.List()
            .Where(stored => query.Length == 0 || MatchesQuery(stored, query))
            .Select(stored => new JsonObject
            {
                ["uri"] = stored.ResourceUri,
                ["title"] = $"{stored.Result["action"]?.GetValue<string>()} {stored.ResultId}",
                ["type"] = "file",
            })
            .ToArray();
    }

    private static bool MatchesQuery(StoredResult stored, string query)
    {
        return stored.ResultId.Contains(query, StringComparison.OrdinalIgnoreCase)
            || (stored.Result["action"]?.GetValue<string>()
                    .Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private static string EncodeCursor(string scope, string query, int offset)
    {
        var raw = string.Join('\u001f', "find", scope, query, offset.ToString());
        return Base64UrlEncode(Encoding.UTF8.GetBytes(raw));
    }

    private static int DecodeCursor(string? cursor, string scope, string query)
    {
        if (cursor is null)
        {
            return 0;
        }

        string raw;
        try
        {
            raw = Encoding.UTF8.GetString(Base64UrlDecode(cursor));
        }
        catch (FormatException ex)
        {
            throw new ToolErrorException(
                ToolErrorCodes.InvalidArgument,
                "find 'cursor' is malformed.",
                ex);
        }

        var parts = raw.Split('\u001f');
        if (parts.Length != 4
            || parts[0] != "find"
            || parts[1] != scope
            || parts[2] != query
            || !int.TryParse(parts[3], out var offset)
            || offset < 0)
        {
            throw Invalid("find 'cursor' is malformed or does not belong to this listing scope.");
        }

        return offset;
    }

    // ---------------------------------------------------------------- fetch

    public Task<JsonObject> FetchAsync(JsonObject? arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var uri = GetRequiredString(arguments, "uri");
        var path = NormalizeRelativePath(uri);

        JsonObject payload = path switch
        {
            "" => DirectoryDocument(),
            "status.json" => StatusDocument(),
            ResultsScope => throw new ToolErrorException(
                ToolErrorCodes.UnsupportedOperation,
                $"'{ResultsScope}' supports find only, not fetch."),
            _ => ResultDocument(path),
        };

        var result = new JsonObject
        {
            ["uri"] = path,
            ["content_type"] = "application/json",
            ["content"] = payload.ToJsonString(JsonOptions.Compact),
        };

        return Task.FromResult(result);
    }

    /// <summary>
    /// Validates and normalizes the plugin-relative path. The host already
    /// strips the patchouli://plugin/{id}/ prefix; we still reject traversal,
    /// percent escapes, query/fragment, OS paths and foreign namespaces.
    /// </summary>
    internal static string NormalizeRelativePath(string uri)
    {
        if (uri.StartsWith(PluginRootUri, StringComparison.Ordinal))
        {
            uri = uri[PluginRootUri.Length..];
        }
        else if (uri.Contains("://", StringComparison.Ordinal)
            || uri.StartsWith("patchouli:", StringComparison.OrdinalIgnoreCase))
        {
            throw Invalid($"URI '{uri}' is outside this plugin's namespace.");
        }

        if (uri.Length > 0
            && (uri.StartsWith('/')
                || uri.Contains("..", StringComparison.Ordinal)
                || uri.Contains('%', StringComparison.Ordinal)
                || uri.Contains('?', StringComparison.Ordinal)
                || uri.Contains('#', StringComparison.Ordinal)
                || uri.Contains('\\', StringComparison.Ordinal)))
        {
            throw Invalid($"URI '{uri}' is not a valid plugin-relative path.");
        }

        return uri;
    }

    private JsonObject DirectoryDocument()
    {
        return new JsonObject
        {
            ["type"] = "directory",
            ["plugin_id"] = PluginId,
            ["children"] = new JsonArray(
                new JsonObject
                {
                    ["uri"] = "status.json",
                    ["type"] = "file",
                    ["media"] = "application/json",
                },
                new JsonObject
                {
                    ["uri"] = ResultsScope,
                    ["type"] = "directory",
                }),
        };
    }

    private JsonObject StatusDocument()
    {
        var report = _assetProbe.Probe();

        return new JsonObject
        {
            ["plugin_id"] = PluginId,
            ["version"] = PluginVersion,
            ["data_root"] = _assetProbe.DataRoot,
            ["downloads_performed"] = false,
            ["assets"] = new JsonObject
            {
                ["classifier_model"] = report.Classifier.ToJson(),
                ["embedding_model"] = report.Embedding.ToJson(),
                ["dataset"] = report.Dataset.ToJson(),
                ["embedding_index"] = new JsonObject
                {
                    ["ready"] = report.Dataset.Ready,
                    ["derived_from"] = "dataset",
                    ["note"] = "The streaming similarity index is built on demand from the cached embedding Parquet.",
                },
            },
            ["capabilities"] = new JsonObject
            {
                ["classify"] = new JsonObject { ["ready"] = report.ClassifyReady },
                ["similar"] = new JsonObject { ["ready"] = report.SimilarReady },
                ["analyze"] = new JsonObject { ["ready"] = report.AnalyzeReady },
            },
            ["missing_assets"] = new JsonArray(report.Classifier.MissingFiles
                .Concat(report.Embedding.MissingFiles)
                .Concat(report.Dataset.MissingFiles)
                .Select(path => (JsonNode)path)
                .ToArray()),
            ["guidance"] = report.ClassifyReady || report.SimilarReady
                ? "Missing assets must be prepared by running the standalone KuzushiClassifierApp once; this plugin never downloads."
                : "No cached assets found. Run the standalone KuzushiClassifierApp once to download models and dataset; this plugin never downloads.",
        };
    }

    private JsonObject ResultDocument(string path)
    {
        const string prefix = "results/";
        const string suffix = ".json";

        if (!path.StartsWith(prefix, StringComparison.Ordinal)
            || !path.EndsWith(suffix, StringComparison.Ordinal)
            || path.Length <= prefix.Length + suffix.Length)
        {
            throw new ToolErrorException(
                ToolErrorCodes.NotFound,
                $"No plugin resource at '{path}'.");
        }

        var resultId = path[prefix.Length..^suffix.Length];
        var stored = _resultStore.Get(resultId)
            ?? throw new ToolErrorException(
                ToolErrorCodes.NotFound,
                $"Result '{resultId}' does not exist in this plugin process (results are not persisted).");

        // The stored node is immutable and shared; every caller needs its own tree.
        return (JsonObject)stored.Result.DeepClone();
    }

    // ----------------------------------------------------------------- send

    public async Task<JsonObject> SendAsync(JsonObject? arguments, CancellationToken cancellationToken)
    {
        var endpoint = GetRequiredString(arguments, "endpoint");
        var action = GetRequiredString(arguments, "action");
        var target = NormalizeTarget(GetRequiredString(arguments, "target"));
        var requestId = GetOptionalString(arguments, "request_id");

        if (endpoint != "control")
        {
            throw new ToolErrorException(
                ToolErrorCodes.UnsupportedOperation,
                $"Unknown endpoint '{endpoint}'; this plugin declares only 'control'.");
        }

        if (!Actions.Contains(action, StringComparer.Ordinal))
        {
            throw new ToolErrorException(
                ToolErrorCodes.UnsupportedOperation,
                $"Unknown action '{action}'; supported: classify, similar, analyze.");
        }

        var args = GetRequiredObject(arguments, "args");
        var (image, topK) = ParseActionArgs(args);
        cancellationToken.ThrowIfCancellationRequested();

        var canonical = new JsonObject
        {
            ["endpoint"] = endpoint,
            ["action"] = action,
            ["target"] = target,
            ["args"] = args.DeepClone(),
        };

        SendOutcome outcome;
        if (requestId is null)
        {
            outcome = await ExecuteSendAsync(action, image, topK, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            var registration = _dedupeStore.Register(requestId, canonical);
            if (registration.IsOwner)
            {
                outcome = await ExecuteSendAsync(action, image, topK, cancellationToken)
                    .ConfigureAwait(false);
                registration.Entry.Complete(outcome);
            }
            else
            {
                outcome = await registration.Entry.Outcome.WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        if (outcome.Error is not null)
        {
            throw new ToolErrorException(outcome.Error.Code, outcome.Error.Message);
        }

        // Dedupe replays share one stored outcome instance; clone so each response
        // attaches its own tree (JsonNode can only have a single parent).
        return (JsonObject)outcome.Result!.DeepClone();
    }

    private static string NormalizeTarget(string target)
    {
        if (target == "")
        {
            return target;
        }

        if (target == PluginRootUri || target == PluginRootUri.TrimEnd('/'))
        {
            return "";
        }

        if (target.StartsWith("patchouli:", StringComparison.OrdinalIgnoreCase))
        {
            throw Invalid($"send 'target' '{target}' is outside this plugin's root.");
        }

        throw Invalid($"send 'target' must be \"\" (this plugin's root); got '{target}'.");
    }

    private async Task<SendOutcome> ExecuteSendAsync(
        string action,
        KuzushiImage image,
        int topK,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _inference
                .RunAsync(action, image, topK, cancellationToken)
                .ConfigureAwait(false);

            var resultId = "r_" + Guid.NewGuid().ToString("N");
            result["result_id"] = resultId;
            result["action"] = action;
            result["resource_uri"] = $"results/{resultId}.json";

            _resultStore.Add(resultId, (JsonObject)result.DeepClone());

            return SendOutcome.Success(new JsonObject
            {
                ["state"] = "completed",
                ["result"] = result,
            });
        }
        catch (ToolErrorException ex)
        {
            return SendOutcome.Failure(ex.ToError());
        }
        catch (OperationCanceledException)
        {
            return SendOutcome.Failure(new ToolError(
                ToolErrorCodes.Cancelled,
                "The request was cancelled."));
        }
    }

    private static (KuzushiImage Image, int TopK) ParseActionArgs(JsonObject args)
    {
        foreach (var key in args.Select(p => p.Key))
        {
            if (!SendArgsKeys.Contains(key, StringComparer.Ordinal))
            {
                throw Invalid($"args contains unsupported property '{key}'.");
            }
        }

        string? imageBase64;
        try
        {
            imageBase64 = args["image_base64"]?.GetValue<string>();
        }
        catch (InvalidOperationException ex)
        {
            throw new ToolErrorException(
                ToolErrorCodes.InvalidArgument,
                "args.image_base64 must be a base64 string.",
                ex);
        }

        string? mediaType;
        try
        {
            mediaType = args["media_type"]?.GetValue<string>();
        }
        catch (InvalidOperationException ex)
        {
            throw new ToolErrorException(
                ToolErrorCodes.InvalidArgument,
                "args.media_type must be a string.",
                ex);
        }

        var topK = 10;
        if (args["top_k"] is JsonNode topKNode)
        {
            if (topKNode is not JsonValue topKValue
                || !topKValue.TryGetValue<int>(out topK))
            {
                throw Invalid("args.top_k must be an integer.");
            }
        }

        if (topK < 1 || topK > 50)
        {
            throw Invalid($"args.top_k must be in 1..50; got {topK}.");
        }

        return (PluginImageInput.Parse(imageBase64, mediaType), topK);
    }

    // -------------------------------------------------------------- helpers

    private static string GetRequiredString(JsonObject? arguments, string key)
    {
        var node = arguments?[key]
            ?? throw Invalid($"Missing required argument '{key}'.");

        try
        {
            return node.GetValue<string>();
        }
        catch (InvalidOperationException ex)
        {
            throw new ToolErrorException(
                ToolErrorCodes.InvalidArgument,
                $"Argument '{key}' must be a string.",
                ex);
        }
    }

    private static string? GetOptionalString(JsonObject? arguments, string key)
    {
        var node = arguments?[key];
        if (node is null)
        {
            return null;
        }

        try
        {
            return node.GetValue<string>();
        }
        catch (InvalidOperationException ex)
        {
            throw new ToolErrorException(
                ToolErrorCodes.InvalidArgument,
                $"Argument '{key}' must be a string.",
                ex);
        }
    }

    private static int GetOptionalInt(JsonObject? arguments, string key, int defaultValue)
    {
        var node = arguments?[key];
        if (node is null)
        {
            return defaultValue;
        }

        if (node is JsonValue value && value.TryGetValue<int>(out var parsed))
        {
            return parsed;
        }

        throw Invalid($"Argument '{key}' must be an integer.");
    }

    private static JsonObject GetRequiredObject(JsonObject? arguments, string key)
    {
        var node = arguments?[key]
            ?? throw Invalid($"Missing required argument '{key}'.");

        return node as JsonObject
            ?? throw Invalid($"Argument '{key}' must be an object.");
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var builder = new StringBuilder(value.Replace('-', '+').Replace('_', '/'));
        while (builder.Length % 4 != 0)
        {
            builder.Append('=');
        }

        return Convert.FromBase64String(builder.ToString());
    }

    private static ToolErrorException Invalid(string message) =>
        new(ToolErrorCodes.InvalidArgument, message);
}

internal static class JsonOptions
{
    public static readonly JsonSerializerOptions Compact = new()
    {
        WriteIndented = false,
    };
}
