using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KuzushiClassifierApp.Plugin;

/// <summary>
/// Process-local, bounded request_id dedupe. Same id + canonical same
/// message reuses the first (immutable) result — including while it is still
/// in flight. Same id + different message is a REVISION_CONFLICT.
/// </summary>
public sealed class DedupeStore
{
    public const int MaxEntries = 256;

    private readonly object _gate = new();
    private readonly Dictionary<string, DedupeEntry> _entries = new(StringComparer.Ordinal);
    private readonly Queue<string> _insertionOrder = new();

    /// <summary>
    /// Registers a request_id. Returns the entry to await/complete when this
    /// caller is the owner, or the existing entry when the request is a
    /// replay. Throws REVISION_CONFLICT when the id was used with a different
    /// message.
    /// </summary>
    public DedupeRegistration Register(string requestId, JsonObject canonicalMessage)
    {
        var hash = CanonicalHash(canonicalMessage);

        lock (_gate)
        {
            if (_entries.TryGetValue(requestId, out var existing))
            {
                if (!string.Equals(existing.MessageHash, hash, StringComparison.Ordinal))
                {
                    throw new ToolErrorException(
                        ToolErrorCodes.RevisionConflict,
                        $"request_id '{requestId}' was already used with a different message.");
                }

                return new DedupeRegistration(existing, false);
            }

            var entry = new DedupeEntry(hash);
            _entries[requestId] = entry;
            _insertionOrder.Enqueue(requestId);

            // Only evict entries whose work has already completed; never drop
            // an in-flight registration.
            while (_entries.Count > MaxEntries && _insertionOrder.Count > 0)
            {
                var oldest = _insertionOrder.Peek();
                if (_entries[oldest].IsCompleted)
                {
                    _insertionOrder.Dequeue();
                    _entries.Remove(oldest);
                }
                else
                {
                    _insertionOrder.Dequeue();
                    _insertionOrder.Enqueue(oldest);
                    if (_insertionOrder.All(id => !_entries[id].IsCompleted))
                    {
                        break;
                    }
                }
            }

            return new DedupeRegistration(entry, true);
        }
    }

    /// <summary>Deterministic hash of the canonical (key-sorted) message.</summary>
    public static string CanonicalHash(JsonObject message)
    {
        var canonical = Canonicalize(message);
        var bytes = Encoding.UTF8.GetBytes(canonical);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static string Canonicalize(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return "null";
            case JsonObject obj:
            {
                var properties = obj
                    .Select(property => property)
                    .OrderBy(property => property.Key, StringComparer.Ordinal)
                    .Select(property =>
                        JsonSerializer.Serialize(property.Key) + ":" + Canonicalize(property.Value));
                return "{" + string.Join(",", properties) + "}";
            }
            case JsonArray array:
                return "[" + string.Join(",", array.Select(Canonicalize)) + "]";
            case JsonValue value:
                return value.ToJsonString(JsonSerializerOptions.Default);
            default:
                return "null";
        }
    }
}

public sealed class DedupeEntry
{
    private readonly TaskCompletionSource<SendOutcome> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public DedupeEntry(string messageHash)
    {
        MessageHash = messageHash;
    }

    public string MessageHash { get; }

    public bool IsCompleted => _completion.Task.IsCompleted;

    public Task<SendOutcome> Outcome => _completion.Task;

    public void Complete(SendOutcome outcome) => _completion.TrySetResult(outcome);

    public void Fault(ToolError error) => _completion.TrySetResult(SendOutcome.Failure(error));
}

public sealed class DedupeRegistration
{
    public DedupeRegistration(DedupeEntry entry, bool isOwner)
    {
        Entry = entry;
        IsOwner = isOwner;
    }

    public DedupeEntry Entry { get; }

    /// <summary>True when this caller must execute the work; false = replay.</summary>
    public bool IsOwner { get; }
}

/// <summary>Immutable outcome shared by dedupe replays.</summary>
public sealed record SendOutcome(ToolError? Error, JsonObject? Result)
{
    public bool IsSuccess => Error is null;

    public static SendOutcome Success(JsonObject result) => new(null, result);

    public static SendOutcome Failure(ToolError error) => new(error, null);
}
