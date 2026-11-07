using KuzushiClassifierApp.Platform;
using KuzushiClassifierApp.Services;

namespace KuzushiClassifierApp.Plugin;

/// <summary>
/// Fixed application-data root used when the plugin is launched with an
/// explicit --data-root. Layout matches AppDataPathProvider: {root}/models,
/// {root}/datasets, {root}/logs.
/// </summary>
public sealed class FixedAppDataPathProvider : IAppDataPathProvider
{
    private readonly string _root;

    public FixedAppDataPathProvider(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
    }

    public string Root => _root;

    public string GetAppDataDirectory()
    {
        Directory.CreateDirectory(_root);
        return _root;
    }

    public string GetModelCacheDirectory() => Path.Combine(_root, "models");

    public string GetDatasetCacheDirectory() => Path.Combine(_root, "datasets");
}

/// <summary>
/// Model path provider backed only by the cache directories; unlike
/// HuggingFaceModelAssetService it owns no HttpClient and can never download.
/// </summary>
public sealed class CachedModelPathProvider : IModelPathProvider
{
    private readonly IAppDataPathProvider _appDataPathProvider;

    public CachedModelPathProvider(IAppDataPathProvider appDataPathProvider)
    {
        ArgumentNullException.ThrowIfNull(appDataPathProvider);
        _appDataPathProvider = appDataPathProvider;
    }

    public string ClassifierModelPath => Path.Combine(
        _appDataPathProvider.GetModelCacheDirectory(),
        "supervised_pretrain_checkpoint.onnx");

    public string EmbeddingModelPath => Path.Combine(
        _appDataPathProvider.GetModelCacheDirectory(),
        "supervised_pretrain_checkpoint.embedding.onnx");

    public static string MetadataPathFor(string modelPath) =>
        Path.ChangeExtension(modelPath, ".metadata.json");
}
