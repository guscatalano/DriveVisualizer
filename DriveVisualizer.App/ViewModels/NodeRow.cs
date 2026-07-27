using CommunityToolkit.Mvvm.ComponentModel;
using DriveVisualizer.Core;
using Microsoft.UI.Xaml;

namespace DriveVisualizer_App.ViewModels;

/// <summary>
/// One visible row in the flattened directory tree. Value properties read the
/// underlying node directly, so during a scan the view model calls
/// <see cref="Refresh"/> each progress tick and rows update in place —
/// no collection churn, scroll position preserved.
/// </summary>
public sealed partial class NodeRow : ObservableObject
{
    // Segoe Fluent icon code points.
    private const string ChevronDown = "";
    private const string ChevronRight = "";
    private const string FolderIcon = "";
    private const string FileIcon = "";
    private const string LockIcon = "";
    private const string LinkIcon = "";

    public FsNode Node { get; }
    public int Depth { get; }

    private bool _isExpanded;

    public NodeRow(FsNode node, int depth, bool isExpanded)
    {
        Node = node;
        Depth = depth;
        _isExpanded = isExpanded;
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value))
                OnPropertyChanged(nameof(ChevronGlyph));
        }
    }

    public bool HasChildren => Node.Children is { Length: > 0 };

    public string Name => Node.Name;
    public string ChevronGlyph => !HasChildren ? "" : IsExpanded ? ChevronDown : ChevronRight;
    public string IconGlyph => Node.IsDirectory ? FolderIcon : FileIcon;

    /// <summary>Share of the parent's allocated size, 0–100.</summary>
    public double PercentOfParent
    {
        get
        {
            long parentSize = Node.Parent?.AllocatedSize ?? Node.AllocatedSize;
            return parentSize > 0 ? 100.0 * Node.AllocatedSize / parentSize : 0;
        }
    }

    public string PercentText => $"{PercentOfParent:F1}%";
    public string SizeText => ByteFormatter.Format(Node.AllocatedSize);
    public string FilesText => Node.IsDirectory ? Node.SubtreeFileCount.ToString("N0") : "";

    public string ModifiedText => Node.LastWriteTimeTicks > 0
        ? new DateTime(Node.LastWriteTimeTicks, DateTimeKind.Utc).ToLocalTime().ToString("g")
        : "";

    public string Badge =>
        Node.Flags.HasFlag(NodeFlags.AccessDenied) ? LockIcon :
        Node.Flags.HasFlag(NodeFlags.ReparseSkipped) ? LinkIcon :
        "";

    public Thickness Indent => new(Depth * 20, 0, 0, 0);

    /// <summary>Re-raises change notifications for everything that moves during a scan.</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(PercentOfParent));
        OnPropertyChanged(nameof(PercentText));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(FilesText));
        OnPropertyChanged(nameof(ChevronGlyph));
        OnPropertyChanged(nameof(HasChildren));
        OnPropertyChanged(nameof(Badge));
    }
}
