using System.Text.Json.Nodes;
using Xunit;

namespace KuzushiClassifierApp.Plugin.Tests;

/// <summary>
/// find/fetch argument validation, path safety, cursor scope, and status
/// probing — fake backend only, temp data root only.
/// </summary>
public sealed class FindFetchTests : IDisposable
{
    private readonly FakeDataRoot _root = FakeDataRoot.Create();
    private readonly McpClientConnection _client;

    public FindFetchTests()
    {
        _root.MakeClassifierReady();
        var dispatcher = PluginServices.CreateDispatcher(
            _root.CreateProbe(),
            new FakeClassifierService(),
            new FakeEmbeddingService(),
            new FakeEmbeddingIndexService());
        _client = new McpClientConnection(dispatcher);
    }

    [Fact]
    public async Task Find_Root_BrowsesDeclaredChildren()
    {
        var response = await _client.RequestAsync(McpMessages.Find(1));
        var entries = Assert.IsAssignableFrom<JsonArray>(
            McpAssertions.AssertToolSuccess(response)["entries"]);

        var uris = entries.Select(e => e!["uri"]!.GetValue<string>()).ToArray();
        Assert.Equal(new[] { "status.json", "results/" }, uris);
        Assert.Equal(new[] { "file", "directory" },
            entries.Select(e => e!["type"]!.GetValue<string>()).ToArray());
        await _client.ShutdownAsync();
    }

    [Fact]
    public async Task Find_RejectsUnknownScopeQueryAndLimits()
    {
        Assert.Equal("INVALID_ARGUMENT",
            McpAssertions.AssertToolError(
                await _client.RequestAsync(McpMessages.Find(2, scope: "bogus/"))));

        Assert.Equal("INVALID_ARGUMENT",
            McpAssertions.AssertToolError(
                await _client.RequestAsync(McpMessages.Find(3, query: "x"))));

        Assert.Equal("INVALID_ARGUMENT",
            McpAssertions.AssertToolError(
                await _client.RequestAsync(McpMessages.Find(4, limit: 0))));

        Assert.Equal("INVALID_ARGUMENT",
            McpAssertions.AssertToolError(
                await _client.RequestAsync(McpMessages.Find(5, limit: 51))));

        Assert.Equal("INVALID_ARGUMENT",
            McpAssertions.AssertToolError(
                await _client.RequestAsync(McpMessages.Find(6, cursor: "not-a-cursor"))));
        await _client.ShutdownAsync();
    }

    [Fact]
    public async Task Fetch_Status_ReportsReadinessWithoutInitializing()
    {
        var response = await _client.RequestAsync(McpMessages.Fetch(7, "status.json"));
        var content = McpAssertions.AssertToolSuccess(response);
        Assert.Equal("status.json", content["uri"]!.GetValue<string>());
        Assert.Equal("application/json", content["content_type"]!.GetValue<string>());

        var status = JsonNode.Parse(content["content"]!.GetValue<string>())!;
        Assert.Equal("kwadraten.kuzushi-classifier", status["plugin_id"]!.GetValue<string>());
        Assert.True(status["assets"]!["classifier_model"]!["ready"]!.GetValue<bool>());
        Assert.False(status["assets"]!["dataset"]!["ready"]!.GetValue<bool>());
        Assert.True(status["capabilities"]!["classify"]!["ready"]!.GetValue<bool>());
        Assert.False(status["capabilities"]!["similar"]!["ready"]!.GetValue<bool>());
        Assert.False(status["capabilities"]!["analyze"]!["ready"]!.GetValue<bool>());
        Assert.False(status["downloads_performed"]!.GetValue<bool>());
        await _client.ShutdownAsync();
    }

    [Fact]
    public async Task Fetch_Status_DoesNotDownloadOrCreateAssetDirectories()
    {
        using var empty = FakeDataRoot.Create();
        var dispatcher = PluginServices.CreateDispatcher(
            empty.CreateProbe(),
            new FakeClassifierService(),
            new FakeEmbeddingService(),
            new FakeEmbeddingIndexService());
        using var client = new McpClientConnection(dispatcher);

        var response = await client.RequestAsync(McpMessages.Fetch(8, "status.json"));
        var content = McpAssertions.AssertToolSuccess(response);
        var status = JsonNode.Parse(content["content"]!.GetValue<string>())!;

        Assert.False(status["capabilities"]!["classify"]!["ready"]!.GetValue<bool>());
        Assert.NotEmpty(status["missing_assets"]!.AsArray());

        // The probe must not manufacture cache directories or trigger any download.
        Assert.False(Directory.Exists(empty.ModelsDirectory));
        Assert.False(Directory.Exists(empty.DatasetsDirectory));
        await client.ShutdownAsync();
    }

    [Fact]
    public async Task Fetch_Root_ReturnsDirectoryDocument()
    {
        var response = await _client.RequestAsync(McpMessages.Fetch(9, ""));
        var content = McpAssertions.AssertToolSuccess(response);
        var doc = JsonNode.Parse(content["content"]!.GetValue<string>())!;
        Assert.Equal("directory", doc["type"]!.GetValue<string>());
        Assert.Equal(2, doc["children"]!.AsArray().Count);
        await _client.ShutdownAsync();
    }

    [Theory]
    [InlineData("../escape.json")]
    [InlineData("results/../../status.json")]
    [InlineData("%2e%2e/status.json")]
    [InlineData("status.json?x=1")]
    [InlineData("status.json#frag")]
    [InlineData("results\\x.json")]
    [InlineData("/status.json")]
    [InlineData("patchouli://plugin/other-plugin/status.json")]
    public async Task Fetch_RejectsUnsafeOrForeignPaths(string uri)
    {
        var response = await _client.RequestAsync(McpMessages.Fetch(10, uri));
        Assert.Equal("INVALID_ARGUMENT",
            McpAssertions.AssertToolError(response));
        await _client.ShutdownAsync();
    }

    [Fact]
    public async Task Fetch_UnknownPath_NotFound()
    {
        var response = await _client.RequestAsync(McpMessages.Fetch(11, "nope.json"));
        Assert.Equal("NOT_FOUND", McpAssertions.AssertToolError(response));
        await _client.ShutdownAsync();
    }

    [Fact]
    public async Task Fetch_ResultsDirectory_IsUnsupportedOperation()
    {
        var response = await _client.RequestAsync(McpMessages.Fetch(12, "results/"));
        Assert.Equal("UNSUPPORTED_OPERATION", McpAssertions.AssertToolError(response));
        await _client.ShutdownAsync();
    }

    [Fact]
    public async Task Fetch_MainView_ReturnsEmbeddedHtml()
    {
        var response = await _client.RequestAsync(McpMessages.Fetch(14, "ui/main.html"));
        var content = McpAssertions.AssertToolSuccess(response);

        Assert.Equal("ui/main.html", content["uri"]!.GetValue<string>());
        Assert.Equal("text/html", content["content_type"]!.GetValue<string>());

        var html = content["content"]!.GetValue<string>();
        Assert.Contains("<html", html);
        Assert.Contains("本地崩字识别", html);
        await _client.ShutdownAsync();
    }

    [Fact]
    public async Task Fetch_MissingResult_NotFound()
    {
        var response = await _client.RequestAsync(McpMessages.Fetch(13, "results/r_missing.json"));
        Assert.Equal("NOT_FOUND", McpAssertions.AssertToolError(response));
        await _client.ShutdownAsync();
    }

    public void Dispose()
    {
        _client.Dispose();
        _root.Dispose();
    }
}
