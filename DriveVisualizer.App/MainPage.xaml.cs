using System.Numerics;
using DriveVisualizer.Core;
using DriveVisualizer.Core.Treemap;
using DriveVisualizer_App.Rendering;
using DriveVisualizer_App.ViewModels;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Storage.Pickers;
using Color = Windows.UI.Color;
using RectangleF = System.Drawing.RectangleF;

namespace DriveVisualizer_App;

public sealed partial class MainPage : Page
{
    public MainViewModel ViewModel { get; }

    private enum VizMode { Treemap = 0, Sunburst = 1, Icicle = 2 }

    private readonly DispatcherQueueTimer _resizeTimer;
    private readonly DispatcherQueueTimer _liveMapTimer;
    private VizMode _vizMode;
    private TreemapResult? _treemap;
    private List<SunburstArc>? _sunburst;
    private List<IcicleRect>? _icicle;
    private Vector2 _sunCenter;
    private float _sunR0, _sunRing;
    private const int SunMaxDepth = 5;
    private float _icicleRowH = 52f;
    private Dictionary<FsNode, FileCategory>? _dominant;

    /// <summary>Folder tint: its dominant content category, blended toward the surface.</summary>
    private Color DirFill(FsNode dir, int depth)
    {
        if (_dominant is { } dom && dom.TryGetValue(dir, out var cat))
        {
            var c = FileCategories.ColorOf(cat);
            float mix = 0.62f;
            return Color.FromArgb(255,
                (byte)(26 + (c.R - 26) * mix),
                (byte)(26 + (c.G - 26) * mix),
                (byte)(25 + (c.B - 25) * mix));
        }
        byte g = (byte)(52 + depth * 8);
        return Color.FromArgb(255, g, g, (byte)(g - 2));
    }
    private FsNode? _treemapRoot;
    private FsNode? _hoverNode;
    private int _layoutVersion;
    private bool _sideBySide;
    private bool _settingsLoading;

    private static readonly Microsoft.Graphics.Canvas.Text.CanvasTextFormat DirLabelFormat = new()
    {
        FontSize = 11,
        FontFamily = "Segoe UI",
        WordWrapping = Microsoft.Graphics.Canvas.Text.CanvasWordWrapping.NoWrap,
    };

