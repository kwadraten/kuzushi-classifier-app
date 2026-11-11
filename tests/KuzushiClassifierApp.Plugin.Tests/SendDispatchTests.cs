using System.Text.Json.Nodes;
using KuzushiClassifierApp.Models;
using KuzushiClassifierApp.Services;
using SkiaSharp;
using Xunit;

namespace KuzushiClassifierApp.Plugin.Tests;

/// <summary>
/// send dispatch: arg validation, per-action readiness gating, result shape,
/// results browsing/fetch, request_id dedupe/conflict, cancellation and
/// concurrency — all with a fake backend on a temp data root.
/// </summary>
public sealed class SendDispatchTests : IDisposable
{
    private readonly FakeDataRoot _root = FakeDataRoot.Create();
    private readonly FakeClassifierService _classifier = new();
    private readonly FakeEmbeddingIndexService _index = new();
    private readonly McpClientConnection _client;

    public SendDispatchTests()
    {
        _root.MakeClassifierReady();
        _root.MakeEmbeddingReady();
        _root.MakeDatasetReady();
        var dispatcher = PluginServices.CreateDispatcher(
            _root.CreateProbe(),
            _classifier,
            new FakeEmbeddingService(),
            _index,
            imageLibrary: new FakeImageLibraryService());
        _client = new McpClientConnection(dispatcher);
    }

    [Fact]
    public async Task Classify_ReturnsResultAndStoresFetchableResource()
    {
        var response = await _client.RequestAsync(
            McpMessages.Send(1, "classify", McpMessages.ImageArgs(topK: 5)));

        var result = Assert.IsAssignableFrom<JsonObject>(
            McpAssertions.AssertToolSuccess(response)["result"]);
        Assert.Equal("completed",
            McpAssertions.AssertToolSuccess(response)["state"]!.GetValue<string>());
        Assert.Equal("classify", result["action"]!.GetValue<string>());

        var resultId = result["result_id"]!.GetValue<string>();
        var resourceUri = result["resource_uri"]!.GetValue<string>();
        Assert.Equal($"results/{resultId}.json", resourceUri);

        var candidates = Assert.IsAssignableFrom<JsonArray>(result["candidates"]);
        Assert.Equal(2, candidates.Count);
        Assert.Equal("あ", candidates[0]!["label"]!.GetValue<string>());
        Assert.Empty(Assert.IsAssignableFrom<JsonArray>(result["similar_images"]));

        // The result is browseable and fetchable through the declared nodes.
        var find = await _client.RequestAsync(McpMessages.Find(2, scope: "results/"));
        var entries = Assert.IsAssignableFrom<JsonArray>(
            McpAssertions.AssertToolSuccess(find)["entries"]);
        Assert.Contains(entries, e => e!["uri"]!.GetValue<string>() == resourceUri);

        var fetch = await _client.RequestAsync(McpMessages.Fetch(3, resourceUri));
        var fetched = JsonNode.Parse(
            McpAssertions.AssertToolSuccess(fetch)["content"]!.GetValue<string>())!;
        Assert.Equal(resultId, fetched["result_id"]!.GetValue<string>());
        await _client.ShutdownAsync();
    }

    [Fact]
    public async Task Find_Results_FiltersByActionAndId()
    {
        await _client.RequestAsync(McpMessages.Send(1, "classify", McpMessages.ImageArgs()));
        await _client.RequestAsync(McpMessages.Send(2, "similar", McpMessages.ImageArgs()));

        var filtered = await _client.RequestAsync(
            McpMessages.Find(3, scope: "results/", query: "similar"));
        var entries = Assert.IsAssignableFrom<JsonArray>(
            McpAssertions.AssertToolSuccess(filtered)["entries"]);
        Assert.Single(entries);
        Assert.Equal("similar", entries[0]!["title"]!.GetValue<string>().Split(' ')[0]);
        await _client.ShutdownAsync();
    }

