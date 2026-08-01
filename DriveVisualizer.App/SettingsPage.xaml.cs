using DriveVisualizer.Core;
using DriveVisualizer_App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace DriveVisualizer_App;

public sealed partial class SettingsPage : Page
{
    private MainViewModel? _vm;
    private bool _loading;

    public SettingsPage()
    {
        InitializeComponent();
        BackIcon.Glyph = "";
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _vm = e.Parameter as MainViewModel;

        _loading = true;
        AutoSaveToggle.IsOn = Services.AppSettings.AutoSaveSnapshots;
        SizeDetailCombo.SelectedIndex = (int)ByteFormatter.Detail;
        ThemeCombo.SelectedIndex = Math.Clamp(Services.AppSettings.Theme, 0, 2);
        FrequencyCombo.SelectedIndex = Math.Clamp(Services.AppSettings.SnapshotFrequency, 0, 3);
        RetentionCombo.SelectedIndex = Math.Clamp(Services.AppSettings.SnapshotRetention, 0, 5);
        ScheduledTaskToggle.IsOn = Services.ScheduledSnapshotTask.IsRegistered();

        string version;
        try
        {
            var v = Windows.ApplicationModel.Package.Current.Id.Version;
            version = $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
        }
        catch
        {
            version = "dev";
        }
        AboutTitle.Text = $"DriveVisualizer {version}";
        McpPortBox.Value = Services.AppSettings.McpPort;
        ReflectMcpState();
        RefreshMcpConfigs();
        _loading = false;
    }

