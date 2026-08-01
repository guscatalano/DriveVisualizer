using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace DriveVisualizer_App;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private Window? _window;

    /// <summary>Main window, used for picker interop (InitializeWithWindow).</summary>
    public static Window? Window { get; private set; }


    /// <summary>
    /// Initializes the singleton application object.  This is the first line of authored code
    /// executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    public App()
    {
        InitializeComponent();

        UnhandledException += (_, e) =>
        {
            LogCrash("XamlUnhandled", e.Exception, e.Message);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            LogCrash("AppDomainUnhandled", e.ExceptionObject as Exception, null);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogCrash("UnobservedTask", e.Exception, null);
            e.SetObserved();
        };
    }

    private static void LogCrash(string source, Exception? ex, string? message)
    {
        try
        {
            string dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DriveVisualizer");
            Directory.CreateDirectory(dir);
            File.AppendAllText(System.IO.Path.Combine(dir, "crash.log"),
                $"[{DateTime.Now:O}] {source}: {message}\n{ex}\n\n");
        }
        catch { }
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        // Headless mode for the scheduled task: scan, snapshot, exit — no window.
        var cmdArgs = Environment.GetCommandLineArgs();
        int snapshotArg = Array.IndexOf(cmdArgs, "--snapshot");
        if (snapshotArg >= 0 && snapshotArg + 1 < cmdArgs.Length)
        {
            RunHeadlessSnapshot(cmdArgs[snapshotArg + 1]);
            return;
        }

        _window = new MainWindow();
        Window = _window;
        SettingsPage.ApplyTheme(Services.AppSettings.Theme);
        Services.Mcp.McpHttpServer.Instance.ApplyEnabledSetting(); // resume MCP server if it was on
        _window.Activate();
    }

    private async void RunHeadlessSnapshot(string target)
    {
        try
        {
            await Services.SnapshotJob.RunAsync(target);
        }
        finally
        {
            Exit();
        }
    }
}