    [Fact]
    public async Task Similar_ReturnsSimilarImagesOnly()
    {
        var response = await _client.RequestAsync(
            McpMessages.Send(4, "similar", McpMessages.ImageArgs()));
        var result = Assert.IsAssignableFrom<JsonObject>(
            McpAssertions.AssertToolSuccess(response)["result"]);

        Assert.Empty(Assert.IsAssignableFrom<JsonArray>(result["candidates"]));
        var images = Assert.IsAssignableFrom<JsonArray>(result["similar_images"]);
        Assert.Equal("img-1", images[0]!["id"]!.GetValue<string>());
        Assert.Equal("あ", images[0]!["label"]!.GetValue<string>());
        Assert.True(images[0]!["similarity"]!.GetValue<float>() is > -1f and <= 1f);

        // Matched images travel inside the result so hosts can render them.
        var encoded = images[0]!["image_base64"]!.GetValue<string>();
        Assert.NotEmpty(encoded);
        Assert.NotEmpty(Convert.FromBase64String(encoded));
        Assert.Equal("image/webp", images[0]!["media_type"]!.GetValue<string>());

        Assert.True(_index.BuildCalled);
        await _client.ShutdownAsync();
    }

    [Fact]
    public async Task Analyze_FillsBothSides()
    {
        var response = await _client.RequestAsync(
            McpMessages.Send(5, "analyze", McpMessages.ImageArgs()));
        var result = Assert.IsAssignableFrom<JsonObject>(
            McpAssertions.AssertToolSuccess(response)["result"]);
        Assert.NotEmpty(Assert.IsAssignableFrom<JsonArray>(result["candidates"]));
        Assert.NotEmpty(Assert.IsAssignableFrom<JsonArray>(result["similar_images"]));
        await _client.ShutdownAsync();
    }

    [Fact]
    public async Task Send_UnknownActionAndEndpoint_UnsupportedOperation()
    {
        Assert.Equal("UNSUPPORTED_OPERATION",
            McpAssertions.AssertToolError(await _client.RequestAsync(
                McpMessages.Send(6, "download", McpMessages.ImageArgs()))));

        Assert.Equal("UNSUPPORTED_OPERATION",
            McpAssertions.AssertToolError(await _client.RequestAsync(
                McpMessages.Send(7, "classify", McpMessages.ImageArgs(), endpoint: "files"))));
        await _client.ShutdownAsync();
    }

    [Fact]
    public async Task Send_ForeignTarget_IsRejected()
    {
        Assert.Equal("INVALID_ARGUMENT",
            McpAssertions.AssertToolError(await _client.RequestAsync(
                McpMessages.Send(8, "classify", McpMessages.ImageArgs(),
                    target: "patchouli://plugin/other-plugin/"))));
        await _client.ShutdownAsync();
    }

    [Theory]
    [InlineData("image/bmp")]
    [InlineData("text/plain")]
    public async Task Send_RejectsUnsupportedMediaType(string mediaType)
    {
        Assert.Equal("INVALID_ARGUMENT",
            McpAssertions.AssertToolError(await _client.RequestAsync(
                McpMessages.Send(9, "classify", McpMessages.ImageArgs(mediaType: mediaType)))));
        await _client.ShutdownAsync();
    }

    [Fact]
    public async Task Send_RejectsMissingBase64UnknownKeyAndBadTopK()
    {
        var missing = McpMessages.ImageArgs();
        missing.Remove("image_base64");
        Assert.Equal("INVALID_ARGUMENT",
            McpAssertions.AssertToolError(await _client.RequestAsync(
                McpMessages.Send(10, "classify", missing))));

        Assert.Equal("INVALID_ARGUMENT",
            McpAssertions.AssertToolError(await _client.RequestAsync(
                McpMessages.Send(11, "classify", McpMessages.ImageArgs(extraKey: "surprise")))));

        Assert.Equal("INVALID_ARGUMENT",
            McpAssertions.AssertToolError(await _client.RequestAsync(
                McpMessages.Send(12, "classify", McpMessages.ImageArgs(topK: 0)))));

        Assert.Equal("INVALID_ARGUMENT",
            McpAssertions.AssertToolError(await _client.RequestAsync(
                McpMessages.Send(13, "classify", McpMessages.ImageArgs(topK: 51)))));

        var notAnInt = McpMessages.ImageArgs();
        notAnInt["top_k"] = "ten";
        Assert.Equal("INVALID_ARGUMENT",
            McpAssertions.AssertToolError(await _client.RequestAsync(
                McpMessages.Send(14, "classify", notAnInt))));
        await _client.ShutdownAsync();
    }

