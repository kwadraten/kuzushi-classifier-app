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
                    $"Please ensure the model repository '{_modelRepo}' exists and is public. " +
                    $"You can manually place model files in: {modelDirectory}");
            }
        }

        var datasetDirectory = _appDataPathProvider.GetDatasetCacheDirectory();
        var parquetDir = Path.Combine(datasetDirectory, "data");
        var datasetReady = ParquetFilesExist(parquetDir);

        if (!datasetReady)
        {
            progress?.Report(new AssetPreparationProgress(
                AssetPreparationStep.DownloadingDataset,
                "Downloading dataset from HuggingFace.",
                0.5));

            await EnsureDatasetAsync(datasetDirectory, progress, cancellationToken)
                .ConfigureAwait(false);

            datasetReady = ParquetFilesExist(parquetDir);

            if (!datasetReady)
            {
                throw new InvalidOperationException(
                    "Failed to download dataset Parquet files. " +
                    $"Ensure the dataset repository '{_datasetRepo}' is public.");
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
        }
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

    private static bool ParquetFilesExist(string parquetDir)
    {
        return Directory.Exists(parquetDir)
            && Directory.EnumerateFiles(parquetDir, "*.parquet").Any();
    }

    private static bool ModelFilesReady(string modelDirectory, string baseName)
    {
        var onnxPath = Path.Combine(modelDirectory, $"{baseName}.onnx");
        var metadataPath = Path.Combine(modelDirectory, $"{baseName}.metadata.json");

        return File.Exists(onnxPath) && File.Exists(metadataPath);
    }
}
