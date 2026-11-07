using System.Text.Json.Nodes;

namespace KuzushiClassifierApp.Plugin;

/// <summary>
/// In-memory, process-lifetime store for immutable action results.
/// Bounded to <see cref="MaxResults"/> entries; oldest entries are evicted.
/// </summary>
public sealed class ResultStore
{
    public const int MaxResults = 64;

    private readonly object _gate = new();
    private readonly Dictionary<string, StoredResult> _results = new(StringComparer.Ordinal);
    private readonly Queue<string> _insertionOrder = new();

    public StoredResult Add(string resultId, JsonObject result)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resultId);
        ArgumentNullException.ThrowIfNull(result);

        var stored = new StoredResult(resultId, result);

        lock (_gate)
        {
            _results[resultId] = stored;
            _insertionOrder.Enqueue(resultId);

            while (_results.Count > MaxResults && _insertionOrder.Count > 0)
            {
                _results.Remove(_insertionOrder.Dequeue());
            }
        }

        return stored;
    }

    public StoredResult? Get(string resultId)
    {
        lock (_gate)
        {
            return _results.TryGetValue(resultId, out var result) ? result : null;
        }
    }

    /// <summary>
    /// Snapshot in insertion order for results/ browsing.
    /// </summary>
    public IReadOnlyList<StoredResult> List()
    {
        lock (_gate)
        {
            return _insertionOrder
                .Where(_results.ContainsKey)
                .Select(id => _results[id])
                .ToArray();
        }
    }
}

public sealed record StoredResult(string ResultId, JsonObject Result)
{
    public string ResourceUri => $"results/{ResultId}.json";
}
