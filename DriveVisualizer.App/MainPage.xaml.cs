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

    private readonly DispatcherQueueTimer _resizeTimer;
    private readonly DispatcherQueueTimer _liveMapTimer;
    private TreemapResult? _treemap;
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
        AutoSaveToggle.IsOn = Services.AppSettings.AutoSaveSnapshots;
        ByteFormatter.Detail = (SizeDetail)Math.Clamp(Services.AppSettings.SizeDetail, 0, 2);
        SizeDetailCombo.SelectedIndex = (int)ByteFormatter.Detail;
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
                UpdateZoomBar();
                RecomputeTreemap();
            }
            else if (e.PropertyName == nameof(MainViewModel.LiveRoot))
            {
                if (ViewModel.LiveRoot is { } live)
                {
                    _treemapRoot = live;
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

    // ---------- Layout: bottom / side-by-side ----------

    private void LayoutToggle_Click(object sender, RoutedEventArgs e)
    {
        _sideBySide = !_sideBySide;
        Services.AppSettings.SideBySideLayout = _sideBySide;
        ApplyLayout();
        RecomputeTreemap();
    }

    private async void AutoSaveToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_settingsLoading)
            return;

        if (AutoSaveToggle.IsOn)
        {
            Services.AppSettings.AutoSaveSnapshots = true;
            return;
        }

        bool confirmed = await ConfirmAsync("Turn off auto-save?",
            "This also deletes the stored last-scan snapshots and the daily size history, so " +
            "\"What changed since last scan\", the Change column, and \"Size history\" will have " +
            "no data until you scan again with auto-save on.",
            "Turn off & delete");
        if (!confirmed)
        {
            _settingsLoading = true;
            AutoSaveToggle.IsOn = true;
            _settingsLoading = false;
            return;
        }

        Services.AppSettings.AutoSaveSnapshots = false;
        try
        {
            string dir = MainViewModel.GetAutoSnapshotDirectory();
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
            string history = MainViewModel.GetHistoryRootDirectory();
            if (Directory.Exists(history))
                Directory.Delete(history, recursive: true);
            ViewModel.StatusText = "Auto-save turned off; stored snapshots and size history deleted.";
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = $"Auto-save turned off, but stored data could not be deleted: {ex.Message}";
        }
    }

    private void SizeDetail_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_settingsLoading || SizeDetailCombo.SelectedIndex < 0)
            return;
        ByteFormatter.Detail = (SizeDetail)SizeDetailCombo.SelectedIndex;
        Services.AppSettings.SizeDetail = SizeDetailCombo.SelectedIndex;
        ViewModel.RefreshFormatting();
    }

    private void OpenHistory_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // Prefer the current target's folder; fall back to the history root.
            string dir = ViewModel.CurrentSnapshot is { } snap
                ? MainViewModel.GetHistoryDirectory(snap.Target)
                : MainViewModel.GetHistoryRootDirectory();
            if (!Directory.Exists(dir))
                dir = MainViewModel.GetHistoryRootDirectory();
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start("explorer.exe", $"\"{dir}\"");
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = $"Could not open history folder: {ex.Message}";
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
            ViewModel.StatusText = "No size history stored yet.";
            return;
        }

        long bytes = files.Sum(f => new FileInfo(f).Length);
        bool confirmed = await ConfirmAsync("Clear size history?",
            $"Delete {files.Length} daily snapshot{(files.Length == 1 ? "" : "s")} " +
            $"({ByteFormatter.Format(bytes)})?\n\nThe last-scan baseline used by the Change column is kept.",
            "Clear history");
        if (!confirmed)
            return;

        try
        {
            Directory.Delete(root, recursive: true);
            ViewModel.StatusText = $"Size history cleared — freed {ByteFormatter.Format(bytes)}.";
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = $"Could not clear history: {ex.Message}";
        }
    }

    private void CleanupCheckBox_Toggled(object sender, RoutedEventArgs e)
    {
        ViewModel.CleanupCandidatesOnly = CleanupCheckBox.IsChecked == true;
        TreemapCanvas.Invalidate();
    }

    private async void About_Click(object sender, RoutedEventArgs e)
    {
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

        var links = new StackPanel { Spacing = 4 };
        links.Children.Add(new TextBlock
        {
            Text = $"DriveVisualizer {version}\nSee what's eating your disk: scan, map, clean up.",
            TextWrapping = TextWrapping.Wrap,
        });
        links.Children.Add(new HyperlinkButton
        {
            Content = "github.com/guscatalano/DriveVisualizer",
            NavigateUri = new Uri("https://github.com/guscatalano/DriveVisualizer"),
            Padding = new Thickness(0),
        });
        links.Children.Add(new HyperlinkButton
        {
            Content = "guscatalano.com",
            NavigateUri = new Uri("https://guscatalano.com"),
            Padding = new Thickness(0),
        });
        links.Children.Add(new TextBlock
        {
            Text = "Built with WinUI 3, Win2D, and the Windows App SDK.",
            Opacity = 0.7,
            Margin = new Thickness(0, 8, 0, 0),
        });

        var dialog = new ContentDialog
        {
            Title = "About DriveVisualizer",
            Content = links,
            CloseButtonText = "Close",
            XamlRoot = XamlRoot,
        };
        await dialog.ShowAsync();
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

    private async void Report_History(object sender, RoutedEventArgs e)
    {
        if (ViewModel.CurrentSnapshot is not { } current)
        {
            ViewModel.StatusText = "Run a scan first.";
            return;
        }
        try
        {
            string dir = MainViewModel.GetHistoryDirectory(current.Target);
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
                        "Every day you run a scan, DriveVisualizer keeps one snapshot of that day's totals " +
                        "(overall size plus each category: apps, temp files, disk images, …).\n\n" +
                        "Once you've scanned on two or more different days, this menu turns those daily snapshots " +
                        "into a chart — one stacked bar per day — so you can see whether the drive is filling up " +
                        "over time and which kind of files are doing it.\n\n" +
                        $"Recorded so far: {files.Length} day{(files.Length == 1 ? "" : "s")} for {current.Target}. " +
                        "Scan again on another day and the chart will appear here." + autoSaveNote,
                    CloseButtonText = "Got it",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = XamlRoot,
                };
                await dialog.ShowAsync();
                return;
            }
            string html = await Task.Run(() =>
            {
                var history = files.Select(DriveVisualizer.Core.Snapshots.ScanSnapshot.Load).ToList();
                return DriveVisualizer.Core.Snapshots.HistoryChart.BuildHtml(history);
            });
            string path = Path.Combine(Path.GetTempPath(), $"DriveVisualizer-history-{DateTime.Now:yyyyMMdd-HHmmss}.html");
            await File.WriteAllTextAsync(path, html);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ViewModel.StatusText = $"Could not build history: {ex.Message}";
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
            TreemapCanvas.Invalidate();
            return;
        }

        var result = await Task.Run(() => TreemapLayout.Compute(root, w, h));
        if (version != _layoutVersion)
            return; // superseded by a newer layout request
        _treemap = result;
        TreemapCanvas.Invalidate();
    }

    private void TreemapCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var ds = args.DrawingSession;

        // Chart surface (dark, matches the validated dark palette steps).
        ds.Clear(Color.FromArgb(255, 26, 26, 25));

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

    private Tile? HitTest(Windows.Foundation.Point p)
    {
        if (_treemap is not { } treemap)
            return null;
        foreach (var tile in treemap.Tiles)
            if (tile.Rect.Contains((float)p.X, (float)p.Y))
                return tile;
        return null;
    }

    private void TreemapCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var pos = e.GetCurrentPoint(TreemapCanvas).Position;
        var tile = HitTest(pos);
        var node = tile?.Node;

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
            HoverTipText.Text = tile!.Value.IsAggregate
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
        if (HitTest(point.Position)?.Node is not { } node)
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
        if (HitTest(pos)?.Node is not { } node)
            return;

        var dir = node.IsDirectory ? node : node.Parent;
        if (dir is null || ReferenceEquals(dir, _treemapRoot))
            return;

        _treemapRoot = dir;
        UpdateZoomBar();
        RecomputeTreemap();
    }

    private void ZoomOut_Click(object sender, RoutedEventArgs e) => ZoomOutOneLevel();

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