    [Fact]
    public async Task Send_RejectsInvalidImageInputs()
    {
        // Not base64 at all.
        Assert.Equal("INVALID_ARGUMENT",
            McpAssertions.AssertToolError(await _client.RequestAsync(
                McpMessages.Send(15, "classify", McpMessages.ImageArgs("%%not-base64%%")))));

        // Valid base64 but not an image.
        Assert.Equal("INVALID_ARGUMENT",
            McpAssertions.AssertToolError(await _client.RequestAsync(
                McpMessages.Send(16, "classify",
                    McpMessages.ImageArgs(Convert.ToBase64String(new byte[] { 1, 2, 3, 4 }))))));

        // Header-advertised dimensions beyond the decompression bounds.
        Assert.Equal("INVALID_ARGUMENT",
            McpAssertions.AssertToolError(await _client.RequestAsync(
                McpMessages.Send(17, "classify",
                    McpMessages.ImageArgs(TestImages.OversizedPngBase64())))));

        // Decoded payload beyond the 8 MiB cap.
        var tooBig = Convert.ToBase64String(new byte[8 * 1024 * 1024 + 1]);
        Assert.Equal("INVALID_ARGUMENT",
            McpAssertions.AssertToolError(await _client.RequestAsync(
                McpMessages.Send(18, "classify", McpMessages.ImageArgs(tooBig)))));

        Assert.Equal(0, _classifier.CallCount);
        await _client.ShutdownAsync();
    }

    [Theory]
    [InlineData(SKEncodedImageFormat.Png, "image/png")]
    [InlineData(SKEncodedImageFormat.Jpeg, "image/jpeg")]
    [InlineData(SKEncodedImageFormat.Webp, "image/webp")]
    public void ImageInput_DetectsSupportedFormats(SKEncodedImageFormat format, string mediaType)
    {
        var image = PluginImageInput.Parse(TestImages.EncodeBase64(format), null);
        Assert.Equal(mediaType, image.MediaType);
        Assert.Equal(mediaType, PluginImageInput.Parse(Convert.ToBase64String(image.Bytes), mediaType).MediaType);
        Assert.Throws<ToolErrorException>(() =>
            PluginImageInput.Parse(Convert.ToBase64String(image.Bytes), "image/png" == mediaType ? "image/jpeg" : "image/png"));
    }

    [Fact]
    public void ImageInput_RejectsUnsupportedEncodedFormat()
    {
        var gif = "R0lGODlhAQABAAD/ACwAAAAAAQABAAACADs=";
        using var codec = SKCodec.Create(SKData.CreateCopy(Convert.FromBase64String(gif)));
        Assert.Equal(SKEncodedImageFormat.Gif, codec.EncodedFormat);
        Assert.Throws<ToolErrorException>(() =>
            PluginImageInput.Parse(gif, null));
    }

    [Fact]
    public void ImageTensor_ResizesAndNormalizesRgb()
    {
        var image = KuzushiImage.FromBytes(Convert.FromBase64String(TestImages.PngBase64(1, 1)));
        var tensor = OnnxImageTensorFactory.CreateInputTensor(image, 2);
        Assert.Equal(new[] { 1, 3, 2, 2 }, tensor.Dimensions.ToArray());
        for (var y = 0; y < 2; y++)
        for (var x = 0; x < 2; x++)
        {
            Assert.Equal(0f, tensor[0, 0, y, x]);
            Assert.Equal(0f, tensor[0, 1, y, x]);
            Assert.Equal(128f / 255f, tensor[0, 2, y, x]);
        }
    }