    public MainPage()
    {
        ViewModel = new MainViewModel(DispatcherQueue);
        InitializeComponent();
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;

        DriveInfoIcon.Glyph = "";
        ZoomInIcon.Glyph = "";
        ZoomOutSmallIcon.Glyph = "";
        ZoomOutIcon.Glyph = "";        // back arrow
        LayoutToggleIcon.Glyph = "";   // dock bottom

        // Category filter flyout, one toggle per legend color.
        var filterMenu = new Microsoft.UI.Xaml.Controls.MenuFlyout
        {
            Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft,
        };
        foreach (var (category, name, _) in FileCategories.All)
        {
            var item = new Microsoft.UI.Xaml.Controls.ToggleMenuFlyoutItem { Text = name, IsChecked = true };
            var captured = category;
            item.Click += (_, _) => ViewModel.SetCategoryEnabled(captured, item.IsChecked);
            filterMenu.Items.Add(item);
        }
        FilterButton.Flyout = filterMenu;

        // Restore persisted settings without letting the change handlers re-save them.
        _settingsLoading = true;
        SettingsIcon.Glyph = ""; // gear
        ByteFormatter.Detail = (SizeDetail)Math.Clamp(Services.AppSettings.SizeDetail, 0, 2);
        _vizMode = (VizMode)Math.Clamp(Services.AppSettings.VizMode, 0, 2);
        UpdateViewButton();
        _sideBySide = Services.AppSettings.SideBySideLayout;
        if (_sideBySide)
            ApplyLayout();
        _settingsLoading = false;

        _resizeTimer = DispatcherQueue.CreateTimer();
        _resizeTimer.Interval = TimeSpan.FromMilliseconds(150);
        _resizeTimer.IsRepeating = false;
        _resizeTimer.Tick += (_, _) => RecomputeTreemap();

        // While a scan runs, re-lay the treemap from the growing tree so the
        // map assembles itself in front of the user.
        _liveMapTimer = DispatcherQueue.CreateTimer();
        _liveMapTimer.Interval = TimeSpan.FromMilliseconds(800);
        _liveMapTimer.IsRepeating = true;
        _liveMapTimer.Tick += (_, _) => RecomputeTreemap();

        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.ScanRoot))
            {
                _treemapRoot = ViewModel.ScanRoot;
                _dominant = null; // folder tints must be recomputed for the fresh tree
                UpdateZoomBar();
                RecomputeTreemap();
            }
            else if (e.PropertyName == nameof(MainViewModel.LiveRoot))
            {
                if (ViewModel.LiveRoot is { } live)
                {
                    _treemapRoot = live;
                    _dominant = null;
                    UpdateZoomBar();
                    RecomputeTreemap();
                    _liveMapTimer.Start();
                }
                else
                {
                    _liveMapTimer.Stop();
                }
            }
            else if (e.PropertyName == nameof(MainViewModel.TreeVersion))
            {
                // After a delete/compress the zoom root may no longer be attached.
                if (!IsAttachedToScanRoot(_treemapRoot))
                    _treemapRoot = ViewModel.ScanRoot;
                _dominant = null;
                UpdateZoomBar();
                RecomputeTreemap();
            }
            else if (e.PropertyName == nameof(MainViewModel.FilterVersion))
            {
                TreemapCanvas.Invalidate();
            }
            else if (e.PropertyName == nameof(MainViewModel.SelectedRow))
            {
                TreemapCanvas.Invalidate();
            }
        };
    }

    private bool IsAttachedToScanRoot(FsNode? node)
    {
        for (; node is not null; node = node.Parent)
            if (ReferenceEquals(node, ViewModel.ScanRoot))
                return true;
        return false;
    }

    // ---------- Toolbar ----------

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(App.Window!.AppWindow.Id);
            var result = await picker.PickSingleFolderAsync();
            if (result is null)
                return;

            if (!ViewModel.Targets.Contains(result.Path))
                ViewModel.Targets.Add(result.Path);
            ViewModel.SelectedTarget = result.Path;
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = $"Could not open folder picker: {ex.Message}";
        }
    }

    // ---------- View mode ----------

    private void ViewMode_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string tag && int.TryParse(tag, out int mode))
        {
            _vizMode = (VizMode)Math.Clamp(mode, 0, 2);
            Services.AppSettings.VizMode = (int)_vizMode;
            UpdateViewButton();
            RecomputeTreemap();
        }
    }

    private void UpdateViewButton()
    {
        ViewButton.Content = _vizMode.ToString();
        ViewTreemapItem.IsChecked = _vizMode == VizMode.Treemap;
        ViewSunburstItem.IsChecked = _vizMode == VizMode.Sunburst;
        ViewIcicleItem.IsChecked = _vizMode == VizMode.Icicle;
    }

    // ---------- Layout: bottom / side-by-side ----------

    private void LayoutToggle_Click(object sender, RoutedEventArgs e)
    {
        _sideBySide = !_sideBySide;
        Services.AppSettings.SideBySideLayout = _sideBySide;
        ApplyLayout();
        RecomputeTreemap();
    }

    private void Settings_Click(object sender, RoutedEventArgs e) =>
        Frame.Navigate(typeof(SettingsPage), ViewModel);

    private async void DriveInfo_Click(object sender, RoutedEventArgs e)
    {
        string? target = ViewModel.SelectedTarget;
        if (string.IsNullOrWhiteSpace(target))
        {
            DriveInfoText.Text = "Pick a drive or folder first.";
            return;
        }
        DriveInfoText.Text = "Reading drive details…";
        var d = await Task.Run(() => Services.DriveStats.Get(target));
        if (d is null)
        {
            DriveInfoText.Text = "No local drive details available for this target.";
            return;
        }

        long used = d.TotalBytes - d.FreeBytes;
        double usedPct = d.TotalBytes > 0 ? 100.0 * used / d.TotalBytes : 0;
        var lines = new List<string>
        {
            $"Volume     {d.Root}  {(string.IsNullOrEmpty(d.VolumeLabel) ? "(no label)" : d.VolumeLabel)}",
            $"Filesystem {d.FileSystem}   cluster {d.ClusterSize:N0} B",
            $"Capacity   {ByteFormatter.Format(d.TotalBytes)}",
            $"Used       {ByteFormatter.Format(used)}  ({usedPct:F1}%)",
            $"Free       {ByteFormatter.Format(d.FreeBytes)}",
        };
        if (d.Model is not null)
            lines.Add($"Disk       {d.Model}");
        if (d.MediaType is not null)
            lines.Add($"Media      {d.MediaType}{(d.SpindleSpeedRpm is { } rpm ? $" ({rpm:N0} rpm)" : "")}");
        if (d.BusType is not null)
            lines.Add($"Bus        {d.BusType}");
        if (d.SerialNumber is { Length: > 0 })
            lines.Add($"Serial     {d.SerialNumber}");
        if (d.FirmwareVersion is { Length: > 0 })
            lines.Add($"Firmware   {d.FirmwareVersion}");
        if (d.LogicalSectorSize is { } ls && d.PhysicalSectorSize is { } ps)
            lines.Add($"Sectors    {ls:N0} B logical / {ps:N0} B physical");
        if (d.Health is not null)
            lines.Add($"Health     {d.Health}");

        lines.Add("");
        if (d.Smart is { } sm)
        {
            lines.Add($"S.M.A.R.T.  ({sm.Source})");
            lines.Add($"Status     {(sm.CriticalWarning is null ? "OK — no critical warnings" : $"⚠ {sm.CriticalWarning}")}");
            if (sm.TemperatureC is { } t)
                lines.Add($"Temp       {t} °C{(sm.TemperatureMaxC is { } tmax ? $"  (max recorded {tmax} °C)" : "")}");
            if (sm.WearPercent is { } w)
                lines.Add($"Wear       {w}% used{(sm.SparePercent is { } sp ? $"  ·  spare {sp}%" : "")}");
            if (sm.PowerOnHours is { } h)
                lines.Add($"Power-on   {h:N0} h  (~{h / 24:N0} days)");
            if (sm.PowerCycles is { } cyc)
                lines.Add($"Cycles     {cyc:N0} power-on{(sm.UnsafeShutdowns is { } us ? $"  ·  {us:N0} unsafe shutdowns" : "")}");
            if (sm.DataWrittenBytes is { } dw)
                lines.Add($"Written    {ByteFormatter.Format(dw)} lifetime{(sm.DataReadBytes is { } dr ? $"  ·  read {ByteFormatter.Format(dr)}" : "")}");
            if (sm.MediaErrors is { } me)
                lines.Add($"Media errs {me:N0}");
        }
        else
        {
            lines.Add("S.M.A.R.T. counters unavailable for this disk");
            lines.Add("(USB enclosures hide them; running as administrator may help)");
        }

        DriveInfoText.Text = string.Join("\n", lines);
    }

    private void CleanupCheckBox_Toggled(object sender, RoutedEventArgs e)
    {
        ViewModel.CleanupCandidatesOnly = CleanupCheckBox.IsChecked == true;
        TreemapCanvas.Invalidate();
    }



    private void ApplyLayout()
    {
        var rows = RootGrid.RowDefinitions;
        var cols = RootGrid.ColumnDefinitions;

        if (!_sideBySide)
        {
            LayoutToggleText.Text = "Map below";
            LayoutToggleIcon.Glyph = ""; // dock bottom

            rows[2].Height = new GridLength(2, GridUnitType.Star);
            rows[2].MinHeight = 120;
            rows[3].Height = GridLength.Auto;
            rows[4].Height = new GridLength(1.4, GridUnitType.Star);
            rows[4].MinHeight = 160;
            cols[0].Width = new GridLength(1, GridUnitType.Star);
            cols[0].MinWidth = 0;
            cols[1].Width = new GridLength(0);
            cols[2].Width = new GridLength(0);
            cols[2].MinWidth = 0;

            Grid.SetColumnSpan(HeaderGrid, 3);
            Grid.SetColumnSpan(TreeList, 3);

            Grid.SetRow(SplitterBar, 3);
            Grid.SetColumn(SplitterBar, 0);
            Grid.SetColumnSpan(SplitterBar, 3);
            Grid.SetRowSpan(SplitterBar, 1);
            SplitterBar.Height = 12;
            SplitterBar.Width = double.NaN;
            SplitterBar.ManipulationMode = ManipulationModes.TranslateY;
            SplitterGrip.Width = 48;
            SplitterGrip.Height = 4;

            Grid.SetRow(TreemapHost, 4);
            Grid.SetColumn(TreemapHost, 0);
            Grid.SetColumnSpan(TreemapHost, 3);
            Grid.SetRowSpan(TreemapHost, 1);
        }
        else
        {
            LayoutToggleText.Text = "Map right";
            LayoutToggleIcon.Glyph = ""; // dock right

            rows[2].Height = new GridLength(1, GridUnitType.Star);
            rows[2].MinHeight = 120;
            rows[3].Height = new GridLength(0);
            rows[4].Height = new GridLength(0);
            rows[4].MinHeight = 0;
            cols[0].Width = new GridLength(1.2, GridUnitType.Star);
            cols[0].MinWidth = 320;
            cols[1].Width = GridLength.Auto;
            cols[2].Width = new GridLength(1, GridUnitType.Star);
            cols[2].MinWidth = 260;

            Grid.SetColumnSpan(HeaderGrid, 1);
            Grid.SetColumnSpan(TreeList, 1);

            Grid.SetRow(SplitterBar, 1);
            Grid.SetColumn(SplitterBar, 1);
            Grid.SetColumnSpan(SplitterBar, 1);
            Grid.SetRowSpan(SplitterBar, 2);
            SplitterBar.Height = double.NaN;
            SplitterBar.Width = 12;
            SplitterBar.ManipulationMode = ManipulationModes.TranslateX;
            SplitterGrip.Width = 4;
            SplitterGrip.Height = 48;

            Grid.SetRow(TreemapHost, 1);
            Grid.SetColumn(TreemapHost, 2);
            Grid.SetColumnSpan(TreemapHost, 1);
            Grid.SetRowSpan(TreemapHost, 2);
        }
    }

    private void Splitter_ManipulationDelta(object sender, ManipulationDeltaRoutedEventArgs e)
    {
        if (!_sideBySide)
        {
            double target = TreeList.ActualHeight + e.Delta.Translation.Y;
            double max = Math.Max(140, RootGrid.ActualHeight - 320);
            RootGrid.RowDefinitions[2].Height =
                new GridLength(Math.Clamp(target, 120, max), GridUnitType.Pixel);
        }
        else
        {
            double target = RootGrid.ColumnDefinitions[0].ActualWidth + e.Delta.Translation.X;
            double max = Math.Max(340, RootGrid.ActualWidth - 300);
            RootGrid.ColumnDefinitions[0].Width =
                new GridLength(Math.Clamp(target, 320, max), GridUnitType.Pixel);
        }
    }

    // ---------- Tree ----------

    private void Chevron_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is NodeRow row)
            ViewModel.ToggleExpand(row);
    }

    private void TreeList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is NodeRow row)
            ViewModel.ToggleExpand(row);
    }

    // ---------- Reports & snapshots ----------

    private void Report_Diff(object sender, RoutedEventArgs e) => OpenDiffReport();

    private void OpenDiffReport()
    {
        if (ViewModel is not { CurrentSnapshot: { } current, AutoBaseline: { } baseline })
        {
            ViewModel.StatusText = "No previous scan of this target to diff against yet.";
            return;
        }
        string html = DriveVisualizer.Core.Snapshots.ReportGenerator.BuildHtml(current, baseline,
            ViewModel.AutoBaselinePath is { } p ? $"auto-saved snapshot — {p}" : "auto-saved snapshot");
        string path = Path.Combine(Path.GetTempPath(), $"DriveVisualizer-diff-{DateTime.Now:yyyyMMdd-HHmmss}.html");
        File.WriteAllText(path, html);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
    }

    /// <summary>Guards the report/history builders against double-clicks while busy.</summary>
    private bool _reportBusy;

    private async void Report_History(object sender, RoutedEventArgs e)
    {
        // History reads stored snapshots — no scan needed, just a target.
        string? target = ViewModel.CurrentSnapshot?.Target ?? ViewModel.SelectedTarget;
        if (string.IsNullOrWhiteSpace(target))
        {
            ViewModel.StatusText = "Pick a drive or folder first.";
            return;
        }
        if (_reportBusy)
            return;
        _reportBusy = true;
        ViewModel.StatusText = "Building size history — this loads every stored snapshot, give it a moment…";
        try
        {
            string dir = MainViewModel.GetHistoryDirectory(target);
            var files = Directory.Exists(dir)
                ? Directory.GetFiles(dir, "*.dvsnap").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray()
                : [];
            if (files.Length < 2)
            {
                string autoSaveNote = Services.AppSettings.AutoSaveSnapshots
                    ? ""
                    : "\n\n⚠ Auto-save is currently OFF in Settings, so no history is being recorded at all.";
                var dialog = new ContentDialog
                {
                    Title = "Size history — how it works",
                    Content =
                        "Each completed scan records a snapshot of the drive's totals (overall size plus " +
                        "each category: apps, temp files, disk images, …). How often a new snapshot is kept " +
                        "— per scan, hour, day, or week — is set in Settings, along with how many to retain.\n\n" +
                        "Once two or more snapshots exist, this menu turns them into a chart with per-category " +
                        "breakdowns and the folders that changed between snapshots.\n\n" +
                        $"Recorded so far: {files.Length} snapshot{(files.Length == 1 ? "" : "s")} for {target}. " +
                        "Note: snapshots are only taken while the app is running a scan — there is no background service." +
                        autoSaveNote,
                    CloseButtonText = "Got it",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = XamlRoot,
                };
                await dialog.ShowAsync();
                return;
            }
            string html = await Task.Run(() =>
            {
                var history = files.AsParallel().AsOrdered()
                    .Select(DriveVisualizer.Core.Snapshots.ScanSnapshot.Load)
                    .ToList();
                return DriveVisualizer.Core.Snapshots.HistoryChart.BuildHtml(history);
            });
            string path = Path.Combine(Path.GetTempPath(), $"DriveVisualizer-history-{DateTime.Now:yyyyMMdd-HHmmss}.html");
            await File.WriteAllTextAsync(path, html);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            ViewModel.StatusText = $"Size history opened in your browser ({files.Length} snapshots).";
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = $"Could not build history: {ex.Message}";
        }
        finally
        {
            _reportBusy = false;
        }
    }

    private async void Report_SaveSnapshot(object sender, RoutedEventArgs e)
    {
        if (ViewModel.CurrentSnapshot is not { } snapshot)
        {
            ViewModel.StatusText = "Run a scan first.";
            return;
        }
        try
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(App.Window!.AppWindow.Id)
            {
                SuggestedFileName = $"scan-{DateTime.Now:yyyy-MM-dd}",
                DefaultFileExtension = ".dvsnap",
            };
            picker.FileTypeChoices.Add("DriveVisualizer snapshot", [".dvsnap"]);
            var result = await picker.PickSaveFileAsync();
            if (result is null)
                return;
            await Task.Run(() => snapshot.Save(result.Path));
            ViewModel.StatusText = $"Snapshot saved to {result.Path}";
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = $"Could not save snapshot: {ex.Message}";
        }
    }

    private async void Report_Export(object sender, RoutedEventArgs e)
    {
        if (ViewModel.CurrentSnapshot is not { } snapshot)
        {
            ViewModel.StatusText = "Run a scan first.";
            return;
        }
        try
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(App.Window!.AppWindow.Id)
            {
                SuggestedFileName = $"DriveVisualizer-report-{DateTime.Now:yyyy-MM-dd}",
                DefaultFileExtension = ".html",
            };
            picker.FileTypeChoices.Add("HTML report", [".html"]);
            var result = await picker.PickSaveFileAsync();
            if (result is null)
                return;
            string html = await Task.Run(() =>
                DriveVisualizer.Core.Snapshots.ReportGenerator.BuildHtml(snapshot, ViewModel.AutoBaseline,
                    ViewModel.AutoBaselinePath is { } p ? $"auto-saved snapshot — {p}" : null));
            await File.WriteAllTextAsync(result.Path, html);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(result.Path) { UseShellExecute = true });
            ViewModel.StatusText = $"Report saved to {result.Path}";
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = $"Could not export report: {ex.Message}";
        }
    }

    private async void Report_Compare(object sender, RoutedEventArgs e)
    {
        if (ViewModel.CurrentSnapshot is not { } snapshot)
        {
            ViewModel.StatusText = "Run a scan first.";
            return;
        }
        try
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(App.Window!.AppWindow.Id);
            picker.FileTypeFilter.Add(".dvsnap");
            var result = await picker.PickSingleFileAsync();
            if (result is null)
                return;
            string html = await Task.Run(() =>
            {
                var baseline = DriveVisualizer.Core.Snapshots.ScanSnapshot.Load(result.Path);
                return DriveVisualizer.Core.Snapshots.ReportGenerator.BuildHtml(snapshot, baseline, result.Path);
            });
            string path = Path.Combine(Path.GetTempPath(), $"DriveVisualizer-compare-{DateTime.Now:yyyyMMdd-HHmmss}.html");
            await File.WriteAllTextAsync(path, html);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = $"Could not compare: {ex.Message}";
        }
    }

    // ---------- Context-menu actions ----------

    private NodeRow? RowFrom(object sender) =>
        (sender as FrameworkElement)?.DataContext as NodeRow ?? ViewModel.SelectedRow;

    /// <summary>Per-target menu shaping: no compress for archives, refresh only for folders.</summary>
    private void RowMenu_Opening(object sender, object e)
    {
        if (sender is not Microsoft.UI.Xaml.Controls.MenuFlyout menu)
            return;
        var row = (menu.Target as FrameworkElement)?.DataContext as NodeRow ?? ViewModel.SelectedRow;
        foreach (var item in menu.Items)
        {
            if (item is not MenuFlyoutItem mi || mi.Tag is not string tag)
                continue;
            mi.Visibility = tag switch
            {
                "compress" => row?.Node is { Parent: not null } n &&
                              !(n.IsDirectory is false && FileCategories.Classify(n.Name) == FileCategory.Archives)
                    ? Visibility.Visible : Visibility.Collapsed,
                "refresh" => row?.Node.IsDirectory == true
                    ? Visibility.Visible : Visibility.Collapsed,
                _ => mi.Visibility,
            };
        }
    }

    private async void Menu_RefreshFolder(object sender, RoutedEventArgs e)
    {
        if (RowFrom(sender)?.Node is { } node && node.IsDirectory)
            await ViewModel.RefreshFolderAsync(node);
    }

    private void WatchCheckBox_Toggled(object sender, RoutedEventArgs e) =>
        ViewModel.WatchForChanges = WatchCheckBox.IsChecked == true;

    private void Menu_Open(object sender, RoutedEventArgs e)
    {
        if (RowFrom(sender)?.Node is { } node)
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(node.GetFullPath()) { UseShellExecute = true });
    }

    private void Menu_Reveal(object sender, RoutedEventArgs e)
    {
        if (RowFrom(sender)?.Node is { } node)
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{node.GetFullPath()}\"");
    }

    private void Menu_CopyPath(object sender, RoutedEventArgs e)
    {
        if (RowFrom(sender)?.Node is { } node)
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(node.GetFullPath());
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        }
    }

    private async void Menu_Recycle(object sender, RoutedEventArgs e) =>
        await DeleteAsync(RowFrom(sender), permanent: false);

    private async void Menu_DeletePermanent(object sender, RoutedEventArgs e) =>
        await DeleteAsync(RowFrom(sender), permanent: true);

    private async void DeleteAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ViewModel.SelectedRow is { } row)
        {
            args.Handled = true;
            await DeleteAsync(row, permanent: false);
        }
    }

    private async void ShiftDeleteAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ViewModel.SelectedRow is { } row)
        {
            args.Handled = true;
            await DeleteAsync(row, permanent: true);
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

    private static string Describe(NodeRow row) =>
        row.Node.IsDirectory
            ? $"the folder \"{row.Node.Name}\" ({row.SizeText}, {row.Node.SubtreeFileCount:N0} files)"
            : $"\"{row.Node.Name}\" ({row.SizeText})";

    private async Task DeleteAsync(NodeRow? row, bool permanent)
    {
        if (row?.Node is not { Parent: not null } node || ViewModel.IsScanning)
            return;

        string path = node.GetFullPath();
        bool confirmed = permanent
            ? await ConfirmAsync("Delete permanently?",
                $"Permanently delete {Describe(row)}?\n\n{path}\n\nThis cannot be undone.",
                "Delete permanently")
            : await ConfirmAsync("Move to Recycle Bin?",
                $"Move {Describe(row)} to the Recycle Bin?\n\n{path}",
                "Recycle");
        if (!confirmed)
            return;

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.Window);
        bool gone = Interop.ShellFileOps.Delete(path, permanent, hwnd);
        if (gone)
        {
            ViewModel.RemoveNode(node);
            ViewModel.StatusText = $"Deleted {node.Name} — freed {ByteFormatter.Format(node.AllocatedSize)}";
        }
        else
        {
            ViewModel.StatusText = $"Delete of {node.Name} was cancelled or failed";
        }
    }

    private async void Menu_Compress(object sender, RoutedEventArgs e)
    {
        var row = RowFrom(sender);
        if (row?.Node is not { Parent: not null } node || ViewModel.IsScanning)
            return;

        if (!node.IsDirectory && FileCategories.Classify(node.Name) == FileCategory.Archives)
        {
            ViewModel.StatusText = $"{node.Name} is already compressed — compressing it again wouldn't help.";
            return;
        }

        string path = node.GetFullPath();
        string zipPath = Services.ZipCompressor.MakeZipPath(path);
        bool confirmed = await ConfirmAsync("Compress and delete original?",
            $"Compress {Describe(row)} into\n\n{zipPath}\n\nand then permanently delete the original? " +
            "Space savings depend on how compressible the content is.",
            "Compress & delete");
        if (!confirmed)
            return;

        ViewModel.StatusText = $"Compressing {node.Name}…";
        try
        {
            long zipSize = await Services.ZipCompressor.CompressAsync(path, zipPath, node.IsDirectory);

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.Window);
            bool gone = Interop.ShellFileOps.Delete(path, permanent: true, hwnd);

            var parent = node.Parent!;
            if (gone)
                ViewModel.RemoveNode(node);
            ViewModel.AddFile(parent, Path.GetFileName(zipPath), zipSize);

            long freed = node.AllocatedSize - zipSize;
            ViewModel.StatusText = gone
                ? $"Compressed {node.Name} to {ByteFormatter.Format(zipSize)} — freed {ByteFormatter.Format(Math.Max(0, freed))}"
                : $"Compressed {node.Name}, but the original could not be deleted";
        }
        catch (Exception ex)
        {
            try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { }
            ViewModel.StatusText = $"Compress failed: {ex.Message}";
        }
    }

    // ---------- Treemap ----------

    private async void RecomputeTreemap()
    {
        int version = ++_layoutVersion;
        var root = _treemapRoot;
        float w = (float)TreemapCanvas.ActualWidth;
        float h = (float)TreemapCanvas.ActualHeight;

        if (root is null || w <= 1 || h <= 1)
        {
            _treemap = null;
            _sunburst = null;
            _icicle = null;
            TreemapCanvas.Invalidate();
            return;
        }

        switch (_vizMode)
        {
            case VizMode.Sunburst:
            {
                var scanRoot = ViewModel.ScanRoot ?? ViewModel.LiveRoot;
                var (arcs, dom) = await Task.Run(() =>
                    (SunburstLayout.Compute(root, SunMaxDepth),
                     _dominant ?? (scanRoot is null ? null : DominantCategories.Compute(scanRoot))));
                if (version != _layoutVersion)
                    return;
                _sunburst = arcs;
                _dominant ??= dom;
                break;
            }
            case VizMode.Icicle:
            {
                int rows = Math.Clamp((int)(h / 52f), 3, 8);
                var scanRoot = ViewModel.ScanRoot ?? ViewModel.LiveRoot;
                var (rects, dom) = await Task.Run(() =>
                    (IcicleLayout.Compute(root, rows),
                     _dominant ?? (scanRoot is null ? null : DominantCategories.Compute(scanRoot))));
                if (version != _layoutVersion)
                    return;
                _icicle = rects;
                _dominant ??= dom;
                break;
            }
            default:
            {
                var result = await Task.Run(() => TreemapLayout.Compute(root, w, h));
                if (version != _layoutVersion)
                    return; // superseded by a newer layout request
                _treemap = result;
                break;
            }
        }
        TreemapCanvas.Invalidate();
    }

    private void TreemapCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var ds = args.DrawingSession;

        // Chart surface (dark, matches the validated dark palette steps).
        ds.Clear(Color.FromArgb(255, 26, 26, 25));

        if (_vizMode == VizMode.Sunburst)
        {
            DrawSunburst(sender, ds);
            return;
        }
        if (_vizMode == VizMode.Icicle)
        {
            DrawIcicle(ds);
            return;
        }

        if (_treemap is not { } treemap || treemap.Tiles.Count == 0)
        {
            ds.DrawText("Scan a drive to see the treemap", 12, 12, Color.FromArgb(255, 137, 135, 129));
            return;
        }

        // Shared cushion gradient: light falls from the top-left, shade gathers
        // bottom-right. Repositioned per tile — one brush for the whole draw.
        using var cushion = new CanvasLinearGradientBrush(sender,
        [
            new CanvasGradientStop { Position = 0.0f, Color = Color.FromArgb(85, 255, 255, 255) },
            new CanvasGradientStop { Position = 0.55f, Color = Color.FromArgb(0, 255, 255, 255) },
            new CanvasGradientStop { Position = 1.0f, Color = Color.FromArgb(110, 0, 0, 0) },
        ]);

        var tileEdge = Color.FromArgb(70, 0, 0, 0);
        var aggregateFill = Color.FromArgb(255, 56, 56, 53);

        var surface = Color.FromArgb(255, 26, 26, 25);
        foreach (var tile in treemap.Tiles)
        {
            var r = tile.Rect;

            bool filteredOut = !tile.IsAggregate && !tile.Node.IsDirectory &&
                (!ViewModel.IsCategoryEnabled(FileCategories.Classify(tile.Node.Name)) ||
                 (ViewModel.CleanupCandidatesOnly && !tile.Node.Flags.HasFlag(NodeFlags.CleanupCandidate)));
            if (filteredOut)
            {
                // Ghost of the original color, so it reads as "dimmed", not recolored.
                var c = FileCategories.TileColor(tile.Node.Name);
                ds.FillRectangle(r.X, r.Y, r.Width, r.Height, surface);
                ds.FillRectangle(r.X, r.Y, r.Width, r.Height, Color.FromArgb(45, c.R, c.G, c.B));
                continue;
            }

            Color fill = tile.IsAggregate ? aggregateFill : FileCategories.TileColor(tile.Node.Name);
            ds.FillRectangle(r.X, r.Y, r.Width, r.Height, fill);

            cushion.StartPoint = new Vector2(r.X, r.Y);
            cushion.EndPoint = new Vector2(r.Right, r.Bottom);
            ds.FillRectangle(r.X, r.Y, r.Width, r.Height, cushion);

            if (r.Width > 5 && r.Height > 5)
                ds.DrawRectangle(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1, tileEdge, 1f);
        }

        // Subtle dark seams around directories give grouping without shouting.
        var dirOutline = Color.FromArgb(150, 13, 13, 13);
        foreach (var (dir, rect) in treemap.DirectoryBounds)
        {
            if (dir != _treemapRoot && rect.Width > 24 && rect.Height > 24)
                ds.DrawRectangle(rect.X, rect.Y, rect.Width, rect.Height, dirOutline, 1.5f);
        }

        // Folder names on regions large enough to fit them, so the nesting reads at a
        // glance. A child whose corner coincides with a labeled ancestor stays quiet —
        // otherwise nested labels overprint into garble.
        bool IsLabelable(FsNode d, RectangleF rr) =>
            !ReferenceEquals(d, _treemapRoot) && rr.Width >= 72 && rr.Height >= 30;

        foreach (var (dir, rect) in treemap.DirectoryBounds)
        {
            if (!IsLabelable(dir, rect))
                continue;

            bool suppressed = false;
            for (var ancestor = dir.Parent; ancestor is not null; ancestor = ancestor.Parent)
            {
                if (treemap.DirectoryBounds.TryGetValue(ancestor, out var ar) && IsLabelable(ancestor, ar) &&
                    Math.Abs(ar.X - rect.X) < 10 && Math.Abs(ar.Y - rect.Y) < 18)
                {
                    suppressed = true;
                    break;
                }
            }
            if (suppressed)
                continue;

            // Dark backing pill sized to the text keeps labels readable over any mosaic.
            using var layout = new Microsoft.Graphics.Canvas.Text.CanvasTextLayout(
                ds, dir.Name, DirLabelFormat, Math.Max(8f, rect.Width - 10f), 18f);
            float textWidth = (float)layout.LayoutBounds.Width;
            using var clip = ds.CreateLayer(1f, new Windows.Foundation.Rect(rect.X, rect.Y, rect.Width - 2, rect.Height));
            ds.FillRoundedRectangle(
                rect.X + 2f, rect.Y + 2f,
                Math.Min(textWidth + 10f, rect.Width - 5f), 17f,
                3f, 3f, Color.FromArgb(165, 10, 10, 10));
            ds.DrawTextLayout(layout, rect.X + 7f, rect.Y + 2f, Color.FromArgb(240, 255, 255, 255));
        }

        // Selection highlight: a directory gets its recorded bounds, a file its tile.
        if (ViewModel.SelectedRow?.Node is { } selected)
        {
            RectangleF? highlight = null;
            if (treemap.DirectoryBounds.TryGetValue(selected, out var dirRect))
                highlight = dirRect;
            else
                foreach (var tile in treemap.Tiles)
                    if (ReferenceEquals(tile.Node, selected))
                    {
                        highlight = tile.Rect;
                        break;
                    }

            if (highlight is { } hr)
            {
                ds.DrawRectangle(hr.X, hr.Y, hr.Width, hr.Height, Color.FromArgb(200, 0, 0, 0), 4f);
                ds.DrawRectangle(hr.X, hr.Y, hr.Width, hr.Height, Colors.White, 2f);
            }
        }
    }

    private void DrawSunburst(CanvasControl sender, Microsoft.Graphics.Canvas.CanvasDrawingSession ds)
    {
        if (_sunburst is not { Count: > 0 } arcs || _treemapRoot is not { } root)
        {
            ds.DrawText("Scan a drive to see the sunburst", 12, 12, Color.FromArgb(255, 137, 135, 129));
            return;
        }

        float w = (float)TreemapCanvas.ActualWidth, h = (float)TreemapCanvas.ActualHeight;
        _sunCenter = new Vector2(w / 2f, h / 2f);
        float maxR = Math.Min(w, h) / 2f - 8f;
        _sunR0 = Math.Max(34f, maxR * 0.16f);
        _sunRing = (maxR - _sunR0) / SunMaxDepth;

        var surface = Color.FromArgb(255, 26, 26, 25);
        var selected = ViewModel.SelectedRow?.Node;
        Microsoft.Graphics.Canvas.Geometry.CanvasGeometry? selectedGeo = null;

        foreach (var arc in arcs)
        {
            float rIn = _sunR0 + (arc.Depth - 1) * _sunRing;
            float rOut = rIn + _sunRing - 2f;
            var geo = BuildArcGeometry(sender, _sunCenter, rIn, rOut, arc.StartAngle, arc.SweepAngle);

            Color fill;
            if (arc.Node.IsDirectory)
            {
                fill = DirFill(arc.Node, arc.Depth);
            }
            else if (IsGhosted(arc.Node))
            {
                var c = FileCategories.TileColor(arc.Node.Name);
                fill = Color.FromArgb(255,
                    (byte)(26 + c.R * 45 / 255), (byte)(26 + c.G * 45 / 255), (byte)(25 + c.B * 45 / 255));
            }
            else
            {
                fill = FileCategories.TileColor(arc.Node.Name);
            }

            ds.FillGeometry(geo, fill);
            ds.DrawGeometry(geo, surface, 1.5f);

            if (ReferenceEquals(arc.Node, selected))
                selectedGeo = geo;
            else
                geo.Dispose();
        }

        if (selectedGeo is not null)
        {
            ds.DrawGeometry(selectedGeo, Colors.White, 2.5f);
            selectedGeo.Dispose();
        }

        // Labels on arcs long enough to carry them.
        foreach (var arc in arcs)
        {
            float rMid = _sunR0 + (arc.Depth - 0.5f) * _sunRing;
            if (arc.SweepAngle * rMid < 56f)
                continue;
            float mid = arc.StartAngle + arc.SweepAngle / 2f;
            var pos = _sunCenter + rMid * new Vector2(MathF.Cos(mid), MathF.Sin(mid));
            using var layout = new Microsoft.Graphics.Canvas.Text.CanvasTextLayout(
                ds, arc.Node.Name, DirLabelFormat, arc.SweepAngle * rMid, 18f);
            float tw = (float)layout.LayoutBounds.Width;
            ds.DrawTextLayout(layout, pos.X - tw / 2f + 1, pos.Y - 8f + 1, Color.FromArgb(170, 0, 0, 0));
            ds.DrawTextLayout(layout, pos.X - tw / 2f, pos.Y - 8f, Color.FromArgb(240, 255, 255, 255));
        }

        // Center disc: current root, size, and (when zoomed) a hint that it goes up.
        ds.FillCircle(_sunCenter, _sunR0 - 3f, Color.FromArgb(255, 38, 38, 36));
        ds.DrawCircle(_sunCenter, _sunR0 - 3f, Color.FromArgb(255, 74, 74, 71), 1f);
        string rootName = Path.GetFileName(root.Name.TrimEnd('\\'));
        if (string.IsNullOrEmpty(rootName))
            rootName = root.Name;
        using (var nameLayout = new Microsoft.Graphics.Canvas.Text.CanvasTextLayout(ds, rootName, DirLabelFormat, _sunR0 * 1.7f, 20f))
        {
            float tw = (float)nameLayout.LayoutBounds.Width;
            ds.DrawTextLayout(nameLayout, _sunCenter.X - tw / 2f, _sunCenter.Y - 18f, Colors.White);
        }
        string sizeText = ByteFormatter.Format(root.AllocatedSize);
        using (var sizeLayout = new Microsoft.Graphics.Canvas.Text.CanvasTextLayout(ds, sizeText, DirLabelFormat, _sunR0 * 1.9f, 20f))
        {
            float tw = (float)sizeLayout.LayoutBounds.Width;
            ds.DrawTextLayout(sizeLayout, _sunCenter.X - tw / 2f, _sunCenter.Y + 1f, Color.FromArgb(255, 195, 194, 183));
        }
        if (ZoomBar.Visibility == Visibility.Visible)
        {
            using var upLayout = new Microsoft.Graphics.Canvas.Text.CanvasTextLayout(ds, "click: up", DirLabelFormat, _sunR0 * 1.9f, 20f);
            float tw = (float)upLayout.LayoutBounds.Width;
            ds.DrawTextLayout(upLayout, _sunCenter.X - tw / 2f, _sunCenter.Y + 18f, Color.FromArgb(255, 137, 135, 129));
        }
    }

    private static Microsoft.Graphics.Canvas.Geometry.CanvasGeometry BuildArcGeometry(
        CanvasControl sender, Vector2 center, float rIn, float rOut, float start, float sweep)
    {
        using var pb = new Microsoft.Graphics.Canvas.Geometry.CanvasPathBuilder(sender);
        var p0 = center + rIn * new Vector2(MathF.Cos(start), MathF.Sin(start));
        pb.BeginFigure(p0);
        pb.AddArc(center, rIn, rIn, start, sweep);
        float end = start + sweep;
        pb.AddLine(center + rOut * new Vector2(MathF.Cos(end), MathF.Sin(end)));
        pb.AddArc(center, rOut, rOut, end, -sweep);
        pb.EndFigure(Microsoft.Graphics.Canvas.Geometry.CanvasFigureLoop.Closed);
        return Microsoft.Graphics.Canvas.Geometry.CanvasGeometry.CreatePath(pb);
    }

    private void DrawIcicle(Microsoft.Graphics.Canvas.CanvasDrawingSession ds)
    {
        if (_icicle is not { Count: > 0 } rects)
        {
            ds.DrawText("Scan a drive to see the icicle view", 12, 12, Color.FromArgb(255, 137, 135, 129));
            return;
        }

        float w = (float)TreemapCanvas.ActualWidth, h = (float)TreemapCanvas.ActualHeight;
        int depthCount = rects.Max(r => r.Depth) + 1;
        _icicleRowH = Math.Min(64f, (h - 2f) / depthCount);
        var selected = ViewModel.SelectedRow?.Node;
        var surface = Color.FromArgb(255, 26, 26, 25);
        RectangleF? selectedRect = null;

        foreach (var rect in rects)
        {
            float x = rect.X0 * w;
            float width = (rect.X1 - rect.X0) * w;
            float y = rect.Depth * _icicleRowH;
            var pixel = new RectangleF(x, y, Math.Max(1f, width - 1.5f), _icicleRowH - 3f);

            Color fill;
            if (rect.Node.IsDirectory)
            {
                fill = DirFill(rect.Node, rect.Depth);
            }
            else if (IsGhosted(rect.Node))
            {
                var c = FileCategories.TileColor(rect.Node.Name);
                fill = Color.FromArgb(255,
                    (byte)(26 + c.R * 45 / 255), (byte)(26 + c.G * 45 / 255), (byte)(25 + c.B * 45 / 255));
            }
            else
            {
                fill = FileCategories.TileColor(rect.Node.Name);
            }

            ds.FillRoundedRectangle(pixel.X, pixel.Y, pixel.Width, pixel.Height, 3f, 3f, fill);
            ds.DrawRoundedRectangle(pixel.X, pixel.Y, pixel.Width, pixel.Height, 3f, 3f, surface, 1f);

            if (ReferenceEquals(rect.Node, selected))
                selectedRect = pixel;

            if (pixel.Width > 64f)
            {
                using var clip = ds.CreateLayer(1f, new Windows.Foundation.Rect(pixel.X, pixel.Y, pixel.Width - 4, pixel.Height));
                ds.DrawText(rect.Node.Name, pixel.X + 7f, pixel.Y + 4f, Colors.White, DirLabelFormat);
                if (_icicleRowH >= 42f)
                    ds.DrawText(ByteFormatter.Format(rect.Node.AllocatedSize), pixel.X + 7f, pixel.Y + 21f,
                        Color.FromArgb(200, 255, 255, 255), DirLabelFormat);
            }
        }

        if (selectedRect is { } sr)
            ds.DrawRoundedRectangle(sr.X, sr.Y, sr.Width, sr.Height, 3f, 3f, Colors.White, 2.5f);
    }

    private bool IsGhosted(FsNode node) =>
        !node.IsDirectory &&
        (!ViewModel.IsCategoryEnabled(FileCategories.Classify(node.Name)) ||
         (ViewModel.CleanupCandidatesOnly && !node.Flags.HasFlag(NodeFlags.CleanupCandidate)));

    private Tile? HitTest(Windows.Foundation.Point p)
    {
        if (_treemap is not { } treemap)
            return null;
        foreach (var tile in treemap.Tiles)
            if (tile.Rect.Contains((float)p.X, (float)p.Y))
                return tile;
        return null;
    }

    /// <summary>Mode-aware hit test: node under the pointer plus whether it's an aggregate tile.</summary>
    private (FsNode Node, bool IsAggregate)? HitNode(Windows.Foundation.Point p)
    {
        switch (_vizMode)
        {
            case VizMode.Sunburst:
            {
                if (_sunburst is not { } arcs || _sunRing <= 0)
                    return null;
                float dx = (float)p.X - _sunCenter.X, dy = (float)p.Y - _sunCenter.Y;
                float r = MathF.Sqrt(dx * dx + dy * dy);
                if (r < _sunR0)
                    return null;
                int depth = (int)((r - _sunR0) / _sunRing) + 1;
                if (depth > SunMaxDepth)
                    return null;
                float angle = MathF.Atan2(dy, dx);
                while (angle < -MathF.PI / 2f)
                    angle += MathF.Tau;
                foreach (var arc in arcs)
                    if (arc.Depth == depth && angle >= arc.StartAngle && angle < arc.StartAngle + arc.SweepAngle)
                        return (arc.Node, false);
                return null;
            }
            case VizMode.Icicle:
            {
                if (_icicle is not { } rects || _icicleRowH <= 0)
                    return null;
                int depth = (int)(p.Y / _icicleRowH);
                float x = (float)(p.X / TreemapCanvas.ActualWidth);
                foreach (var rect in rects)
                    if (rect.Depth == depth && x >= rect.X0 && x < rect.X1)
                        return (rect.Node, false);
                return null;
            }
            default:
                return HitTest(p) is { } tile ? (tile.Node, tile.IsAggregate) : null;
        }
    }

    private void TreemapCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var pos = e.GetCurrentPoint(TreemapCanvas).Position;
        var hit = HitNode(pos);
        var node = hit?.Node;

        if (node is null)
        {
            HoverTip.Visibility = Visibility.Collapsed;
            _hoverNode = null;
            return;
        }

        if (!ReferenceEquals(node, _hoverNode))
        {
            _hoverNode = node;
            string category = node.IsDirectory
                ? "folder"
                : FileCategories.NameOf(FileCategories.Classify(node.Name));
            HoverTipText.Text = hit!.Value.IsAggregate
                ? $"{node.GetFullPath()}  ·  small items  ·  {ByteFormatter.Format(node.AllocatedSize)}"
                : $"{node.GetFullPath()}  ·  {category}  ·  {ByteFormatter.Format(node.AllocatedSize)}";
        }

        HoverTip.Margin = new Thickness(
            Math.Min(pos.X + 14, Math.Max(0, TreemapCanvas.ActualWidth - HoverTip.ActualWidth - 4)),
            Math.Min(pos.Y + 18, Math.Max(0, TreemapCanvas.ActualHeight - HoverTip.ActualHeight - 4)),
            0, 0);
        HoverTip.Visibility = Visibility.Visible;
    }

    private void TreemapCanvas_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        HoverTip.Visibility = Visibility.Collapsed;
        _hoverNode = null;
    }

    private void TreemapCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(TreemapCanvas);

        // Sunburst center disc acts as "go up one level" when zoomed.
        if (_vizMode == VizMode.Sunburst && ZoomBar.Visibility == Visibility.Visible)
        {
            float dx = (float)point.Position.X - _sunCenter.X, dy = (float)point.Position.Y - _sunCenter.Y;
            if (MathF.Sqrt(dx * dx + dy * dy) < _sunR0)
            {
                ZoomOutOneLevel();
                return;
            }
        }

        if (HitNode(point.Position)?.Node is not { } node)
            return;

        ViewModel.SelectNode(node);
        if (ViewModel.SelectedRow is { } row)
            TreeList.ScrollIntoView(row);

        if (point.Properties.IsRightButtonPressed &&
            Resources["MapMenu"] is Microsoft.UI.Xaml.Controls.MenuFlyout menu)
        {
            menu.ShowAt(TreemapCanvas, point.Position);
        }
    }

    private void TreemapCanvas_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var pos = e.GetPosition(TreemapCanvas);
        if (HitNode(pos)?.Node is not { } node)
            return;

        var dir = node.IsDirectory ? node : node.Parent;
        if (dir is null || ReferenceEquals(dir, _treemapRoot))
            return;

        _treemapRoot = dir;
        UpdateZoomBar();
        RecomputeTreemap();
    }

    private void ZoomOut_Click(object sender, RoutedEventArgs e) => ZoomOutOneLevel();

    private void ZoomIn_Click(object sender, RoutedEventArgs e)
    {
        var node = ViewModel.SelectedRow?.Node;
        var dir = node is null ? null : node.IsDirectory ? node : node.Parent;
        if (dir is null || ReferenceEquals(dir, _treemapRoot) || !IsAttachedToScanRoot(dir))
        {
            ViewModel.StatusText = "Select a folder (in the tree or the map) to zoom into.";
            return;
        }
        _treemapRoot = dir;
        UpdateZoomBar();
        RecomputeTreemap();
    }

    private void EscapeAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ZoomBar.Visibility == Visibility.Visible)
        {
            ZoomOutOneLevel();
            args.Handled = true;
        }
    }

    private void ZoomOutOneLevel()
    {
        _treemapRoot = _treemapRoot?.Parent ?? ViewModel.ScanRoot;
        UpdateZoomBar();
        RecomputeTreemap();
    }

    private void TreemapCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        _resizeTimer.Stop();
        _resizeTimer.Start();
    }

    private void UpdateZoomBar()
    {
        bool zoomed = _treemapRoot is not null
            && !ReferenceEquals(_treemapRoot, ViewModel.ScanRoot)
            && !ReferenceEquals(_treemapRoot, ViewModel.LiveRoot);
        ZoomBar.Visibility = zoomed ? Visibility.Visible : Visibility.Collapsed;
        ZoomPathText.Text = zoomed ? _treemapRoot!.GetFullPath() : "";
    }
}
