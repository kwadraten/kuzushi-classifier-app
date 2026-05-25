using KuzushiClassifierApp.Models;

namespace KuzushiClassifierApp.Services;

public sealed class PassThroughImagePreprocessingService : IImagePreprocessingService
{
    public Task<KuzushiImage> PrepareForModelAsync(
        KuzushiImage image,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(image);
    }
}
