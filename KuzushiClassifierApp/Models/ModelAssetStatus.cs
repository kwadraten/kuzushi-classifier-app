namespace KuzushiClassifierApp.Models;

public sealed record ModelAssetStatus(
    bool ClassifierModelReady,
    bool EmbeddingModelReady,
    bool DatasetReady,
    bool EmbeddingIndexReady,
    string CacheDirectory);
