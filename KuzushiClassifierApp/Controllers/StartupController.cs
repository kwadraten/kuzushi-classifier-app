using KuzushiClassifierApp.Models;
using KuzushiClassifierApp.Services;

namespace KuzushiClassifierApp.Controllers;

public sealed class StartupController(
    IModelAssetService modelAssetService,
    IImageLibraryService imageLibraryService,
    IEmbeddingIndexService embeddingIndexService)
{
    public async Task<StartupPreparationResult> PrepareAsync(
        IProgress<AssetPreparationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new AssetPreparationProgress(
            AssetPreparationStep.CheckingCache,
            "Checking cached model and dataset assets."));

        var assetStatus = await modelAssetService
            .PrepareAsync(progress, cancellationToken)
            .ConfigureAwait(false);

        progress?.Report(new AssetPreparationProgress(
            AssetPreparationStep.LoadingDataset,
            "Loading dataset metadata."));

        var images = await imageLibraryService
            .LoadImagesAsync(cancellationToken, progress)
            .ConfigureAwait(false);

        progress?.Report(new AssetPreparationProgress(
            AssetPreparationStep.BuildingEmbeddingIndex,
            "Loading prebuilt image embedding index."));

        await embeddingIndexService
            .BuildAsync(Enumerable.Empty<DatasetImageEmbedding>(), cancellationToken)
            .ConfigureAwait(false);

        progress?.Report(new AssetPreparationProgress(
            AssetPreparationStep.Ready,
            "Startup assets are ready.",
            1));

        return new StartupPreparationResult(
            assetStatus with { EmbeddingIndexReady = embeddingIndexService.IsReady },
            images.Count,
            embeddingIndexService.Count);
    }
}
