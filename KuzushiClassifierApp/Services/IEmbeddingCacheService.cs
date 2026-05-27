using KuzushiClassifierApp.Models;

namespace KuzushiClassifierApp.Services;

public interface IEmbeddingCacheService
{
    Task<EmbeddingCacheMetadata?> TryReadMetadataAsync(
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<DatasetImageEmbedding> StreamAsync(
        CancellationToken cancellationToken = default);

    Task SaveAsync(
        IAsyncEnumerable<DatasetImageEmbedding> embeddings,
        CancellationToken cancellationToken = default);
}

public sealed record EmbeddingCacheMetadata(
    int Count,
    int VectorDimensions);
