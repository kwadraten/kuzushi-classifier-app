namespace KuzushiClassifierApp.Models;

public enum AssetPreparationStep
{
    CheckingCache,
    DownloadingModels,
    DownloadingDataset,
    LoadingDataset,
    LoadingEmbeddingCache,
    CalculatingEmbeddings,
    SavingEmbeddingCache,
    BuildingEmbeddingIndex,
    Ready,
}

public sealed record AssetPreparationProgress(
    AssetPreparationStep Step,
    string Message,
    double? Fraction = null,
    long? BytesDownloaded = null,
    long? TotalBytes = null,
    int? ItemsProcessed = null,
    int? TotalItems = null);
