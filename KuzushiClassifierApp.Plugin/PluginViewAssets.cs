using System.Text;

namespace KuzushiClassifierApp.Plugin;

/// <summary>
/// Serves the self-contained HTML documents declared as ui.views sources in
/// patchouli-plugin.toml. The documents are compiled into the assembly as
/// embedded resources so a packaged plugin zip needs no sidecar files next
/// to the executable.
/// </summary>
internal static class PluginViewAssets
{
    /// <summary>Plugin-relative fetch path of the "main" window view.</summary>
    public const string MainViewPath = "ui/main.html";

    // Keep in sync with the EmbeddedResource LogicalName in the csproj.
    private const string MainViewResourceName = "KuzushiClassifierApp.Plugin.Views.main.html";

    private static readonly Lazy<string> s_mainViewHtml = new(LoadMainViewHtml);

    /// <summary>The complete HTML document for <see cref="MainViewPath"/>.</summary>
    public static string MainViewHtml => s_mainViewHtml.Value;

    private static string LoadMainViewHtml()
    {
        var assembly = typeof(PluginViewAssets).Assembly;
        using var stream = assembly.GetManifestResourceStream(MainViewResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{MainViewResourceName}' is missing from the plugin assembly.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
