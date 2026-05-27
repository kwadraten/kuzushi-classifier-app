using KuzushiClassifierApp.Models;

namespace KuzushiClassifierApp.Services;

public sealed class ParquetStreamingEmbeddingIndexService(IEmbeddingCacheService embeddingCacheService)
    : IEmbeddingIndexService
{
    private int _count;
    private int _dimensions;

    public bool IsReady { get; private set; }

    public int Count => _count;

    public async Task BuildAsync(
        IEnumerable<DatasetImageEmbedding> embeddings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(embeddings);

        var metadata = await embeddingCacheService
            .TryReadMetadataAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("The embedding Parquet cache has not been built.");

        _count = metadata.Count;
        _dimensions = metadata.VectorDimensions;
        IsReady = _count > 0 && _dimensions > 0;
    }

    public async Task<IReadOnlyList<SimilarImageResult>> SearchAsync(
        ImageEmbedding query,
        int topK = 10,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(topK);

        if (!IsReady)
        {
            throw new InvalidOperationException("The embedding index has not been built.");
        }

        if (query.Dimensions != _dimensions)
        {
            throw new ArgumentException(
                "The query embedding dimensions do not match the index.",
                nameof(query));
        }

        var topResults = new List<SimilarImageResult>(topK);

        await foreach (var embedding in embeddingCacheService
            .StreamAsync(cancellationToken)
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (embedding.Vector.Count != query.Dimensions)
            {
                throw new InvalidOperationException(
                    $"Embedding '{embedding.Image.Id}' has {embedding.Vector.Count} dimensions; expected {query.Dimensions}.");
            }

            var result = new SimilarImageResult(
                embedding.Image,
                EmbeddingSimilarity.CosineSimilarity(query.Vector, embedding.Vector));

            InsertTopResult(topResults, result, topK);
        }

        return topResults;
    }

    private static void InsertTopResult(
        List<SimilarImageResult> topResults,
        SimilarImageResult result,
        int topK)
    {
        var insertIndex = topResults.FindIndex(existing => result.Similarity > existing.Similarity);
        if (insertIndex < 0)
        {
            if (topResults.Count < topK)
            {
                topResults.Add(result);
            }

            return;
        }

        topResults.Insert(insertIndex, result);

        if (topResults.Count > topK)
        {
            topResults.RemoveAt(topResults.Count - 1);
        }
    }
}
