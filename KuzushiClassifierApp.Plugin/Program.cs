using KuzushiClassifierApp.Plugin;
using Microsoft.Extensions.Logging;

// Plugin entry point: stdio MCP server only. Never touches Avalonia or any UI.
// stdout carries JSON-RPC exclusively; all diagnostics go to stderr.

PluginLaunchOptions? options;
try
{
    options = PluginLaunchOptions.Parse(args);
}
catch (ArgumentException exception)
{
    Console.Error.WriteLine(exception.Message);
    return 2;
}

if (options is null)
{
    Console.Error.WriteLine(PluginLaunchOptions.UsageText);
    return 0;
}

try
{
    using var services = PluginServices.Create(options.DataRoot, options.ManagedFiles);
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
