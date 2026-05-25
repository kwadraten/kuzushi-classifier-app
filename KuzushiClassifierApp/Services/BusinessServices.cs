using KuzushiClassifierApp.Controllers;
using KuzushiClassifierApp.Platform;

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
    ImageAnalysisController ImageAnalysisController)
{
    public static BusinessServices Create(string? startDirectory = null)
    {
        var appDataPathProvider = new AppDataPathProvider(startDirectory);
        var modelAssetService = new HuggingFaceModelAssetService(appDataPathProvider);
        var imageLibraryService = new JsonLinesDevelopmentImageLibraryService(appDataPathProvider);
        var imagePreprocessingService = new PassThroughImagePreprocessingService();
        var imageClassifierService = new OnnxImageClassifierService(modelAssetService);
        var imageEmbeddingService = new OnnxImageEmbeddingService(modelAssetService);
        var embeddingCacheService = new JsonFileEmbeddingCacheService(appDataPathProvider);
        var embeddingIndexService = new InMemoryEmbeddingIndexService();

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
            imageAnalysisController);
    }
}
