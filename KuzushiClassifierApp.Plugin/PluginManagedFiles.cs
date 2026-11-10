namespace KuzushiClassifierApp.Plugin;

/// <summary>
/// Host-injected managed-file mapping. The manifest declares every runtime
/// payload under <c>[[files]]</c>; the host downloads and verifies them into
/// its own per-device managed store and tells the plugin where each declared
/// file landed via <c>--patchouli-managed-files &lt;root&gt;</c> plus optional
/// repeatable <c>--patchouli-managed-file "&lt;relative&gt;|&lt;absolute&gt;"</c>
/// launch arguments. The plugin only ever reads these paths — it never creates
/// directories under the managed root and never downloads.
/// </summary>
public sealed class PluginManagedFiles
{
    // Declared relative paths — keep in sync with [[files]] in patchouli-plugin.toml.
    public const string ClassifierModel = "models/supervised_pretrain_checkpoint.onnx";
    public const string ClassifierMetadata = "models/supervised_pretrain_checkpoint.metadata.json";
    public const string EmbeddingModel = "models/supervised_pretrain_checkpoint.embedding.onnx";
    public const string EmbeddingMetadata = "models/supervised_pretrain_checkpoint.embedding.metadata.json";
    public const string DatasetParquet = "datasets/image-embeddings.with-webp.parquet";

    public static readonly IReadOnlyList<string> DeclaredPaths = new[]
    {
        ClassifierModel,
        ClassifierMetadata,
        EmbeddingModel,
        EmbeddingMetadata,
        DatasetParquet,
    };

    /// <summary>Shared instance for launches without any managed-file args.</summary>
    public static PluginManagedFiles None { get; } = new(null, null);

    private readonly string? _root;
    private readonly IReadOnlyDictionary<string, string> _files;

    public PluginManagedFiles(string? root, IReadOnlyDictionary<string, string>? files)
    {
        _root = string.IsNullOrWhiteSpace(root) ? null : Path.GetFullPath(root);
        _files = files ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }

    /// <summary>The --patchouli-managed-files root, when the host supplied one.</summary>
    public string? Root => _root;

    /// <summary>Explicit per-file mappings from --patchouli-managed-file.</summary>
    public IReadOnlyDictionary<string, string> Files => _files;

    /// <summary>
    /// Whether the host injected any managed-file location at launch. When
    /// false, declared files resolve from the app-data layout (dev/direct runs).
    /// </summary>
    public bool Configured => _root is not null || _files.Count > 0;

    /// <summary>
    /// Resolved absolute host-managed path for a declared relative path, or
    /// null when no managed mapping covers it (callers then use the app-data
    /// layout). An explicit per-file mapping wins over the managed root.
    /// </summary>
    public string? Resolve(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        if (_files.TryGetValue(Normalize(relativePath), out var absolute))
        {
            return absolute;
        }

        return _root is null
            ? null
            : Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>Whether the declared relative path resolves to a host-managed location.</summary>
    public bool IsManaged(string relativePath) => Resolve(relativePath) is not null;

    /// <summary>Normalizes a declared relative path key ('/' separators, no leading slash).</summary>
    public static string Normalize(string relativePath)
    {
        return relativePath.Replace('\\', '/').Trim('/');
    }
}

/// <summary>
/// Parsed plugin launch arguments. <c>--data-root</c> keeps direct/dev runs on
/// the app-data layout; the host injects the managed-file arguments instead.
/// </summary>
public sealed record PluginLaunchOptions(string? DataRoot, PluginManagedFiles ManagedFiles)
{
    public const string UsageText =
        "Usage: KuzushiClassifierApp.Plugin.exe [--data-root <path>] " +
        "[--patchouli-managed-files <root>] " +
        "[--patchouli-managed-file <relative>|<absolute>]...\n" +
        "Runs a newline-delimited JSON-RPC MCP server on stdin/stdout.\n" +
        "Managed-file arguments are injected by the host; without them declared " +
        "files resolve from the app-data layout used by direct/dev runs.";

    /// <summary>
    /// Parses the launch arguments. Returns null when help was requested.
    /// </summary>
    /// <exception cref="ArgumentException">Unknown or malformed arguments.</exception>
    public static PluginLaunchOptions? Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? dataRoot = null;
        string? managedRoot = null;
        Dictionary<string, string>? map = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--data-root" when i + 1 < args.Length:
                    dataRoot = args[++i];
                    break;
                case "--patchouli-managed-files" when i + 1 < args.Length:
                    managedRoot = args[++i];
                    break;
                case "--patchouli-managed-file" when i + 1 < args.Length:
                {
                    var spec = args[++i];
                    var separator = spec.IndexOf('|', StringComparison.Ordinal);
                    if (separator <= 0 || separator >= spec.Length - 1)
                    {
                        throw new ArgumentException(
                            $"--patchouli-managed-file expects '<relative>|<absolute>'; got '{spec}'.");
                    }

                    var relative = PluginManagedFiles.Normalize(spec[..separator]);
                    if (relative.Length == 0 || relative.Split('/').Any(string.IsNullOrEmpty))
                    {
                        throw new ArgumentException(
                            $"--patchouli-managed-file relative path is invalid: '{spec[..separator]}'.");
                    }

                    map ??= new Dictionary<string, string>(StringComparer.Ordinal);
                    map[relative] = spec[(separator + 1)..];
                    break;
                }
                case "--help" or "-h":
                    return null;
                default:
                    throw new ArgumentException($"Unknown argument: {args[i]}");
            }
        }

        return new PluginLaunchOptions(dataRoot, new PluginManagedFiles(managedRoot, map));
    }
}
