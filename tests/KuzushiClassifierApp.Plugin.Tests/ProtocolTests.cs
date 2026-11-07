using System.Text.Json.Nodes;
using Xunit;

namespace KuzushiClassifierApp.Plugin.Tests;

/// <summary>
/// JSON-RPC framing, handshake, fixed tool list, and protocol-level error
/// handling. All against a fake backend — no model or dataset involved.
/// </summary>
public sealed class ProtocolTests : IDisposable
{
    private readonly FakeDataRoot _root = FakeDataRoot.Create();
    private readonly FakeClassifierService _classifier = new();
    private readonly FakeEmbeddingIndexService _index = new();
    private readonly McpClientConnection _client;

    public ProtocolTests()
    {
        var dispatcher = PluginServices.CreateDispatcher(
            _root.CreateProbe(),
            _classifier,
            new FakeEmbeddingService(),
            _index);
        _client = new McpClientConnection(dispatcher);
    }

    [Fact]
    public async Task Initialize_ReturnsProtocolAndToolCapability()
    {
        var response = await _client.RequestAsync(McpMessages.Initialize());
        var result = Assert.IsAssignableFrom<JsonObject>(response["result"]);
        Assert.Equal("2025-06-18", result["protocolVersion"]!.GetValue<string>());
        var serverInfo = Assert.IsAssignableFrom<JsonObject>(result["serverInfo"]);
        Assert.Equal("kwadraten.kuzushi-classifier", serverInfo["name"]!.GetValue<string>());
        await _client.ShutdownAsync();
    }

    [Fact]
    public async Task ToolsList_ExposesExactlyTheThreeFixedTools()
    {
        await _client.SendAsync(McpMessages.Initialize());
        await _client.ReadMessageAsync();

        var response = await _client.RequestAsync(
            new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 2, ["method"] = "tools/list" });

        var tools = Assert.IsAssignableFrom<JsonArray>(
            Assert.IsAssignableFrom<JsonObject>(response["result"])["tools"]);
        var names = tools
            .Select(t => t!["name"]!.GetValue<string>())
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[] { "patchouli.plugin.fetch", "patchouli.plugin.find", "patchouli.plugin.send" },
            names);
        await _client.ShutdownAsync();
    }

    [Fact]
    public async Task MalformedJson_RespondsParseError()
    {
        await _client.SendRawAsync("{this is not json");
        var response = await _client.ReadMessageAsync();
        Assert.Equal(-32700, McpAssertions.AssertProtocolError(response));
        Assert.Null(response["id"]);
        await _client.ShutdownAsync();
    }

    [Fact]
    public async Task UnknownMethod_RespondsMethodNotFound()
    {
        var response = await _client.RequestAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 7, ["method"] = "resources/list",
        });
        Assert.Equal(-32601, McpAssertions.AssertProtocolError(response));
        await _client.ShutdownAsync();
    }

    [Fact]
    public async Task ToolCall_UnknownTool_ReturnsUnsupportedOperation()
    {
        var response = await _client.RequestAsync(
            McpMessages.ToolCall(11, "patchouli.plugin.put", new JsonObject()));
        Assert.Equal("UNSUPPORTED_OPERATION", McpAssertions.AssertToolError(response));
        await _client.ShutdownAsync();
    }

    [Fact]
    public async Task ToolCall_MissingParams_RespondsInvalidParams()
    {
        var response = await _client.RequestAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 12, ["method"] = "tools/call",
        });
        Assert.Equal(-32602, McpAssertions.AssertProtocolError(response));
        await _client.ShutdownAsync();
    }

    [Fact]
    public async Task Ping_RespondsEmptyObject()
    {
        var response = await _client.RequestAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 13, ["method"] = "ping",
        });
        Assert.NotNull(response["result"]);
        await _client.ShutdownAsync();
    }

    [Fact]
    public async Task OversizedRequest_IsRejectedAndServerContinues()
    {
        // 17 MiB > MaxRequestBytes (16 MiB): the line is dropped, an error is
        // returned, and the next request still works.
        var huge = new byte[17 * 1024 * 1024 + 1];
        Array.Fill(huge, (byte)'x');
        huge[^1] = (byte)'\n';
        await _client.SendBytesAsync(huge);

        var error = await _client.ReadMessageAsync();
        Assert.Equal(-32600, McpAssertions.AssertProtocolError(error));

        var pong = await _client.RequestAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 14, ["method"] = "ping",
        });
        Assert.NotNull(pong["result"]);
        await _client.ShutdownAsync();
    }

    [Fact]
    public async Task StringAndNumericIds_AreEchoedVerbatim()
    {
        var stringResponse = await _client.RequestAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = "abc-1", ["method"] = "ping",
        });
        Assert.Equal("abc-1", stringResponse["id"]!.GetValue<string>());

        var numericResponse = await _client.RequestAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 42, ["method"] = "ping",
        });
        Assert.Equal(42, numericResponse["id"]!.GetValue<int>());
        await _client.ShutdownAsync();
    }

    public void Dispose()
    {
        _client.Dispose();
        _root.Dispose();
    }
}
