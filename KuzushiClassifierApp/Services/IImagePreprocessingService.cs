using KuzushiClassifierApp.Models;

namespace KuzushiClassifierApp.Services;

public interface IImagePreprocessingService
{
    Task<KuzushiImage> PrepareForModelAsync(
        KuzushiImage image,
        CancellationToken cancellationToken = default);
}
