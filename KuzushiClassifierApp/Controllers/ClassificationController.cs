using KuzushiClassifierApp.Models;
using KuzushiClassifierApp.Services;

namespace KuzushiClassifierApp.Controllers;

public sealed class ClassificationController(
    IImagePreprocessingService imagePreprocessingService,
    IImageClassifierService imageClassifierService)
{
    public const int DefaultResultCount = 10;

    public async Task<KuzushiPrediction> ClassifyAsync(
        KuzushiImage image,
        int topK = DefaultResultCount,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(topK);

        var preparedImage = await imagePreprocessingService
            .PrepareForModelAsync(image, cancellationToken)
            .ConfigureAwait(false);

        var prediction = await imageClassifierService
            .PredictAsync(preparedImage, topK, cancellationToken)
            .ConfigureAwait(false);

        return prediction.Top(topK);
    }
}
