using System.Text.Json.Nodes;
using KuzushiClassifierApp.Models;

namespace KuzushiClassifierApp.Plugin;

/// <summary>
/// Owns the single background preparation job for the plugin process: local
/// model/index loading from already-present files, serialized through
/// <see cref="PluginInference.PrepareAsync"/>. The <c>prepare_assets</c> control
/// action synchronously starts or reports the job and returns a snapshot; clients
/// poll the real state through <c>status.json</c>. The plugin never downloads:
/// declared files are host-managed, so any missing file short-circuits to
/// <see cref="StateNeedsDownload"/> — the host downloads them through its own
/// managed-files surface. <c>allow_download</c> is still accepted for host/UI
/// orchestration compatibility but cannot unlock a plugin-side download that
/// does not exist.
/// </summary>
public sealed class PluginPreparation : IDisposable
{
    public const string StateIdle = "idle";
    public const string StateNeedsDownload = "needs_download";
    public const string StatePreparing = "preparing";
    public const string StateReady = "ready";
    public const string StateFailed = "failed";
    public const string StateCancelled = "cancelled";

    /// <summary>
    /// Point-in-time preparation state surfaced in status.json and as the result of the
    /// prepare/cancel control actions. <see cref="ModelsLoaded"/> and
    /// <see cref="IndexLoaded"/> reflect the live services, never file existence.
    /// </summary>
    public sealed record Snapshot(
        string State,
        string Message,
        double? Fraction,
        bool ModelsLoaded,
        bool IndexLoaded,
        string? Error,
        bool DownloadsPerformed,
        long? BytesDownloaded,
        long? TotalBytes,
        int? ItemsProcessed,
        int? TotalItems)
    {
        public JsonObject ToJson()
        {
            return new JsonObject
            {
                ["state"] = State,
                ["message"] = Message,
                ["fraction"] = Fraction is { } f ? JsonValue.Create(f) : null,
                ["models_loaded"] = ModelsLoaded,
                ["index_loaded"] = IndexLoaded,
                ["error"] = Error,
                ["downloads_performed"] = DownloadsPerformed,
                ["bytes_downloaded"] = BytesDownloaded is { } b ? JsonValue.Create(b) : null,
                ["total_bytes"] = TotalBytes is { } t ? JsonValue.Create(t) : null,
                ["items_processed"] = ItemsProcessed is { } i ? JsonValue.Create(i) : null,
                ["total_items"] = TotalItems is { } ti ? JsonValue.Create(ti) : null,
            };
        }
    }

    private readonly Func<IProgress<AssetPreparationProgress>?, CancellationToken, Task> _preparation;
    private readonly Func<(bool Models, bool Index)> _readinessProbe;
    private readonly PluginAssetProbe _assetProbe;
    private readonly Progress<AssetPreparationProgress> _progress;
    private readonly object _gate = new();
    private Task? _running;
    private CancellationTokenSource? _cancellation;
    private Snapshot _snapshot;

    /// <param name="preparation">Runs the local pipeline (dataset scan, index
    /// build, model load) — no network, no downloads. In production this is bound
    /// through <see cref="PluginInference.PrepareAsync"/> so it serializes against
    /// inference.</param>
    /// <param name="readinessProbe">Non-loading probe of live service state.</param>
    /// <param name="assetProbe">File probe used only to detect missing declared files.</param>
    public PluginPreparation(
        Func<IProgress<AssetPreparationProgress>?, CancellationToken, Task> preparation,
        Func<(bool Models, bool Index)> readinessProbe,
        PluginAssetProbe assetProbe)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        ArgumentNullException.ThrowIfNull(readinessProbe);
        ArgumentNullException.ThrowIfNull(assetProbe);

        _preparation = preparation;
        _readinessProbe = readinessProbe;
        _assetProbe = assetProbe;
        _progress = new Progress<AssetPreparationProgress>(OnProgress);

