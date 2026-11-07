using KuzushiClassifierApp.Plugin;
using Microsoft.Extensions.Logging;

// Plugin entry point: stdio MCP server only. Never touches Avalonia or any UI.
// stdout carries JSON-RPC exclusively; all diagnostics go to stderr.

string? dataRoot = null;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--data-root" when i + 1 < args.Length:
            dataRoot = args[++i];
            break;
        case "--help" or "-h":
            Console.Error.WriteLine(
                "Usage: KuzushiClassifierApp.Plugin.exe [--data-root <path>]\n" +
                "Runs a newline-delimited JSON-RPC MCP server on stdin/stdout.");
            return 0;
        default:
            Console.Error.WriteLine($"Unknown argument: {args[i]}");
            return 2;
    }
}

try
{
    using var services = PluginServices.Create(dataRoot);
    var logger = services.LoggerFactory.CreateLogger("KuzushiClassifierApp.Plugin");
    var server = new McpServer(services.Dispatcher, logger);

    using var stdin = Console.OpenStandardInput();
    using var stdout = Console.OpenStandardOutput();

    await server.RunAsync(stdin, stdout).ConfigureAwait(false);
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Plugin server failed: {ex}");
    return 1;
}
