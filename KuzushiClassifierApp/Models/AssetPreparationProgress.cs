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
    double? Fraction = null);
