namespace DriveVisualizer.Core;

[Flags]
public enum NodeFlags : byte
{
    None = 0,
    AccessDenied = 1,
    ReparseSkipped = 2,
}

/// <summary>
/// One file or directory in the scanned tree. Kept deliberately lean: a full
/// system drive produces millions of these, so no full path is stored per node
/// (derive it by walking parents) and children use a plain array.
/// </summary>
public sealed class FsNode
{
    public const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
    public const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x400;

    public required string Name { get; init; }
    public FsNode? Parent { get; init; }

    /// <summary>Null for files and for directories that could not be scanned.</summary>
    public FsNode[]? Children { get; set; }

    // Plain fields (not properties) so scanner workers can Interlocked.Add into
    // them while propagating file sums up the ancestor chain during the scan.
    // Directory values therefore grow live and are complete when the scan ends.

    /// <summary>File length; for directories, aggregated subtree total.</summary>
    public long LogicalSize;

    /// <summary>Size on disk (cluster-rounded, compression/placeholder-aware); aggregated for directories.</summary>
    public long AllocatedSize;

    /// <summary>Number of files in this subtree (0 for file nodes themselves).</summary>
    public int SubtreeFileCount;

    public long LastWriteTimeTicks { get; set; }
    public uint Attributes { get; set; }
    public NodeFlags Flags { get; set; }

    public bool IsDirectory => (Attributes & FILE_ATTRIBUTE_DIRECTORY) != 0;
    public bool IsReparsePoint => (Attributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0;

    public string GetFullPath()
    {
        if (Parent is null)
            return Name;

        var parts = new List<string>(8);
        for (FsNode? n = this; n is not null; n = n.Parent)
            parts.Add(n.Name);
        parts.Reverse();

        // Root's Name is a full path like "C:\" — join without doubling separators.
        var sb = new System.Text.StringBuilder(parts[0].TrimEnd('\\'));
        for (int i = 1; i < parts.Count; i++)
        {
            sb.Append('\\');
            sb.Append(parts[i]);
        }
        return sb.ToString();
    }
}
