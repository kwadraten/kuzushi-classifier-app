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
/// Resolves every declared asset path for the plugin process. Host-managed
/// locations injected at launch (<see cref="PluginManagedFiles"/>) win; without
/// them the app-data cache layout is used so direct/dev runs keep working.
/// Unlike HuggingFaceModelAssetService it owns no HttpClient and can never
/// download — it only reports where files should be found.
/// </summary>
public sealed class CachedModelPathProvider : IModelPathProvider
{
    private readonly IAppDataPathProvider _appDataPathProvider;
    private readonly PluginManagedFiles _managedFiles;

    public CachedModelPathProvider(
        IAppDataPathProvider appDataPathProvider,
        PluginManagedFiles? managedFiles = null)
    {
        ArgumentNullException.ThrowIfNull(appDataPathProvider);
        _appDataPathProvider = appDataPathProvider;
        _managedFiles = managedFiles ?? PluginManagedFiles.None;
    }

    /// <summary>Whether the host injected any managed-file location at launch.</summary>
    public bool ManagedFilesConfigured => _managedFiles.Configured;

    /// <summary>Whether the declared relative path resolves to a host-managed location.</summary>
    public bool IsManaged(string relativePath) => _managedFiles.IsManaged(relativePath);

    public string ClassifierModelPath => _managedFiles.Resolve(PluginManagedFiles.ClassifierModel)
        ?? Path.Combine(
            _appDataPathProvider.GetModelCacheDirectory(),
            "supervised_pretrain_checkpoint.onnx");

    public string EmbeddingModelPath => _managedFiles.Resolve(PluginManagedFiles.EmbeddingModel)
        ?? Path.Combine(
            _appDataPathProvider.GetModelCacheDirectory(),
            "supervised_pretrain_checkpoint.embedding.onnx");

    /// <summary>Metadata JSON paired with <see cref="ClassifierModelPath"/>.</summary>
    public string ClassifierMetadataPath => _managedFiles.Resolve(PluginManagedFiles.ClassifierMetadata)
        ?? MetadataPathFor(ClassifierModelPath);

    /// <summary>Metadata JSON paired with <see cref="EmbeddingModelPath"/>.</summary>
    public string EmbeddingMetadataPath => _managedFiles.Resolve(PluginManagedFiles.EmbeddingMetadata)
        ?? MetadataPathFor(EmbeddingModelPath);

    /// <summary>The image-embedding Parquet dataset consumed by cache/index/library services.</summary>
    public string DatasetParquetPath => _managedFiles.Resolve(PluginManagedFiles.DatasetParquet)
        ?? Path.Combine(
            _appDataPathProvider.GetDatasetCacheDirectory(),
            ParquetFileEmbeddingCacheService.CacheFileName);

    public static string MetadataPathFor(string modelPath) =>
        Path.ChangeExtension(modelPath, ".metadata.json");
}
