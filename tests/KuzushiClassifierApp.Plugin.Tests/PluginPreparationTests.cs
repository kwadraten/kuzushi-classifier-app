using System.Text.Json.Nodes;
using KuzushiClassifierApp.Models;
using KuzushiClassifierApp.Plugin;
using Xunit;

namespace KuzushiClassifierApp.Plugin.Tests;

/// <summary>
/// prepare_assets / cancel_preparation lifecycle over fake backends: missing-file
/// gating (needs_download regardless of consent — the plugin never downloads),
/// single-job dedupe, progress snapshots, failure/cancel/retry, version + status
/// shape. No real downloads and no real model files are ever touched.
/// </summary>
public sealed class PluginPreparationTests : IDisposable
{
    private readonly FakeDataRoot _root = FakeDataRoot.Create();

    private PluginPreparation Preparation(
        Func<IProgress<AssetPreparationProgress>?, CancellationToken, Task> pipeline,
        Func<(bool Models, bool Index)>? readiness = null)
    {
        return new PluginPreparation(
            pipeline,
            readiness ?? (() => (false, false)),
            _root.CreateProbe());
    }

    private McpClientConnection Client(PluginPreparation preparation)
    {
        var dispatcher = PluginServices.CreateDispatcher(
            _root.CreateProbe(),
            new FakeClassifierService(),
            new FakeEmbeddingService(),
            new FakeEmbeddingIndexService(),
            preparation);
        return new McpClientConnection(dispatcher);
    }

    private static Task<JsonObject> SendPrepareAsync(
        McpClientConnection client, int id, bool allowDownload, string? requestId = null)
    {
        return client.RequestAsync(McpMessages.Send(id, "prepare_assets",
            new JsonObject { ["allow_download"] = allowDownload }, requestId));
    }

    private static Task<JsonObject> SendCancelAsync(McpClientConnection client, int id)
    {
        return client.RequestAsync(McpMessages.Send(id, "cancel_preparation", new JsonObject()));
    }

    private static JsonObject ResultSnapshot(JsonObject response)
    {
        var result = McpAssertions.AssertToolSuccess(response);
        Assert.Equal("completed", result["state"]!.GetValue<string>());
        return Assert.IsAssignableFrom<JsonObject>(result["result"]);
    }

    [Fact]
    public async Task Status_reports_version_matching_the_manifest_and_idle_preparation()
    {
        using var preparation = Preparation((_, _) => Task.CompletedTask);
        using var client = Client(preparation);

        var status = JsonNode.Parse(
            McpAssertions.AssertToolSuccess(
                await client.RequestAsync(McpMessages.Fetch(1, "status.json")))
                ["content"]!.GetValue<string>())!;

        Assert.Equal("0.2.0", status["version"]!.GetValue<string>());
        var prep = Assert.IsAssignableFrom<JsonObject>(status["preparation"]);
        Assert.Equal("idle", prep["state"]!.GetValue<string>());
        Assert.False(prep["models_loaded"]!.GetValue<bool>());
        Assert.False(prep["index_loaded"]!.GetValue<bool>());
        Assert.False(status["assets"]!["embedding_index"]!["ready"]!.GetValue<bool>());
        Assert.False(status["downloads_performed"]!.GetValue<bool>());
        Assert.False(status["managed_files"]!["enabled"]!.GetValue<bool>());
        // No absolute local paths are ever exposed to agents.
        Assert.Null(status["data_root"]);
        Assert.DoesNotContain(_root.Root, status.ToJsonString());
        await client.ShutdownAsync();
    }

    [Fact]
    public async Task Prepare_without_consent_never_invokes_the_pipeline_on_missing_assets()
    {
        var calls = 0;
        using var preparation = Preparation((_, _) =>
        {
            calls++;
            return Task.CompletedTask;
        });
        using var client = Client(preparation);

        var snapshot = ResultSnapshot(await SendPrepareAsync(client, 1, allowDownload: false));

        Assert.Equal("needs_download", snapshot["state"]!.GetValue<string>());
        Assert.Equal(0, calls);
        Assert.False(preparation.GetSnapshot().DownloadsPerformed);
        await client.ShutdownAsync();
    }

