using DotVector.Api;
using KuzushiClassifierApp.Models;
using KuzushiClassifierApp.Platform;

namespace KuzushiClassifierApp.Services;

public sealed class DotVectorEmbeddingIndexService : IEmbeddingIndexService, IDisposable
{
    private readonly string _datasetDirectory;
    private readonly string _vectorDirectory;
    private VectorDatabase? _database;
    private Collection<string>? _collection;

    public DotVectorEmbeddingIndexService(IAppDataPathProvider appDataPathProvider)
    {
        ArgumentNullException.ThrowIfNull(appDataPathProvider);

        _datasetDirectory = appDataPathProvider.GetDatasetCacheDirectory();
        _vectorDirectory = Path.Combine(_datasetDirectory, "vectors", "dotvector-shikiji-diskann");
    }

    public bool IsReady => _collection is not null;

    public int Count { get; private set; }

    public async Task BuildAsync(
        IEnumerable<DatasetImageEmbedding> embeddings,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!Directory.Exists(_vectorDirectory))
        {
            throw new DirectoryNotFoundException($"Prebuilt DotVector index was not found: {_vectorDirectory}");
        }

        _database = new VectorDatabase(_vectorDirectory);
        _collection = _database.GetCollection<string>("images");

        Count = _collection.Count > int.MaxValue
            ? int.MaxValue
            : (int)_collection.Count;

        if (Count == 0)
        {
            throw new InvalidOperationException("Prebuilt DotVector image collection is empty or missing.");
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SimilarImageResult>> SearchAsync(
        ImageEmbedding query,
        int topK = 10,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(topK);
        cancellationToken.ThrowIfCancellationRequested();

        if (_collection is null)
        {
            throw new InvalidOperationException("The DotVector embedding index has not been loaded.");
        }

        var results = _collection.Search(query.Vector.ToArray(), topK);

        var similarImages = results
            .Select(result =>
            {
                var imageId = result.Key;
                var label = GetPayloadString(result.Payload, "label");
                var shardFile = GetPayloadString(result.Payload, "shard_file");
                var rowGroup = GetPayloadInt(result.Payload, "row_group");
                var row = GetPayloadInt(result.Payload, "row");
                var localPath = string.IsNullOrWhiteSpace(shardFile) || rowGroup < 0 || row < 0
                    ? null
                    : $"{Path.Combine(_datasetDirectory, "data", shardFile)}::{rowGroup}::{row}";

                return new SimilarImageResult(
                    new DatasetImage(imageId, label, SourceUri: null, LocalPath: localPath),
                    Math.Clamp(1f - result.Score, 0f, 1f));
            })
            .ToArray();

        await Task.CompletedTask.ConfigureAwait(false);
        return similarImages;
    }

    public void Dispose()
    {
        _database?.Dispose();
    }

    private static string GetPayloadString(
        IReadOnlyDictionary<string, object?>? payload,
        string key)
    {
        if (payload is null || !payload.TryGetValue(key, out var value) || value is null)
        {
            return "";
        }

        return value.ToString() ?? "";
    }

    private static int GetPayloadInt(
        IReadOnlyDictionary<string, object?>? payload,
        string key)
    {
        if (payload is null || !payload.TryGetValue(key, out var value) || value is null)
        {
            return -1;
        }

        if (value is int intValue)
        {
            return intValue;
        }

        return int.TryParse(value.ToString(), out var parsed) ? parsed : -1;
    }
}
