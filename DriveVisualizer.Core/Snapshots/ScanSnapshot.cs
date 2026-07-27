using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DriveVisualizer.Core.Snapshots;

public sealed class SnapshotDir
{
    [JsonPropertyName("n")] public required string Name { get; set; }
    [JsonPropertyName("p")] public int ParentIndex { get; set; } // -1 for root
    [JsonPropertyName("a")] public long AllocatedSize { get; set; }
    [JsonPropertyName("l")] public long LogicalSize { get; set; }
    [JsonPropertyName("f")] public int FileCount { get; set; }
}

public sealed class SnapshotFile
{
    [JsonPropertyName("p")] public required string Path { get; set; }
    [JsonPropertyName("a")] public long AllocatedSize { get; set; }
}

/// <summary>
/// A saved scan: totals, per-category bytes, every directory (flat, parent-indexed),
/// and the largest files. Serialized as gzipped JSON (*.dvsnap).
/// </summary>
public sealed class ScanSnapshot
{
    public int Version { get; set; } = 1;
    public required string Target { get; set; }
    public DateTime TimestampUtc { get; set; }
    public long TotalFiles { get; set; }
    public long TotalDirectories { get; set; }
    public long TotalLogical { get; set; }
    public long TotalAllocated { get; set; }

    /// <summary>Indexed by (int)FileCategory.</summary>
    public long[] CategoryBytes { get; set; } = [];

    public List<SnapshotFile> TopFiles { get; set; } = [];
    public List<SnapshotDir> Directories { get; set; } = [];

    private static readonly JsonSerializerOptions JsonOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault };

    public void Save(string path)
    {
        using var file = File.Create(path);
        using var gzip = new GZipStream(file, CompressionLevel.Fastest);
        JsonSerializer.Serialize(gzip, this, JsonOptions);
    }

    public static ScanSnapshot Load(string path)
    {
        using var file = File.OpenRead(path);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        return JsonSerializer.Deserialize<ScanSnapshot>(gzip, JsonOptions)
            ?? throw new InvalidDataException("Snapshot file is empty or corrupt.");
    }

    /// <summary>Full path of each directory, reconstructed from parent indices.</summary>
    public string[] BuildDirectoryPaths()
    {
        var paths = new string[Directories.Count];
        for (int i = 0; i < Directories.Count; i++)
        {
            var dir = Directories[i];
            paths[i] = dir.ParentIndex < 0
                ? dir.Name
                : paths[dir.ParentIndex].TrimEnd('\\') + '\\' + dir.Name;
        }
        return paths; // parents always precede children (pre-order build)
    }

    public static ScanSnapshot Build(FsNode root, string target, DateTime timestampUtc, int topFileCount = 100)
    {
        var snapshot = new ScanSnapshot
        {
            Target = target,
            TimestampUtc = timestampUtc,
            TotalFiles = root.SubtreeFileCount,
            TotalLogical = root.LogicalSize,
            TotalAllocated = root.AllocatedSize,
            CategoryBytes = new long[FileClassification.CategoryCount],
        };

        var indexOf = new Dictionary<FsNode, int>();
        var stack = new Stack<FsNode>();
        stack.Push(root);
        // Track the smallest of the current top-N files for a cheap bound.
        var top = new List<(long Size, FsNode Node)>(topFileCount + 1);
        long topThreshold = 0;

        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node.IsDirectory)
            {
                int parentIndex = node.Parent is { } p ? indexOf[p] : -1;
                indexOf[node] = snapshot.Directories.Count;
                snapshot.Directories.Add(new SnapshotDir
                {
                    Name = node.Name,
                    ParentIndex = parentIndex,
                    AllocatedSize = node.AllocatedSize,
                    LogicalSize = node.LogicalSize,
                    FileCount = node.SubtreeFileCount,
                });
                snapshot.TotalDirectories++;

                if (node.Children is { } children)
                    for (int i = children.Length - 1; i >= 0; i--)
                        stack.Push(children[i]);
            }
            else
            {
                snapshot.CategoryBytes[(int)FileClassification.Classify(node.Name)] += node.AllocatedSize;
                if (node.AllocatedSize > topThreshold || top.Count < topFileCount)
                {
                    top.Add((node.AllocatedSize, node));
                    if (top.Count > topFileCount)
                    {
                        top.Sort(static (a, b) => b.Size.CompareTo(a.Size));
                        top.RemoveAt(top.Count - 1);
                        topThreshold = top[^1].Size;
                    }
                }
            }
        }

        // Root itself was counted as a directory; keep TotalDirectories as subdirectory count.
        snapshot.TotalDirectories = Math.Max(0, snapshot.TotalDirectories - 1);

        top.Sort(static (a, b) => b.Size.CompareTo(a.Size));
        foreach (var (size, node) in top)
            snapshot.TopFiles.Add(new SnapshotFile { Path = node.GetFullPath(), AllocatedSize = size });

        return snapshot;
    }
}