    [Theory]
    [InlineData(SKEncodedImageFormat.Jpeg)]
    [InlineData(SKEncodedImageFormat.Webp)]
    public void ImageTensor_DecodesSupportedFormats(SKEncodedImageFormat format)
    {
        var image = KuzushiImage.FromBytes(Convert.FromBase64String(TestImages.EncodeBase64(format, 1, 1)));
        var tensor = OnnxImageTensorFactory.CreateInputTensor(image, 2);
        Assert.InRange(tensor[0, 2, 0, 0], 0.4f, 0.6f);
    }

    [Fact]
    public async Task RequestId_SameMessageReplaysSameResult()
    {
        var first = await _client.RequestAsync(
            McpMessages.Send(20, "classify", McpMessages.ImageArgs(), requestId: "dup-1"));
        var second = await _client.RequestAsync(
            McpMessages.Send(21, "classify", McpMessages.ImageArgs(), requestId: "dup-1"));

        var firstResult = Assert.IsAssignableFrom<JsonObject>(
            McpAssertions.AssertToolSuccess(first)["result"]);
        var secondResult = Assert.IsAssignableFrom<JsonObject>(
            McpAssertions.AssertToolSuccess(second)["result"]);

        Assert.Equal(
            firstResult["result_id"]!.GetValue<string>(),
            secondResult["result_id"]!.GetValue<string>());
        Assert.Equal(1, _classifier.CallCount);
        await _client.ShutdownAsync();
    }

    [Fact]
    public async Task RequestId_DifferentMessage_Conflicts()
    {
        var first = await _client.RequestAsync(
            McpMessages.Send(22, "classify", McpMessages.ImageArgs(), requestId: "dup-2"));
        Assert.NotNull(McpAssertions.AssertToolSuccess(first));

        var conflict = await _client.RequestAsync(
            McpMessages.Send(23, "classify",
                McpMessages.ImageArgs(topK: 3), requestId: "dup-2"));
        Assert.Equal("REVISION_CONFLICT", McpAssertions.AssertToolError(conflict));
        await _client.ShutdownAsync();
    }

    [Fact]
    public async Task RequestId_ConflictDetectedEvenWhileInFlight()
    {
        var gate = new TaskCompletionSource();
        _classifier.Handler = async (image, topK, ct) =>
        {
            await gate.Task.WaitAsync(ct);
            return new KuzushiPrediction(new PredictionCandidate[] { new("あ", 1f) });
        };

        await _client.SendAsync(McpMessages.Send(30, "classify",
            McpMessages.ImageArgs(), requestId: "dup-3"));

        // Give the first request a moment to reach inference, then fire a
        // conflicting reuse of the same request_id.
        await Task.Delay(200);
        var conflict = await _client.RequestAsync(
            McpMessages.Send(31, "classify",
                McpMessages.ImageArgs(topK: 9), requestId: "dup-3"));
        Assert.Equal("REVISION_CONFLICT", McpAssertions.AssertToolError(conflict));

        gate.TrySetResult();
        var first = await _client.ReadMessageAsync();
        Assert.Equal("30", first["id"]!.ToJsonString());
        await _client.ShutdownAsync();
    }

    [Fact]
    public async Task CancelledNotification_CancelsInFlightRequest()
    {
        var entered = new TaskCompletionSource();
        _classifier.Handler = async (image, topK, ct) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return KuzushiPrediction.Empty;
        };

        await _client.SendAsync(McpMessages.Send(40, "classify", McpMessages.ImageArgs()));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await _client.SendAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "notifications/cancelled",
            ["params"] = new JsonObject { ["requestId"] = 40 },
        });

        var response = await _client.ReadMessageAsync();
        Assert.Equal("40", response["id"]!.ToJsonString());
        Assert.Equal("CANCELLED", McpAssertions.AssertToolError(response));
        await _client.ShutdownAsync();
    }

    [Fact]
    public async Task ConcurrentSends_AllCompleteWithDistinctResults()
    {
        const int count = 6;
        for (var i = 0; i < count; i++)
        {
            await _client.SendAsync(McpMessages.Send(50 + i, "classify", McpMessages.ImageArgs()));
        }

        var seen = new HashSet<string>();
        for (var i = 0; i < count; i++)
        {
            var response = await _client.ReadMessageAsync();
            var result = Assert.IsAssignableFrom<JsonObject>(
                McpAssertions.AssertToolSuccess(response)["result"]);
            Assert.True(seen.Add(result["result_id"]!.GetValue<string>()));
        }

        Assert.Equal(count, _classifier.CallCount);
        await _client.ShutdownAsync();
    }

    public void Dispose()
    {
        _client.Dispose();
        _root.Dispose();
    }
}

