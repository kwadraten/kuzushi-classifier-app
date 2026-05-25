using KuzushiClassifierApp.Models;

namespace KuzushiClassifierApp.Services;

public interface IImageEmbeddingService
{
    Task<ImageEmbedding> EmbedAsync(
        KuzushiImage image,
        CancellationToken cancellationToken = default);
}
