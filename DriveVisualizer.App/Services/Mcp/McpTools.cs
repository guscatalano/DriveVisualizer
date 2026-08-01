using System.Text.Json;
using DriveVisualizer.Core;
using DriveVisualizer.Core.Scanning;
using DriveVisualizer.Core.Snapshots;
using DriveVisualizer_App.ViewModels;

namespace DriveVisualizer_App.Services.Mcp;

/// <summary>
/// MCP tool definitions. Everything is read-only: tools scan, read snapshot
/// history, and report drive health, but never delete or modify files.
/// </summary>
internal static class McpTools
{
    public static object[] Describe() =>
    [
        new
        {
            name = "scan_folder",
            description = "Scan a folder or drive and return totals, per-category bytes, the largest subfolders and files, and how much of it is safe-to-review cleanup data (temp, caches, logs, node_modules). Scans run at full speed but a whole drive can take tens of seconds.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    path = new { type = "string", description = @"Folder or drive root to scan, e.g. C:\ or C:\Users\me\Downloads." },
                    top_n = new { type = "integer", description = "How many top folders/files to return. Default 15, max 100.", minimum = 1, maximum = 100 },
                },
                required = new[] { "path" },
            },
        },
        new
        {
            name = "get_current_scan",
            description = "Summarize the scan currently open in the DriveVisualizer window, if any — no rescan, instant. Same shape as scan_folder output.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    top_n = new { type = "integer", description = "How many top folders/files to return. Default 15, max 100.", minimum = 1, maximum = 100 },
                },
            },
        },
        new
        {
            name = "list_snapshot_targets",
            description = "List every target (drive or folder) that has saved snapshot history, with snapshot counts and the newest snapshot's date and total size.",
            inputSchema = new { type = "object", properties = new { } },
        },
        new
        {
            name = "get_history",
            description = "Return the snapshot history for one target: timestamp, total size, file count, per-category bytes, and drive health (free space, temperature, wear) per snapshot. Feed a target string exactly as returned by list_snapshot_targets.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    target = new { type = "string", description = "The scanned target path whose history to read." },
                },
                required = new[] { "target" },
            },
        },
        new
        {
            name = "diff_snapshots",
            description = "Compare two snapshots of a target and return what changed: total delta, per-category deltas, and the folders and files that grew or shrank the most. Defaults to the two newest snapshots.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    target = new { type = "string", description = "The scanned target path." },
                    from = new { type = "string", description = "Optional. Timestamp (any prefix of an ISO date, e.g. 2026-07-24) of the baseline snapshot. Default: second newest." },
                    to = new { type = "string", description = "Optional. Timestamp of the later snapshot. Default: newest." },
                    top_n = new { type = "integer", description = "How many movers to return. Default 15, max 100.", minimum = 1, maximum = 100 },
                },
                required = new[] { "target" },
            },
        },
        new
        {
            name = "drive_info",
            description = "Physical-drive facts for the drive hosting a path: model, SSD/HDD, bus (NVMe/SATA/USB), capacity, partition table, and S.M.A.R.T. health (temperature, wear, power-on hours, lifetime writes, media errors).",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    path = new { type = "string", description = @"Any path on the drive of interest, e.g. C:\." },
                },
                required = new[] { "path" },
            },
        },
        new
        {
            name = "find_cleanup_candidates",
            description = "Scan a folder or drive and return the largest safe-to-review disposable items: temp folders, caches, logs, recycle bin, node_modules. Read-only — nothing is deleted.",
            inputSchema = new
            {
                type = "object",
                properties = new
                {
                    path = new { type = "string", description = "Folder or drive root to scan." },
                    top_n = new { type = "integer", description = "How many candidates to return. Default 20, max 100.", minimum = 1, maximum = 100 },
                },
                required = new[] { "path" },
            },
        },
    ];

    public static async Task<McpJsonRpcResponse> CallAsync(object? id, string name, JsonElement args)
    {
        try
        {
            object? payload = await DispatchAsync(name, args).ConfigureAwait(false);
            if (payload is null)
                return ErrorContent(id, $"Unknown tool: {name}");

            string text = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
            return new McpJsonRpcResponse(id, new
            {
                content = new object[] { new { type = "text", text } },
            }, null);
        }
        catch (Exception ex)
        {
            return ErrorContent(id, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static McpJsonRpcResponse ErrorContent(object? id, string text) =>
        new(id, new { content = new object[] { new { type = "text", text } }, isError = true }, null);

    private static async Task<object?> DispatchAsync(string name, JsonElement args) => name switch
    {
        "scan_folder" => await ScanFolderAsync(args),
        "get_current_scan" => GetCurrentScan(args),
        "list_snapshot_targets" => ListSnapshotTargets(),
        "get_history" => GetHistory(args),
        "diff_snapshots" => DiffSnapshots(args),
        "drive_info" => DriveInfo(args),
        "find_cleanup_candidates" => await FindCleanupCandidatesAsync(args),
        _ => null,
    };

    // ===================== argument helpers =====================

    private static string RequireString(JsonElement args, string property)
    {
        if (args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty(property, out var el)
            && el.ValueKind == JsonValueKind.String
            && el.GetString() is { Length: > 0 } s)
            return s;
        throw new ArgumentException($"Missing required string argument '{property}'.");
    }

    private static string? OptionalString(JsonElement args, string property) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(property, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    private static int TopN(JsonElement args, int fallback) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty("top_n", out var el) && el.TryGetInt32(out int n)
            ? Math.Clamp(n, 1, 100)
            : fallback;

    // ===================== scan_folder / get_current_scan =====================

    private static async Task<object> ScanFolderAsync(JsonElement args)
    {
        string path = RequireString(args, "path");
        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException($"Folder not found: {path}");
        int topN = TopN(args, 15);

        var scanner = new ParallelScanner();
        var result = await scanner.ScanAsync(path).ConfigureAwait(false);
        var (cleanupBytes, cleanupFiles) = CleanupHeuristics.MarkCandidates(result.Root);
        var snapshot = ScanSnapshot.Build(result.Root, path, DateTime.UtcNow);

        var summary = (Dictionary<string, object?>)SummarizeSnapshot(snapshot, topN);
        summary["scanSeconds"] = Math.Round(result.Elapsed.TotalSeconds, 1);
        summary["cleanupCandidates"] = new
        {
            totalBytes = cleanupBytes,
            files = cleanupFiles,
            note = "Use find_cleanup_candidates for an itemized list.",
        };
        return summary;
    }

    private static object GetCurrentScan(JsonElement args)
    {
        var snapshot = MainViewModel.Current?.CurrentSnapshot
            ?? throw new InvalidOperationException("No scan is open in the DriveVisualizer window. Use scan_folder, or run a scan in the app first.");
        return SummarizeSnapshot(snapshot, TopN(args, 15));
    }

    private static object SummarizeSnapshot(ScanSnapshot snap, int topN)
    {
        var paths = snap.BuildDirectoryPaths();
        var topDirs = snap.Directories
            .Select((d, i) => new { path = paths[i], bytes = d.AllocatedSize, files = d.FileCount })
            .OrderByDescending(d => d.bytes)
            .Skip(1) // index 0 is the root itself
            .Take(topN)
            .ToArray();

        return new Dictionary<string, object?>
        {
            ["target"] = snap.Target,
            ["scannedUtc"] = snap.TimestampUtc,
            ["totalAllocatedBytes"] = snap.TotalAllocated,
            ["totalAllocated"] = ByteFormatter.Format(snap.TotalAllocated),
            ["totalFiles"] = snap.TotalFiles,
            ["totalDirectories"] = snap.TotalDirectories,
            ["categories"] = CategoryMap(snap.CategoryBytes),
            ["topFolders"] = topDirs,
            ["topFiles"] = snap.TopFiles.Take(topN).Select(f => new { f.Path, bytes = f.AllocatedSize }).ToArray(),
            ["driveHealth"] = snap.DriveHealth,
        };
    }

    private static Dictionary<string, long> CategoryMap(long[] categoryBytes)
    {
        var map = new Dictionary<string, long>();
        for (int i = 0; i < categoryBytes.Length && i < FileClassification.CategoryCount; i++)
            if (categoryBytes[i] > 0)
                map[FileClassification.DisplayNames[i]] = categoryBytes[i];
        return map;
    }

    // ===================== snapshot history =====================

    private static object ListSnapshotTargets()
    {
        string root = MainViewModel.GetHistoryRootDirectory();
        if (!Directory.Exists(root))
            return new { targets = Array.Empty<object>() };

        var targets = new List<object>();
        foreach (string dir in Directory.GetDirectories(root))
        {
            string[] files = Directory.GetFiles(dir, "*.dvsnap");
            if (files.Length == 0)
                continue;
            string newest = files.OrderByDescending(File.GetLastWriteTimeUtc).First();
            try
            {
                var snap = ScanSnapshot.Load(newest);
                targets.Add(new
                {
                    target = snap.Target,
                    snapshots = files.Length,
                    newestUtc = snap.TimestampUtc,
                    totalAllocatedBytes = snap.TotalAllocated,
                    totalAllocated = ByteFormatter.Format(snap.TotalAllocated),
                });
            }
            catch { }
        }
        return new { targets };
    }

    private static List<ScanSnapshot> LoadHistory(string target)
    {
        string dir = MainViewModel.GetHistoryDirectory(target);
        if (!Directory.Exists(dir))
            throw new DirectoryNotFoundException($"No snapshot history for target: {target}. Use list_snapshot_targets to see what exists.");
        var history = Directory.GetFiles(dir, "*.dvsnap")
            .AsParallel()
            .Select(f => { try { return ScanSnapshot.Load(f); } catch { return null; } })
            .Where(s => s is not null)
            .Select(s => s!)
            .OrderBy(s => s.TimestampUtc)
            .ToList();
        if (history.Count == 0)
            throw new InvalidOperationException($"Snapshot folder for {target} exists but contains no readable snapshots.");
        return history;
    }

    private static object GetHistory(JsonElement args)
    {
        var history = LoadHistory(RequireString(args, "target"));
        return new
        {
            target = history[^1].Target,
            snapshots = history.Select(s => new
            {
                timestampUtc = s.TimestampUtc,
                totalAllocatedBytes = s.TotalAllocated,
                totalAllocated = ByteFormatter.Format(s.TotalAllocated),
                totalFiles = s.TotalFiles,
                categories = CategoryMap(s.CategoryBytes),
                driveHealth = s.DriveHealth is { } h ? new
                {
                    freeBytes = h.VolumeFreeBytes,
                    temperatureC = h.TemperatureC,
                    wearPercent = h.WearPercent,
                    health = h.Health,
                } : null,
            }).ToArray(),
        };
    }

    private static object DiffSnapshots(JsonElement args)
    {
        var history = LoadHistory(RequireString(args, "target"));
        if (history.Count < 2)
            throw new InvalidOperationException("Need at least two snapshots to diff; this target has one.");
        int topN = TopN(args, 15);

        ScanSnapshot Pick(string? stamp, ScanSnapshot fallback)
        {
            if (stamp is null)
                return fallback;
            return history.FirstOrDefault(s =>
                    s.TimestampUtc.ToString("o").StartsWith(stamp, StringComparison.OrdinalIgnoreCase) ||
                    s.TimestampUtc.ToLocalTime().ToString("o").StartsWith(stamp, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"No snapshot matches '{stamp}'. Available: {string.Join(", ", history.Select(s => s.TimestampUtc.ToString("yyyy-MM-dd HH:mm"))) }");
        }

        var before = Pick(OptionalString(args, "from"), history[^2]);
        var after = Pick(OptionalString(args, "to"), history[^1]);

        var categoryDeltas = new Dictionary<string, long>();
        for (int i = 0; i < FileClassification.CategoryCount; i++)
        {
            long b = i < before.CategoryBytes.Length ? before.CategoryBytes[i] : 0;
            long a = i < after.CategoryBytes.Length ? after.CategoryBytes[i] : 0;
            if (a - b != 0)
                categoryDeltas[FileClassification.DisplayNames[i]] = a - b;
        }

        return new
        {
            target = after.Target,
            fromUtc = before.TimestampUtc,
            toUtc = after.TimestampUtc,
            totalDeltaBytes = after.TotalAllocated - before.TotalAllocated,
            totalDelta = FormatDelta(after.TotalAllocated - before.TotalAllocated),
            fileCountDelta = after.TotalFiles - before.TotalFiles,
            categoryDeltas,
            folderMovers = HistoryChart.ComputeDirMovers(before, after, topN)
                .Select(m => new { path = m.Path, deltaBytes = m.Delta, delta = FormatDelta(m.Delta) }).ToArray(),
            fileMovers = HistoryChart.ComputeFileMovers(before, after, topN)
                .Select(m => new { path = m.Path, deltaBytes = m.Delta, delta = FormatDelta(m.Delta) }).ToArray(),
        };
    }

    private static string FormatDelta(long delta) =>
        delta == 0 ? "0" : (delta > 0 ? "+" : "-") + ByteFormatter.Format(Math.Abs(delta));

    // ===================== drive_info =====================

    private static object DriveInfo(JsonElement args)
    {
        string path = RequireString(args, "path");
        var d = DriveStats.Get(path)
            ?? throw new InvalidOperationException($"No local drive details available for: {path}");
        return new
        {
            volume = new { root = d.Root, label = d.VolumeLabel, filesystem = d.FileSystem, totalBytes = d.TotalBytes, freeBytes = d.FreeBytes, clusterSize = d.ClusterSize },
            disk = new { model = d.Model, mediaType = d.MediaType, busType = d.BusType, health = d.Health, spindleSpeedRpm = d.SpindleSpeedRpm, serialNumber = d.SerialNumber, firmwareVersion = d.FirmwareVersion, sizeBytes = d.PhysicalSizeBytes, logicalSectorSize = d.LogicalSectorSize, physicalSectorSize = d.PhysicalSectorSize },
            partitions = d.Partitions.Select(p => new { p.Number, driveLetter = p.DriveLetter?.ToString(), p.TypeName, sizeBytes = p.SizeBytes, p.VolumeLabel, p.FileSystem, freeBytes = p.FreeBytes, p.IsBoot, p.IsSystem }).ToArray(),
            smart = d.Smart,
        };
    }

    // ===================== find_cleanup_candidates =====================

    private static async Task<object> FindCleanupCandidatesAsync(JsonElement args)
    {
        string path = RequireString(args, "path");
        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException($"Folder not found: {path}");
        int topN = TopN(args, 20);

        var scanner = new ParallelScanner();
        var result = await scanner.ScanAsync(path).ConfigureAwait(false);
        var (totalBytes, files) = CleanupHeuristics.MarkCandidates(result.Root);

        // Collect top-most candidate nodes: once a directory is a candidate,
        // don't also list its descendants.
        var candidates = new List<(string Path, long Bytes, bool IsDirectory)>();
        void Walk(FsNode node)
        {
            if ((node.Flags & NodeFlags.CleanupCandidate) != 0)
            {
                candidates.Add((node.GetFullPath(), node.AllocatedSize, node.IsDirectory));
                return;
            }
            if (node.Children is { } children)
                foreach (var child in children)
                    Walk(child);
        }
        Walk(result.Root);

        return new
        {
            target = path,
            totalBytes,
            total = ByteFormatter.Format(totalBytes),
            files,
            note = "Read-only report. Deleting is up to you (or the DriveVisualizer UI's right-click actions).",
            candidates = candidates
                .OrderByDescending(c => c.Bytes)
                .Take(topN)
                .Select(c => new { path = c.Path, bytes = c.Bytes, size = ByteFormatter.Format(c.Bytes), isDirectory = c.IsDirectory })
                .ToArray(),
        };
    }
}