/// <summary>
/// Missing-asset gating: classify needs only the classifier model pair;
/// similar/analyze additionally need the embedding model and dataset.
/// Failures must name the missing assets precisely — no downloads.
/// </summary>
public sealed class ReadinessTests : IDisposable
{
    [Fact]
    public async Task Classify_WorksWithoutDataset()
    {
        using var root = FakeDataRoot.Create();
        root.MakeClassifierReady();
        using var client = Client(root);

        var response = await client.RequestAsync(
            McpMessages.Send(1, "classify", McpMessages.ImageArgs()));
        var result = McpAssertions.AssertToolSuccess(response);
        Assert.Equal("completed", result["state"]!.GetValue<string>());

        // And similar still reports its own missing prerequisites accurately.
        var similar = await client.RequestAsync(
            McpMessages.Send(2, "similar", McpMessages.ImageArgs()));
        Assert.Equal("UNAVAILABLE", McpAssertions.AssertToolError(similar));
        var detail = ToolErrorText(similar);
        Assert.Contains("embedding dataset", detail);
        Assert.DoesNotContain("classifier model", detail);
        await client.ShutdownAsync();
    }

    [Fact]
    public async Task Similar_WithoutEmbeddingModel_NamesBothMissingGroups()
    {
        using var root = FakeDataRoot.Create();
        root.MakeClassifierReady();
        root.MakeDatasetReady();
        using var client = Client(root);

        var response = await client.RequestAsync(
            McpMessages.Send(3, "similar", McpMessages.ImageArgs()));
        Assert.Equal("UNAVAILABLE", McpAssertions.AssertToolError(response));
        Assert.Contains("embedding model", ToolErrorText(response));
        await client.ShutdownAsync();
    }

    [Fact]
    public async Task Analyze_WithoutAnything_ListsAllMissingAssets()
    {
        using var root = FakeDataRoot.Create();
        using var client = Client(root);

        var response = await client.RequestAsync(
            McpMessages.Send(4, "analyze", McpMessages.ImageArgs()));
        Assert.Equal("UNAVAILABLE", McpAssertions.AssertToolError(response));
        var detail = ToolErrorText(response);
        Assert.Contains("classifier model", detail);
        Assert.Contains("embedding model", detail);
        Assert.Contains("embedding dataset", detail);
        Assert.DoesNotContain("supervised_pretrain_checkpoint.onnx\":", detail);
        await client.ShutdownAsync();
    }

    [Fact]
    public async Task Validation_HappensBeforeReadiness()
    {
        // Even with all assets missing, malformed args are INVALID_ARGUMENT,
        // not UNAVAILABLE.
        using var root = FakeDataRoot.Create();
        using var client = Client(root);

        var response = await client.RequestAsync(
            McpMessages.Send(5, "classify",
                McpMessages.ImageArgs("not-valid-base64!!")));
        Assert.Equal("INVALID_ARGUMENT", McpAssertions.AssertToolError(response));
        await client.ShutdownAsync();
    }

    private static McpClientConnection Client(FakeDataRoot root)
    {
        var dispatcher = PluginServices.CreateDispatcher(
            root.CreateProbe(),
            new FakeClassifierService(),
            new FakeEmbeddingService(),
            new FakeEmbeddingIndexService());
        return new McpClientConnection(dispatcher);
    }

    private static string ToolErrorText(JsonObject response)
    {
        var result = Assert.IsAssignableFrom<JsonObject>(response["result"]);
        var error = Assert.IsAssignableFrom<JsonObject>(
            Assert.IsAssignableFrom<JsonObject>(result["structuredContent"])["error"]);
        return error["message"]!.GetValue<string>();
    }

    public void Dispose()
    {
    }
}
