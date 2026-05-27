using System.Net.Http;
using System.Formats.Tar;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
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
    private const string HuggingFaceRawBase = "https://huggingface.co";
    private const string PrebuiltDatasetUrl =
        "https://scripts-1303933394.cos.ap-tokyo.myqcloud.com/embeddings/kuzushi-shikiji-webp-dotvector-diskann.tar";

    private readonly IAppDataPathProvider _appDataPathProvider;
    private readonly HttpClient _httpClient;
    private readonly string _modelRepo;

    public HuggingFaceModelAssetService(
        IAppDataPathProvider appDataPathProvider,
        string modelRepo = DefaultModelRepo)
    {
        _appDataPathProvider = appDataPathProvider;
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
        var datasetReady = PrebuiltDatasetReady(datasetDirectory);

        if (!datasetReady)
        {
            progress?.Report(new AssetPreparationProgress(
                AssetPreparationStep.DownloadingDataset,
                "Downloading prebuilt dataset package.",
                0.5));

            await EnsurePrebuiltDatasetAsync(datasetDirectory, progress, cancellationToken)
                .ConfigureAwait(false);

            datasetReady = PrebuiltDatasetReady(datasetDirectory);

            if (!datasetReady)
            {
                throw new InvalidOperationException(
                    "Failed to download and unpack the prebuilt dataset package.");
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
                throw new InvalidOperationException(
                    $"Failed to download {fileName} from HuggingFace ({url}). " +
                    $"Status: {ex.StatusCode}. Ensure the model repository '{_modelRepo}' is public.",
                    ex);
            }
        }
    }

    private async Task EnsurePrebuiltDatasetAsync(
        string datasetDirectory,
        IProgress<AssetPreparationProgress>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(datasetDirectory);

        if (PrebuiltDatasetReady(datasetDirectory))
        {
            return;
        }

        var tarPath = Path.Combine(datasetDirectory, "kuzushi-shikiji-webp-dotvector-diskann.tar");

        await DownloadWithProgressAsync(
            PrebuiltDatasetUrl,
            tarPath,
            AssetPreparationStep.DownloadingDataset,
            "kuzushi-shikiji-webp-dotvector-diskann.tar",
            progress,
            cancellationToken)
            .ConfigureAwait(false);

        progress?.Report(new AssetPreparationProgress(
            AssetPreparationStep.LoadingDataset,
            "Unpacking prebuilt dataset package.",
            0.95));

        ExtractTarToDirectory(tarPath, datasetDirectory, cancellationToken);
        if (PrebuiltDatasetReady(datasetDirectory))
        {
            File.Delete(tarPath);
        }
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
            catch (IOException) when (retryCount < maxRetries)
            {
                retryCount++;
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, retryCount * 2)), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (HttpRequestException) when (retryCount < maxRetries)
            {
                retryCount++;
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
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
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
            if (!remoteBytes.HasValue || fileBytes == remoteBytes.Value)
            {
                ReportDownloadProgress(progress, step, label, fileBytes, remoteBytes ?? fileBytes);
                return true;
            }

            if (fileBytes > 0 && fileBytes < remoteBytes.Value && !File.Exists(tempPath))
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

    private static void ExtractTarToDirectory(
        string tarPath,
        string targetDirectory,
        CancellationToken cancellationToken)
    {
        var targetRoot = Path.GetFullPath(targetDirectory);
        var targetRootWithSeparator = targetRoot.EndsWith(Path.DirectorySeparatorChar)
            ? targetRoot
            : targetRoot + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(targetRoot);

        using var stream = File.OpenRead(tarPath);
        using var reader = new TarReader(stream);

        while (reader.GetNextEntry() is { } entry)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(entry.Name))
            {
                continue;
            }

            var normalizedName = entry.Name.Replace('\\', Path.DirectorySeparatorChar);
            var destinationPath = Path.GetFullPath(Path.Combine(targetRoot, normalizedName));

            if (!destinationPath.Equals(targetRoot, StringComparison.OrdinalIgnoreCase)
                && !destinationPath.StartsWith(targetRootWithSeparator, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Unsafe tar entry path: {entry.Name}");
            }

            if (entry.EntryType is TarEntryType.Directory)
            {
                Directory.CreateDirectory(destinationPath);
                continue;
            }

            var directory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            entry.ExtractToFile(destinationPath, overwrite: true);
        }
    }

    private static bool PrebuiltDatasetReady(string datasetDirectory)
    {
        return File.Exists(Path.Combine(datasetDirectory, "manifest.json"))
            && File.Exists(Path.Combine(datasetDirectory, "metadata", "records.jsonl"))
            && Directory.Exists(Path.Combine(datasetDirectory, "images-webp"))
            && Directory.EnumerateFiles(
                Path.Combine(datasetDirectory, "images-webp"),
                "*.webp",
                SearchOption.AllDirectories).Any()
            && Directory.Exists(Path.Combine(datasetDirectory, "vectors", "dotvector-shikiji-diskann"))
            && Directory.EnumerateFiles(
                Path.Combine(datasetDirectory, "vectors", "dotvector-shikiji-diskann"),
                "*",
                SearchOption.AllDirectories).Any(file => new FileInfo(file).Length > 1024);
    }

    private static bool ModelFilesReady(string modelDirectory, string baseName)
    {
        var onnxPath = Path.Combine(modelDirectory, $"{baseName}.onnx");
        var metadataPath = Path.Combine(modelDirectory, $"{baseName}.metadata.json");

        return File.Exists(onnxPath) && File.Exists(metadataPath);
    }
}
