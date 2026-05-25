using KuzushiClassifierApp.Models;

namespace KuzushiClassifierApp.Services;

public interface IImageLibraryService
{
    Task<IReadOnlyList<DatasetImage>> LoadImagesAsync(
        CancellationToken cancellationToken = default,
        IProgress<AssetPreparationProgress>? progress = null);

    Task<KuzushiImage> LoadImageAsync(
        DatasetImage image,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<(DatasetImage Metadata, KuzushiImage Image)> StreamAllImagesAsync(
        CancellationToken cancellationToken = default);
}
