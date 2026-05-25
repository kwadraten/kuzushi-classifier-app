using KuzushiClassifierApp.Models;

namespace KuzushiClassifierApp.Services;

public interface IEmbeddingIndexService
{
    bool IsReady { get; }

    int Count { get; }

    Task BuildAsync(
        IEnumerable<DatasetImageEmbedding> embeddings,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SimilarImageResult>> SearchAsync(
        ImageEmbedding query,
        int topK = 10,
        CancellationToken cancellationToken = default);
}
