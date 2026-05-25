using System.Text.Json;
using KuzushiClassifierApp.Models;
using KuzushiClassifierApp.Platform;

namespace KuzushiClassifierApp.Services;

public sealed class JsonLinesDevelopmentImageLibraryService : IImageLibraryService
{
    private readonly string _cacheDirectory;
    private readonly string _metadataPath;
    private IReadOnlyList<DatasetImage>? _images;

    public JsonLinesDevelopmentImageLibraryService(IAppDataPathProvider appDataPathProvider)
    {
        ArgumentNullException.ThrowIfNull(appDataPathProvider);

        _cacheDirectory = Path.Combine(appDataPathProvider.GetDatasetCacheDirectory(), "cache");
        _metadataPath = Path.Combine(_cacheDirectory, "metadata.jsonl");
    }

    public async Task<IReadOnlyList<DatasetImage>> LoadImagesAsync(
        CancellationToken cancellationToken = default)
    {
        if (_images is not null)
        {
            return _images;
        }

        if (!File.Exists(_metadataPath))
        {
            throw new FileNotFoundException(
                "Development dataset cache is missing. It will be built automatically from Parquet files on first run.",
                _metadataPath);
        }

        var images = new List<DatasetImage>();

        await foreach (var line in File
            .ReadLinesAsync(_metadataPath, cancellationToken)
            .ConfigureAwait(false))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var row = JsonSerializer.Deserialize<DevelopmentDatasetRow>(
                line,
                JsonSerializerOptions.Web);

            if (row is null)
            {
                continue;
            }

            images.Add(new DatasetImage(
                row.Id,
                row.Label,
                SourceUri: null,
                Path.Combine(_cacheDirectory, row.LocalPath)));
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
            GuessMediaType(image.LocalPath),
            image.Id);
    }

    private static string GuessMediaType(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            _ => "application/octet-stream",
        };
    }

    private sealed record DevelopmentDatasetRow(
        string Id,
        string Label,
        string? Unicode,
        string? SourcePath,
        string LocalPath);
}
