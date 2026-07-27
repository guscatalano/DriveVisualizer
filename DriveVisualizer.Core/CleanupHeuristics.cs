namespace DriveVisualizer.Core;

/// <summary>
/// Conservative, explainable "you could probably delete this" marking:
/// files inside well-known disposable directories (temp, caches, recycle bin,
/// node_modules, crash dumps) and files whose extension classifies as
/// Temp &amp; logs. No content inspection, no guessing about user documents.
/// </summary>
public static class CleanupHeuristics
{
    private static readonly HashSet<string> DisposableDirNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "temp", "tmp", "cache", "caches", ".cache", "$recycle.bin",
        "crashdumps", "node_modules", "npm-cache", "__pycache__",
    };

    /// <summary>
    /// Sets or clears <see cref="NodeFlags.CleanupCandidate"/> on every file in
    /// the tree; returns total candidate bytes and file count.
    /// </summary>
    public static (long Bytes, int Files) MarkCandidates(FsNode root)
    {
        long bytes = 0;
        int files = 0;
        var stack = new Stack<(FsNode Node, bool InDisposableDir)>();
        stack.Push((root, false));

        while (stack.Count > 0)
        {
            var (node, inherited) = stack.Pop();
            if (node.IsDirectory)
            {
                bool disposable = inherited || DisposableDirNames.Contains(node.Name);
                if (node.Children is { } children)
                    foreach (var child in children)
                        stack.Push((child, disposable));
            }
            else
            {
                bool candidate = inherited ||
                    FileClassification.Classify(node.Name) == FileCategory.TempAndLogs;
                if (candidate)
                {
                    node.Flags |= NodeFlags.CleanupCandidate;
                    bytes += node.AllocatedSize;
                    files++;
                }
                else
                {
                    node.Flags &= ~NodeFlags.CleanupCandidate;
                }
            }
        }
        return (bytes, files);
    }
}
