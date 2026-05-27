using System.IO;
using System.Linq;
using KuzushiClassifierApp.Controllers;
using KuzushiClassifierApp.Platform;
using Microsoft.Extensions.Logging;
using ZLogger;
using ZLogger.Providers;

namespace KuzushiClassifierApp.Services;

public sealed record BusinessServices(
    IAppDataPathProvider AppDataPathProvider,
    IModelPathProvider ModelPathProvider,
    IModelAssetService ModelAssetService,
    IImageLibraryService ImageLibraryService,
    IImagePreprocessingService ImagePreprocessingService,
    IImageClassifierService ImageClassifierService,
    IImageEmbeddingService ImageEmbeddingService,
    IEmbeddingCacheService EmbeddingCacheService,
    IEmbeddingIndexService EmbeddingIndexService,
    StartupController StartupController,
    ClassificationController ClassificationController,
    SimilaritySearchController SimilaritySearchController,
    ImageAnalysisController ImageAnalysisController,
    ILoggerFactory LoggerFactory)
{
    public static BusinessServices Create(string? startDirectory = null)
    {
        var appDataPathProvider = new AppDataPathProvider(startDirectory);

        // Configure rolling logging to logs subfolder
        var logDir = Path.Combine(appDataPathProvider.GetAppDataDirectory(), "logs");
        Directory.CreateDirectory(logDir);
        CleanOldLogFiles(logDir, 3);

        var loggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddZLoggerConsole();
            builder.AddZLoggerRollingFile(
                (dt, index) => Path.Combine(logDir, $"app_{dt:yyyyMMdd}_{index:D3}.log"),
                RollingInterval.Day
            );
        });

        var modelLogger = loggerFactory.CreateLogger<HuggingFaceModelAssetService>();
        var cacheLogger = loggerFactory.CreateLogger<ParquetFileEmbeddingCacheService>();

        var modelAssetService = new HuggingFaceModelAssetService(appDataPathProvider, modelLogger);
        var imageLibraryService = new StreamingParquetImageLibraryService(appDataPathProvider);
        var imagePreprocessingService = new PassThroughImagePreprocessingService();
        var imageClassifierService = new OnnxImageClassifierService(modelAssetService);
        var imageEmbeddingService = new OnnxImageEmbeddingService(modelAssetService);
        var embeddingCacheService = new ParquetFileEmbeddingCacheService(appDataPathProvider, cacheLogger);
        var embeddingIndexService = new ParquetStreamingEmbeddingIndexService(embeddingCacheService);

        var startupController = new StartupController(
            modelAssetService,
            imageLibraryService,
            imagePreprocessingService,
            imageEmbeddingService,
            embeddingCacheService,
            embeddingIndexService);

        var classificationController = new ClassificationController(
            imagePreprocessingService,
            imageClassifierService);

        var similaritySearchController = new SimilaritySearchController(
            imagePreprocessingService,
            imageEmbeddingService,
            embeddingIndexService);

        var imageAnalysisController = new ImageAnalysisController(
            classificationController,
            similaritySearchController);

        return new BusinessServices(
            appDataPathProvider,
            modelAssetService,
            modelAssetService,
            imageLibraryService,
            imagePreprocessingService,
            imageClassifierService,
            imageEmbeddingService,
            embeddingCacheService,
            embeddingIndexService,
            startupController,
            classificationController,
            similaritySearchController,
            imageAnalysisController,
            loggerFactory);
    }

    private static void CleanOldLogFiles(string logDir, int keepCount)
    {
        try
        {
            if (!Directory.Exists(logDir)) return;
            var logFiles = Directory.EnumerateFiles(logDir, "app_*.log")
                .OrderBy(f => f)
                .ToList();

            if (logFiles.Count > keepCount)
            {
                for (var i = 0; i < logFiles.Count - keepCount; i++)
                {
                    File.Delete(logFiles[i]);
                }
            }
        }
        catch
        {
            // Do not crash application startup on log cleanup failures
        }
    }
}
