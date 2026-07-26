using System.Diagnostics;
using System.Threading.Channels;
using DriveVisualizer.Core.Interop;

namespace DriveVisualizer.Core.Scanning;

/// <summary>
/// Parallel breadth-first directory scanner over raw FindFirstFileExW.
/// Each worker pulls a directory off a channel, enumerates it in one pass
/// (names + sizes + attributes come back together), and pushes subdirectories
/// back onto the channel. Reparse points (junctions, symlinks) are recorded
/// but never recursed into, so junction cycles and double-counting can't happen.
/// </summary>
public sealed class ParallelScanner : IScanner
{
    private ScanStatistics _statistics = new();

    public ScanStatistics Statistics => _statistics;

    public int DegreeOfParallelism { get; init; } = Math.Max(2, Environment.ProcessorCount);

    public async Task<ScanResult> ScanAsync(string rootPath, CancellationToken cancellationToken = default)
    {
        rootPath = Path.GetFullPath(rootPath);
        if (!Directory.Exists(rootPath))
            throw new DirectoryNotFoundException($"Directory not found: {rootPath}");

        var stats = new ScanStatistics();
        _statistics = stats;
        long clusterSize = GetClusterSize(rootPath);
        var stopwatch = Stopwatch.StartNew();

        var root = new FsNode
        {
            Name = rootPath,
            Attributes = (uint)File.GetAttributes(rootPath),
        };

        var channel = Channel.CreateUnbounded<FsNode>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false,
        });

        int pending = 1;
        channel.Writer.TryWrite(root);

        var workers = new Task[DegreeOfParallelism];
        for (int i = 0; i < workers.Length; i++)
        {
            workers[i] = Task.Run(async () =>
            {
                await foreach (var dir in channel.Reader.ReadAllAsync().ConfigureAwait(false))
                {
                    try
                    {
                        if (!cancellationToken.IsCancellationRequested)
                            EnumerateDirectory(dir);
                    }
                    finally
                    {
                        if (Interlocked.Decrement(ref pending) == 0)
                            channel.Writer.TryComplete();
                    }
                }
            }, CancellationToken.None);
        }

        await Task.WhenAll(workers).ConfigureAwait(false);
        Aggregate(root);
        stopwatch.Stop();

        return new ScanResult(root, stopwatch.Elapsed, cancellationToken.IsCancellationRequested);

        void EnumerateDirectory(FsNode dir)
        {
            string dirPath = dir.GetFullPath();
            string extendedDirPath = ToExtendedPath(dirPath);

            using var handle = NativeMethods.FindFirstFileExW(
                extendedDirPath + @"\*",
                NativeMethods.FindExInfoBasic,
                out WIN32_FIND_DATAW findData,
                NativeMethods.FindExSearchNameMatch,
                IntPtr.Zero,
                NativeMethods.FIND_FIRST_EX_LARGE_FETCH);

            if (handle.IsInvalid)
            {
                int error = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
                if (error == NativeMethods.ERROR_ACCESS_DENIED)
                {
                    dir.Flags |= NodeFlags.AccessDenied;
                    stats.AddAccessDenied();
                }
                return;
            }

            var children = new List<FsNode>();
            do
            {
                string name = findData.cFileName;
                if (name is "." or "..")
                    continue;

                bool isDirectory = (findData.dwFileAttributes & FsNode.FILE_ATTRIBUTE_DIRECTORY) != 0;
                bool isReparse = (findData.dwFileAttributes & FsNode.FILE_ATTRIBUTE_REPARSE_POINT) != 0;

                var node = new FsNode
                {
                    Name = name,
                    Parent = dir,
                    Attributes = findData.dwFileAttributes,
                    LastWriteTimeTicks = findData.LastWriteTimeTicks,
                };
                children.Add(node);

                if (isDirectory)
                {
                    stats.AddDirectory();
                    if (isReparse)
                    {
                        // Junction or directory symlink: its contents live elsewhere
                        // (or under another path we'll scan anyway) — don't recurse.
                        node.Flags |= NodeFlags.ReparseSkipped;
                        stats.AddReparseSkipped();
                    }
                    else
                    {
                        Interlocked.Increment(ref pending);
                        channel.Writer.TryWrite(node);
                    }
                }
                else
                {
                    node.LogicalSize = findData.FileSize;
                    node.AllocatedSize = ComputeAllocatedSize(
                        findData, extendedDirPath, name, clusterSize);
                    stats.AddFile(node.LogicalSize, node.AllocatedSize);
                }
            }
            while (NativeMethods.FindNextFileW(handle, out findData));

            dir.Children = children.Count > 0 ? children.ToArray() : [];
        }
    }

    private static long ComputeAllocatedSize(
        in WIN32_FIND_DATAW findData, string extendedDirPath, string name, long clusterSize)
    {
        const uint placeholderMask =
            NativeMethods.FILE_ATTRIBUTE_OFFLINE |
            NativeMethods.FILE_ATTRIBUTE_RECALL_ON_OPEN |
            NativeMethods.FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS;

        // Cloud placeholders (OneDrive online-only) and offline files occupy no local space.
        if ((findData.dwFileAttributes & placeholderMask) != 0)
            return 0;

        long logical = findData.FileSize;
        if (logical == 0)
            return 0;

        const uint compressedOrSparse =
            NativeMethods.FILE_ATTRIBUTE_COMPRESSED | NativeMethods.FILE_ATTRIBUTE_SPARSE_FILE;

        if ((findData.dwFileAttributes & compressedOrSparse) != 0)
        {
            uint low = NativeMethods.GetCompressedFileSizeW(extendedDirPath + '\\' + name, out uint high);
            if (low != NativeMethods.INVALID_FILE_SIZE ||
                System.Runtime.InteropServices.Marshal.GetLastPInvokeError() == 0)
            {
                logical = ((long)high << 32) | low;
            }
        }

        return (logical + clusterSize - 1) / clusterSize * clusterSize;
    }

    /// <summary>Bottom-up size/count aggregation into directory nodes.</summary>
    private static void Aggregate(FsNode root)
    {
        // Pre-order flatten, then reverse-iterate: every child is processed
        // before its parent, without recursion (paths can nest very deep).
        var order = new List<FsNode>(1024);
        var stack = new Stack<FsNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            order.Add(node);
            if (node.Children is { } kids)
                foreach (var child in kids)
                    stack.Push(child);
        }

        for (int i = order.Count - 1; i > 0; i--)
        {
            var node = order[i];
            var parent = node.Parent!;
            parent.LogicalSize += node.LogicalSize;
            parent.AllocatedSize += node.AllocatedSize;
            parent.SubtreeFileCount += node.IsDirectory ? node.SubtreeFileCount : 1;
        }
    }

    private static long GetClusterSize(string path)
    {
        string? volumeRoot = Path.GetPathRoot(path);
        if (volumeRoot is not null &&
            NativeMethods.GetDiskFreeSpaceW(volumeRoot, out uint sectorsPerCluster, out uint bytesPerSector, out _, out _))
        {
            return (long)sectorsPerCluster * bytesPerSector;
        }
        return 4096;
    }

    private static string ToExtendedPath(string path) =>
        path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path :
        path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC" + path[1..] :
        @"\\?\" + path;
}
