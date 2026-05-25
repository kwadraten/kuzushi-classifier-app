using KuzushiClassifierApp.Models;
using KuzushiClassifierApp.Platform;

namespace KuzushiClassifierApp.Services;

public sealed class StreamingParquetImageLibraryService : IImageLibraryService
{
    private readonly string _parquetDir;

    public StreamingParquetImageLibraryService(IAppDataPathProvider appDataPathProvider)
    {
        _parquetDir = Path.Combine(appDataPathProvider.GetDatasetCacheDirectory(), "data");
    }

    public async Task<IReadOnlyList<DatasetImage>> LoadImagesAsync(
        CancellationToken cancellationToken = default,
        IProgress<AssetPreparationProgress>? progress = null)
    {
        var parquetFiles = GetParquetFiles();
        var images = new List<DatasetImage>();

        for (var fileIndex = 0; fileIndex < parquetFiles.Length; fileIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var file = parquetFiles[fileIndex];
            var fileName = Path.GetFileName(file);

            progress?.Report(new AssetPreparationProgress(
                AssetPreparationStep.LoadingDataset,
                $"Scanning Parquet metadata: {fileName} ({fileIndex + 1} of {parquetFiles.Length}).",
                Fraction: (double)fileIndex / parquetFiles.Length,
                ItemsProcessed: images.Count));

            await using var fs = File.OpenRead(file);
            using var reader = await Parquet.ParquetReader
                .CreateAsync(fs, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var dataFields = reader.Schema.GetDataFields();
            var charField = dataFields.FirstOrDefault(f =>
                f.Name.Equals("char", StringComparison.OrdinalIgnoreCase));
            var unicodeField = dataFields.FirstOrDefault(f =>
                f.Name.Equals("unicode", StringComparison.OrdinalIgnoreCase));

            if (charField is null)
            {
                continue;
            }

            for (var rgi = 0; rgi < reader.RowGroupCount; rgi++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                using var rowGroupReader = reader.OpenRowGroupReader(rgi);

                var charColumn = await rowGroupReader
                    .ReadColumnAsync(charField, cancellationToken)
                    .ConfigureAwait(false);

                var charValues = (string[])charColumn.Data;

                for (var row = 0; row < charValues.Length; row++)
                {
                    var globalIndex = images.Count;

                    images.Add(new DatasetImage(
                        Id: $"parq_{globalIndex:D6}",
                        Label: charValues[row],
                        SourceUri: null,
                        LocalPath: $"{file}::{rgi}::{row}"));
                }
            }
        }

        return images;
    }

    public async Task<KuzushiImage> LoadImageAsync(
        DatasetImage image,
        CancellationToken cancellationToken = default)
    {
        var parsed = ParseLocalPath(image.LocalPath);
        if (parsed is null)
        {
            throw new InvalidOperationException(
                $"Cannot resolve image {image.Id} from local path.");
        }

        var bytes = await ReadImageBytesAsync(
            parsed.Value.File, parsed.Value.RowGroup, parsed.Value.Row, cancellationToken)
            .ConfigureAwait(false);

        return KuzushiImage.FromBytes(bytes, $"{image.Id}.png", "image/png", image.Id);
    }

    public async IAsyncEnumerable<(DatasetImage Metadata, KuzushiImage Image)> StreamAllImagesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var parquetFiles = GetParquetFiles();
        var globalIndex = 0;

        foreach (var file in parquetFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await using var fs = File.OpenRead(file);
            using var reader = await Parquet.ParquetReader
                .CreateAsync(fs, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var dataFields = reader.Schema.GetDataFields();
            var imageField = dataFields.FirstOrDefault(f =>
                f.Name.Equals("image", StringComparison.OrdinalIgnoreCase));
            var charField = dataFields.FirstOrDefault(f =>
                f.Name.Equals("char", StringComparison.OrdinalIgnoreCase));

            if (imageField is null || charField is null)
            {
                continue;
            }

            for (var rgi = 0; rgi < reader.RowGroupCount; rgi++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                using var rowGroupReader = reader.OpenRowGroupReader(rgi);

                var imageColumn = await rowGroupReader
                    .ReadColumnAsync(imageField, cancellationToken)
                    .ConfigureAwait(false);

                var charColumn = await rowGroupReader
                    .ReadColumnAsync(charField, cancellationToken)
                    .ConfigureAwait(false);

                var charValues = (string[])charColumn.Data;
                var imageBytes = ExtractImageBytes(imageColumn.Data);
                var rowCount = charValues.Length;

                for (var row = 0; row < rowCount; row++)
                {
                    var metadata = new DatasetImage(
                        Id: $"parq_{globalIndex:D6}",
                        Label: charValues[row],
                        SourceUri: null,
                        LocalPath: $"{file}::{rgi}::{row}");

                    var bytes = row < imageBytes.Length ? imageBytes[row] : null;
                    if (bytes is null || bytes.Length == 0)
                    {
                        globalIndex++;
                        continue;
                    }

                    var kuzushiImage = KuzushiImage.FromBytes(
                        bytes, $"{metadata.Id}.png", "image/png", metadata.Id);

                    globalIndex++;

                    yield return (metadata, kuzushiImage);
                }
            }
        }
    }

    private string[] GetParquetFiles()
    {
        if (!Directory.Exists(_parquetDir))
        {
            return Array.Empty<string>();
        }

        return Directory.EnumerateFiles(_parquetDir, "*.parquet")
            .OrderBy(f => f)
            .ToArray();
    }

    private static byte[][] ExtractImageBytes(Array imageData)
    {
        if (imageData is byte[][] directBytes)
        {
            return directBytes;
        }

        if (imageData is Array rowArray && rowArray.Length > 0)
        {
            var result = new byte[rowArray.Length][];

            for (var r = 0; r < rowArray.Length; r++)
            {
                var element = rowArray.GetValue(r);

                if (element is byte[] elementBytes)
                {
                    result[r] = elementBytes;
                }
                else if (element is object[] nestedValues && nestedValues.Length > 0
                    && nestedValues[0] is byte[] nestedBytes)
                {
                    result[r] = nestedBytes;
                }
                else
                {
                    result[r] = Array.Empty<byte>();
                }
            }

            return result;
        }

        return Array.Empty<byte[]>();
    }

    private static (string File, int RowGroup, int Row)? ParseLocalPath(string? localPath)
    {
        if (string.IsNullOrEmpty(localPath))
        {
            return null;
        }

        var parts = localPath.Split("::");
        if (parts.Length != 3)
        {
            return null;
        }

        if (!int.TryParse(parts[1], out var rowGroup) || !int.TryParse(parts[2], out var row))
        {
            return null;
        }

        return (parts[0], rowGroup, row);
    }

    private static async Task<byte[]> ReadImageBytesAsync(
        string filePath, int rowGroup, int row, CancellationToken cancellationToken)
    {
        await using var fs = File.OpenRead(filePath);
        using var reader = await Parquet.ParquetReader
            .CreateAsync(fs, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var dataFields = reader.Schema.GetDataFields();
        var imageField = dataFields.FirstOrDefault(f =>
            f.Name.Equals("image", StringComparison.OrdinalIgnoreCase));

        if (imageField is null)
        {
            return Array.Empty<byte>();
        }

        using var rowGroupReader = reader.OpenRowGroupReader(rowGroup);
        var imageColumn = await rowGroupReader
            .ReadColumnAsync(imageField, cancellationToken)
            .ConfigureAwait(false);

        var imageBytes = ExtractImageBytes(imageColumn.Data);

        return row < imageBytes.Length ? imageBytes[row] : Array.Empty<byte>();
    }
}
