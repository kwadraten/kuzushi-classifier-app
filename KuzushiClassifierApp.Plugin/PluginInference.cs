using System.Text.Json.Nodes;
using KuzushiClassifierApp.Controllers;
using KuzushiClassifierApp.Models;
using KuzushiClassifierApp.Services;

namespace KuzushiClassifierApp.Plugin;

/// <summary>
/// Inference adapter in front of the existing controllers/services.
/// OnnxImageClassifierService/OnnxImageEmbeddingService lazily initialize
/// their sessions without locking, so every model touch is serialized
/// through a single gate. Readiness is probed from the file system before
/// any work; missing declared files fail UNAVAILABLE — the plugin never
/// downloads: files are host-managed and fetched by the host itself.
/// </summary>
public sealed class PluginInference
{
    private readonly PluginAssetProbe _assetProbe;
    private readonly ClassificationController _classificationController;
    private readonly SimilaritySearchController _similaritySearchController;
    private readonly IEmbeddingIndexService _embeddingIndexService;
    private readonly SemaphoreSlim _inferenceGate = new(1, 1);
    private bool _embeddingIndexBuilt;

    public PluginInference(
        PluginAssetProbe assetProbe,
        ClassificationController classificationController,
        SimilaritySearchController similaritySearchController,
        IEmbeddingIndexService embeddingIndexService)
    {
        _assetProbe = assetProbe;
        _classificationController = classificationController;
        _similaritySearchController = similaritySearchController;
        _embeddingIndexService = embeddingIndexService;
    }

    /// <summary>Whether the streaming similarity index is actually built.</summary>
    public bool IndexReady => _embeddingIndexService.IsReady;

    /// <summary>
    /// Runs an asset/model preparation delegate under the same gate as inference so model
    /// loading and index building can never race a running classify/similar/analyze call.
    /// This method only serializes; the delegate is the local-load pipeline and never
    /// downloads — declared files are host-managed.
    /// </summary>
    public async Task PrepareAsync(
        Func<IProgress<AssetPreparationProgress>?, CancellationToken, Task> preparation,
        IProgress<AssetPreparationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preparation);

        await _inferenceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await preparation(progress, cancellationToken).ConfigureAwait(false);
            if (_embeddingIndexService.IsReady)
            {
                _embeddingIndexBuilt = true;
            }
        }
        finally
        {
            _inferenceGate.Release();
        }
    }

    public async Task<JsonObject> RunAsync(
        string action,
        KuzushiImage image,
        int topK,
        CancellationToken cancellationToken)
    {
        var missing = _assetProbe.MissingFor(action);
        if (missing.Count > 0)
        {
            throw new ToolErrorException(
                ToolErrorCodes.Unavailable,
                $"Cannot run '{action}': required assets are missing. " +
                $"Missing: {string.Join("; ", missing)}. " +
                "These are declared host-managed files — download them through the " +
                "host's managed-files flow first (the plugin never downloads), " +
                "then run 'prepare_assets' to load them.");
        }

        await _inferenceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return action switch
            {
                "classify" => await ClassifyCoreAsync(image, topK, cancellationToken)
                    .ConfigureAwait(false),
                "similar" => await SimilarCoreAsync(image, topK, cancellationToken)
                    .ConfigureAwait(false),
                "analyze" => await AnalyzeCoreAsync(image, topK, cancellationToken)
                    .ConfigureAwait(false),
                _ => throw new ToolErrorException(
                    ToolErrorCodes.UnsupportedOperation,
                    $"Unsupported action '{action}'."),
            };
        }
        catch (ToolErrorException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (FileNotFoundException ex)
        {
            throw new ToolErrorException(
                ToolErrorCodes.Unavailable,
                $"Cached asset became unavailable: {ex.Message}",
                ex);
        }
        catch (Exception ex)
        {
            throw new ToolErrorException(
                ToolErrorCodes.Unavailable,
                $"Inference failed: {ex.Message}",
                ex);
        }
        finally
        {
            _inferenceGate.Release();
        }
    }

    private async Task<JsonObject> ClassifyCoreAsync(
        KuzushiImage image,
        int topK,
        CancellationToken cancellationToken)
    {
        var prediction = await _classificationController
            .ClassifyAsync(image, topK, cancellationToken)
            .ConfigureAwait(false);

        return BuildResultPayload(prediction, null);
    }

    private async Task<JsonObject> SimilarCoreAsync(
        KuzushiImage image,
        int topK,
        CancellationToken cancellationToken)
    {
        await EnsureEmbeddingIndexAsync(cancellationToken).ConfigureAwait(false);

        var similar = await _similaritySearchController
            .FindSimilarAsync(image, topK, cancellationToken)
            .ConfigureAwait(false);

        return BuildResultPayload(null, similar);
    }

    private async Task<JsonObject> AnalyzeCoreAsync(
        KuzushiImage image,
        int topK,
        CancellationToken cancellationToken)
    {
        await EnsureEmbeddingIndexAsync(cancellationToken).ConfigureAwait(false);

        var prediction = await _classificationController
            .ClassifyAsync(image, topK, cancellationToken)
            .ConfigureAwait(false);

        var similar = await _similaritySearchController
            .FindSimilarAsync(image, topK, cancellationToken)
            .ConfigureAwait(false);

        return BuildResultPayload(prediction, similar);
    }

    /// <summary>
    /// Initializes the streaming index from the already-cached embedding
    /// Parquet only. This is metadata/readiness loading; it never calls
    /// StartupController.PrepareAsync and never downloads.
    /// </summary>
    private async Task EnsureEmbeddingIndexAsync(CancellationToken cancellationToken)
    {
        if (_embeddingIndexBuilt && _embeddingIndexService.IsReady)
        {
            return;
        }

        try
        {
            await _embeddingIndexService
                .BuildAsync(Enumerable.Empty<DatasetImageEmbedding>(), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            throw new ToolErrorException(
                ToolErrorCodes.Unavailable,
                "Embedding index could not be initialized from the cached dataset: " +
                ex.Message,
                ex);
        }

        _embeddingIndexBuilt = true;
    }

    private static JsonObject BuildResultPayload(
        KuzushiPrediction? prediction,
        IReadOnlyList<SimilarImageResult>? similarImages)
    {
        var candidates = new JsonArray();
        if (prediction is not null)
        {
            foreach (var candidate in prediction.Candidates)
            {
                if (!float.IsFinite(candidate.Confidence))
                {
                    continue;
                }

                candidates.Add(new JsonObject
                {
                    ["label"] = candidate.Label,
                    ["confidence"] = candidate.Confidence,
                });
            }
        }

        var similar = new JsonArray();
        if (similarImages is not null)
        {
            foreach (var result in similarImages)
            {
                if (!float.IsFinite(result.Similarity))
                {
                    continue;
                }

                similar.Add(new JsonObject
                {
                    ["id"] = result.Image.Id,
                    ["label"] = result.Image.Label,
                    ["similarity"] = result.Similarity,
                });
            }
        }

        return new JsonObject
        {
            ["candidates"] = candidates,
            ["similar_images"] = similar,
        };
    }
}
