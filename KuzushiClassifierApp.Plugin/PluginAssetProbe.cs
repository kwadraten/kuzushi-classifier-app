using System.Text.Json.Nodes;

namespace KuzushiClassifierApp.Plugin;

/// <summary>
/// Pure file-system readiness probe. Never downloads, never creates ONNX
/// sessions, never builds the embedding index, never creates directories —
/// it only inspects the resolved locations from <see cref="CachedModelPathProvider"/>.
/// Agent-facing output reports the manifest-declared relative paths; the
/// absolute locations (host-managed or app-data) stay internal.
/// </summary>
public sealed class PluginAssetProbe
{
    private readonly CachedModelPathProvider _paths;

    public PluginAssetProbe(CachedModelPathProvider modelPaths)
    {
        ArgumentNullException.ThrowIfNull(modelPaths);
        _paths = modelPaths;
    }

    /// <summary>Whether the host injected managed-file locations at launch.</summary>
    public bool ManagedFilesConfigured => _paths.ManagedFilesConfigured;

    /// <summary>Internal resolved dataset location — never exposed in agent-facing JSON.</summary>
    internal string DatasetParquetPath => _paths.DatasetParquetPath;

    public PluginAssetReport Probe()
    {
        var classifier = ProbeGroup(
            (PluginManagedFiles.ClassifierModel, _paths.ClassifierModelPath),
            (PluginManagedFiles.ClassifierMetadata, _paths.ClassifierMetadataPath));
        var embedding = ProbeGroup(
            (PluginManagedFiles.EmbeddingModel, _paths.EmbeddingModelPath),
            (PluginManagedFiles.EmbeddingMetadata, _paths.EmbeddingMetadataPath));
        var dataset = ProbeDataset();

        return new PluginAssetReport(classifier, embedding, dataset);
    }

    /// <summary>
    /// Assets an action needs before any inference is attempted.
    /// classify only needs the classifier model; similar needs the embedding
    /// model plus the cached embedding dataset; analyze needs all of them.
    /// Missing entries name the declared relative paths (agent-safe).
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

    private AssetGroup ProbeGroup(params (string Relative, string Absolute)[] files)
    {
        return new AssetGroup(files
            .Select(file => new AssetFile(
                file.Relative,
                file.Absolute,
                File.Exists(file.Absolute),
                _paths.IsManaged(file.Relative)))
            .ToArray());
    }

    private AssetGroup ProbeDataset()
    {
        var absolute = _paths.DatasetParquetPath;
        var exists = File.Exists(absolute) && new FileInfo(absolute).Length > 1024;
        return new AssetGroup(new[]
        {
            new AssetFile(
                PluginManagedFiles.DatasetParquet,
                absolute,
                exists,
                _paths.IsManaged(PluginManagedFiles.DatasetParquet)),
        });
    }
}

/// <summary>
/// One probed file. <see cref="RelativePath"/> is the manifest-declared path
/// shown to agents; <see cref="AbsolutePath"/> is the resolved internal
/// location and is never serialized into status payloads.
/// </summary>
public sealed record AssetFile(
    string RelativePath,
    string AbsolutePath,
    bool Exists,
    bool Managed);

public sealed record AssetGroup(IReadOnlyList<AssetFile> Files)
{
    public bool Ready => Files.All(file => file.Exists);

    /// <summary>Declared relative paths of missing files (agent-safe).</summary>
    public IReadOnlyList<string> MissingFiles =>
        Files.Where(file => !file.Exists).Select(file => file.RelativePath).ToArray();

    public JsonObject ToJson()
    {
        return new JsonObject
        {
            ["ready"] = Ready,
            ["files"] = new JsonArray(Files
                .Select(file => (JsonNode)new JsonObject
                {
                    ["path"] = file.RelativePath,
                    ["exists"] = file.Exists,
                    ["managed"] = file.Managed,
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
