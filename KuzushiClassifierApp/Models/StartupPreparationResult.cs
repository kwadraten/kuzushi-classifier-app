namespace KuzushiClassifierApp.Models;

public sealed record StartupPreparationResult(
    ModelAssetStatus AssetStatus,
    int DatasetImageCount,
    int IndexedEmbeddingCount);
