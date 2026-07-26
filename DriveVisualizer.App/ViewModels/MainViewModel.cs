using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DriveVisualizer.Core;
using DriveVisualizer.Core.Scanning;
using Microsoft.UI.Dispatching;

namespace DriveVisualizer_App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly DispatcherQueueTimer _progressTimer;
    private ParallelScanner? _scanner;
    private CancellationTokenSource? _cts;
    private FsNode? _root;
    private readonly HashSet<FsNode> _expanded = [];

    [ObservableProperty]
    public partial IReadOnlyList<NodeRow> Rows { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; }

    [ObservableProperty]
    public partial string SelectionText { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScanButtonText), nameof(IsNotScanning))]
    public partial bool IsScanning { get; set; }

    [ObservableProperty]
    public partial string? SelectedTarget { get; set; }

    [ObservableProperty]
    public partial NodeRow? SelectedRow { get; set; }

    public ObservableCollection<string> Targets { get; } = [];

    public string ScanButtonText => IsScanning ? "Stop" : "Scan";
    public bool IsNotScanning => !IsScanning;

    public MainViewModel(DispatcherQueue dispatcher)
    {
        Rows = [];
        StatusText = "Pick a drive or folder and press Scan.";
        SelectionText = "";

        _progressTimer = dispatcher.CreateTimer();
        _progressTimer.Interval = TimeSpan.FromMilliseconds(250);
        _progressTimer.Tick += (_, _) => ReportProgress();

        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
            Targets.Add(drive.Name);
        SelectedTarget = Targets.FirstOrDefault();
    }

    partial void OnSelectedRowChanged(NodeRow? value) =>
        SelectionText = value is null ? "" : $"{value.Node.GetFullPath()}  —  {value.SizeText}";

    [RelayCommand]
    private async Task ScanOrStopAsync()
    {
        if (IsScanning)
        {
            _cts?.Cancel();
            return;
        }

        if (string.IsNullOrWhiteSpace(SelectedTarget))
            return;

        IsScanning = true;
        Rows = [];
        SelectedRow = null;
        _scanner = new ParallelScanner();
        _cts = new CancellationTokenSource();
        _progressTimer.Start();

        try
        {
            var result = await _scanner.ScanAsync(SelectedTarget, _cts.Token);
            await Task.Run(() => SortChildrenBySize(result.Root));

            _root = result.Root;
            _expanded.Clear();
            _expanded.Add(_root);
            RebuildRows();

            var stats = _scanner.Statistics;
            string denied = stats.AccessDenied > 0 ? $" — {stats.AccessDenied:N0} folders not readable" : "";
            string cancelled = result.WasCancelled ? " (stopped — partial results)" : "";
            StatusText = $"{stats.Files:N0} files, {stats.Directories:N0} folders, " +
                         $"{ByteFormatter.Format(_root.AllocatedSize)} in {result.Elapsed.TotalSeconds:F1}s{denied}{cancelled}";
        }
        catch (Exception ex)
        {
            StatusText = $"Scan failed: {ex.Message}";
        }
        finally
        {
            _progressTimer.Stop();
            IsScanning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void ReportProgress()
    {
        if (_scanner is { } s)
            StatusText = $"Scanning…  {s.Statistics.Files:N0} files, {s.Statistics.Directories:N0} folders, " +
                         $"{ByteFormatter.Format(s.Statistics.LogicalBytes)}";
    }

    public void ToggleExpand(NodeRow row)
    {
        if (!row.HasChildren)
            return;
        if (!_expanded.Remove(row.Node))
            _expanded.Add(row.Node);
        RebuildRows();
        SelectedRow = Rows.FirstOrDefault(r => r.Node == row.Node);
    }

    private void RebuildRows()
    {
        if (_root is null)
        {
            Rows = [];
            return;
        }

        var list = new List<NodeRow>(256);
        var stack = new Stack<(FsNode Node, int Depth)>();
        stack.Push((_root, 0));

        while (stack.Count > 0)
        {
            var (node, depth) = stack.Pop();
            bool expanded = _expanded.Contains(node);
            long parentSize = node.Parent?.AllocatedSize ?? node.AllocatedSize;

            list.Add(new NodeRow
            {
                Node = node,
                Depth = depth,
                IsExpanded = expanded,
                PercentOfParent = parentSize > 0 ? 100.0 * node.AllocatedSize / parentSize : 0,
            });

            if (expanded && node.Children is { } children)
                for (int i = children.Length - 1; i >= 0; i--)
                    stack.Push((children[i], depth + 1));
        }

        Rows = list;
    }

    /// <summary>Sorts every directory's children by allocated size, biggest first (one-time, post-scan).</summary>
    private static void SortChildrenBySize(FsNode root)
    {
        var stack = new Stack<FsNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node.Children is { Length: > 0 } children)
            {
                Array.Sort(children, static (a, b) => b.AllocatedSize.CompareTo(a.AllocatedSize));
                foreach (var child in children)
                    if (child.IsDirectory)
                        stack.Push(child);
            }
        }
    }
}
