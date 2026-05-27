using System.Runtime.CompilerServices;
using KuzushiClassifierApp.Models;
using KuzushiClassifierApp.Platform;
using Microsoft.Extensions.Logging;
using ZLogger;
using Parquet;
using Parquet.Data;
using Parquet.Schema;

namespace KuzushiClassifierApp.Services;

public sealed class ParquetFileEmbeddingCacheService : IEmbeddingCacheService
{
    public const string CacheFileName = "image-embeddings.with-webp.parquet";

    private const int CacheVersion = 1;
    private const int DefaultVectorDimensions = 768;
    private const int BatchSize = 2_000;

    private readonly string _cacheFilePath;
    private readonly ILogger<ParquetFileEmbeddingCacheService> _logger;

    public ParquetFileEmbeddingCacheService(
        IAppDataPathProvider appDataPathProvider,
        ILogger<ParquetFileEmbeddingCacheService> logger)
    {
        ArgumentNullException.ThrowIfNull(appDataPathProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
        _cacheFilePath = Path.Combine(
            appDataPathProvider.GetDatasetCacheDirectory(),
            CacheFileName);
    }

    public async Task<EmbeddingCacheMetadata?> TryReadMetadataAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_cacheFilePath) || new FileInfo(_cacheFilePath).Length <= 1024)
        {
            return null;
        }

        try
        {
            await using var reader = await ParquetReader
                .CreateAsync(_cacheFilePath, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var layout = await GetVectorLayoutAsync(reader, cancellationToken)
                .ConfigureAwait(false);

            if (layout.Dimensions <= 0)
            {
                return null;
            }

            var count = 0;
            for (var rowGroupIndex = 0; rowGroupIndex < reader.RowGroupCount; rowGroupIndex++)
            {
                using var rowGroupReader = reader.OpenRowGroupReader(rowGroupIndex);
                count += checked((int)rowGroupReader.RowCount);
            }

            return new EmbeddingCacheMetadata(count, layout.Dimensions);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.ZLogWarning(ex, $"Failed to read embedding cache metadata from {_cacheFilePath}");
            return null;
        }
    }

    public async IAsyncEnumerable<DatasetImageEmbedding> StreamAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_cacheFilePath))
        {
            yield break;
        }

        await using var reader = await ParquetReader
            .CreateAsync(_cacheFilePath, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var layout = await GetVectorLayoutAsync(reader, cancellationToken)
            .ConfigureAwait(false);

        for (var rowGroupIndex = 0; rowGroupIndex < reader.RowGroupCount; rowGroupIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var rowGroupReader = reader.OpenRowGroupReader(rowGroupIndex);
            var rowCount = checked((int)rowGroupReader.RowCount);

            var ids = await ReadStringColumnAsync(
                    rowGroupReader,
                    layout.IdField,
                    rowCount,
                    cancellationToken)
                .ConfigureAwait(false);
            var labels = await ReadStringColumnAsync(
                    rowGroupReader,
                    layout.LabelField,
                    rowCount,
                    cancellationToken)
                .ConfigureAwait(false);
            var shards = await ReadStringColumnAsync(
                    rowGroupReader,
                    layout.ShardField,
                    rowCount,
                    cancellationToken)
                .ConfigureAwait(false);
            var sourceRowGroups = await ReadInt16ColumnAsync(
                    rowGroupReader,
                    layout.RowGroupField,
                    rowCount,
                    cancellationToken)
                .ConfigureAwait(false);
            var sourceRows = await ReadInt32ColumnAsync(
                    rowGroupReader,
                    layout.RowField,
                    rowCount,
                    cancellationToken)
                .ConfigureAwait(false);

            if (layout.ScalarVectorFields is not null)
            {
                var columns = new float[layout.ScalarVectorFields.Length][];
                for (var dimension = 0; dimension < layout.ScalarVectorFields.Length; dimension++)
                {
                    columns[dimension] = new float[rowCount];
                    await rowGroupReader
                        .ReadAsync(
                            layout.ScalarVectorFields[dimension],
                            columns[dimension].AsMemory(),
                            null,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                for (var row = 0; row < rowCount; row++)
                {
                    var vector = new float[layout.ScalarVectorFields.Length];
                    for (var dimension = 0; dimension < vector.Length; dimension++)
                    {
                        vector[dimension] = columns[dimension][row];
                    }

                    yield return CreateEmbedding(ids, labels, shards, sourceRowGroups, sourceRows, row, vector);
                }
            }
            else if (layout.FlatVectorField is not null)
            {
                using var rawBase = await rowGroupReader
                    .ReadRawColumnDataBaseAsync(layout.FlatVectorField, cancellationToken)
                    .ConfigureAwait(false);

                if (rawBase is not RawColumnData<float> raw)
                {
                    throw new InvalidOperationException(
                        $"Vector column '{layout.FlatVectorField.Path}' is not a float column.");
                }

                var flatValues = raw.Values.ToArray();
                var dimensions = rowCount == 0
                    ? layout.Dimensions
                    : flatValues.Length / rowCount;

                if (dimensions <= 0 || flatValues.Length != rowCount * dimensions)
                {
                    throw new InvalidOperationException(
                        $"Vector column '{layout.FlatVectorField.Path}' does not contain fixed-width vectors.");
                }

                for (var row = 0; row < rowCount; row++)
                {
                    var vector = new float[dimensions];
                    Array.Copy(flatValues, row * dimensions, vector, 0, dimensions);

                    yield return CreateEmbedding(ids, labels, shards, sourceRowGroups, sourceRows, row, vector);
                }
            }
        }
    }

    public async Task SaveAsync(
        IAsyncEnumerable<DatasetImageEmbedding> embeddings,
        CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        throw new NotSupportedException(
            "The runtime embedding cache must be downloaded as image-embeddings.with-webp.parquet.");
    }

    private static async Task WriteBatchAsync(
        ParquetWriter writer,
        ParquetSchema schema,
        IReadOnlyList<DatasetImageEmbedding> batch,
        CancellationToken cancellationToken)
    {
        using var rowGroupWriter = writer.CreateRowGroup();

        await rowGroupWriter.WriteAsync(schema.DataFields[0], batch.Select(e => e.Image.Id).ToArray(), null)
            .ConfigureAwait(false);
        await rowGroupWriter.WriteAsync(schema.DataFields[1], batch.Select(e => e.Image.Label).ToArray(), null)
            .ConfigureAwait(false);
        await rowGroupWriter.WriteAsync(
                schema.DataFields[2],
                batch.Select(e => e.Image.SourceUri ?? "").ToArray(),
                null)
            .ConfigureAwait(false);
        await rowGroupWriter.WriteAsync(
                schema.DataFields[3],
                batch.Select(e => e.Image.LocalPath ?? "").ToArray(),
                null)
            .ConfigureAwait(false);

        for (var dimension = 0; dimension < DefaultVectorDimensions; dimension++)
        {
            var values = new float[batch.Count];
            for (var row = 0; row < batch.Count; row++)
            {
                values[row] = batch[row].Vector[dimension];
            }

            await rowGroupWriter
                .WriteAsync<float>(
                    schema.DataFields[dimension + 4],
                    values.AsMemory(),
                    null,
                    null,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static DatasetImageEmbedding CreateEmbedding(
        IReadOnlyList<string?> ids,
        IReadOnlyList<string?> labels,
        IReadOnlyList<string?> shards,
        IReadOnlyList<short> rowGroups,
        IReadOnlyList<int> rows,
        int row,
        float[] vector)
    {
        var localPath = string.IsNullOrWhiteSpace(shards[row])
            ? null
            : $"{shards[row]}::{rowGroups[row]}::{rows[row]}";

        return new DatasetImageEmbedding(
            new DatasetImage(
                ids[row] ?? $"parq_{row:D6}",
                labels[row] ?? "",
                SourceUri: null,
                localPath),
            vector);
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

    private static async Task<VectorLayout> GetVectorLayoutAsync(
        ParquetReader reader,
        CancellationToken cancellationToken)
    {
        var dataFields = reader.Schema.GetDataFields();
        var idField = RequiredField(dataFields, "id");
        var labelField = RequiredField(dataFields, "label");
        var shardField = RequiredField(dataFields, "shard");
        var rowGroupField = RequiredField(dataFields, "rowGroup");
        var rowField = RequiredField(dataFields, "row");

        var scalarFields = Enumerable
            .Range(0, DefaultVectorDimensions)
            .Select(index => dataFields.FirstOrDefault(f =>
                f.Name.Equals($"v{index:D3}", StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        if (scalarFields.All(field => field is not null))
        {
            return new VectorLayout(
                idField,
                labelField,
                shardField,
                rowGroupField,
                rowField,
                scalarFields!,
                null,
                scalarFields.Length);
        }

        var flatVectorField = dataFields.FirstOrDefault(field =>
            field.ClrType == typeof(float)
            && field.Path.ToString().Contains("vector", StringComparison.OrdinalIgnoreCase));

        if (flatVectorField is null)
        {
            var found = string.Join(", ", dataFields.Select(field => field.Path.ToString()));
            throw new InvalidOperationException(
                $"Required vector column not found in embedding Parquet. Available columns: [{found}]");
        }

        var dimensions = await ReadFlatVectorDimensionsAsync(reader, flatVectorField, cancellationToken)
            .ConfigureAwait(false);

        return new VectorLayout(
            idField,
            labelField,
            shardField,
            rowGroupField,
            rowField,
            null,
            flatVectorField,
            dimensions);
    }

    private static async Task<int> ReadFlatVectorDimensionsAsync(
        ParquetReader reader,
        DataField vectorField,
        CancellationToken cancellationToken)
    {
        if (reader.CustomMetadata.TryGetValue("vectorDimensions", out var metadataValue)
            && int.TryParse(metadataValue, out var metadataDimensions)
            && metadataDimensions > 0)
        {
            return metadataDimensions;
        }

        for (var rowGroupIndex = 0; rowGroupIndex < reader.RowGroupCount; rowGroupIndex++)
        {
            using var rowGroupReader = reader.OpenRowGroupReader(rowGroupIndex);
            var rowCount = checked((int)rowGroupReader.RowCount);

            if (rowCount == 0)
            {
                continue;
            }

            using var rawBase = await rowGroupReader
                .ReadRawColumnDataBaseAsync(vectorField, cancellationToken)
                .ConfigureAwait(false);

            if (rawBase is RawColumnData<float> raw && raw.Values.Length % rowCount == 0)
            {
                return raw.Values.Length / rowCount;
            }
        }

        return DefaultVectorDimensions;
    }

    private static ParquetSchema CreateScalarVectorSchema(int dimensions)
    {
        var fields = new List<Field>
        {
            new DataField<string>("id"),
            new DataField<string>("label"),
            new DataField<string>("sourceUri"),
            new DataField<string>("localPath"),
        };

        for (var dimension = 0; dimension < dimensions; dimension++)
        {
            fields.Add(new DataField<float>($"v{dimension:D3}"));
        }

        return new ParquetSchema(fields);
    }

    private static DataField RequiredField(DataField[] fields, string name)
    {
        return fields.FirstOrDefault(field => field.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Required column '{name}' not found in embedding Parquet.");
    }

    private static void ValidateVector(DatasetImageEmbedding embedding)
    {
        if (embedding.Vector.Count != DefaultVectorDimensions)
        {
            throw new InvalidOperationException(
                $"Embedding '{embedding.Image.Id}' has {embedding.Vector.Count} dimensions; expected {DefaultVectorDimensions}.");
        }
    }

    private sealed record VectorLayout(
        DataField IdField,
        DataField LabelField,
        DataField ShardField,
        DataField RowGroupField,
        DataField RowField,
        DataField[]? ScalarVectorFields,
        DataField? FlatVectorField,
        int Dimensions);
}
