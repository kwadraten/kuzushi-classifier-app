using System.Text.Json.Nodes;
using KuzushiClassifierApp.Platform;
using KuzushiClassifierApp.Services;

namespace KuzushiClassifierApp.Plugin;

/// <summary>
/// Pure file-system readiness probe. Never downloads, never creates ONNX
/// sessions, never builds the embedding index — it only inspects the cache
/// directories the standalone app already maintains.
/// </summary>
public sealed class PluginAssetProbe
{
    private readonly IModelPathProvider _modelPathProvider;
    private readonly string _datasetDirectory;
    private readonly string _dataRoot;

    public PluginAssetProbe(
        IModelPathProvider modelPathProvider,
        IAppDataPathProvider appDataPathProvider)
    {
        ArgumentNullException.ThrowIfNull(modelPathProvider);
        ArgumentNullException.ThrowIfNull(appDataPathProvider);

        _modelPathProvider = modelPathProvider;
        _datasetDirectory = appDataPathProvider.GetDatasetCacheDirectory();
        _dataRoot = appDataPathProvider.GetAppDataDirectory();
    }

    public string DataRoot => _dataRoot;

    public string DatasetParquetPath => Path.Combine(
        _datasetDirectory,
        ParquetFileEmbeddingCacheService.CacheFileName);

    public PluginAssetReport Probe()
    {
        var classifier = ProbeModelPair(_modelPathProvider.ClassifierModelPath);
        var embedding = ProbeModelPair(_modelPathProvider.EmbeddingModelPath);
        var dataset = ProbeDataset();

        return new PluginAssetReport(classifier, embedding, dataset);
    }

    /// <summary>
    /// Assets an action needs before any inference is attempted.
    /// classify only needs the classifier model; similar needs the embedding
    /// model plus the cached embedding dataset; analyze needs all of them.
    /// </summary>
    public IReadOnlyList<string> MissingFor(string action, PluginAssetReport? report = null)
    {
        report ??= Probe();
        var missing = new List<string>();

        void AddMissing(AssetGroup group, string label)
        {
            foreach (var file in group.MissingFiles)
            {
                missing.Add($"{label}: {file}");
            }
        }

        if (action is "classify" or "analyze")
        {
            AddMissing(report.Classifier, "classifier model");
        }

        if (action is "similar" or "analyze")
        {
            AddMissing(report.Embedding, "embedding model");
            AddMissing(report.Dataset, "embedding dataset");
        }

        return missing;
    }

    private static AssetGroup ProbeModelPair(string modelPath)
    {
        var metadataPath = CachedModelPathProvider.MetadataPathFor(modelPath);
        var files = new[]
        {
            new AssetFile(modelPath, File.Exists(modelPath)),
            new AssetFile(metadataPath, File.Exists(metadataPath)),
        };

        return new AssetGroup(files);
    }

    private AssetGroup ProbeDataset()
    {
        var path = DatasetParquetPath;
        var exists = File.Exists(path) && new FileInfo(path).Length > 1024;
        return new AssetGroup(new[] { new AssetFile(path, exists) });
    }
}

public sealed record AssetFile(string Path, bool Exists);

public sealed record AssetGroup(IReadOnlyList<AssetFile> Files)
{
    public bool Ready => Files.All(file => file.Exists);

    public IReadOnlyList<string> MissingFiles =>
        Files.Where(file => !file.Exists).Select(file => file.Path).ToArray();

    public JsonObject ToJson()
    {
        return new JsonObject
        {
            ["ready"] = Ready,
            ["files"] = new JsonArray(Files
                .Select(file => (JsonNode)new JsonObject
                {
                    ["path"] = file.Path,
                    ["exists"] = file.Exists,
                })
                .ToArray()),
        };
    }
}

public sealed record PluginAssetReport(
    AssetGroup Classifier,
    AssetGroup Embedding,
    AssetGroup Dataset)
{
    public bool ClassifyReady => Classifier.Ready;

    public bool SimilarReady => Embedding.Ready && Dataset.Ready;

    public bool AnalyzeReady => ClassifyReady && SimilarReady;
}
