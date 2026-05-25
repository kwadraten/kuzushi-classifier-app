using System.Net.Http;
using System.Text.Json;
using KuzushiClassifierApp.Models;
using KuzushiClassifierApp.Platform;

namespace KuzushiClassifierApp.Services;

public sealed class HuggingFaceModelAssetService :
    IModelAssetService,
    IModelPathProvider,
    IDisposable
{
    private const string DefaultModelRepo = "kwadraten/shikiji";
    private const string DefaultDatasetRepo = "kwadraten/hi-utokyo-kuzushi";
    private const string HuggingFaceRawBase = "https://huggingface.co";

    private readonly IAppDataPathProvider _appDataPathProvider;
    private readonly HttpClient _httpClient;
    private readonly string _modelRepo;
    private readonly string _datasetRepo;

    public HuggingFaceModelAssetService(
        IAppDataPathProvider appDataPathProvider,
        string modelRepo = DefaultModelRepo,
        string datasetRepo = DefaultDatasetRepo)
    {
        _appDataPathProvider = appDataPathProvider;
        _modelRepo = modelRepo;
        _datasetRepo = datasetRepo;

        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(30),
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("KuzushiClassifierApp/1.0");
    }

    public string ClassifierModelPath => Path.Combine(
        _appDataPathProvider.GetModelCacheDirectory(),
        "supervised_pretrain_checkpoint.onnx");

    public string EmbeddingModelPath => Path.Combine(
        _appDataPathProvider.GetModelCacheDirectory(),
        "supervised_pretrain_checkpoint.embedding.onnx");

    public static readonly string[] ModelFileNames =
    {
        "supervised_pretrain_checkpoint.onnx",
        "supervised_pretrain_checkpoint.metadata.json",
        "supervised_pretrain_checkpoint.embedding.onnx",
        "supervised_pretrain_checkpoint.embedding.metadata.json",
    };

    public async Task<ModelAssetStatus> PrepareAsync(
        IProgress<AssetPreparationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var modelDirectory = _appDataPathProvider.GetModelCacheDirectory();

        progress?.Report(new AssetPreparationProgress(
            AssetPreparationStep.CheckingCache,
            "Checking local model files.",
            0));

        var classifierReady = ModelFilesReady(modelDirectory, "supervised_pretrain_checkpoint");
        var embeddingReady = ModelFilesReady(modelDirectory, "supervised_pretrain_checkpoint.embedding");

        if (!classifierReady || !embeddingReady)
        {
            progress?.Report(new AssetPreparationProgress(
                AssetPreparationStep.DownloadingModels,
                "Downloading ONNX model files from HuggingFace.",
                0.1));

            Directory.CreateDirectory(modelDirectory);

            await DownloadModelFilesAsync(modelDirectory, progress, cancellationToken)
                .ConfigureAwait(false);

            classifierReady = ModelFilesReady(modelDirectory, "supervised_pretrain_checkpoint");
            embeddingReady = ModelFilesReady(modelDirectory, "supervised_pretrain_checkpoint.embedding");

            if (!classifierReady || !embeddingReady)
            {
                throw new InvalidOperationException(
                    "Failed to download ONNX model files from HuggingFace. " +
                    $"Please ensure the dataset repository '{_datasetRepo}' exists and is public. " +
                    $"You can manually place model files in: {modelDirectory}");
            }
        }

        var datasetDirectory = _appDataPathProvider.GetDatasetCacheDirectory();
        var datasetReady = DatasetCacheExists(datasetDirectory);

        if (!datasetReady)
        {
            progress?.Report(new AssetPreparationProgress(
                AssetPreparationStep.DownloadingDataset,
                "Downloading dataset from HuggingFace.",
                0.5));

            await EnsureDatasetAsync(datasetDirectory, progress, cancellationToken)
                .ConfigureAwait(false);

            datasetReady = DatasetCacheExists(datasetDirectory);

            if (!datasetReady)
            {
                throw new InvalidOperationException(
                    "Failed to prepare dataset. " +
                    $"Ensure Parquet files with columns 'image', 'char', 'unicode' exist in: {datasetDirectory}");
            }
        }

        progress?.Report(new AssetPreparationProgress(
            AssetPreparationStep.CheckingCache,
            "Local assets ready.",
            1));

        return new ModelAssetStatus(
            ClassifierModelReady: classifierReady,
            EmbeddingModelReady: embeddingReady,
            DatasetReady: datasetReady,
            EmbeddingIndexReady: false,
            CacheDirectory: _appDataPathProvider.GetAppDataDirectory());
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private async Task DownloadModelFilesAsync(
        string modelDirectory,
        IProgress<AssetPreparationProgress>? progress,
        CancellationToken cancellationToken)
    {
        for (var i = 0; i < ModelFileNames.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fileName = ModelFileNames[i];
            var filePath = Path.Combine(modelDirectory, fileName);

            if (File.Exists(filePath))
            {
                continue;
            }

            progress?.Report(new AssetPreparationProgress(
                AssetPreparationStep.DownloadingModels,
                $"Downloading {fileName} ({i + 1} of {ModelFileNames.Length}).",
                (double)i / ModelFileNames.Length));

            var url = $"{HuggingFaceRawBase}/{_modelRepo}/resolve/main/{fileName}";

            try
            {
                var bytes = await _httpClient
                    .GetByteArrayAsync(url, cancellationToken)
                    .ConfigureAwait(false);

                await File
                    .WriteAllBytesAsync(filePath, bytes, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new InvalidOperationException(
                    $"Failed to download {fileName} from HuggingFace ({url}). " +
                    $"Status: {ex.StatusCode}. Ensure the model repository '{_modelRepo}' is public.",
                    ex);
            }
        }
    }

    private async Task EnsureDatasetAsync(
        string datasetDirectory,
        IProgress<AssetPreparationProgress>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(datasetDirectory);

        var cacheDir = Path.Combine(datasetDirectory, "cache");
        var parquetDir = Path.Combine(datasetDirectory, "data");

        var localParquetFiles = Directory.Exists(parquetDir)
            ? Directory.EnumerateFiles(parquetDir, "*.parquet").OrderBy(f => f).ToArray()
            : Array.Empty<string>();

        if (localParquetFiles.Length == 0)
        {
            progress?.Report(new AssetPreparationProgress(
                AssetPreparationStep.DownloadingDataset,
                "No local Parquet files found. Downloading from HuggingFace.",
                0));

            Directory.CreateDirectory(parquetDir);

            var parquetFiles = await ListParquetFilesAsync(cancellationToken).ConfigureAwait(false);

            if (parquetFiles.Length == 0)
            {
                throw new InvalidOperationException(
                    $"No Parquet files found in HuggingFace dataset '{_datasetRepo}'. " +
                    "Please check the dataset repository is public.");
            }

            for (var i = 0; i < parquetFiles.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var relativePath = parquetFiles[i];
                var fileName = Path.GetFileName(relativePath);
                var filePath = Path.Combine(parquetDir, fileName);

                if (File.Exists(filePath))
                {
                    continue;
                }

                progress?.Report(new AssetPreparationProgress(
                    AssetPreparationStep.DownloadingDataset,
                    $"Downloading {fileName} ({i + 1} of {parquetFiles.Length}).",
                    (double)i / parquetFiles.Length));

                var url = $"{HuggingFaceRawBase}/datasets/{_datasetRepo}/resolve/main/{relativePath}";
                var bytes = await _httpClient
                    .GetByteArrayAsync(url, cancellationToken)
                    .ConfigureAwait(false);

                await File
                    .WriteAllBytesAsync(filePath, bytes, cancellationToken)
                    .ConfigureAwait(false);
            }

            localParquetFiles = Directory.EnumerateFiles(parquetDir, "*.parquet")
                .OrderBy(f => f).ToArray();
        }

        var totalParquetRows = await CountParquetRowsAsync(
            localParquetFiles, cancellationToken).ConfigureAwait(false);

        var cachedRowCount = CountCachedRows(cacheDir);

        if (cachedRowCount > 0 && cachedRowCount >= totalParquetRows)
        {
            return;
        }

        if (cachedRowCount > 0)
        {
            progress?.Report(new AssetPreparationProgress(
                AssetPreparationStep.DownloadingDataset,
                $"Image cache is stale ({cachedRowCount} entries, {totalParquetRows} in Parquet). Rebuilding.",
                0.1));

            var staleMetadata = Path.Combine(cacheDir, "metadata.jsonl");
            var staleImages = Path.Combine(cacheDir, "images");
            if (File.Exists(staleMetadata)) File.Delete(staleMetadata);
            if (Directory.Exists(staleImages)) Directory.Delete(staleImages, recursive: true);
        }

        progress?.Report(new AssetPreparationProgress(
            AssetPreparationStep.DownloadingDataset,
            $"Expanding {totalParquetRows} images from {localParquetFiles.Length} Parquet files.",
            0.2));

        await ExpandParquetToImageCacheAsync(
            localParquetFiles, cacheDir, progress, cancellationToken).ConfigureAwait(false);

        if (!DatasetCacheExists(datasetDirectory))
        {
            throw new InvalidOperationException(
                "Failed to expand Parquet files to image cache. " +
                $"Check that Parquet files in '{parquetDir}' contain 'image', 'char', 'unicode' columns.");
        }
    }

    private static int CountCachedRows(string cacheDir)
    {
        var metadataPath = Path.Combine(cacheDir, "metadata.jsonl");
        if (!File.Exists(metadataPath))
        {
            return 0;
        }

        var lines = 0;
        foreach (var line in File.ReadLines(metadataPath))
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                lines++;
            }
        }

        return lines;
    }

    private static async Task<int> CountParquetRowsAsync(
        string[] parquetFiles,
        CancellationToken cancellationToken)
    {
        var total = 0L;

        foreach (var file in parquetFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await using var fs = File.OpenRead(file);
                using var reader = await Parquet.ParquetReader
                    .CreateAsync(fs, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

                for (var rgi = 0; rgi < reader.RowGroupCount; rgi++)
                {
                    using var rowGroupReader = reader.OpenRowGroupReader(rgi);
                    total += rowGroupReader.RowCount;
                }
            }
            catch
            {
                // Skip unreadable parquet files
            }
        }

        return (int)total;
    }

    private async Task<string[]> ListParquetFilesAsync(CancellationToken cancellationToken)
    {
        var apiUrl = $"https://huggingface.co/api/datasets/{_datasetRepo}";

        try
        {
            var json = await _httpClient
                .GetStringAsync(apiUrl, cancellationToken)
                .ConfigureAwait(false);

            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("siblings", out var siblings))
            {
                var files = new List<string>();

                foreach (var sibling in siblings.EnumerateArray())
                {
                    if (sibling.TryGetProperty("rfilename", out var rfilename))
                    {
                        var path = rfilename.GetString();
                        if (path is not null && path.StartsWith("data/") && path.EndsWith(".parquet"))
                        {
                            files.Add(path);
                        }
                    }
                }

                return files.ToArray();
            }
        }
        catch
        {
            // Fallback: try with known shard names
        }

        // Common Parquet shard patterns for HF datasets
        return new[]
        {
            "data/train-00000-of-00005.parquet",
            "data/train-00001-of-00005.parquet",
            "data/train-00002-of-00005.parquet",
            "data/train-00003-of-00005.parquet",
            "data/train-00004-of-00005.parquet",
        };
    }

    private async Task<bool> ExpandParquetToImageCacheAsync(
        string[] parquetFiles,
        string cacheDir,
        IProgress<AssetPreparationProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (parquetFiles.Length == 0)
        {
            return false;
        }

        var metadataPath = Path.Combine(cacheDir, "metadata.jsonl");
        var imagesDir = Path.Combine(cacheDir, "images");

        Directory.CreateDirectory(cacheDir);
        Directory.CreateDirectory(imagesDir);

        var imageIndex = 0;

        await using var writer = new StreamWriter(metadataPath, append: false);

        for (var fileIndex = 0; fileIndex < parquetFiles.Length; fileIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var parquetFile = parquetFiles[fileIndex];

            progress?.Report(new AssetPreparationProgress(
                AssetPreparationStep.DownloadingDataset,
                $"Expanding Parquet file {fileIndex + 1} of {parquetFiles.Length}...",
                (double)fileIndex / parquetFiles.Length));

            await foreach (var row in ReadParquetRowsAsync(parquetFile, cancellationToken)
                .ConfigureAwait(false))
            {
                if (row.Bytes is null || row.Bytes.Length == 0)
                {
                    continue;
                }

                var imageId = $"img_{imageIndex:D6}";
                var imagePath = Path.Combine(imagesDir, $"{imageId}.png");

                await File.WriteAllBytesAsync(imagePath, row.Bytes, cancellationToken)
                    .ConfigureAwait(false);

                await writer.WriteLineAsync(JsonSerializer.Serialize(
                    new
                    {
                        Id = imageId,
                        Label = row.Label ?? "",
                        Unicode = row.Unicode ?? "",
                        LocalPath = $"images/{imageId}.png",
                    },
                    JsonSerializerOptions.Web)).ConfigureAwait(false);

                imageIndex++;
            }
        }

        return imageIndex > 0;
    }

    private sealed record ParquetRow(string? Label, string? Unicode, byte[]? Bytes);

    private static async IAsyncEnumerable<ParquetRow> ReadParquetRowsAsync(
        string parquetPath,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var fs = File.OpenRead(parquetPath);
        using var reader = await Parquet.ParquetReader.CreateAsync(fs, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var dataFields = reader.Schema.GetDataFields();

        var imageField = dataFields.FirstOrDefault(f =>
            f.Name.Equals("image", StringComparison.OrdinalIgnoreCase));
        var charField = dataFields.FirstOrDefault(f =>
            f.Name.Equals("char", StringComparison.OrdinalIgnoreCase));
        var unicodeField = dataFields.FirstOrDefault(f =>
            f.Name.Equals("unicode", StringComparison.OrdinalIgnoreCase));

        if (imageField is null || charField is null)
        {
            yield break;
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

            string[]? unicodeValues = null;
            if (unicodeField is not null)
            {
                var unicodeColumn = await rowGroupReader
                    .ReadColumnAsync(unicodeField, cancellationToken)
                    .ConfigureAwait(false);
                unicodeValues = (string[])unicodeColumn.Data;
            }

            var charValues = (string[])charColumn.Data;

            var imageData = imageColumn.Data;
            byte[][] imageBytes;

            if (imageData is byte[][] directBytes)
            {
                imageBytes = directBytes;
            }
            else if (imageData is Array rowArray && rowArray.Length > 0)
            {
                imageBytes = new byte[rowArray.Length][];
                for (var r = 0; r < rowArray.Length; r++)
                {
                    var element = rowArray.GetValue(r);
                    if (element is byte[] elementBytes)
                    {
                        imageBytes[r] = elementBytes;
                    }
                    else if (element is object[] nestedValues && nestedValues.Length > 0
                        && nestedValues[0] is byte[] nestedBytes)
                    {
                        // Struct type: first field is bytes
                        imageBytes[r] = nestedBytes;
                    }
                    else
                    {
                        imageBytes[r] = Array.Empty<byte>();
                    }
                }
            }
            else
            {
                imageBytes = Array.Empty<byte[]>();
            }

            var rowCount = charValues.Length;

            for (var row = 0; row < rowCount; row++)
            {
                yield return new ParquetRow(
                    charValues[row],
                    unicodeValues?[row],
                    row < imageBytes.Length ? imageBytes[row] : null);
            }
        }
    }

    private static bool ModelFilesReady(string modelDirectory, string baseName)
    {
        var onnxPath = Path.Combine(modelDirectory, $"{baseName}.onnx");
        var metadataPath = Path.Combine(modelDirectory, $"{baseName}.metadata.json");

        return File.Exists(onnxPath) && File.Exists(metadataPath);
    }

    private static bool DatasetCacheExists(string datasetDirectory)
    {
        if (!Directory.Exists(datasetDirectory))
        {
            return false;
        }

        var metadataPath = Path.Combine(datasetDirectory, "cache", "metadata.jsonl");
        if (!File.Exists(metadataPath))
        {
            return false;
        }

        var imagesDir = Path.Combine(datasetDirectory, "cache", "images");
        if (!Directory.Exists(imagesDir))
        {
            return false;
        }

        return Directory.EnumerateFiles(imagesDir).Any();
    }
}
