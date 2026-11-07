using KuzushiClassifierApp.Controllers;
using KuzushiClassifierApp.Platform;
using KuzushiClassifierApp.Services;
using Microsoft.Extensions.Logging;

namespace KuzushiClassifierApp.Plugin;

/// <summary>
/// Composition root for plugin mode. Reuses the app's existing services and
/// controllers unchanged — the only differences are stderr-only logging and
/// that nothing here ever calls the download path.
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
    /// otherwise %LocalAppData%/KuzushiClassifierApp).
    /// </summary>
    public static PluginServices Create(string? dataRoot = null)
    {
        IAppDataPathProvider appDataPathProvider = dataRoot is null
            ? new AppDataPathProvider()
            : new FixedAppDataPathProvider(dataRoot);

        var loggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddProvider(new StderrLoggerProvider());
        });

        var cacheLogger = loggerFactory.CreateLogger<ParquetFileEmbeddingCacheService>();
        var modelPathProvider = new CachedModelPathProvider(appDataPathProvider);
        var imagePreprocessingService = new PassThroughImagePreprocessingService();
        var imageClassifierService = new OnnxImageClassifierService(modelPathProvider);
        var imageEmbeddingService = new OnnxImageEmbeddingService(modelPathProvider);
        var embeddingCacheService = new ParquetFileEmbeddingCacheService(
            appDataPathProvider, cacheLogger);
        var embeddingIndexService = new ParquetStreamingEmbeddingIndexService(embeddingCacheService);

        var classificationController = new ClassificationController(
            imagePreprocessingService, imageClassifierService);
        var similaritySearchController = new SimilaritySearchController(
            imagePreprocessingService, imageEmbeddingService, embeddingIndexService);

        var assetProbe = new PluginAssetProbe(modelPathProvider, appDataPathProvider);
        var inference = new PluginInference(
            assetProbe,
            classificationController,
            similaritySearchController,
            embeddingIndexService);
        var dispatcher = new PluginDispatcher(
            assetProbe, inference, new ResultStore(), new DedupeStore());

        return new PluginServices(
            assetProbe,
            inference,
            dispatcher,
            loggerFactory,
            imageClassifierService,
            imageEmbeddingService);
    }

    /// <summary>
    /// Test seam: a dispatcher over caller-supplied backends and directories.
    /// </summary>
    public static PluginDispatcher CreateDispatcher(
        PluginAssetProbe assetProbe,
        IImageClassifierService classifier,
        IImageEmbeddingService embedding,
        IEmbeddingIndexService embeddingIndex)
    {
        var preprocessing = new PassThroughImagePreprocessingService();
        var inference = new PluginInference(
            assetProbe,
            new ClassificationController(preprocessing, classifier),
            new SimilaritySearchController(preprocessing, embedding, embeddingIndex),
            embeddingIndex);

        return new PluginDispatcher(assetProbe, inference, new ResultStore(), new DedupeStore());
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
