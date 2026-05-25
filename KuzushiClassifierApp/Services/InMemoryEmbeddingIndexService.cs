using KuzushiClassifierApp.Models;

namespace KuzushiClassifierApp.Services;

public sealed class InMemoryEmbeddingIndexService : IEmbeddingIndexService
{
    private IReadOnlyList<DatasetImageEmbedding> _embeddings = Array.Empty<DatasetImageEmbedding>();

    public bool IsReady => _embeddings.Count > 0;

    public int Count => _embeddings.Count;

    public Task BuildAsync(
        IEnumerable<DatasetImageEmbedding> embeddings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(embeddings);
        cancellationToken.ThrowIfCancellationRequested();

        var materialized = embeddings.ToArray();
        ValidateEmbeddings(materialized);

        _embeddings = materialized;

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SimilarImageResult>> SearchAsync(
        ImageEmbedding query,
        int topK = 10,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(topK);
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsReady)
        {
            throw new InvalidOperationException("The embedding index has not been built.");
        }

        if (query.Dimensions != _embeddings[0].Vector.Count)
        {
            throw new ArgumentException(
                "The query embedding dimensions do not match the index.",
                nameof(query));
        }

        IReadOnlyList<SimilarImageResult> results = _embeddings
            .Select(embedding => new SimilarImageResult(
                embedding.Image,
                EmbeddingSimilarity.CosineSimilarity(query.Vector, embedding.Vector)))
            .OrderByDescending(result => result.Similarity)
            .Take(topK)
            .ToArray();

        return Task.FromResult(results);
    }

    private static void ValidateEmbeddings(IReadOnlyList<DatasetImageEmbedding> embeddings)
    {
        if (embeddings.Count == 0)
        {
            return;
        }

        var expectedDimensions = embeddings[0].Vector.Count;

        if (expectedDimensions == 0)
        {
            throw new ArgumentException("Embedding vectors must not be empty.", nameof(embeddings));
        }

        foreach (var embedding in embeddings)
        {
            if (embedding.Vector.Count != expectedDimensions)
            {
                throw new ArgumentException(
                    "All embedding vectors in the index must have the same dimensions.",
                    nameof(embeddings));
            }
        }
    }
}
