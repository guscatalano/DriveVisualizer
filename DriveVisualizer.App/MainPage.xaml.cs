using DriveVisualizer.Core;
using DriveVisualizer.Core.Treemap;
using DriveVisualizer_App.Rendering;
using DriveVisualizer_App.ViewModels;
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
    private TreemapResult? _treemap;
    private FsNode? _treemapRoot;
    private FsNode? _hoverNode;
    private int _layoutVersion;

    public MainPage()
    {
        ViewModel = new MainViewModel(DispatcherQueue);
        InitializeComponent();

        _resizeTimer = DispatcherQueue.CreateTimer();
        _resizeTimer.Interval = TimeSpan.FromMilliseconds(150);
        _resizeTimer.IsRepeating = false;
        _resizeTimer.Tick += (_, _) => RecomputeTreemap();

        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.ScanRoot))
            {
                _treemapRoot = ViewModel.ScanRoot;
                UpdateZoomOutButton();
                RecomputeTreemap();
            }
            else if (e.PropertyName == nameof(MainViewModel.SelectedRow))
            {
                TreemapCanvas.Invalidate();
            }
        };
    }

    // ---------- Toolbar ----------

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
        picker.FileTypeFilter.Add("*");

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.Window);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var folder = await picker.PickSingleFolderAsync();
        if (folder is null)
            return;

        if (!ViewModel.Targets.Contains(folder.Path))
            ViewModel.Targets.Add(folder.Path);
        ViewModel.SelectedTarget = folder.Path;
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

        if (_treemap is not { } treemap || treemap.Tiles.Count == 0)
        {
            ds.DrawText("Scan a drive to see the treemap", 12, 12, Color.FromArgb(255, 128, 128, 128));
            return;
        }

        var border = Color.FromArgb(90, 0, 0, 0);
        foreach (var tile in treemap.Tiles)
        {
            var r = tile.Rect;
            Color fill = tile.IsAggregate ? TileColors.Aggregate : TileColors.ForFileName(tile.Node.Name);
            ds.FillRectangle(r.X, r.Y, r.Width, r.Height, fill);
            if (r.Width > 3 && r.Height > 3)
                ds.DrawRectangle(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1, border, 1f);
        }

        // Directory outlines give the nested structure some visual grouping.
        var dirOutline = Color.FromArgb(140, 255, 255, 255);
        foreach (var (dir, rect) in treemap.DirectoryBounds)
        {
            if (dir != _treemapRoot && rect.Width > 24 && rect.Height > 24)
                ds.DrawRectangle(rect.X, rect.Y, rect.Width, rect.Height, dirOutline, 1f);
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
                ds.DrawRectangle(hr.X + 1, hr.Y + 1, hr.Width - 2, hr.Height - 2, Colors.White, 2.5f);
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
            HoverTipText.Text = tile!.Value.IsAggregate
                ? $"{node.GetFullPath()}  (small items)  {ByteFormatter.Format(node.AllocatedSize)}"
                : $"{node.GetFullPath()}  {ByteFormatter.Format(node.AllocatedSize)}";
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
        var pos = e.GetCurrentPoint(TreemapCanvas).Position;
        if (HitTest(pos)?.Node is { } node)
        {
            ViewModel.SelectNode(node);
            if (ViewModel.SelectedRow is { } row)
                TreeList.ScrollIntoView(row);
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
        UpdateZoomOutButton();
        RecomputeTreemap();
    }

    private void ZoomOut_Click(object sender, RoutedEventArgs e)
    {
        _treemapRoot = _treemapRoot?.Parent ?? ViewModel.ScanRoot;
        UpdateZoomOutButton();
        RecomputeTreemap();
    }

    private void TreemapCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        _resizeTimer.Stop();
        _resizeTimer.Start();
    }

    private void UpdateZoomOutButton() =>
        ZoomOutButton.Visibility =
            _treemapRoot is not null && !ReferenceEquals(_treemapRoot, ViewModel.ScanRoot)
                ? Visibility.Visible
                : Visibility.Collapsed;
}
