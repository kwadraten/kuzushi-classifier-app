namespace KuzushiClassifierApp.Platform;

public sealed class AppDataPathProvider : IAppDataPathProvider
{
    private const string DevDataRelativePath = ".agents/dev_data";
    private const string AppName = "KuzushiClassifierApp";

    private readonly string _appDataRoot;
    private readonly bool _isDevelopment;

    public AppDataPathProvider(string? startDirectory = null)
    {
        startDirectory ??= AppContext.BaseDirectory;

        var devPath = FindDevDataDirectory(startDirectory);
        if (devPath is not null)
        {
            _appDataRoot = devPath;
            _isDevelopment = true;
        }
        else
        {
            _appDataRoot = Path.GetFullPath(startDirectory);
            _isDevelopment = false;
        }
    }

    public bool IsDevelopment => _isDevelopment;

    public string GetAppDataDirectory()
    {
        string directory;
        if (_isDevelopment)
        {
            directory = Path.Combine(_appDataRoot, "runtime");
        }
        else
        {
            directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                AppName,
                "runtime");
        }

        Directory.CreateDirectory(directory);
        return directory;
    }

    public string GetModelCacheDirectory()
    {
        return _isDevelopment
            ? Path.Combine(_appDataRoot, "models", "shikiji")
            : Path.Combine(_appDataRoot, "models");
    }

    public string GetDatasetCacheDirectory()
    {
        return _isDevelopment
            ? Path.Combine(_appDataRoot, "datasets", "hi-utokyo-kuzushi")
            : Path.Combine(_appDataRoot, "datasets");
    }

    private static string? FindDevDataDirectory(string startDirectory)
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

        return null;
    }
}
