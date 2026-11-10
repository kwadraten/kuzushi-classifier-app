using System.Text.Json.Nodes;
using KuzushiClassifierApp.Plugin;
using Xunit;

namespace KuzushiClassifierApp.Plugin.Tests;

/// <summary>
/// Host-managed files: launch-arg parsing, declared-path resolution
/// (explicit mapping → managed root → app-data fallback), probe readiness on
/// managed locations, and the agent-facing contract that status.json never
/// leaks absolute local paths. Everything is on isolated temp directories —
/// nothing is downloaded and no real model/dataset is touched.
/// </summary>
public sealed class ManagedFilesTests : IDisposable
{
    private readonly FakeDataRoot _root = FakeDataRoot.Create();
    private readonly FakeManagedStore _store = FakeManagedStore.Create();

    // ------------------------------------------------------- launch args

    [Fact]
    public void Parse_NoArgs_PreservesAppDataFallback()
    {
        var options = PluginLaunchOptions.Parse(Array.Empty<string>())!;

        Assert.Null(options.DataRoot);
        Assert.NotNull(options.ManagedFiles);
        Assert.False(options.ManagedFiles.Configured);
        Assert.Null(options.ManagedFiles.Resolve(PluginManagedFiles.ClassifierModel));
    }

    [Fact]
    public void Parse_DataRoot_And_ManagedArgs()
    {
        var options = PluginLaunchOptions.Parse(new[]
        {
            "--data-root", "d:\\data",
            "--patchouli-managed-files", "d:\\managed\\root",
            "--patchouli-managed-file", "models/a.onnx|d:\\elsewhere\\a.onnx",
            "--patchouli-managed-file", "datasets/x.parquet|/mnt/x.parquet",
        })!;

        Assert.Equal("d:\\data", options.DataRoot);
        Assert.True(options.ManagedFiles.Configured);
        Assert.Equal("d:\\managed\\root", options.ManagedFiles.Root);
        Assert.Equal("d:\\elsewhere\\a.onnx", options.ManagedFiles.Files["models/a.onnx"]);
        Assert.Equal("/mnt/x.parquet", options.ManagedFiles.Files["datasets/x.parquet"]);
    }

    [Fact]
    public void Parse_ManagedFile_NormalizesRelativeKey()
    {
        var options = PluginLaunchOptions.Parse(new[]
        {
            "--patchouli-managed-file", "\\models\\a.onnx|/abs/a.onnx",
        })!;

        Assert.Equal("/abs/a.onnx", options.ManagedFiles.Files["models/a.onnx"]);
    }

    [Fact]
    public void Parse_Help_ReturnsNull()
    {
        Assert.Null(PluginLaunchOptions.Parse(new[] { "--help" }));
        Assert.Null(PluginLaunchOptions.Parse(new[] { "-h" }));
    }

    [Theory]
    [InlineData("--bogus")]
    [InlineData("--data-root")]                                  // missing value
    [InlineData("--patchouli-managed-files")]                    // missing value
    [InlineData("--patchouli-managed-file")]                     // missing value
    [InlineData("--patchouli-managed-file", "no-separator")]
    [InlineData("--patchouli-managed-file", "|/abs/only")]
    [InlineData("--patchouli-managed-file", "rel/only|")]
    [InlineData("--patchouli-managed-file", "models//a.onnx|/abs/a.onnx")]
    public void Parse_Rejects_MalformedArgs(params string[] args)
    {
        Assert.Throws<ArgumentException>(() => PluginLaunchOptions.Parse(args));
    }

    // ------------------------------------------------- path resolution

    [Fact]
    public void Resolve_ManagedRoot_Layout()
    {
        var managed = new PluginManagedFiles("d:\\managed", null);

        Assert.Equal(
            Path.Combine("d:\\managed", "models", "supervised_pretrain_checkpoint.onnx"),
            managed.Resolve(PluginManagedFiles.ClassifierModel));
        Assert.Equal(
            Path.Combine("d:\\managed", "datasets", "image-embeddings.with-webp.parquet"),
            managed.Resolve(PluginManagedFiles.DatasetParquet));
        Assert.True(managed.IsManaged(PluginManagedFiles.ClassifierMetadata));
    }

    [Fact]
    public void Resolve_ExplicitMapping_WinsOverRoot()
    {
        var managed = new PluginManagedFiles("d:\\managed", new Dictionary<string, string>
        {
            [PluginManagedFiles.ClassifierModel] = "d:\\shared\\weights.onnx",
        });

        Assert.Equal("d:\\shared\\weights.onnx", managed.Resolve(PluginManagedFiles.ClassifierModel));
        Assert.Equal(
            Path.Combine("d:\\managed", "models", "supervised_pretrain_checkpoint.embedding.onnx"),
            managed.Resolve(PluginManagedFiles.EmbeddingModel));
    }

