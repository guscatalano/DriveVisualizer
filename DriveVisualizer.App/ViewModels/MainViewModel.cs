using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DriveVisualizer.Core;
using DriveVisualizer.Core.Scanning;
using DriveVisualizer.Core.Snapshots;
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

    /// <summary>
    /// In-progress scan root, non-null only while scanning — lets the treemap
    /// build itself live. Cleared before the post-scan sort so nothing lays out
    /// a tree that is being reordered.
    /// </summary>
    [ObservableProperty]
    public partial FsNode? LiveRoot { get; set; }

    /// <summary>Per-category totals for the treemap legend (empty categories omitted).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<CategoryStat> Categories { get; set; }

    /// <summary>Bumped after structural tree edits (delete/compress) so the treemap re-lays.</summary>
    [ObservableProperty]
    public partial int TreeVersion { get; set; }

    /// <summary>Bumped when the category filter changes so the treemap repaints.</summary>
    [ObservableProperty]
    public partial int FilterVersion { get; set; }

    /// <summary>Snapshot of the completed scan (built once, reused by save/report/compare).</summary>
    public ScanSnapshot? CurrentSnapshot { get; private set; }

    /// <summary>The auto-saved snapshot from the previous scan of the same target, if any.</summary>
    public ScanSnapshot? AutoBaseline { get; private set; }

    private static string AutoSnapshotPath(string target)
    {
        string baseDir;
        try
        {
            baseDir = Windows.Storage.ApplicationData.Current.LocalFolder.Path;
        }
        catch
        {
            baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DriveVisualizer");
        }
        var invalid = Path.GetInvalidFileNameChars();
        string safe = new([.. target.Select(c => invalid.Contains(c) ? '_' : c)]);
        return Path.Combine(baseDir, "autosnap", safe + ".dvsnap");
    }

    private readonly HashSet<FileCategory> _enabledCategories =
        [.. Rendering.FileCategories.All.Select(c => c.Category)];

    private bool FilterActive => _enabledCategories.Count < Rendering.FileCategories.All.Length;

    public bool IsCategoryEnabled(FileCategory category) => _enabledCategories.Contains(category);

    private long[]? _categoryTotals;

    public void SetCategoryEnabled(FileCategory category, bool enabled)
    {
        bool changed = enabled ? _enabledCategories.Add(category) : _enabledCategories.Remove(category);
        if (!changed)
            return;
        FilterVersion++;
        if (_root is not null)
        {
            RebuildAllRows();
            if (_categoryTotals is { } totals)
                Categories = BuildCategoryStats(totals, _root.AllocatedSize);
        }
    }

    public ObservableCollection<string> Targets { get; } = [];

    public ColumnPrefs Prefs => ColumnPrefs.Instance;

    public string ScanButtonText => IsScanning ? "Stop" : "Scan";
    public bool IsNotScanning => !IsScanning;

    public MainViewModel(DispatcherQueue dispatcher)
    {
        Rows = [];
        Categories = [];
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
        LiveRoot = null;
        CurrentSnapshot = null;
        AutoBaseline = null;
        Rows = [];
        SelectedRow = null;
        _expanded.Clear();
        _scanner = new ParallelScanner();
        _cts = new CancellationTokenSource();
        _progressTimer.Start();

        try
        {
            var result = await _scanner.ScanAsync(SelectedTarget, _cts.Token);
            LiveRoot = null;       // stop live treemap layout before the sort reorders the tree
            _progressTimer.Stop(); // live phase over — don't let ticks overwrite the final summary
            long[] categoryTotals = [];
            await Task.Run(() =>
            {
                SortChildrenBySize(result.Root);
                categoryTotals = ComputeCategoryTotals(result.Root);
            });

            _root = result.Root;
            _expanded.Add(_root);
            RebuildAllRows();
            Categories = BuildCategoryStats(categoryTotals, _root.AllocatedSize);
            ScanRoot = _root;

            var stats = _scanner.Statistics;
            string denied = stats.AccessDenied > 0 ? $" — {stats.AccessDenied:N0} folders not readable" : "";
            string cancelled = result.WasCancelled ? " (stopped — partial results)" : "";
            StatusText = $"{stats.Files:N0} files, {stats.Directories:N0} folders, " +
                         $"{ByteFormatter.Format(_root.AllocatedSize)} in {result.Elapsed.TotalSeconds:F1}s{denied}{cancelled}";

            if (!result.WasCancelled)
            {
                // Auto-save this scan and pick up the previous one as a baseline,
                // so "diff since last scan" always has something to compare with.
                string target = SelectedTarget!;
                var scanRoot = _root;
                bool autoSave = Services.AppSettings.AutoSaveSnapshots;
                var (snapshot, previous) = await Task.Run(() =>
                {
                    var snap = ScanSnapshot.Build(scanRoot, target, DateTime.UtcNow);
                    ScanSnapshot? prev = null;
                    if (autoSave)
                    {
                        string autoPath = AutoSnapshotPath(target);
                        try { if (File.Exists(autoPath)) prev = ScanSnapshot.Load(autoPath); } catch { }
                        try
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(autoPath)!);
                            snap.Save(autoPath);
                        }
                        catch { }
                    }
                    return (snap, prev);
                });
                CurrentSnapshot = snapshot;
                AutoBaseline = previous;
            }
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
            LiveRoot = live;
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
                {
                    var child = children[c];
                    if (FilterActive && !child.IsDirectory &&
                        !_enabledCategories.Contains(FileClassification.Classify(child.Name)))
                        continue;
                    stack.Push((child, depth + 1));
                }
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
            if (FilterActive && !child.IsDirectory &&
                !_enabledCategories.Contains(FileClassification.Classify(child.Name)))
                continue;
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

    /// <summary>
    /// Removes a deleted node from the model: detaches it, subtracts its sizes
    /// from every ancestor, and refreshes rows, legend, and treemap.
    /// </summary>
    public void RemoveNode(FsNode node)
    {
        if (node.Parent is not { } parent)
            return;

        long alloc = node.AllocatedSize;
        long logical = node.LogicalSize;
        int files = node.IsDirectory ? node.SubtreeFileCount : 1;

        parent.Children = parent.Children?.Where(c => !ReferenceEquals(c, node)).ToArray();
        for (FsNode? n = parent; n is not null; n = n.Parent)
        {
            n.AllocatedSize -= alloc;
            n.LogicalSize -= logical;
            n.SubtreeFileCount -= files;
        }
        _expanded.Remove(node);
        SelectedRow = null;
        AfterTreeMutation();
    }

    /// <summary>Adds a newly created file (e.g. the zip from compress-and-delete) to the model.</summary>
    public void AddFile(FsNode parent, string name, long size)
    {
        const uint FILE_ATTRIBUTE_NORMAL = 0x80;
        var node = new FsNode
        {
            Name = name,
            Parent = parent,
            Attributes = FILE_ATTRIBUTE_NORMAL,
            LastWriteTimeTicks = DateTime.UtcNow.Ticks,
        };
        node.LogicalSize = size;
        node.AllocatedSize = (size + 4095) / 4096 * 4096;

        var children = parent.Children ?? [];
        var expanded = new FsNode[children.Length + 1];
        children.CopyTo(expanded, 0);
        expanded[^1] = node;
        Array.Sort(expanded, static (a, b) => b.AllocatedSize.CompareTo(a.AllocatedSize));
        parent.Children = expanded;

        for (FsNode? n = parent; n is not null; n = n.Parent)
        {
            n.AllocatedSize += node.AllocatedSize;
            n.LogicalSize += size;
            n.SubtreeFileCount += 1;
        }
        AfterTreeMutation();
    }

    private async void AfterTreeMutation()
    {
        RebuildAllRows();
        TreeVersion++;
        if (_root is { } root)
        {
            var totals = await Task.Run(() => ComputeCategoryTotals(root));
            Categories = BuildCategoryStats(totals, root.AllocatedSize);
        }
    }

    private static long[] ComputeCategoryTotals(FsNode root)
    {
        var totals = new long[FileClassification.CategoryCount];
        var stack = new Stack<FsNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node.Children is { } children)
            {
                foreach (var child in children)
                {
                    if (child.IsDirectory)
                        stack.Push(child);
                    else
                        totals[(int)FileClassification.Classify(child.Name)] += child.AllocatedSize;
                }
            }
        }
        return totals;
    }

    private IReadOnlyList<CategoryStat> BuildCategoryStats(long[] totals, long grandTotal)
    {
        _categoryTotals = totals;
        var stats = new List<CategoryStat>();
        foreach (var (category, name, color) in Rendering.FileCategories.All)
        {
            long size = totals[(int)category];
            if (size <= 0)
                continue;
            double pct = grandTotal > 0 ? 100.0 * size / grandTotal : 0;
            stats.Add(new CategoryStat(
                name,
                new Microsoft.UI.Xaml.Media.SolidColorBrush(color),
                ByteFormatter.Format(size),
                $"{pct:F1}%",
                _enabledCategories.Contains(category) ? 1.0 : 0.35));
        }
        return stats;
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
