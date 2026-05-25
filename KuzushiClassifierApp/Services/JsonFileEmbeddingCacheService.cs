using System.Text.Json;
using KuzushiClassifierApp.Models;
using KuzushiClassifierApp.Platform;

namespace KuzushiClassifierApp.Services;

public sealed class JsonFileEmbeddingCacheService : IEmbeddingCacheService
{
    private const int CacheVersion = 1;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    private readonly string _cacheFilePath;

    public JsonFileEmbeddingCacheService(
        IAppDataPathProvider appDataPathProvider,
        string cacheFileName = "image-embeddings.shikiji-768.v1.json")
    {
        ArgumentNullException.ThrowIfNull(appDataPathProvider);

        _cacheFilePath = Path.Combine(
            appDataPathProvider.GetDatasetCacheDirectory(),
            cacheFileName);
    }

    public async Task<IReadOnlyList<DatasetImageEmbedding>?> TryLoadAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_cacheFilePath))
        {
            return null;
        }

        await using var stream = File.OpenRead(_cacheFilePath);

        EmbeddingCacheDocument? document;

        try
        {
            document = await JsonSerializer
                .DeserializeAsync<EmbeddingCacheDocument>(stream, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }

        if (document is null || document.Version != CacheVersion)
        {
            return null;
        }

        return document.Items
            .Select(item => new DatasetImageEmbedding(
                new DatasetImage(
                    item.Id,
                    item.Label,
                    item.SourceUri,
                    item.LocalPath),
                item.Vector))
            .ToArray();
    }

    public async Task SaveAsync(
        IReadOnlyList<DatasetImageEmbedding> embeddings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(embeddings);

        var directory = Path.GetDirectoryName(_cacheFilePath);

        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var document = new EmbeddingCacheDocument(
            CacheVersion,
            DateTimeOffset.UtcNow,
            embeddings
                .Select(embedding => new EmbeddingCacheItem(
                    embedding.Image.Id,
                    embedding.Image.Label,
                    embedding.Image.SourceUri,
                    embedding.Image.LocalPath,
                    embedding.Vector.ToArray()))
                .ToArray());

        var tempFilePath = $"{_cacheFilePath}.{Guid.NewGuid():N}.tmp";

        try
        {
            await using (var stream = File.Create(tempFilePath))
            {
                await JsonSerializer
                    .SerializeAsync(stream, document, SerializerOptions, cancellationToken)
                    .ConfigureAwait(false);
            }

            File.Move(tempFilePath, _cacheFilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempFilePath))
            {
                File.Delete(tempFilePath);
            }
        }
    }

    private sealed record EmbeddingCacheDocument(
        int Version,
        DateTimeOffset CreatedAt,
        EmbeddingCacheItem[] Items);

    private sealed record EmbeddingCacheItem(
        string Id,
        string Label,
        string? SourceUri,
        string? LocalPath,
        float[] Vector);
}