    [Fact]
    public void Resolve_ReturnsNull_WithoutManagedArgs()
    {
        Assert.Null(PluginManagedFiles.None.Resolve(PluginManagedFiles.ClassifierModel));
        Assert.False(PluginManagedFiles.None.IsManaged(PluginManagedFiles.DatasetParquet));
    }

    [Fact]
    public void PathProvider_PrefersManagedPaths_ThenAppData()
    {
        var managedProvider = new CachedModelPathProvider(_root, _store.Files);
        Assert.Equal(_store.AbsolutePathFor(PluginManagedFiles.ClassifierModel),
            managedProvider.ClassifierModelPath);
        Assert.Equal(_store.AbsolutePathFor(PluginManagedFiles.ClassifierMetadata),
            managedProvider.ClassifierMetadataPath);
        Assert.Equal(_store.AbsolutePathFor(PluginManagedFiles.EmbeddingModel),
            managedProvider.EmbeddingModelPath);
        Assert.Equal(_store.AbsolutePathFor(PluginManagedFiles.EmbeddingMetadata),
            managedProvider.EmbeddingMetadataPath);
        Assert.Equal(_store.AbsolutePathFor(PluginManagedFiles.DatasetParquet),
            managedProvider.DatasetParquetPath);
        Assert.True(managedProvider.ManagedFilesConfigured);

        var fallbackProvider = new CachedModelPathProvider(_root);
        Assert.Equal(Path.Combine(_root.ModelsDirectory, "supervised_pretrain_checkpoint.onnx"),
            fallbackProvider.ClassifierModelPath);
        Assert.Equal(Path.Combine(_root.ModelsDirectory, "supervised_pretrain_checkpoint.metadata.json"),
            fallbackProvider.ClassifierMetadataPath);
        Assert.Equal(Path.Combine(_root.DatasetsDirectory, "image-embeddings.with-webp.parquet"),
            fallbackProvider.DatasetParquetPath);
        Assert.False(fallbackProvider.ManagedFilesConfigured);
    }

    // ------------------------------------------------------------ probe

    [Fact]
    public void Probe_ReportsManagedFiles_ByDeclaredRelativePath()
    {
        _store.TouchAllDeclared();
        var probe = _root.CreateProbe(_store.Files);

        var report = probe.Probe();

        Assert.True(report.AnalyzeReady);
        Assert.Empty(probe.MissingFor("analyze", report));
        foreach (var file in report.Classifier.Files
            .Concat(report.Embedding.Files)
            .Concat(report.Dataset.Files))
        {
            Assert.True(file.Managed);
            Assert.True(file.Exists);
            Assert.Contains(file.RelativePath, PluginManagedFiles.DeclaredPaths);
            Assert.Equal(_store.AbsolutePathFor(file.RelativePath), file.AbsolutePath);
            Assert.DoesNotContain(_store.Root, file.RelativePath);
        }
    }

    [Fact]
    public void Probe_ReportsMissing_DeclaredRelativePaths()
    {
        _store.Touch(PluginManagedFiles.ClassifierModel);
        var probe = _root.CreateProbe(_store.Files);

        var report = probe.Probe();
        var missing = probe.MissingFor("analyze", report);

        Assert.False(report.AnalyzeReady);
        Assert.Contains(missing, entry => entry.Contains(PluginManagedFiles.ClassifierMetadata));
        Assert.Contains(missing, entry => entry.Contains(PluginManagedFiles.EmbeddingModel));
        Assert.Contains(missing, entry => entry.Contains(PluginManagedFiles.DatasetParquet));
        // Declared relative paths only — resolved locations stay internal.
        Assert.All(missing, entry => Assert.DoesNotContain(_store.Root, entry));
    }

    // ------------------------------------------------- status document

