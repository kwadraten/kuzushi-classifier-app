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
        if (System.OperatingSystem.IsAndroid())
        {
            var androidCandidates = new[]
            {
                "/sdcard/KuzushiClassifierApp/dev_data",
                "/storage/emulated/0/KuzushiClassifierApp/dev_data",
                System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.Personal), "dev_data")
            };

            foreach (var candidate in androidCandidates)
            {
                if (System.IO.Directory.Exists(candidate))
                {
                    return candidate;
                }
            }

            var fallback = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.Personal), "dev_data");
            System.IO.Directory.CreateDirectory(fallback);
            return fallback;
        }

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
