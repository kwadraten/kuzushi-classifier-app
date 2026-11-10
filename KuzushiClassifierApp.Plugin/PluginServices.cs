using KuzushiClassifierApp.Controllers;
using KuzushiClassifierApp.Models;
using KuzushiClassifierApp.Platform;
using KuzushiClassifierApp.Services;
using Microsoft.Extensions.Logging;

namespace KuzushiClassifierApp.Plugin;

/// <summary>
/// Composition root for plugin mode. Reuses the app's existing services and
/// controllers unchanged — the only differences are stderr-only logging,
/// host-managed file locations injected at launch, and that nothing here ever
/// touches the network or calls a download path.
/// </summary>
public sealed class PluginServices : IDisposable
{
    private PluginServices(
        PluginAssetProbe assetProbe,
        PluginInference inference,
        PluginDispatcher dispatcher,
        ILoggerFactory loggerFactory,
        params IDisposable[] disposables)
    {
        AssetProbe = assetProbe;
        Inference = inference;
        Dispatcher = dispatcher;
        LoggerFactory = loggerFactory;
        _disposables = disposables;
    }

    private readonly IDisposable[] _disposables;

    public PluginAssetProbe AssetProbe { get; }

    public PluginInference Inference { get; }

    public PluginDispatcher Dispatcher { get; }

    public ILoggerFactory LoggerFactory { get; }