    [Fact]
    public async Task Status_NeverExposesAbsolutePaths()
    {
        _store.TouchAllDeclared();
        var dispatcher = PluginServices.CreateDispatcher(
            _root.CreateProbe(_store.Files),
            new FakeClassifierService(),
            new FakeEmbeddingService(),
            new FakeEmbeddingIndexService());
        using var client = new McpClientConnection(dispatcher);

        var content = McpAssertions.AssertToolSuccess(
            await client.RequestAsync(McpMessages.Fetch(1, "status.json")));
        var raw = content["content"]!.GetValue<string>();
        var status = JsonNode.Parse(raw)!;

        // Agent-facing output: declared relative paths, no absolute locations.
        Assert.DoesNotContain(_store.Root, raw);
        Assert.DoesNotContain(_root.Root, raw);
        Assert.Null(status["data_root"]);

        Assert.True(status["managed_files"]!["enabled"]!.GetValue<bool>());
        Assert.Empty(status["missing_assets"]!.AsArray());

        var files = status["assets"]!["classifier_model"]!["files"]!.AsArray();
        Assert.Equal(2, files.Count);
        Assert.Equal(PluginManagedFiles.ClassifierModel, files[0]!["path"]!.GetValue<string>());
        Assert.True(files[0]!["exists"]!.GetValue<bool>());
        Assert.True(files[0]!["managed"]!.GetValue<bool>());
        Assert.Equal(PluginManagedFiles.ClassifierMetadata, files[1]!["path"]!.GetValue<string>());

        Assert.True(status["assets"]!["dataset"]!["ready"]!.GetValue<bool>());
        Assert.Equal(PluginManagedFiles.DatasetParquet,
            status["assets"]!["dataset"]!["files"]![0]!["path"]!.GetValue<string>());

        Assert.True(status["capabilities"]!["analyze"]!["ready"]!.GetValue<bool>());
        await client.ShutdownAsync();
    }

    [Fact]
    public async Task Status_MissingFiles_ListDeclaredRelativePaths()
    {
        // Only the classifier model pair is present; the rest is missing.
        _store.Touch(PluginManagedFiles.ClassifierModel);
        _store.Touch(PluginManagedFiles.ClassifierMetadata);
        var dispatcher = PluginServices.CreateDispatcher(
            _root.CreateProbe(_store.Files),
            new FakeClassifierService(),
            new FakeEmbeddingService(),
            new FakeEmbeddingIndexService());
        using var client = new McpClientConnection(dispatcher);

        var content = McpAssertions.AssertToolSuccess(
            await client.RequestAsync(McpMessages.Fetch(1, "status.json")));
        var raw = content["content"]!.GetValue<string>();
        var status = JsonNode.Parse(raw)!;

        var missing = status["missing_assets"]!.AsArray()
            .Select(node => node!.GetValue<string>())
            .ToArray();
        Assert.Equal(new[]
        {
            PluginManagedFiles.EmbeddingModel,
            PluginManagedFiles.EmbeddingMetadata,
            PluginManagedFiles.DatasetParquet,
        }, missing);
        Assert.DoesNotContain(_store.Root, raw);
        Assert.False(status["capabilities"]!["analyze"]!["ready"]!.GetValue<bool>());
        Assert.True(status["capabilities"]!["classify"]!["ready"]!.GetValue<bool>());
        await client.ShutdownAsync();
    }

    // --------------------------------------- prepare: missing vs loaded

    [Fact]
    public async Task Prepare_ReturnsNeedsDownload_UntilHostSuppliesFiles_ThenLoads()
    {
        var calls = 0;
        var readiness = (false, false);
        using var preparation = new PluginPreparation(
            (_, _) =>
            {
                calls++;
                readiness = (true, true);
                return Task.CompletedTask;
            },
            () => readiness,
            _root.CreateProbe(_store.Files));
        var dispatcher = PluginServices.CreateDispatcher(
            _root.CreateProbe(_store.Files),
            new FakeClassifierService(),
            new FakeEmbeddingService(),
            new FakeEmbeddingIndexService(),
            preparation);
        using var client = new McpClientConnection(dispatcher);

        // Nothing downloaded by the host yet: even with consent the plugin
        // can only report needs_download — it never fetches the files itself.
        var missing = await client.RequestAsync(McpMessages.Send(1, "prepare_assets",
            new JsonObject { ["allow_download"] = true }));
        var missingResult = McpAssertions.AssertToolSuccess(missing)["result"];
        Assert.Equal("needs_download", missingResult!["state"]!.GetValue<string>());
        Assert.Equal(0, calls);

        // The host's managed-file download has completed → the same action now
        // performs the local load pipeline.
        _store.TouchAllDeclared();
        var started = await client.RequestAsync(McpMessages.Send(2, "prepare_assets",
            new JsonObject { ["allow_download"] = false }));
        var startedResult = McpAssertions.AssertToolSuccess(started)["result"];
        Assert.Equal("preparing", startedResult!["state"]!.GetValue<string>());

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (preparation.GetSnapshot().State != PluginPreparation.StateReady
            && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        var snapshot = preparation.GetSnapshot().ToJson();
        Assert.Equal("ready", snapshot["state"]!.GetValue<string>());
        Assert.True(snapshot["models_loaded"]!.GetValue<bool>());
        Assert.True(snapshot["index_loaded"]!.GetValue<bool>());
        Assert.False(snapshot["downloads_performed"]!.GetValue<bool>());
        Assert.Equal(1, calls);
        await client.ShutdownAsync();
    }

    public void Dispose()
    {
        _store.Dispose();
        _root.Dispose();
    }
}