    private void McpStart_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            int port = (int)McpPortBox.Value;
            Services.Mcp.McpHttpServer.Instance.Start(port);
            Services.AppSettings.McpEnabled = true;
            ReflectMcpState();
            ShowMcpStatus($"MCP server running on http://localhost:{port}/ — starts with the app until you turn it off.", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowMcpStatus("Failed to start: " + ex.Message, InfoBarSeverity.Error);
        }
    }

    private void McpStop_Click(object sender, RoutedEventArgs e)
    {
        Services.Mcp.McpHttpServer.Instance.Stop();
        Services.AppSettings.McpEnabled = false;
        ReflectMcpState();
        ShowMcpStatus("Stopped.", InfoBarSeverity.Informational);
    }

    private void McpPort_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!_loading && !double.IsNaN(McpPortBox.Value))
            Services.AppSettings.McpPort = (int)McpPortBox.Value;
        RefreshMcpConfigs();
    }

    private void ReflectMcpState()
    {
        bool running = Services.Mcp.McpHttpServer.Instance.IsRunning;
        McpStartButton.IsEnabled = !running;
        McpStopButton.IsEnabled = running;
        McpPortBox.IsEnabled = !running;
        McpStatusLabel.Text = running && Services.Mcp.McpHttpServer.Instance.Port is int p
            ? $"listening on :{p}"
            : "stopped";
        if (running && Services.Mcp.McpHttpServer.Instance.Port is int current)
            McpPortBox.Value = current;
    }

    private void RefreshMcpConfigs()
    {
        if (CfgClaudeCodeBox is null)
            return; // fires during InitializeComponent before siblings exist
        int port = double.IsNaN(McpPortBox.Value) ? 18766 : (int)McpPortBox.Value;
        string url = $"http://localhost:{port}/";

        CfgClaudeCodeBox.Text = $"claude mcp add --transport http drivevisualizer {url}";

        CfgVsCodeBox.Text =
            "{\r\n" +
            "  \"servers\": {\r\n" +
            "    \"drivevisualizer\": {\r\n" +
            "      \"type\": \"http\",\r\n" +
            $"      \"url\": \"{url}\"\r\n" +
            "    }\r\n" +
            "  }\r\n" +
            "}";

        CfgCurlBox.Text =
            $"curl -s {url}tools\r\n" +
            $"curl -s {url} -H \"Content-Type: application/json\" -d '{{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}}'";
    }

    private void McpCopyConfig_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string key)
            return;
        TextBox? box = key switch
        {
            "ClaudeCode" => CfgClaudeCodeBox,
            "VsCode" => CfgVsCodeBox,
            "Curl" => CfgCurlBox,
            _ => null,
        };
        if (box is null || string.IsNullOrEmpty(box.Text))
            return;
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(box.Text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        ShowMcpStatus("Copied.", InfoBarSeverity.Success);
    }

    private void ShowMcpStatus(string message, InfoBarSeverity severity)
    {
        McpStatusBar.Severity = severity;
        McpStatusBar.Message = message;
        McpStatusBar.IsOpen = true;
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (Frame.CanGoBack)
            Frame.GoBack();
    }

    public static void ApplyTheme(int theme)
    {
        if (App.Window?.Content is FrameworkElement root)
        {
            root.RequestedTheme = theme switch
            {
                1 => ElementTheme.Light,
                2 => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };
        }
    }

    private void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ThemeCombo.SelectedIndex < 0)
            return;
        Services.AppSettings.Theme = ThemeCombo.SelectedIndex;
        ApplyTheme(ThemeCombo.SelectedIndex);
    }

    private async void ScheduledTask_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;

        if (!ScheduledTaskToggle.IsOn)
        {
            var (ok, msg) = await Task.Run(Services.ScheduledSnapshotTask.Unregister);
            Services.AppSettings.ScheduledTaskTarget = "";
            if (_vm is not null)
                _vm.StatusText = ok ? "Background snapshot task removed." : $"Could not remove task: {msg}";
            return;
        }

        string? target = _vm?.CurrentSnapshot?.Target ?? _vm?.SelectedTarget;
        if (string.IsNullOrWhiteSpace(target))
        {
            _loading = true;
            ScheduledTaskToggle.IsOn = false;
            _loading = false;
            await new ContentDialog
            {
                Title = "Pick a target first",
                Content = "Run a scan (or select a drive) so there is a target for the scheduled task to snapshot.",
                CloseButtonText = "OK",
                XamlRoot = XamlRoot,
            }.ShowAsync();
            return;
        }

        int frequency = Services.AppSettings.SnapshotFrequency;
        var (created, message) = await Task.Run(() => Services.ScheduledSnapshotTask.Register(target, frequency));
        if (created)
        {
            Services.AppSettings.ScheduledTaskTarget = target;
            string cadence = frequency switch { 1 => "hourly", 3 => "weekly", _ => "daily" };
            ScheduledTaskDesc.Text = $"Scheduled: {cadence} hidden scan of {target}. Cadence changes update the task automatically; re-enable to change the target.";
            if (_vm is not null)
                _vm.StatusText = $"Background snapshot task registered ({cadence}, {target}).";
        }
        else
        {
            _loading = true;
            ScheduledTaskToggle.IsOn = false;
            _loading = false;
            if (_vm is not null)
                _vm.StatusText = $"Could not register task: {message}";
        }
    }

    private async void Frequency_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || FrequencyCombo.SelectedIndex < 0)
            return;
        int frequency = FrequencyCombo.SelectedIndex;
        Services.AppSettings.SnapshotFrequency = frequency;

        // Keep the background task in step with the cadence automatically.
        string storedTarget = Services.AppSettings.ScheduledTaskTarget;
        if (string.IsNullOrEmpty(storedTarget))
            storedTarget = _vm?.CurrentSnapshot?.Target ?? _vm?.SelectedTarget ?? "";
        if (!string.IsNullOrEmpty(storedTarget) &&
            await Task.Run(Services.ScheduledSnapshotTask.IsRegistered))
        {
            var (ok, msg) = await Task.Run(() => Services.ScheduledSnapshotTask.Register(storedTarget, frequency));
            if (_vm is not null)
                _vm.StatusText = ok
                    ? $"Background snapshot task updated to the new cadence ({storedTarget})."
                    : $"Could not update background task: {msg}";
        }
    }

    private void Retention_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && RetentionCombo.SelectedIndex >= 0)
            Services.AppSettings.SnapshotRetention = RetentionCombo.SelectedIndex;
    }

    private void SizeDetail_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || SizeDetailCombo.SelectedIndex < 0)
            return;
        ByteFormatter.Detail = (SizeDetail)SizeDetailCombo.SelectedIndex;
        Services.AppSettings.SizeDetail = SizeDetailCombo.SelectedIndex;
        _vm?.RefreshFormatting();
    }

    private async void AutoSaveToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;

        if (AutoSaveToggle.IsOn)
        {
            Services.AppSettings.AutoSaveSnapshots = true;
            return;
        }

        bool confirmed = await ConfirmAsync("Turn off auto-save?",
            "This also deletes the stored daily snapshots (one folder holds both the last-scan " +
            "baseline and the size history), so \"What changed since last scan\", the Change column, " +
            "and \"Size history\" will have no data until you scan again with auto-save on.",
            "Turn off & delete");
        if (!confirmed)
        {
            _loading = true;
            AutoSaveToggle.IsOn = true;
            _loading = false;
            return;
        }

        Services.AppSettings.AutoSaveSnapshots = false;
        try
        {
            string history = MainViewModel.GetHistoryRootDirectory();
            if (Directory.Exists(history))
                Directory.Delete(history, recursive: true);
            string legacy = MainViewModel.GetLegacyAutoSnapshotDirectory();
            if (Directory.Exists(legacy))
                Directory.Delete(legacy, recursive: true);
            if (_vm is not null)
                _vm.StatusText = "Auto-save turned off; stored snapshots deleted.";
        }
        catch (Exception ex)
        {
            if (_vm is not null)
                _vm.StatusText = $"Auto-save turned off, but stored data could not be deleted: {ex.Message}";
        }
    }

    private void OpenHistory_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string dir = _vm?.CurrentSnapshot is { } snap
                ? MainViewModel.GetHistoryDirectory(snap.Target)
                : MainViewModel.GetHistoryRootDirectory();
            if (!Directory.Exists(dir))
                dir = MainViewModel.GetHistoryRootDirectory();
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start("explorer.exe", $"\"{dir}\"");
        }
        catch (Exception ex)
        {
            if (_vm is not null)
                _vm.StatusText = $"Could not open history folder: {ex.Message}";
        }
    }

    private async void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        string root = MainViewModel.GetHistoryRootDirectory();
        var files = Directory.Exists(root)
            ? Directory.GetFiles(root, "*.dvsnap", SearchOption.AllDirectories)
            : [];
        if (files.Length == 0)
        {
            await new ContentDialog
            {
                Title = "Nothing to clear",
                Content = "No snapshots are stored yet.",
                CloseButtonText = "OK",
                XamlRoot = XamlRoot,
            }.ShowAsync();
            return;
        }

        long bytes = files.Sum(f => new FileInfo(f).Length);
        bool confirmed = await ConfirmAsync("Clear snapshot data?",
            $"Delete {files.Length} daily snapshot{(files.Length == 1 ? "" : "s")} " +
            $"({ByteFormatter.Format(bytes)})? This also removes the Change-column baseline.",
            "Clear");
        if (!confirmed)
            return;

        try
        {
            Directory.Delete(root, recursive: true);
            if (_vm is not null)
                _vm.StatusText = $"Snapshot data cleared — freed {ByteFormatter.Format(bytes)}.";
        }
        catch (Exception ex)
        {
            if (_vm is not null)
                _vm.StatusText = $"Could not clear snapshots: {ex.Message}";
        }
    }

    private async Task<bool> ConfirmAsync(string title, string message, string primaryText)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            PrimaryButtonText = primaryText,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
