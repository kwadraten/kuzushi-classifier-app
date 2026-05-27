using System.Runtime.CompilerServices;
using KuzushiClassifierApp.Models;
using KuzushiClassifierApp.Platform;
using Parquet;
using Parquet.Schema;

namespace KuzushiClassifierApp.Services;

public sealed class StreamingParquetImageLibraryService : IImageLibraryService
{
    private readonly string _embeddingsPath;

    public StreamingParquetImageLibraryService(IAppDataPathProvider appDataPathProvider)
    {
        ArgumentNullException.ThrowIfNull(appDataPathProvider);

        _embeddingsPath = Path.Combine(
            appDataPathProvider.GetDatasetCacheDirectory(),
            ParquetFileEmbeddingCacheService.CacheFileName);
    }

    public async Task<IReadOnlyList<DatasetImage>> LoadImagesAsync(
        CancellationToken cancellationToken = default,
        IProgress<AssetPreparationProgress>? progress = null)
    {
        var images = new List<DatasetImage>();

        await using var reader = await OpenReaderAsync(cancellationToken).ConfigureAwait(false);
        var fields = GetRequiredFields(reader.Schema);

        for (var rowGroupIndex = 0; rowGroupIndex < reader.RowGroupCount; rowGroupIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            progress?.Report(new AssetPreparationProgress(
                AssetPreparationStep.LoadingDataset,
                $"Scanning image embedding metadata: row group {rowGroupIndex + 1} of {reader.RowGroupCount}.",
                Fraction: (double)rowGroupIndex / reader.RowGroupCount,
                ItemsProcessed: images.Count));

            using var rowGroupReader = reader.OpenRowGroupReader(rowGroupIndex);
            var rowCount = checked((int)rowGroupReader.RowCount);

            var ids = await ReadStringColumnAsync(rowGroupReader, fields.Id, rowCount, cancellationToken)
                .ConfigureAwait(false);
            var labels = await ReadStringColumnAsync(rowGroupReader, fields.Label, rowCount, cancellationToken)
                .ConfigureAwait(false);
            var shards = await ReadStringColumnAsync(rowGroupReader, fields.Shard, rowCount, cancellationToken)
                .ConfigureAwait(false);
            var sourceRowGroups = await ReadInt16ColumnAsync(rowGroupReader, fields.RowGroup, rowCount, cancellationToken)
                .ConfigureAwait(false);
            var sourceRows = await ReadInt32ColumnAsync(rowGroupReader, fields.Row, rowCount, cancellationToken)
                .ConfigureAwait(false);

            for (var row = 0; row < rowCount; row++)
            {
                images.Add(new DatasetImage(
                    ids[row] ?? $"parq_{images.Count:D6}",
                    labels[row] ?? "",
                    SourceUri: null,
                    LocalPath: BuildImageReference(shards[row], sourceRowGroups[row], sourceRows[row])));
            }
        }

        return images;
    }

    public async Task<KuzushiImage> LoadImageAsync(
        DatasetImage image,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);

        var selector = TryParseImageReference(image.LocalPath);

        await using var reader = await OpenReaderAsync(cancellationToken).ConfigureAwait(false);
        var fields = GetRequiredFields(reader.Schema);

        for (var rowGroupIndex = 0; rowGroupIndex < reader.RowGroupCount; rowGroupIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var rowGroupReader = reader.OpenRowGroupReader(rowGroupIndex);
            var rowCount = checked((int)rowGroupReader.RowCount);

            var webpBytes = await TryLoadImageFromRowGroupAsync(
                    rowGroupReader,
                    fields,
                    rowCount,
                    selector,
                    image.Id,
                    cancellationToken)
                .ConfigureAwait(false);

            if (webpBytes is not null)
            {
                return KuzushiImage.FromBytes(
                    webpBytes,
                    $"{image.Id}.webp",
                    "image/webp",
                    image.Id);
            }
        }

