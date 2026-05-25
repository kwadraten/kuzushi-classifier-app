using KuzushiClassifierApp.Models;

namespace KuzushiClassifierApp.Services;

public interface IEmbeddingCacheService
{
    Task<IReadOnlyList<DatasetImageEmbedding>?> TryLoadAsync(
        CancellationToken cancellationToken = default);

    Task SaveAsync(
        IReadOnlyList<DatasetImageEmbedding> embeddings,
        CancellationToken cancellationToken = default);
}
