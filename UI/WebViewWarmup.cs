using Microsoft.Web.WebView2.Core;

namespace GlideScroll.UI;

/// <summary>
/// Triggers the WebView2 environment to initialize in the background at
/// process start. CoreWebView2Environment.CreateAsync is a 200-600ms cold
/// operation (it loads WebView2Loader.dll, locates the Edge runtime, and
/// initializes the user-data directory). Running it eagerly hides that
/// latency the first time the user clicks the tray icon to open Settings.
///
/// MainForm still calls CreateAsync itself on demand. After the warmup
/// completes, that second call returns quickly because the underlying
/// resources are already loaded.
/// </summary>
internal static class WebViewWarmup
{
    public static async Task PrewarmAsync()
    {
        try
        {
            string dataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GlideScroll", "WebView2");
            Directory.CreateDirectory(dataFolder);
            // The returned environment object isn't kept; the side effect of
            // creating it (loader DLL loaded, Edge runtime located, user-data
            // dir warmed) is the whole point. MainForm builds its own env
            // with the same parameters when the user actually opens Settings.
            await CoreWebView2Environment.CreateAsync(null, dataFolder);
        }
        catch
        {
            // Warmup is best-effort. If WebView2 runtime is missing or the
            // env can't be created, MainForm will surface the error to the
            // user via its existing inline retry + MessageBox path.
        }
    }
}
