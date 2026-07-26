using DriveVisualizer.Core;
using Microsoft.UI.Xaml;

namespace DriveVisualizer_App.ViewModels;

/// <summary>
/// One visible row in the flattened directory tree. Rows are rebuilt on every
/// expand/collapse (cost is proportional to visible rows only), so they carry
/// no change notification — all values are computed once at build time.
/// </summary>
public sealed class NodeRow
{
    // Segoe Fluent icon code points.
    private const string ChevronDown = "";
    private const string ChevronRight = "";
    private const string FolderIcon = "";
    private const string FileIcon = "";
    private const string LockIcon = "";
    private const string LinkIcon = "";

    public required FsNode Node { get; init; }
    public required int Depth { get; init; }
    public required bool IsExpanded { get; init; }

    /// <summary>Share of the parent's allocated size, 0–100.</summary>
    public required double PercentOfParent { get; init; }

    public bool HasChildren => Node.Children is { Length: > 0 };

    public string Name => Node.Name;
    public string ChevronGlyph => !HasChildren ? "" : IsExpanded ? ChevronDown : ChevronRight;
    public string IconGlyph => Node.IsDirectory ? FolderIcon : FileIcon;

    public string SizeText => ByteFormatter.Format(Node.AllocatedSize);
    public string PercentText => $"{PercentOfParent:F1}%";
    public string FilesText => Node.IsDirectory ? Node.SubtreeFileCount.ToString("N0") : "";

    public string ModifiedText => Node.LastWriteTimeTicks > 0
        ? new DateTime(Node.LastWriteTimeTicks, DateTimeKind.Utc).ToLocalTime().ToString("g")
        : "";

    public string Badge =>
        Node.Flags.HasFlag(NodeFlags.AccessDenied) ? LockIcon :
        Node.Flags.HasFlag(NodeFlags.ReparseSkipped) ? LinkIcon :
        "";

    public Thickness Indent => new(Depth * 20, 0, 0, 0);
}
