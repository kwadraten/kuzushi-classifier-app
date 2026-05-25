namespace KuzushiClassifierApp.Platform;

public sealed class LocalDevelopmentAppDataPathProvider : IAppDataPathProvider
{
    private const string DevDataRelativePath = ".agents/dev_data";

    private readonly string _devDataDirectory;

    public LocalDevelopmentAppDataPathProvider(string? startDirectory = null)
    {
        _devDataDirectory = FindDevDataDirectory(startDirectory ?? AppContext.BaseDirectory);
    }

    public string GetAppDataDirectory()
    {
        var directory = Path.Combine(_devDataDirectory, "runtime");
        Directory.CreateDirectory(directory);
        return directory;
    }

    public string GetModelCacheDirectory()
    {
        return Path.Combine(_devDataDirectory, "models", "shikiji");
    }

    public string GetDatasetCacheDirectory()
    {
        return Path.Combine(_devDataDirectory, "datasets", "hi-utokyo-kuzushi");
    }

    private static string FindDevDataDirectory(string startDirectory)
    {
        var current = new DirectoryInfo(Path.GetFullPath(startDirectory));

        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, DevDataRelativePath);

            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not find {DevDataRelativePath} from {startDirectory}.");
    }
}