    [Fact]
    public async Task Prepare_with_consent_still_needs_download_when_files_are_missing()
    {
        // allow_download is orchestration metadata only: the plugin never
        // downloads, so consent cannot unlock a plugin-side download — missing
        // declared files are the host's job first.
        var calls = 0;
        using var preparation = Preparation((_, _) =>
        {
            calls++;
            return Task.CompletedTask;
        });
        using var client = Client(preparation);

        var snapshot = ResultSnapshot(await SendPrepareAsync(client, 1, allowDownload: true));

        Assert.Equal("needs_download", snapshot["state"]!.GetValue<string>());
        Assert.Equal(0, calls);
        Assert.False(preparation.GetSnapshot().DownloadsPerformed);
        await client.ShutdownAsync();
    }

    [Fact]
    public async Task Prepare_with_consent_runs_the_pipeline_once_and_reports_progress()
    {
        _root.MakeClassifierReady();
        _root.MakeEmbeddingReady();
        _root.MakeDatasetReady();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var readiness = (false, false);
        using var preparation = new PluginPreparation(
            async (progress, _) =>
            {
                calls++;
                progress?.Report(new AssetPreparationProgress(
                    AssetPreparationStep.LoadingModels, "loading models", 0.4));
                await gate.Task;
                progress?.Report(new AssetPreparationProgress(
                    AssetPreparationStep.Ready, "done", 1));
                readiness = (true, true);
            },
            () => readiness,
            _root.CreateProbe());
        using var client = Client(preparation);

        var started = ResultSnapshot(await SendPrepareAsync(client, 1, allowDownload: true));
        Assert.Equal("preparing", started["state"]!.GetValue<string>());

        // A duplicate start while the job is in flight must not run the pipeline twice.
        var duplicate = ResultSnapshot(await SendPrepareAsync(client, 2, allowDownload: true));
        Assert.Equal("preparing", duplicate["state"]!.GetValue<string>());

        // Progress<T> posts its callback asynchronously — wait until the
        // pipeline's report has landed before polling the live snapshot.
        await WaitForMessageAsync(preparation, "loading models");

        // Polling while the job is blocked still serves a live snapshot.
        var mid = JsonNode.Parse(McpAssertions.AssertToolSuccess(
            await client.RequestAsync(McpMessages.Fetch(3, "status.json")))
            ["content"]!.GetValue<string>())!;
        var midPrep = Assert.IsAssignableFrom<JsonObject>(mid["preparation"]);
        Assert.Equal("preparing", midPrep["state"]!.GetValue<string>());
        Assert.Equal("loading models", midPrep["message"]!.GetValue<string>());
        Assert.False(midPrep["downloads_performed"]!.GetValue<bool>());
        Assert.False(mid["downloads_performed"]!.GetValue<bool>());

        gate.SetResult();
        var ready = await WaitForStateAsync(preparation, "ready");
        Assert.True(ready["models_loaded"]!.GetValue<bool>());
        Assert.True(ready["index_loaded"]!.GetValue<bool>());
        Assert.Equal(1, calls);
        await client.ShutdownAsync();
    }

    [Fact]
    public async Task Cached_complete_root_loads_locally_without_downloading()
    {
        _root.MakeClassifierReady();
        _root.MakeEmbeddingReady();
        _root.MakeDatasetReady();
        var readiness = (false, false);
        var calls = 0;
        using var preparation = new PluginPreparation(
            (progress, _) =>
            {
                calls++;
                readiness = (true, true);
                return Task.CompletedTask;
            },
            () => readiness,
            _root.CreateProbe());
        using var client = Client(preparation);

        var snapshot = ResultSnapshot(await SendPrepareAsync(client, 1, allowDownload: false));
        Assert.Equal("preparing", snapshot["state"]!.GetValue<string>());

        var final = await WaitForStateAsync(preparation, "ready");
        Assert.True(final["models_loaded"]!.GetValue<bool>());
        Assert.True(final["index_loaded"]!.GetValue<bool>());
        Assert.False(final["downloads_performed"]!.GetValue<bool>(),
            "a fully cached root performs no downloads");
        Assert.Equal(1, calls);
        await client.ShutdownAsync();
    }

