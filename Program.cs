using GlideScroll.UI;
using Microsoft.Web.WebView2.Core;

namespace GlideScroll;

internal static class Program
{
    /// <summary>
    /// Single-instance mutex name. Per-user scope so each Windows account
    /// can run their own copy.
    /// </summary>
    private const string MutexName = "Local\\GlideScroll.SingleInstance";

    [STAThread]
    private static void Main()
    {
        // Single-instance check. Two copies running would each install
        // their own WH_MOUSE_LL hook and double-process every event.
        bool createdNew;
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out createdNew);
        if (!createdNew)
        {
            // Another instance is already running. Silent exit; the user
            // probably hit the autostart entry by mistake or double-clicked
            // the exe with the app already in the tray.
            return;
        }

        ApplicationConfiguration.Initialize();

        // Warm up the WebView2 environment in the background BEFORE the user
        // opens the settings window. CoreWebView2Environment.CreateAsync takes
        // 200-600ms cold; doing it preemptively at process start hides that
        // latency entirely the first time the user clicks the tray icon.
        // Fire-and-forget; if it fails MainForm will retry inline as before.
        _ = WebViewWarmup.PrewarmAsync();

        try
        {
            using var ctx = new TrayContext();
            Application.Run(ctx);
        }
        catch (Exception ex)
        {
            // Last-resort error reporting. If we get here, the app is
            // failing in a way the pipeline's defensive try/catches didn't
            // contain, show the user something instead of dying silently.
            MessageBox.Show(
                $"GlideScroll encountered an error and will exit.\n\n{ex.Message}\n\n{ex.StackTrace}",
                "GlideScroll",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }
}
