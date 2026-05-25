using KuzushiClassifierApp.Models;

namespace KuzushiClassifierApp.Services;

public interface IImageLibraryService
{
    Task<IReadOnlyList<DatasetImage>> LoadImagesAsync(
        CancellationToken cancellationToken = default);

    Task<KuzushiImage> LoadImageAsync(
        DatasetImage image,
        CancellationToken cancellationToken = default);
}
