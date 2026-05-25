using KuzushiClassifierApp.Models;

namespace KuzushiClassifierApp.Services;

public interface IModelAssetService
{
    Task<ModelAssetStatus> PrepareAsync(
        IProgress<AssetPreparationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
