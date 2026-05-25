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
            AssetPreparationStep.LoadingEmbeddingCache,
            "Loading cached image embeddings from disk."));

        var cachedEmbeddings = await embeddingCacheService
            .TryLoadAsync(cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<DatasetImageEmbedding> embeddings;

        if (cachedEmbeddings is not null && CanReuseCache(images, cachedEmbeddings))
        {
            embeddings = cachedEmbeddings;
        }
        else
        {
            embeddings = await BuildAndPersistEmbeddingsAsync(
                images,
                progress,
                cancellationToken)
                .ConfigureAwait(false);
        }

        progress?.Report(new AssetPreparationProgress(
            AssetPreparationStep.BuildingEmbeddingIndex,
            "Building image embedding index."));

        await embeddingIndexService
            .BuildAsync(embeddings, cancellationToken)
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

    private async Task<IReadOnlyList<DatasetImageEmbedding>> BuildAndPersistEmbeddingsAsync(
        IReadOnlyList<DatasetImage> images,
        IProgress<AssetPreparationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var totalCount = images.Count;
        var embeddings = new List<DatasetImageEmbedding>(totalCount);
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

            embeddings.Add(new DatasetImageEmbedding(
                metadata,
                embedding.Vector.ToArray()));

            index++;
        }

        progress?.Report(new AssetPreparationProgress(
            AssetPreparationStep.SavingEmbeddingCache,
            "Saving image embeddings to disk.",
            1));

        await embeddingCacheService
            .SaveAsync(embeddings, cancellationToken)
            .ConfigureAwait(false);

        return embeddings;
    }

    private static bool CanReuseCache(
        IReadOnlyList<DatasetImage> images,
        IReadOnlyList<DatasetImageEmbedding>? embeddings)
    {
        if (embeddings is null || embeddings.Count != images.Count)
        {
            return false;
        }

        var imageIds = images
            .Select(image => image.Id)
            .ToHashSet(StringComparer.Ordinal);

        return embeddings.All(embedding => imageIds.Contains(embedding.Image.Id));
    }
}
