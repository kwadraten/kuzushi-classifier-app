using KuzushiClassifierApp.Models;

namespace KuzushiClassifierApp.Controllers;

public sealed class ImageAnalysisController(
    ClassificationController classificationController,
    SimilaritySearchController similaritySearchController)
{
    public const int DefaultResultCount = 10;

    public async Task<ImageAnalysisResult> AnalyzeAsync(
        KuzushiImage image,
        int topK = DefaultResultCount,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(topK);

        var predictionTask = classificationController.ClassifyAsync(image, topK, cancellationToken);
        var similarImagesTask = similaritySearchController.FindSimilarAsync(image, topK, cancellationToken);

        await Task.WhenAll(predictionTask, similarImagesTask).ConfigureAwait(false);

        return new ImageAnalysisResult(
            await predictionTask.ConfigureAwait(false),
            await similarImagesTask.ConfigureAwait(false));
    }
}
