namespace KuzushiClassifierApp.Platform;

public interface IAppDataPathProvider
{
    string GetAppDataDirectory();

    string GetModelCacheDirectory();

    string GetDatasetCacheDirectory();
}