        throw new InvalidOperationException($"Cannot resolve image {image.Id} from embedding Parquet.");
    }

    public async IAsyncEnumerable<(DatasetImage Metadata, KuzushiImage Image)> StreamAllImagesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var reader = await OpenReaderAsync(cancellationToken).ConfigureAwait(false);
        var fields = GetRequiredFields(reader.Schema);
        var globalIndex = 0;

        for (var rowGroupIndex = 0; rowGroupIndex < reader.RowGroupCount; rowGroupIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var rowGroupReader = reader.OpenRowGroupReader(rowGroupIndex);
            var rowCount = checked((int)rowGroupReader.RowCount);

            var ids = await ReadStringColumnAsync(rowGroupReader, fields.Id, rowCount, cancellationToken)
                .ConfigureAwait(false);
            var labels = await ReadStringColumnAsync(rowGroupReader, fields.Label, rowCount, cancellationToken)
                .ConfigureAwait(false);
            var shards = await ReadStringColumnAsync(rowGroupReader, fields.Shard, rowCount, cancellationToken)
                .ConfigureAwait(false);
            var sourceRowGroups = await ReadInt16ColumnAsync(rowGroupReader, fields.RowGroup, rowCount, cancellationToken)
                .ConfigureAwait(false);
            var sourceRows = await ReadInt32ColumnAsync(rowGroupReader, fields.Row, rowCount, cancellationToken)
                .ConfigureAwait(false);
            var webps = await ReadBinaryColumnAsync(rowGroupReader, fields.Webp, rowCount, cancellationToken)
                .ConfigureAwait(false);

            for (var row = 0; row < rowCount; row++)
            {
                var id = ids[row] ?? $"parq_{globalIndex:D6}";
                var metadata = new DatasetImage(
                    id,
                    labels[row] ?? "",
                    SourceUri: null,
                    LocalPath: BuildImageReference(shards[row], sourceRowGroups[row], sourceRows[row]));

                var bytes = webps[row];
                globalIndex++;

                if (bytes is null || bytes.Length == 0)
                {
                    continue;
                }

                yield return (
                    metadata,
                    KuzushiImage.FromBytes(bytes, $"{id}.webp", "image/webp", id));
            }
        }
    }

    private async Task<ParquetReader> OpenReaderAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_embeddingsPath))
        {
            throw new FileNotFoundException(
                "Image embedding Parquet with embedded WebP images is missing.",
                _embeddingsPath);
        }

        return await ParquetReader
            .CreateAsync(_embeddingsPath, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<byte[]?> TryLoadImageFromRowGroupAsync(
        ParquetRowGroupReader rowGroupReader,
        RequiredFields fields,
        int rowCount,
        ImageSelector? selector,
        string imageId,
        CancellationToken cancellationToken)
    {
        if (selector is not null)
        {
            var shards = await ReadStringColumnAsync(rowGroupReader, fields.Shard, rowCount, cancellationToken)
                .ConfigureAwait(false);
            var sourceRowGroups = await ReadInt16ColumnAsync(rowGroupReader, fields.RowGroup, rowCount, cancellationToken)
                .ConfigureAwait(false);
            var sourceRows = await ReadInt32ColumnAsync(rowGroupReader, fields.Row, rowCount, cancellationToken)
                .ConfigureAwait(false);

            for (var row = 0; row < rowCount; row++)
            {
                if (string.Equals(shards[row], selector.Shard, StringComparison.Ordinal)
                    && sourceRowGroups[row] == selector.RowGroup
                    && sourceRows[row] == selector.Row)
                {
                    var webps = await ReadBinaryColumnAsync(rowGroupReader, fields.Webp, rowCount, cancellationToken)
                        .ConfigureAwait(false);
                    return webps[row];
                }
            }
        }
        else if (!string.IsNullOrWhiteSpace(imageId))
        {
            var ids = await ReadStringColumnAsync(rowGroupReader, fields.Id, rowCount, cancellationToken)
                .ConfigureAwait(false);

            for (var row = 0; row < rowCount; row++)
            {
                if (string.Equals(ids[row], imageId, StringComparison.Ordinal))
                {
                    var webps = await ReadBinaryColumnAsync(rowGroupReader, fields.Webp, rowCount, cancellationToken)
                        .ConfigureAwait(false);
                    return webps[row];
                }
            }
        }

        return null;
    }

    private static RequiredFields GetRequiredFields(ParquetSchema schema)
    {
        var dataFields = schema.GetDataFields();

        return new RequiredFields(
            RequiredField(dataFields, "id"),
            RequiredField(dataFields, "label"),
            RequiredField(dataFields, "shard"),
            RequiredField(dataFields, "rowGroup"),
            RequiredField(dataFields, "row"),
            RequiredField(dataFields, "webp"));
    }

    private static DataField RequiredField(DataField[] fields, string name)
    {
        return fields.FirstOrDefault(field => field.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Required column '{name}' not found in embedding Parquet.");
    }

    private static string BuildImageReference(string? shard, short rowGroup, int row)
    {
        return $"{shard ?? ""}::{rowGroup}::{row}";
    }

    private static ImageSelector? TryParseImageReference(string? localPath)
    {
        if (string.IsNullOrWhiteSpace(localPath))
        {
            return null;
        }

        var parts = localPath.Split("::");
        if (parts.Length != 3)
        {
            return null;
        }

        if (!short.TryParse(parts[1], out var rowGroup) || !int.TryParse(parts[2], out var row))
        {
            return null;
        }

        return new ImageSelector(Path.GetFileName(parts[0].Replace('\\', '/')), rowGroup, row);
    }

    private static async Task<string?[]> ReadStringColumnAsync(
        ParquetRowGroupReader rowGroupReader,
        DataField field,
        int rowCount,
        CancellationToken cancellationToken)
    {
        var values = new string?[rowCount];
        await rowGroupReader
            .ReadAsync(field, values.AsMemory(), null, cancellationToken)
            .ConfigureAwait(false);

        return values;
    }

    private static async Task<short[]> ReadInt16ColumnAsync(
        ParquetRowGroupReader rowGroupReader,
        DataField field,
        int rowCount,
        CancellationToken cancellationToken)
    {
        var values = new short?[rowCount];
        await rowGroupReader
            .ReadAsync(field, values.AsMemory(), null, cancellationToken)
            .ConfigureAwait(false);

        return values.Select(value => value ?? 0).ToArray();
    }

    private static async Task<int[]> ReadInt32ColumnAsync(
        ParquetRowGroupReader rowGroupReader,
        DataField field,
        int rowCount,
        CancellationToken cancellationToken)
    {
        var values = new int?[rowCount];
        await rowGroupReader
            .ReadAsync(field, values.AsMemory(), null, cancellationToken)
            .ConfigureAwait(false);

        return values.Select(value => value ?? 0).ToArray();
    }

    private static async Task<byte[]?[]> ReadBinaryColumnAsync(
        ParquetRowGroupReader rowGroupReader,
        DataField field,
        int rowCount,
        CancellationToken cancellationToken)
    {
        var values = new byte[]?[rowCount];
        await rowGroupReader
            .ReadAsync(field, values.AsMemory(), null, cancellationToken)
            .ConfigureAwait(false);

        return values;
    }

    private sealed record RequiredFields(
        DataField Id,
        DataField Label,
        DataField Shard,
        DataField RowGroup,
        DataField Row,
        DataField Webp);

    private sealed record ImageSelector(
        string Shard,
        short RowGroup,
        int Row);
}
