using System.Text.Json.Nodes;
using KuzushiClassifierApp.Platform;
using KuzushiClassifierApp.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace KuzushiClassifierApp.Plugin.Tests;

/// <summary>
/// Evidence-level separation: these tests run the real ONNX classifier and
/// the real Parquet-backed similarity path against the cached
/// .agents/dev_data assets. They never download: missing assets simply skip
/// the assertions (the fake-backend suites cover the contract exhaustively).
/// </summary>
public sealed class RealModelTests
{
    private readonly ITestOutputHelper _output;

    public RealModelTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    [Trait("Backend", "real")]
    public async Task Classify_WithCachedClassifierModel_ReturnsCandidates()
    {
        var context = CreateRealContext();
        if (context is null)
        {
            _output.WriteLine("SKIP: cached classifier model not present under dev_data.");
            return;
        }

        using var client = new McpClientConnection(context.Dispatcher);

        var response = await client.RequestAsync(
            McpMessages.Send(1, "classify", McpMessages.ImageArgs(topK: 10)));
        var structured = McpAssertions.AssertToolSuccess(response);
        var result = Assert.IsAssignableFrom<JsonObject>(structured["result"]);

        var candidates = Assert.IsAssignableFrom<JsonArray>(result["candidates"]);
        Assert.True(candidates.Count is > 0 and <= 10);
        foreach (var candidate in candidates)
        {
            var confidence = candidate!["confidence"]!.GetValue<float>();
            Assert.True(float.IsFinite(confidence));
            Assert.InRange(confidence, 0f, 1f);
            Assert.NotEmpty(candidate["label"]!.GetValue<string>());
        }

        _output.WriteLine(
            $"real classify top: {candidates[0]!["label"]} @ {candidates[0]!["confidence"]}");

        // The persisted result resource must round-trip through fetch.
        var fetch = await client.RequestAsync(
            McpMessages.Fetch(2, result["resource_uri"]!.GetValue<string>()));
        var fetched = JsonNode.Parse(
            McpAssertions.AssertToolSuccess(fetch)["content"]!.GetValue<string>())!;
        Assert.Equal(result["result_id"]!.GetValue<string>(),
            fetched["result_id"]!.GetValue<string>());

        await client.ShutdownAsync();
        context.Dispose();
    }

    [Fact]
    [Trait("Backend", "real")]
    public async Task Similar_WithCachedDataset_ReturnsSimilarImages()
    {
        var context = CreateRealContext();
        if (context is null || !context.Report.SimilarReady)
        {
            _output.WriteLine("SKIP: cached embedding model/dataset not present under dev_data.");
            context?.Dispose();
            return;
        }

        using var client = new McpClientConnection(context.Dispatcher);

        var response = await client.RequestAsync(
            McpMessages.Send(3, "similar", McpMessages.ImageArgs(topK: 5)));
        var result = Assert.IsAssignableFrom<JsonObject>(
            McpAssertions.AssertToolSuccess(response)["result"]);

        var images = Assert.IsAssignableFrom<JsonArray>(result["similar_images"]);
        Assert.True(images.Count is > 0 and <= 5);
        foreach (var image in images)
        {
            Assert.NotEmpty(image!["id"]!.GetValue<string>());
            Assert.NotEmpty(image["label"]!.GetValue<string>());
            Assert.True(float.IsFinite(image["similarity"]!.GetValue<float>()));
        }

        _output.WriteLine(
            $"real similar top: {images[0]!["id"]} '{images[0]!["label"]}' @ {images[0]!["similarity"]}");

        await client.ShutdownAsync();
        context.Dispose();
    }

    private static RealContext? CreateRealContext()
    {
        var appData = new AppDataPathProvider();
        var paths = new CachedModelPathProvider(appData);
        var probe = new PluginAssetProbe(paths, appData);
        var report = probe.Probe();

        if (!report.ClassifyReady)
        {
            return null;
        }

        var classifier = new OnnxImageClassifierService(paths);
        var embedding = new OnnxImageEmbeddingService(paths);
        var cacheService = new ParquetFileEmbeddingCacheService(
            appData, NullLogger<ParquetFileEmbeddingCacheService>.Instance);
        var index = new ParquetStreamingEmbeddingIndexService(cacheService);

        var dispatcher = PluginServices.CreateDispatcher(probe, classifier, embedding, index);
        return new RealContext(dispatcher, report, classifier, embedding);
    }

    private sealed class RealContext : IDisposable
    {
        public RealContext(
            PluginDispatcher dispatcher,
            PluginAssetReport report,
            params IDisposable[] disposables)
        {
            Dispatcher = dispatcher;
            Report = report;
            _disposables = disposables;
        }

        private readonly IDisposable[] _disposables;

        public PluginDispatcher Dispatcher { get; }

        public PluginAssetReport Report { get; }

        public void Dispose()
        {
            foreach (var disposable in _disposables)
            {
                disposable.Dispose();
            }
        }
    }
}