    /// <summary>
    /// Builds services against the given data root, or the normal
    /// AppDataPathProvider resolution (.agents/dev_data when discoverable,
    /// otherwise %LocalAppData%/KuzushiClassifierApp). Host-managed file
    /// locations from <paramref name="managedFiles"/> override the app-data
    /// layout for every declared file they cover.
    /// </summary>
    public static PluginServices Create(
        string? dataRoot = null,
        PluginManagedFiles? managedFiles = null)
    {
        IAppDataPathProvider appDataPathProvider = dataRoot is null
            ? new AppDataPathProvider()
            : new FixedAppDataPathProvider(dataRoot);

        // Materialize the app-data workspace root only; managed directories are
        // host-owned and are never created here.
        appDataPathProvider.GetAppDataDirectory();

        managedFiles ??= PluginManagedFiles.None;

        var loggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddProvider(new StderrLoggerProvider());
        });

        var cacheLogger = loggerFactory.CreateLogger<ParquetFileEmbeddingCacheService>();
        var modelPathProvider = new CachedModelPathProvider(appDataPathProvider, managedFiles);
        var imagePreprocessingService = new PassThroughImagePreprocessingService();
        var imageClassifierService = new OnnxImageClassifierService(
            modelPathProvider, modelPathProvider.ClassifierMetadataPath);
        var imageEmbeddingService = new OnnxImageEmbeddingService(
            modelPathProvider, modelPathProvider.EmbeddingMetadataPath);
        var embeddingCacheService = new ParquetFileEmbeddingCacheService(
            modelPathProvider.DatasetParquetPath, cacheLogger);
        var embeddingIndexService = new ParquetStreamingEmbeddingIndexService(embeddingCacheService);
        var imageLibraryService = new StreamingParquetImageLibraryService(
            modelPathProvider.DatasetParquetPath);

        var classificationController = new ClassificationController(
            imagePreprocessingService, imageClassifierService);
        var similaritySearchController = new SimilaritySearchController(
            imagePreprocessingService, imageEmbeddingService, embeddingIndexService);

        var assetProbe = new PluginAssetProbe(modelPathProvider);
        var inference = new PluginInference(
            assetProbe,
            classificationController,
            similaritySearchController,
            embeddingIndexService);

        // Local-only preparation: the same cache/index/model services inference uses,
        // so a confirmed prepare_assets produces genuinely usable runtime state. The
        // delegate runs under the inference gate via PluginInference.PrepareAsync.
        // Missing declared files are rejected inside PluginPreparation before this
        // pipeline is ever invoked — the plugin performs no downloads.
        var preparation = new PluginPreparation(
            (progress, cancellationToken) => inference.PrepareAsync(
                (pipelineProgress, pipelineToken) => PrepareLocalAsync(
                    imageLibraryService,
                    embeddingCacheService,
                    embeddingIndexService,
                    imageClassifierService,
                    imageEmbeddingService,
                    pipelineProgress,
                    pipelineToken),
                progress, cancellationToken),
            () => (imageClassifierService.IsLoaded && imageEmbeddingService.IsLoaded,
                embeddingIndexService.IsReady),
            assetProbe);

        var dispatcher = new PluginDispatcher(
            assetProbe, inference, new ResultStore(), new DedupeStore(), preparation);

        return new PluginServices(
            assetProbe,
            inference,
            dispatcher,
            loggerFactory,
            preparation,
            imageClassifierService,
            imageEmbeddingService);
    }

    /// <summary>
    /// The local-load pipeline: scans the declared dataset Parquet, validates the
    /// embedding cache metadata against it, builds the streaming similarity index
    /// and loads both ONNX sessions. File reads only — no network, no downloads,
    /// no managed-directory creation. Files the host has not supplied are caught
    /// earlier by the needs_download gate; anything that still fails here is a
    /// corrupt/incomplete payload and surfaces as the failed state.
    /// </summary>
    private static async Task PrepareLocalAsync(
        IImageLibraryService imageLibraryService,
        IEmbeddingCacheService embeddingCacheService,
        IEmbeddingIndexService embeddingIndexService,
        OnnxImageClassifierService imageClassifierService,
        OnnxImageEmbeddingService imageEmbeddingService,
        IProgress<AssetPreparationProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        progress?.Report(new AssetPreparationProgress(
            AssetPreparationStep.CheckingCache,
            "Checking declared model and dataset files."));

        var images = await imageLibraryService
            .LoadImagesAsync(cancellationToken, progress)
            .ConfigureAwait(false);

        progress?.Report(new AssetPreparationProgress(
            AssetPreparationStep.LoadingEmbeddingCache,
            "Loading image embedding cache metadata."));

        var cacheMetadata = await embeddingCacheService
            .TryReadMetadataAsync(cancellationToken)
            .ConfigureAwait(false);

        if (cacheMetadata is null
            || cacheMetadata.Count != images.Count
            || cacheMetadata.VectorDimensions <= 0)
        {
            throw new InvalidOperationException(
                "The image embedding package is missing or does not match the image metadata.");
        }

        progress?.Report(new AssetPreparationProgress(
            AssetPreparationStep.BuildingEmbeddingIndex,
            "Preparing streaming cosine similarity search."));

        await embeddingIndexService
            .BuildAsync(Enumerable.Empty<DatasetImageEmbedding>(), cancellationToken)
            .ConfigureAwait(false);

        progress?.Report(new AssetPreparationProgress(
            AssetPreparationStep.LoadingModels,
            "Loading ONNX model sessions."));

        imageClassifierService.LoadModel();
        imageEmbeddingService.LoadModel();

        progress?.Report(new AssetPreparationProgress(
            AssetPreparationStep.Ready,
            "Local assets are ready.",
            1));
    }

    /// <summary>
    /// Test seam: a dispatcher over caller-supplied backends and directories.
    /// </summary>
    public static PluginDispatcher CreateDispatcher(
        PluginAssetProbe assetProbe,
        IImageClassifierService classifier,
        IImageEmbeddingService embedding,
        IEmbeddingIndexService embeddingIndex,
        PluginPreparation? preparation = null)
    {
        var preprocessing = new PassThroughImagePreprocessingService();
        var inference = new PluginInference(
            assetProbe,
            new ClassificationController(preprocessing, classifier),
            new SimilaritySearchController(preprocessing, embedding, embeddingIndex),
            embeddingIndex);

        return new PluginDispatcher(
            assetProbe, inference, new ResultStore(), new DedupeStore(), preparation);
    }

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }

        LoggerFactory.Dispose();
    }
}
