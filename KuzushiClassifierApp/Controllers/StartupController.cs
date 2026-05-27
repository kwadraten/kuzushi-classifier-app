using KuzushiClassifierApp.Models;
using KuzushiClassifierApp.Services;

namespace KuzushiClassifierApp.Controllers;

public sealed class StartupController(
    IModelAssetService modelAssetService,
    IImageLibraryService imageLibraryService,
    IImagePreprocessingService imagePreprocessingService,
    IImageEmbeddingService imageEmbeddingService,
    IEmbeddingCacheService embeddingCacheService,
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
            "Loading image embedding cache."));

        var cacheMetadata = await embeddingCacheService
            .TryReadMetadataAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!CanReuseCache(images, cacheMetadata))
        {
            await BuildAndPersistEmbeddingsAsync(
                    images.Count,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        progress?.Report(new AssetPreparationProgress(
            AssetPreparationStep.BuildingEmbeddingIndex,
            "Preparing streaming cosine similarity search."));

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

    private async Task BuildAndPersistEmbeddingsAsync(
        int totalCount,
        IProgress<AssetPreparationProgress>? progress,
        CancellationToken cancellationToken)
    {
        await embeddingCacheService
            .SaveAsync(StreamCalculatedEmbeddingsAsync(totalCount, progress, cancellationToken), cancellationToken)
            .ConfigureAwait(false);

        progress?.Report(new AssetPreparationProgress(
            AssetPreparationStep.SavingEmbeddingCache,
            "Saved image embeddings to Parquet.",
            1,
            ItemsProcessed: totalCount,
            TotalItems: totalCount));
    }

    private async IAsyncEnumerable<DatasetImageEmbedding> StreamCalculatedEmbeddingsAsync(
        int totalCount,
        IProgress<AssetPreparationProgress>? progress,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var index = 0;

        await foreach (var (metadata, kuzushiImage) in imageLibraryService
            .StreamAllImagesAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            progress?.Report(new AssetPreparationProgress(
                AssetPreparationStep.CalculatingEmbeddings,
                $"Calculating embedding {index + 1} of {totalCount}.",
                totalCount == 0 ? 1 : (double)index / totalCount,
                ItemsProcessed: index,
                TotalItems: totalCount));

            var preparedImage = await imagePreprocessingService
                .PrepareForModelAsync(kuzushiImage, cancellationToken)
                .ConfigureAwait(false);

            var embedding = await imageEmbeddingService
                .EmbedAsync(preparedImage, cancellationToken)
                .ConfigureAwait(false);

            yield return new DatasetImageEmbedding(
                metadata,
                embedding.Vector.ToArray());

            index++;
        }
    }

    private static bool CanReuseCache(
        IReadOnlyList<DatasetImage> images,
        EmbeddingCacheMetadata? cacheMetadata)
    {
        return cacheMetadata is not null
            && cacheMetadata.Count == images.Count
            && cacheMetadata.VectorDimensions > 0;
    }
}