        // The initial snapshot is a pure probe — no loading side effects.
        var (models, index) = readinessProbe();
        _snapshot = new Snapshot(
            models && index ? StateReady : StateIdle,
            models && index ? "Models and index are already loaded." : "Waiting for preparation.",
            null, models, index, null, false, null, null, null, null);
    }

    /// <summary>The current snapshot with live loaded flags folded in.</summary>
    public Snapshot GetSnapshot()
    {
        lock (_gate)
        {
            var (models, index) = _readinessProbe();
            return _snapshot with { ModelsLoaded = models, IndexLoaded = index };
        }
    }

    /// <summary>
    /// Starts the preparation job (or reports the running/terminal state). Any
    /// missing 'analyze' file returns <see cref="StateNeedsDownload"/> without
    /// invoking the pipeline — the plugin never downloads, so the host must fetch
    /// the declared files first. <paramref name="allowDownload"/> is retained for
    /// contract/orchestration compatibility and does not change that. At most one
    /// job runs; repeated starts return the current snapshot.
    /// </summary>
    public Snapshot Start(bool allowDownload)
    {
        _ = allowDownload; // Contract arg: downloading is host-side, never plugin-side.

        lock (_gate)
        {
            if (_running is { IsCompleted: false })
            {
                return _snapshot;
            }

            var (models, index) = _readinessProbe();
            if (models && index)
            {
                _snapshot = new Snapshot(
                    StateReady, "Models and index are already loaded.", 1,
                    true, true, null, _snapshot.DownloadsPerformed, null, null, null, null);
                return _snapshot;
            }

            if (_assetProbe.MissingFor("analyze") is { Count: > 0 } missing)
            {
                _snapshot = new Snapshot(
                    StateNeedsDownload,
                    "Required files are missing. They are host-managed: the plugin " +
                    "never downloads — fetch them through the host's managed-files " +
                    "flow, then retry. " +
                    $"Missing: {string.Join("; ", missing)}",
                    null, models, index, null, false, null, null, null, null);
                return _snapshot;
            }

            _cancellation?.Dispose();
            _cancellation = new CancellationTokenSource();
            _snapshot = new Snapshot(
                StatePreparing, "Preparation started.", 0,
                models, index, null, false, null, null, null, null);
            CancellationToken token = _cancellation.Token;
            _running = Task.Run(() => RunAsync(token), CancellationToken.None);
            return _snapshot;
        }
    }

    /// <summary>Cancels the running job if any; a later start may retry.</summary>
    public Snapshot Cancel()
    {
        lock (_gate)
        {
            if (_running is { IsCompleted: false })
            {
                _cancellation?.Cancel();
                _snapshot = _snapshot with
                {
                    State = StateCancelled,
                    Message = "Cancelling preparation…",
                };
            }

            return _snapshot;
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _preparation(_progress, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Complete(StateCancelled, "Preparation cancelled.", error: null);
            return;
        }
        catch (Exception exception)
        {
            Complete(StateFailed, $"Preparation failed: {exception.Message}", exception.Message);
            return;
        }

        var (models, index) = _readinessProbe();
        if (models && index)
        {
            Complete(StateReady, "Models and index loaded.", error: null, fraction: 1);
        }
        else
        {
            Complete(StateFailed,
                "Preparation finished but the services did not become ready.",
                $"models_loaded={models} index_loaded={index}");
        }
    }

    private void Complete(string state, string message, string? error, double? fraction = null)
    {
        lock (_gate)
        {
            var (models, index) = _readinessProbe();
            _snapshot = _snapshot with
            {
                State = state,
                Message = message,
                Error = error,
                Fraction = fraction ?? _snapshot.Fraction,
                ModelsLoaded = models,
                IndexLoaded = index,
            };
        }
    }

    private void OnProgress(AssetPreparationProgress progress)
    {
        lock (_gate)
        {
            // downloads_performed is always false: the plugin has no download
            // path at all, so even a delegate reporting a download-named step
            // must not surface "the plugin downloaded" to agents. Download
            // state belongs to the host's managed-files surface.
            _snapshot = _snapshot with
            {
                State = StatePreparing,
                Message = progress.Message,
                Fraction = progress.Fraction ?? _snapshot.Fraction,
                DownloadsPerformed = false,
                BytesDownloaded = progress.BytesDownloaded ?? _snapshot.BytesDownloaded,
                TotalBytes = progress.TotalBytes ?? _snapshot.TotalBytes,
                ItemsProcessed = progress.ItemsProcessed ?? _snapshot.ItemsProcessed,
                TotalItems = progress.TotalItems ?? _snapshot.TotalItems,
            };
        }
    }

    /// <summary>
    /// Cancels and briefly waits for the running job so shared ONNX sessions are never
    /// disposed underneath an in-flight preparation.
    /// </summary>
    public void Dispose()
    {
        Task? running;
        lock (_gate)
        {
            _cancellation?.Cancel();
            running = _running;
        }

        try
        {
            running?.Wait(TimeSpan.FromSeconds(10));
        }
        catch (Exception)
        {
            // Cancellation/fault paths already surfaced through the snapshot.
        }

        lock (_gate)
        {
            _cancellation?.Dispose();
            _cancellation = null;
        }
    }
}
