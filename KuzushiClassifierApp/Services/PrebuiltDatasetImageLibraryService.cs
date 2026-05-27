using System.Text.Json;
using KuzushiClassifierApp.Models;
using KuzushiClassifierApp.Platform;

namespace KuzushiClassifierApp.Services;

public sealed class PrebuiltDatasetImageLibraryService : IImageLibraryService
{
    private readonly string _datasetDirectory;
    private readonly string _recordsPath;
    private IReadOnlyList<DatasetImage>? _images;

    public PrebuiltDatasetImageLibraryService(IAppDataPathProvider appDataPathProvider)
    {
        ArgumentNullException.ThrowIfNull(appDataPathProvider);

        _datasetDirectory = appDataPathProvider.GetDatasetCacheDirectory();
        _recordsPath = Path.Combine(_datasetDirectory, "metadata", "records.jsonl");
    }

    public async Task<IReadOnlyList<DatasetImage>> LoadImagesAsync(
        CancellationToken cancellationToken = default,
        IProgress<AssetPreparationProgress>? progress = null)
    {
        if (_images is not null)
        {
            return _images;
        }

        if (!File.Exists(_recordsPath))
        {
            throw new FileNotFoundException("Prebuilt dataset metadata is missing.", _recordsPath);
        }

        var images = new List<DatasetImage>();

        await foreach (var line in File.ReadLinesAsync(_recordsPath, cancellationToken).ConfigureAwait(false))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var row = JsonSerializer.Deserialize<PrebuiltDatasetRecord>(line, JsonSerializerOptions.Web);
            if (row is null)
            {
                continue;
            }

            images.Add(new DatasetImage(
                row.Id,
                row.Label,
                SourceUri: null,
                LocalPath: Path.Combine(_datasetDirectory, row.ImageFile)));

            if (images.Count % 1000 == 0)
            {
                progress?.Report(new AssetPreparationProgress(
                    AssetPreparationStep.LoadingDataset,
                    $"Loading prebuilt dataset metadata: {images.Count:N0} images.",
                    ItemsProcessed: images.Count));
            }
        }

        _images = images;
        return _images;
    }

    public async Task<KuzushiImage> LoadImageAsync(
        DatasetImage image,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);

        if (string.IsNullOrWhiteSpace(image.LocalPath))
        {
            throw new InvalidOperationException($"Dataset image {image.Id} has no local path.");
        }

        var bytes = await File
            .ReadAllBytesAsync(image.LocalPath, cancellationToken)
            .ConfigureAwait(false);

        return KuzushiImage.FromBytes(
            bytes,
            Path.GetFileName(image.LocalPath),
            "image/webp",
            image.Id);
    }

    public async IAsyncEnumerable<(DatasetImage Metadata, KuzushiImage Image)> StreamAllImagesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var images = await LoadImagesAsync(cancellationToken).ConfigureAwait(false);

        foreach (var image in images)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var kuzushiImage = await LoadImageAsync(image, cancellationToken).ConfigureAwait(false);
            yield return (image, kuzushiImage);
        }
    }

    private sealed record PrebuiltDatasetRecord(
        string Id,
        string Label,
        string ImageFile);
}

