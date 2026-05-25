using KuzushiClassifierApp.Models;
using KuzushiClassifierApp.Platform;

namespace KuzushiClassifierApp.Services;

public sealed class LocalDevelopmentModelAssetService :
    IModelAssetService,
    IModelPathProvider
{
    private readonly IAppDataPathProvider _appDataPathProvider;

    public LocalDevelopmentModelAssetService(IAppDataPathProvider appDataPathProvider)
    {
        _appDataPathProvider = appDataPathProvider;
    }

    public string ClassifierModelPath => Path.Combine(
        _appDataPathProvider.GetModelCacheDirectory(),
        "supervised_pretrain_checkpoint.onnx");

    public string EmbeddingModelPath => Path.Combine(
        _appDataPathProvider.GetModelCacheDirectory(),
        "supervised_pretrain_checkpoint.embedding.onnx");

    public Task<ModelAssetStatus> PrepareAsync(
        IProgress<AssetPreparationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        progress?.Report(new AssetPreparationProgress(
            AssetPreparationStep.CheckingCache,
            "Checking local development model and dataset cache.",
            0));

        var datasetDirectory = _appDataPathProvider.GetDatasetCacheDirectory();
        var classifierModelReady = File.Exists(ClassifierModelPath)
            && File.Exists(Path.ChangeExtension(ClassifierModelPath, ".metadata.json"));
        var embeddingModelReady = File.Exists(EmbeddingModelPath)
            && File.Exists(Path.ChangeExtension(EmbeddingModelPath, ".metadata.json"));
        var parquetDirectory = Path.Combine(datasetDirectory, "data");
        var datasetReady = Directory.Exists(parquetDirectory)
            && Directory.EnumerateFiles(parquetDirectory, "*.parquet").Any();

        progress?.Report(new AssetPreparationProgress(
            AssetPreparationStep.CheckingCache,
            "Local development cache check complete.",
            1));

        return Task.FromResult(new ModelAssetStatus(
            classifierModelReady,
            embeddingModelReady,
            datasetReady,
            EmbeddingIndexReady: false,
            CacheDirectory: _appDataPathProvider.GetAppDataDirectory()));
    }
}
