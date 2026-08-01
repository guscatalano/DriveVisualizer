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

    /// <summary>Disk path the auto-baseline was loaded from (for report provenance).</summary>
    public string? AutoBaselinePath { get; private set; }

    /// <summary>Per-directory allocated-size change vs <see cref="AutoBaseline"/>.</summary>
    private Dictionary<FsNode, long>? _dirDeltas;

    private long? GetDelta(FsNode node) =>
        _dirDeltas is { } deltas && deltas.TryGetValue(node, out long d) ? d : null;

    /// <summary>"vs scan from &lt;date&gt;" — what the Change column is measured against.</summary>
    [ObservableProperty]
    public partial string ChangeBaselineText { get; set; }

    /// <summary>When on, the tree shows only files flagged by CleanupHeuristics; the map ghosts the rest.</summary>
    [ObservableProperty]
    public partial bool CleanupCandidatesOnly { get; set; }

    partial void OnCleanupCandidatesOnlyChanged(bool value)
    {
        if (_root is null)
            return;
        if (value)
        {
            var (bytes, count) = CleanupHeuristics.MarkCandidates(_root);
            StatusText = $"Cleanup candidates: {ByteFormatter.Format(bytes)} in {count:N0} files " +
                         "(temp folders, caches, logs, recycle bin, node_modules)";
        }
        RebuildAllRows();
    }

    private static string GetDataBaseDirectory()
    {
        try
        {
            return Windows.Storage.ApplicationData.Current.LocalFolder.Path;
        }
        catch
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DriveVisualizer");
        }
    }

    /// <summary>Legacy "last scan" folder — superseded by history; kept only so cleanup can delete it.</summary>
    public static string GetLegacyAutoSnapshotDirectory() =>
        Path.Combine(GetDataBaseDirectory(), "autosnap");

    private static string SanitizeTarget(string target)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string([.. target.Select(c => invalid.Contains(c) ? '_' : c)]);
    }

    /// <summary>Root of all per-target daily history folders (the single snapshot store).</summary>
    public static string GetHistoryRootDirectory() =>
        Path.Combine(GetDataBaseDirectory(), "history");

    public static string GetHistoryDirectory(string target) =>
        Path.Combine(GetHistoryRootDirectory(), SanitizeTarget(target));

    private readonly DispatcherQueueTimer _autoScanTimer;

    /// <summary>
    /// Re-scans the current target automatically when the newest snapshot is
    /// older than the configured cadence (0 = manual only). Runs only while
    /// the app is open — there is no background service.
    /// </summary>
    private void AutoScanTick()
    {
        if (IsScanning || _refreshBusy)
            return;
        if (!Services.AppSettings.AutoSaveSnapshots)
            return;
        int frequency = Services.AppSettings.SnapshotFrequency;
        if (frequency == 0)
            return;

        // No manual scan required this session: any selected target that has
        // been snapshotted before resumes its cadence as soon as the app opens.
        string? target = SelectedTarget;
        if (string.IsNullOrWhiteSpace(target))
            return;

        TimeSpan period = frequency switch
        {
            1 => TimeSpan.FromHours(1),
            3 => TimeSpan.FromDays(7),
            _ => TimeSpan.FromDays(1),
        };

        DateTime newestUtc;
        try
        {
            string dir = GetHistoryDirectory(target);
            if (!Directory.Exists(dir))
                return; // never snapshotted — don't start scanning targets unprompted
            newestUtc = Directory.GetFiles(dir, "*.dvsnap")
                .Select(File.GetLastWriteTimeUtc)
                .DefaultIfEmpty(DateTime.MinValue)
                .Max();
        }
        catch { return; }

        if (DateTime.UtcNow - newestUtc >= period)
        {
            StatusText = $"Automatic snapshot: rescanning {target}…";
            ScanOrStopCommand.Execute(null);
        }
    }

    /// <summary>
    /// Filename granularity matches the cadence: scans within the same period
    /// overwrite that period's file (manual-only uses daily granularity).
    /// </summary>
    public static string SnapshotFileName(int frequency)
    {
        var now = DateTime.Now;
        return frequency switch
        {
            1 => $"{now:yyyy-MM-dd_HH}h.dvsnap",                                                 // hourly
            3 => $"{System.Globalization.ISOWeek.GetYear(now)}-W{System.Globalization.ISOWeek.GetWeekOfYear(now):D2}.dvsnap", // weekly
            _ => $"{now:yyyy-MM-dd}.dvsnap",                                                     // manual / daily
        };
    }

    /// <summary>Prunes a target's snapshots per the retention setting (newest always kept).</summary>
    public static void ApplyRetention(string historyDir, int retention)
    {
        if (retention == 0)
            return;

        int keepCount = retention switch { 1 => 10, 2 => 50, _ => int.MaxValue };
        DateTime cutoffUtc = retention switch
        {
            3 => DateTime.UtcNow.AddDays(-30),
            4 => DateTime.UtcNow.AddDays(-90),
            5 => DateTime.UtcNow.AddDays(-365),
            _ => DateTime.MinValue,
        };

        var files = Directory.GetFiles(historyDir, "*.dvsnap")
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ToList();

        for (int i = 0; i < files.Count; i++)
        {
            if (i == 0)
                continue; // newest is the diff baseline — always kept
            if (i >= keepCount || files[i].LastWriteTimeUtc < cutoffUtc)
            {
                try { files[i].Delete(); } catch { }
            }
        }
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

    /// <summary>The app's single live instance, so the embedded MCP server can see the current scan.</summary>
    public static MainViewModel? Current { get; private set; }

    public MainViewModel(DispatcherQueue dispatcher)
    {
        Current = this;
        Rows = [];
        Categories = [];
        StatusText = "Pick a drive or folder and press Scan.";
        SelectionText = "";
        ChangeBaselineText = "";

        _progressTimer = dispatcher.CreateTimer();
        _progressTimer.Interval = TimeSpan.FromMilliseconds(300);
        _progressTimer.Tick += (_, _) => ProgressTick();

        _watchTimer = dispatcher.CreateTimer();
        _watchTimer.Interval = TimeSpan.FromMilliseconds(2500);
        _watchTimer.IsRepeating = true;
        _watchTimer.Tick += (_, _) => WatchTick();

        // Automatic snapshots: while the app runs, re-scan the current target
        // when the newest snapshot is older than the configured cadence.
        _autoScanTimer = dispatcher.CreateTimer();
        _autoScanTimer.Interval = TimeSpan.FromMinutes(2);
        _autoScanTimer.IsRepeating = true;
        _autoScanTimer.Tick += (_, _) => AutoScanTick();
        _autoScanTimer.Start();

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
        StopWatcher();
        _root = null;
        ScanRoot = null;
        LiveRoot = null;
        CurrentSnapshot = null;
        AutoBaseline = null;
        AutoBaselinePath = null;
        _dirDeltas = null;
        ChangeBaselineText = "";
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
            Prefs.FitToLargestValue(_root.AllocatedSize);
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
                int frequency = Services.AppSettings.SnapshotFrequency;
                int retention = Services.AppSettings.SnapshotRetention;
                var (snapshot, previous, baselinePath, deltas) = await Task.Run(() =>
                {
                    var snap = ScanSnapshot.Build(scanRoot, target, DateTime.UtcNow);
                    snap.DriveHealth = Services.DriveStats.GetSnapshotHealth(target);
                    ScanSnapshot? prev = null;
                    string? prevPath = null;
                    if (autoSave)
                    {
                        // Single store: the history folder. The newest entry IS the
                        // previous scan, so no separate "last scan" copy is kept.
                        string historyDir = GetHistoryDirectory(target);
                        try
                        {
                            if (Directory.Exists(historyDir))
                            {
                                prevPath = Directory.GetFiles(historyDir, "*.dvsnap")
                                    .OrderByDescending(File.GetLastWriteTimeUtc)
                                    .FirstOrDefault();
                                if (prevPath is not null)
                                    prev = ScanSnapshot.Load(prevPath);
                            }
                        }
                        catch { prev = null; prevPath = null; }
                        try
                        {
                            Directory.CreateDirectory(historyDir);
                            snap.Save(Path.Combine(historyDir, SnapshotFileName(frequency)));
                            ApplyRetention(historyDir, retention);
                        }
                        catch { }
                    }
                    var dirDeltas = prev is null ? null : ComputeDirDeltas(scanRoot, prev);
                    return (snap, prev, prevPath, dirDeltas);
                });
                CurrentSnapshot = snapshot;
                AutoBaseline = previous;
                AutoBaselinePath = baselinePath;
                _dirDeltas = deltas;
                if (deltas is not null && previous is not null)
                {
                    ChangeBaselineText = $"vs scan from {previous.TimestampUtc.ToLocalTime():g}";
                    RebuildAllRows(); // repopulate the Change column now that deltas exist
                    StatusText += $" — Change {ChangeBaselineText}";
                }
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
            if (WatchForChanges && _root is not null)
                StartWatcher();
        }
    }

    private void ProgressTick()
    {
        if (_scanner is not { } scanner)
            return;

        if (_root is not null)
            Prefs.FitToLargestValue(_root.AllocatedSize);

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
                Rows.Insert(i, new NodeRow(node, depth, _expanded.Contains(node), GetDelta(node)));
            i++;
        }
        while (Rows.Count > i)
            Rows.RemoveAt(Rows.Count - 1);
    }

    private void RebuildAllRows()
    {
        var rows = new ObservableCollection<NodeRow>();
        foreach (var (node, depth) in WalkVisible())
            rows.Add(new NodeRow(node, depth, _expanded.Contains(node), GetDelta(node)));
        Rows = rows;
    }

    private bool IsFileFilteredOut(FsNode node)
    {
        if (node.IsDirectory)
            return false;
        if (FilterActive && !_enabledCategories.Contains(FileClassification.Classify(node.Name)))
            return true;
        if (CleanupCandidatesOnly && !node.Flags.HasFlag(NodeFlags.CleanupCandidate))
            return true;
        return false;
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
                    if (IsFileFilteredOut(child))
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
            if (IsFileFilteredOut(child))
                continue;
            output.Add(new NodeRow(child, depth + 1, _expanded.Contains(child), GetDelta(child)));
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
    /// Matches every directory in the fresh tree against the baseline snapshot
    /// by full path and records the allocated-size change (new dirs count fully).
    /// </summary>
    private static Dictionary<FsNode, long> ComputeDirDeltas(FsNode root, ScanSnapshot baseline)
    {
        var baselinePaths = baseline.BuildDirectoryPaths();
        var baselineByPath = new Dictionary<string, long>(baseline.Directories.Count, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < baseline.Directories.Count; i++)
            baselineByPath[baselinePaths[i]] = baseline.Directories[i].AllocatedSize;

        var deltas = new Dictionary<FsNode, long>();
        var stack = new Stack<(FsNode Node, string Path)>();
        stack.Push((root, root.Name));
        while (stack.Count > 0)
        {
            var (node, path) = stack.Pop();
            long before = baselineByPath.TryGetValue(path, out long b) ? b : 0;
            long delta = node.AllocatedSize - before;
            if (delta != 0)
                deltas[node] = delta;

            if (node.Children is { } children)
                foreach (var child in children)
                    if (child.IsDirectory)
                        stack.Push((child, path.TrimEnd('\\') + '\\' + child.Name));
        }
        return deltas;
    }

    // ---------- Folder refresh & change watching ----------

    private FileSystemWatcher? _watcher;
    private readonly object _pendingLock = new();
    private readonly HashSet<string> _pendingDirs = new(StringComparer.OrdinalIgnoreCase);
    private bool _watcherOverflowed;
    private bool _refreshBusy;
    private readonly DispatcherQueueTimer _watchTimer;

    /// <summary>When on, file-system changes under the scanned target refresh affected folders automatically.</summary>
    [ObservableProperty]
    public partial bool WatchForChanges { get; set; }

    partial void OnWatchForChangesChanged(bool value)
    {
        if (value)
        {
            StartWatcher();
            if (_watcher is not null)
                StatusText = $"Watching {_root?.Name} for changes";
        }
        else
        {
            StopWatcher();
        }
    }

    private void StartWatcher()
    {
        StopWatcher();
        if (_root is null || ScanRoot is null)
            return;
        try
        {
            _watcher = new FileSystemWatcher(_root.Name)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite,
            };
            _watcher.Created += (_, e) => QueueChangedPath(e.FullPath);
            _watcher.Deleted += (_, e) => QueueChangedPath(e.FullPath);
            _watcher.Changed += (_, e) => QueueChangedPath(e.FullPath);
            _watcher.Renamed += (_, e) => { QueueChangedPath(e.OldFullPath); QueueChangedPath(e.FullPath); };
            _watcher.Error += (_, _) => _watcherOverflowed = true;
            _watcher.EnableRaisingEvents = true;
            _watchTimer.Start();
        }
        catch (Exception ex)
        {
            StatusText = $"Could not watch for changes: {ex.Message}";
            WatchForChanges = false;
        }
    }

    private void StopWatcher()
    {
        _watchTimer.Stop();
        _watcher?.Dispose();
        _watcher = null;
        lock (_pendingLock)
            _pendingDirs.Clear();
    }

    private void QueueChangedPath(string fullPath)
    {
        string? dir = Path.GetDirectoryName(fullPath);
        if (dir is null)
            return;
        lock (_pendingLock)
        {
            if (_pendingDirs.Count < 512)
                _pendingDirs.Add(dir);
        }
    }

    private async void WatchTick()
    {
        if (_refreshBusy || IsScanning || _root is null)
            return;

        if (_watcherOverflowed)
        {
            _watcherOverflowed = false;
            StatusText = "Many changes detected — consider a full rescan.";
        }

        List<string> dirs;
        lock (_pendingLock)
        {
            if (_pendingDirs.Count == 0)
                return;
            dirs = [.. _pendingDirs];
            _pendingDirs.Clear();
        }

        // Skip paths nested under another pending path (the ancestor rescan covers them).
        dirs.Sort(StringComparer.OrdinalIgnoreCase);
        var top = new List<string>();
        foreach (var d in dirs)
            if (top.Count == 0 || !d.StartsWith(top[^1].TrimEnd('\\') + '\\', StringComparison.OrdinalIgnoreCase))
                top.Add(d);

        _refreshBusy = true;
        try
        {
            int refreshed = 0;
            foreach (var dir in top.Take(12))
                if (FindNodeByPath(dir) is { } node && await RefreshFolderCoreAsync(node))
                    refreshed++;
            if (refreshed > 0)
            {
                AfterTreeMutation();
                StatusText = $"Auto-refreshed {refreshed} changed folder{(refreshed == 1 ? "" : "s")} — {ByteFormatter.Format(_root.AllocatedSize)} total";
            }
        }
        finally
        {
            _refreshBusy = false;
        }
    }

    /// <summary>Finds the tree node for an absolute path, or null if not part of this scan.</summary>
    public FsNode? FindNodeByPath(string fullPath)
    {
        if (_root is null)
            return null;
        string rootPath = _root.Name.TrimEnd('\\');
        if (!fullPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
            return null;
        if (fullPath.Length <= rootPath.Length + 1)
            return _root;

        var node = _root;
        foreach (var segment in fullPath[(rootPath.Length + 1)..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            var next = node.Children?.FirstOrDefault(c =>
                string.Equals(c.Name, segment, StringComparison.OrdinalIgnoreCase));
            if (next is null)
                return null;
            node = next;
        }
        return node;
    }

    /// <summary>Rescans one folder's subtree and grafts the fresh result into the tree.</summary>
    public async Task RefreshFolderAsync(FsNode dir)
    {
        if (IsScanning || _refreshBusy)
            return;
        _refreshBusy = true;
        try
        {
            StatusText = $"Rescanning {dir.Name}…";
            if (await RefreshFolderCoreAsync(dir))
            {
                AfterTreeMutation();
                StatusText = $"Rescanned {dir.Name} — now {ByteFormatter.Format(dir.AllocatedSize)}";
            }
            else
            {
                StatusText = $"Could not rescan {dir.Name}";
            }
        }
        finally
        {
            _refreshBusy = false;
        }
    }

    private async Task<bool> RefreshFolderCoreAsync(FsNode dir)
    {
        if (!dir.IsDirectory || dir.IsReparsePoint)
            return false;
        string path = dir.GetFullPath();
        if (!Directory.Exists(path))
        {
            RemoveNodeCore(dir);
            return true;
        }

        try
        {
            var scanner = new ParallelScanner();
            var result = await scanner.ScanAsync(path);
            var fresh = result.Root;
            await Task.Run(() => SortChildrenBySize(fresh));

            long deltaAlloc = fresh.AllocatedSize - dir.AllocatedSize;
            long deltaLogical = fresh.LogicalSize - dir.LogicalSize;
            int deltaFiles = fresh.SubtreeFileCount - dir.SubtreeFileCount;

            dir.Children = fresh.Children;
            if (dir.Children is { } children)
                foreach (var child in children)
                    child.Parent = dir;
            dir.LogicalSize = fresh.LogicalSize;
            dir.AllocatedSize = fresh.AllocatedSize;
            dir.SubtreeFileCount = fresh.SubtreeFileCount;
            dir.Flags = fresh.Flags;

            for (FsNode? n = dir.Parent; n is not null; n = n.Parent)
            {
                n.AllocatedSize += deltaAlloc;
                n.LogicalSize += deltaLogical;
                n.SubtreeFileCount += deltaFiles;
            }
            if (CleanupCandidatesOnly)
                CleanupHeuristics.MarkCandidates(dir);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Removes a deleted node from the model: detaches it, subtracts its sizes
    /// from every ancestor, and refreshes rows, legend, and treemap.
    /// </summary>
    public void RemoveNode(FsNode node)
    {
        RemoveNodeCore(node);
        AfterTreeMutation();
    }

    private void RemoveNodeCore(FsNode node)
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

    /// <summary>Re-renders every size string after the size-detail setting changes.</summary>
    public void RefreshFormatting()
    {
        if (_root is null)
            return;
        Prefs.FitToLargestValue(_root.AllocatedSize);
        RebuildAllRows();
        if (_categoryTotals is { } totals)
            Categories = BuildCategoryStats(totals, _root.AllocatedSize);
        if (SelectedRow is { } row)
            SelectionText = $"{row.Node.GetFullPath()}  —  {row.SizeText}";
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
