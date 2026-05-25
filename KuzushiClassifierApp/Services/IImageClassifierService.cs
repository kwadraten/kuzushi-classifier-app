using KuzushiClassifierApp.Models;

namespace KuzushiClassifierApp.Services;

public interface IImageClassifierService
{
    Task<KuzushiPrediction> PredictAsync(
        KuzushiImage image,
        int topK = 10,
        CancellationToken cancellationToken = default);
}
