using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DriveVisualizer.Core;
using DriveVisualizer.Core.Scanning;
using Microsoft.UI.Dispatching;

namespace DriveVisualizer_App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    /// <summary>Above this many rows, expanding inserts item-by-item is slower than a full reset.</summary>
    private const int IncrementalInsertLimit = 500;

    private readonly DispatcherQueueTimer _progressTimer;
    private ParallelScanner? _scanner;
    private CancellationTokenSource? _cts;
    private FsNode? _root;
    private readonly HashSet<FsNode> _expanded = [];

    [ObservableProperty]
    public partial ObservableCollection<NodeRow> Rows { get; set; }

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

    /// <summary>Completed scan root; the treemap pane listens for this.</summary>
    [ObservableProperty]
    public partial FsNode? ScanRoot { get; set; }

    public ObservableCollection<string> Targets { get; } = [];

    public string ScanButtonText => IsScanning ? "Stop" : "Scan";
    public bool IsNotScanning => !IsScanning;

    public MainViewModel(DispatcherQueue dispatcher)
    {
        Rows = [];
        StatusText = "Pick a drive or folder and press Scan.";
        SelectionText = "";

        _progressTimer = dispatcher.CreateTimer();
        _progressTimer.Interval = TimeSpan.FromMilliseconds(300);
        _progressTimer.Tick += (_, _) => ProgressTick();

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
        _root = null;
        ScanRoot = null;
        Rows = [];
        SelectedRow = null;
        _expanded.Clear();
        _scanner = new ParallelScanner();
        _cts = new CancellationTokenSource();
        _progressTimer.Start();

        try
        {
            var result = await _scanner.ScanAsync(SelectedTarget, _cts.Token);
            await Task.Run(() => SortChildrenBySize(result.Root));

            _root = result.Root;
            _expanded.Add(_root);
            RebuildAllRows();
            ScanRoot = _root;

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

    private void ProgressTick()
    {
        if (_scanner is not { } scanner)
            return;

        var s = scanner.Statistics;
        StatusText = $"Scanning…  {s.Files:N0} files, {s.Directories:N0} folders, " +
                     $"{ByteFormatter.Format(s.LogicalBytes)}";

        // Adopt the live tree on the first tick so the list builds while scanning.
        if (_root is null && scanner.LiveRoot is { } live)
        {
            _root = live;
            _expanded.Add(live);
        }
        if (_root is not null && IsScanning)
            SyncLiveRows();
    }

    /// <summary>
    /// Reconciles the visible rows against the growing tree: existing rows are
    /// refreshed in place, newly discovered nodes are inserted. Append-only per
    /// level while scanning, so a positional merge is sufficient.
    /// </summary>
    private void SyncLiveRows()
    {
        int i = 0;
        foreach (var (node, depth) in WalkVisible())
        {
            if (i < Rows.Count && ReferenceEquals(Rows[i].Node, node))
                Rows[i].Refresh();
            else
                Rows.Insert(i, new NodeRow(node, depth, _expanded.Contains(node)));
            i++;
        }
        while (Rows.Count > i)
            Rows.RemoveAt(Rows.Count - 1);
    }

    private void RebuildAllRows()
    {
        var rows = new ObservableCollection<NodeRow>();
        foreach (var (node, depth) in WalkVisible())
            rows.Add(new NodeRow(node, depth, _expanded.Contains(node)));
        Rows = rows;
    }

    private IEnumerable<(FsNode Node, int Depth)> WalkVisible()
    {
        if (_root is null)
            yield break;

        var stack = new Stack<(FsNode, int)>();
        stack.Push((_root, 0));
        while (stack.Count > 0)
        {
            var (node, depth) = stack.Pop();
            yield return (node, depth);

            if (_expanded.Contains(node) && node.Children is { } children)
                for (int c = children.Length - 1; c >= 0; c--)
                    stack.Push((children[c], depth + 1));
        }
    }

    public void ToggleExpand(NodeRow row)
    {
        if (!row.HasChildren)
            return;

        int index = Rows.IndexOf(row);
        if (index < 0)
            return;

        if (_expanded.Remove(row.Node))
        {
            row.IsExpanded = false;
            while (index + 1 < Rows.Count && Rows[index + 1].Depth > row.Depth)
                Rows.RemoveAt(index + 1);
        }
        else
        {
            _expanded.Add(row.Node);
            row.IsExpanded = true;

            var subtree = new List<NodeRow>();
            CollectVisibleSubtree(row.Node, row.Depth, subtree);
            if (subtree.Count > IncrementalInsertLimit)
            {
                RebuildAllRows();
                SelectedRow = Rows.FirstOrDefault(r => ReferenceEquals(r.Node, row.Node));
                return;
            }
            for (int k = 0; k < subtree.Count; k++)
                Rows.Insert(index + 1 + k, subtree[k]);
        }
        SelectedRow = Rows.FirstOrDefault(r => ReferenceEquals(r.Node, row.Node));
    }

    private void CollectVisibleSubtree(FsNode node, int depth, List<NodeRow> output)
    {
        if (node.Children is not { } children)
            return;
        foreach (var child in children)
        {
            output.Add(new NodeRow(child, depth + 1, _expanded.Contains(child)));
            if (_expanded.Contains(child))
                CollectVisibleSubtree(child, depth + 1, output);
        }
    }

    /// <summary>Selects a node picked in the treemap, expanding ancestors so it is visible.</summary>
    public void SelectNode(FsNode node)
    {
        bool structureChanged = false;
        for (var ancestor = node.Parent; ancestor is not null; ancestor = ancestor.Parent)
            structureChanged |= _expanded.Add(ancestor);

        if (structureChanged)
            RebuildAllRows();
        SelectedRow = Rows.FirstOrDefault(r => ReferenceEquals(r.Node, node));
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
