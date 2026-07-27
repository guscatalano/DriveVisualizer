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
        _loading = false;
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
