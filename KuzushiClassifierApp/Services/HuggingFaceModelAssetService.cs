using System.Net.Http;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using KuzushiClassifierApp.Models;
using KuzushiClassifierApp.Platform;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace KuzushiClassifierApp.Services;

public sealed class HuggingFaceModelAssetService :
    IModelAssetService,
    IModelPathProvider,
    IDisposable
{
    private const string DefaultModelRepo = "kwadraten/shikiji";
    private const string HuggingFaceRawBase = "https://huggingface.co";
    private const string PrebuiltEmbeddingsUrl =
        "https://scripts-1303933394.cos.ap-tokyo.myqcloud.com/embeddings/image-embeddings.with-webp.parquet";

    private readonly IAppDataPathProvider _appDataPathProvider;
    private readonly ILogger<HuggingFaceModelAssetService> _logger;
    private readonly HttpClient _httpClient;
    private readonly string _modelRepo;

    public HuggingFaceModelAssetService(
        IAppDataPathProvider appDataPathProvider,
        ILogger<HuggingFaceModelAssetService> logger,
        string modelRepo = DefaultModelRepo)
    {
        _appDataPathProvider = appDataPathProvider;
        _logger = logger;
        _modelRepo = modelRepo;

        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromHours(4),
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

        progress?.Report(new AssetPreparationProgress(
            AssetPreparationStep.CheckingCache,
            "Checking local image embedding package.",
            0));

        var datasetDirectory = _appDataPathProvider.GetDatasetCacheDirectory();
        var embeddingCacheReady = EmbeddingCacheReady(datasetDirectory);

        if (!embeddingCacheReady)
        {
            progress?.Report(new AssetPreparationProgress(
                AssetPreparationStep.DownloadingDataset,
                "Downloading image embeddings and WebP images from COS.",
                0.2));

            await EnsurePrebuiltEmbeddingsAsync(datasetDirectory, progress, cancellationToken)
                .ConfigureAwait(false);
            embeddingCacheReady = EmbeddingCacheReady(datasetDirectory);

            if (!embeddingCacheReady)
            {
                throw new InvalidOperationException("Failed to download image-embeddings.with-webp.parquet.");
            }
        }

        var modelDirectory = _appDataPathProvider.GetModelCacheDirectory();
        var classifierReady = ModelFilesReady(modelDirectory, "supervised_pretrain_checkpoint");
        var embeddingReady = ModelFilesReady(modelDirectory, "supervised_pretrain_checkpoint.embedding");

        if (!classifierReady || !embeddingReady)
        {
            progress?.Report(new AssetPreparationProgress(
                AssetPreparationStep.DownloadingModels,
                "Downloading ONNX model files from HuggingFace.",
                0.8));

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

        progress?.Report(new AssetPreparationProgress(
            AssetPreparationStep.CheckingCache,
            "Local assets ready.",
            1));

        return new ModelAssetStatus(
            ClassifierModelReady: classifierReady,
            EmbeddingModelReady: embeddingReady,
            DatasetReady: embeddingCacheReady,
            EmbeddingIndexReady: embeddingCacheReady,
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

            var url = $"{HuggingFaceRawBase}/{_modelRepo}/resolve/main/{fileName}";

            try
            {
                await DownloadWithProgressAsync(
                    url,
                    filePath,
                    AssetPreparationStep.DownloadingModels,
                    $"{fileName} ({i + 1} of {ModelFileNames.Length})",
                    progress,
                    cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                _logger.ZLogError(ex, $"Failed to download model file {fileName} from {url}");
                throw new InvalidOperationException(
                    $"Failed to download {fileName} from HuggingFace ({url}). " +
                    $"Status: {ex.StatusCode}. Ensure the model repository '{_modelRepo}' is public.",
                    ex);
            }
        }
    }

    private async Task EnsurePrebuiltEmbeddingsAsync(
        string datasetDirectory,
        IProgress<AssetPreparationProgress>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(datasetDirectory);

        if (EmbeddingCacheReady(datasetDirectory))
        {
            return;
        }

        var cachePath = Path.Combine(datasetDirectory, ParquetFileEmbeddingCacheService.CacheFileName);

        await DownloadWithProgressAsync(
            PrebuiltEmbeddingsUrl,
            cachePath,
            AssetPreparationStep.DownloadingDataset,
            ParquetFileEmbeddingCacheService.CacheFileName,
            progress,
            cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task DownloadWithProgressAsync(
        string url,
        string filePath,
        AssetPreparationStep step,
        string label,
        IProgress<AssetPreparationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var tempPath = filePath + ".download";
        var buffer = new byte[1 << 20];
        var retryCount = 0;
        const int maxRetries = 5;
        var remoteBytes = await TryGetRemoteContentLengthAsync(url, cancellationToken)
            .ConfigureAwait(false);

        if (TryUseExistingDownloadFile(
            filePath,
            tempPath,
            remoteBytes,
            progress,
            step,
            label))
        {
            return;
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var existingBytes = File.Exists(tempPath)
                ? new FileInfo(tempPath).Length
                : 0L;

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (existingBytes > 0)
            {
                request.Headers.Range = new RangeHeaderValue(existingBytes, null);
            }

            try
            {
                using var response = await _httpClient
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);

                if (existingBytes > 0 && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    remoteBytes ??= response.Content.Headers.ContentRange?.Length
                        ?? await TryGetRemoteContentLengthAsync(url, cancellationToken)
                            .ConfigureAwait(false);

                    if (remoteBytes.HasValue && existingBytes == remoteBytes.Value)
                    {
                        ReportDownloadProgress(
                            progress,
                            step,
                            label,
                            existingBytes,
                            remoteBytes.Value);

                        File.Move(tempPath, filePath, overwrite: true);
                        return;
                    }

                    if (remoteBytes.HasValue && existingBytes > remoteBytes.Value)
                    {
                        File.Delete(tempPath);
                        retryCount = 0;
                        continue;
                    }

                    File.Delete(tempPath);
                    retryCount = 0;
                    continue;
                }

                if (existingBytes > 0 && response.StatusCode == HttpStatusCode.OK)
                {
                    existingBytes = 0;
                    File.Delete(tempPath);
                }

                response.EnsureSuccessStatusCode();

                var responseBytes = response.Content.Headers.ContentLength ?? -1L;
                var totalBytes = response.StatusCode == HttpStatusCode.PartialContent && responseBytes > 0
                    ? existingBytes + responseBytes
                    : remoteBytes ?? responseBytes;

                await using var contentStream = await response.Content
                    .ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);

                await using var fileStream = new FileStream(
                    tempPath,
                    existingBytes > 0 ? FileMode.Append : FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    buffer.Length,
                    useAsync: true);

                var downloaded = existingBytes;
                var lastReport = Stopwatch.GetTimestamp();

                ReportDownloadProgress(
                    progress,
                    step,
                    label,
                    downloaded,
                    totalBytes);

                while (true)
                {
                    var read = await contentStream
                        .ReadAsync(buffer, cancellationToken)
                        .ConfigureAwait(false);

                    if (read == 0)
                    {
                        break;
                    }

                    await fileStream
                        .WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                        .ConfigureAwait(false);

                    downloaded += read;

                    if (ElapsedSince(lastReport) >= TimeSpan.FromSeconds(1))
                    {
                        lastReport = Stopwatch.GetTimestamp();
                        ReportDownloadProgress(
                            progress,
                            step,
                            label,
                            downloaded,
                            totalBytes);
                    }
                }

                await fileStream.FlushAsync(cancellationToken).ConfigureAwait(false);

                ReportDownloadProgress(
                    progress,
                    step,
                    label,
                    downloaded,
                    totalBytes);

                if (remoteBytes.HasValue && downloaded != remoteBytes.Value)
                {
                    throw new IOException(
                        $"Downloaded {FormatBytes(downloaded)} for {label}, expected {FormatBytes(remoteBytes.Value)}.");
                }

                File.Move(tempPath, filePath, overwrite: true);
                return;
            }
            catch (IOException ex) when (retryCount < maxRetries)
            {
                retryCount++;
                _logger.ZLogWarning(ex, $"IOException during download for {label}. Retrying ({retryCount}/{maxRetries}) after delay...");
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, retryCount * 2)), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (retryCount < maxRetries)
            {
                retryCount++;
                _logger.ZLogWarning(ex, $"HttpRequestException during download for {label}. Retrying ({retryCount}/{maxRetries}) after delay...");
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, retryCount * 2)), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private async Task<long?> TryGetRemoteContentLengthAsync(
        string url,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, url);

        try
        {
            using var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            return response.IsSuccessStatusCode
                ? response.Content.Headers.ContentLength
                : null;
        }
        catch (HttpRequestException ex)
        {
            _logger.ZLogWarning(ex, $"Failed to get remote content length for {url}");
            return null;
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.ZLogWarning(ex, $"Task canceled while getting remote content length for {url}");
            return null;
        }
    }

    private static bool TryUseExistingDownloadFile(
        string filePath,
        string tempPath,
        long? remoteBytes,
        IProgress<AssetPreparationProgress>? progress,
        AssetPreparationStep step,
        string label)
    {
        if (File.Exists(filePath))
        {
            var fileBytes = new FileInfo(filePath).Length;
            if (fileBytes > 0 && (!remoteBytes.HasValue || fileBytes == remoteBytes.Value))
            {
                ReportDownloadProgress(progress, step, label, fileBytes, remoteBytes ?? fileBytes);
                return true;
            }

            if (fileBytes > 0
                && remoteBytes.HasValue
                && fileBytes < remoteBytes.Value
                && !File.Exists(tempPath))
            {
                File.Move(filePath, tempPath, overwrite: true);
            }
            else
            {
                File.Delete(filePath);
            }
        }

        if (!File.Exists(tempPath) || !remoteBytes.HasValue)
        {
            return false;
        }

        var tempBytes = new FileInfo(tempPath).Length;
        if (tempBytes == remoteBytes.Value)
        {
            ReportDownloadProgress(progress, step, label, tempBytes, remoteBytes.Value);
            File.Move(tempPath, filePath, overwrite: true);
            return true;
        }

        if (tempBytes > remoteBytes.Value)
        {
            File.Delete(tempPath);
        }

        return false;
    }

    private static void ReportDownloadProgress(
        IProgress<AssetPreparationProgress>? progress,
        AssetPreparationStep step,
        string label,
        long downloaded,
        long totalBytes)
    {
        progress?.Report(new AssetPreparationProgress(
            step,
            $"Downloading {label}  ({FormatBytes(downloaded)}" +
                (totalBytes > 0 ? $" / {FormatBytes(totalBytes)}" : "") +
                ")",
            Fraction: totalBytes > 0 ? (double)downloaded / totalBytes : null,
            BytesDownloaded: downloaded,
            TotalBytes: totalBytes > 0 ? totalBytes : null));
    }

    private static TimeSpan ElapsedSince(long startTimestamp)
    {
        return TimeSpan.FromSeconds(
            (Stopwatch.GetTimestamp() - startTimestamp) / (double)Stopwatch.Frequency);
    }

    private static string FormatBytes(long bytes)
    {
        return bytes switch
        {
            >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:F1} GB",
            >= 1_048_576 => $"{bytes / 1_048_576.0:F1} MB",
            >= 1024 => $"{bytes / 1024.0:F1} KB",
            _ => $"{bytes} B",
        };
    }

    private static bool EmbeddingCacheReady(string datasetDirectory)
    {
        var cachePath = Path.Combine(datasetDirectory, ParquetFileEmbeddingCacheService.CacheFileName);
        return File.Exists(cachePath) && new FileInfo(cachePath).Length > 1024;
    }

    private static bool ModelFilesReady(string modelDirectory, string baseName)
    {
        var onnxPath = Path.Combine(modelDirectory, $"{baseName}.onnx");
        var metadataPath = Path.Combine(modelDirectory, $"{baseName}.metadata.json");

        return File.Exists(onnxPath) && File.Exists(metadataPath);
    }
}
