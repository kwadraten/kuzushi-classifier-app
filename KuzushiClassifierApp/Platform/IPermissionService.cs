namespace KuzushiClassifierApp.Platform;

public interface IPermissionService
{
    Task<bool> EnsureStorageAccessAsync(CancellationToken cancellationToken = default);
}
