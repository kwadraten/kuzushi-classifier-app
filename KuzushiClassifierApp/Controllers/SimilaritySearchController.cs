using KuzushiClassifierApp.Models;
using KuzushiClassifierApp.Services;

namespace KuzushiClassifierApp.Controllers;

public sealed class SimilaritySearchController(
    IImagePreprocessingService imagePreprocessingService,
    IImageEmbeddingService imageEmbeddingService,
    IEmbeddingIndexService embeddingIndexService)
{
    public const int DefaultResultCount = 10;

    public async Task<IReadOnlyList<SimilarImageResult>> FindSimilarAsync(
        KuzushiImage image,
        int topK = DefaultResultCount,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(topK);

        var preparedImage = await imagePreprocessingService
            .PrepareForModelAsync(image, cancellationToken)
            .ConfigureAwait(false);

        var queryEmbedding = await imageEmbeddingService
            .EmbedAsync(preparedImage, cancellationToken)
            .ConfigureAwait(false);

        return await embeddingIndexService
            .SearchAsync(queryEmbedding, topK, cancellationToken)
            .ConfigureAwait(false);
    }
}