    [Fact]
    public async Task Failure_surfaces_failed_state_and_never_turns_green()
    {
        _root.MakeClassifierReady();
        _root.MakeEmbeddingReady();
        _root.MakeDatasetReady();
        using var preparation = Preparation((_, _) =>
            throw new InvalidOperationException("simulated failure"));
        using var client = Client(preparation);

        await SendPrepareAsync(client, 1, allowDownload: true);
        var failed = await WaitForStateAsync(preparation, "failed");

        Assert.Contains("simulated failure", failed["error"]!.GetValue<string>());
        Assert.False(failed["models_loaded"]!.GetValue<bool>());
        Assert.False(failed["index_loaded"]!.GetValue<bool>());
        await client.ShutdownAsync();
    }

    [Fact]
    public async Task Cancel_then_retry_starts_a_new_job()
    {
        _root.MakeClassifierReady();
        _root.MakeEmbeddingReady();
        _root.MakeDatasetReady();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var preparation = Preparation(async (_, token) =>
        {
            calls++;
            await gate.Task.WaitAsync(token);
        }, () => (calls >= 2, calls >= 2));
        using var client = Client(preparation);

        await SendPrepareAsync(client, 1, allowDownload: true);
        var cancelling = ResultSnapshot(await SendCancelAsync(client, 2));
        Assert.Equal("cancelled", cancelling["state"]!.GetValue<string>());
        gate.SetResult();
        await WaitForStateAsync(preparation, "cancelled");

        var retry = ResultSnapshot(await SendPrepareAsync(client, 3, allowDownload: true));
        Assert.Equal("preparing", retry["state"]!.GetValue<string>());
        await WaitForStateAsync(preparation, "ready");
        Assert.Equal(2, calls);
        await client.ShutdownAsync();
    }

    [Fact]
    public async Task Arg_validation_rejects_extra_and_missing_properties()
    {
        using var preparation = Preparation((_, _) => Task.CompletedTask);
        using var client = Client(preparation);

        var extra = await client.RequestAsync(McpMessages.Send(10, "prepare_assets",
            new JsonObject { ["allow_download"] = true, ["bogus"] = 1 }));
        McpAssertions.AssertToolError(extra);

        var missing = await client.RequestAsync(McpMessages.Send(11, "prepare_assets",
            new JsonObject()));
        McpAssertions.AssertToolError(missing);
        await client.ShutdownAsync();
    }

    [Fact]
    public void Snapshot_serializes_the_schema_shape()
    {
        var snapshot = new PluginPreparation.Snapshot(
            "preparing", "msg", 0.5, true, false, null, true, 10, 20, 1, 2).ToJson();

        Assert.Equal("preparing", snapshot["state"]!.GetValue<string>());
        Assert.Equal(0.5, snapshot["fraction"]!.GetValue<double>());
        Assert.True(snapshot["models_loaded"]!.GetValue<bool>());
        Assert.False(snapshot["index_loaded"]!.GetValue<bool>());
        Assert.Equal(10L, snapshot["bytes_downloaded"]!.GetValue<long>());
        Assert.Equal(20L, snapshot["total_bytes"]!.GetValue<long>());
    }

    private static async Task<JsonObject> WaitForStateAsync(
        PluginPreparation preparation, string state)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var snapshot = preparation.GetSnapshot();
            if (snapshot.State == state)
            {
                return snapshot.ToJson();
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"preparation never reached state '{state}'.");
    }

    private static async Task WaitForMessageAsync(
        PluginPreparation preparation, string message)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (preparation.GetSnapshot().Message == message)
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"preparation never reported message '{message}'.");
    }

    public void Dispose()
    {
        _root.Dispose();
    }
}
