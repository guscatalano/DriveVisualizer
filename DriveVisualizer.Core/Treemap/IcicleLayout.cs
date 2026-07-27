namespace DriveVisualizer.Core.Treemap;

/// <summary>One icicle bar: node, horizontal extent (normalized 0..1), row depth (0 = root).</summary>
public readonly record struct IcicleRect(FsNode Node, float X0, float X1, int Depth);

/// <summary>
/// Icicle / flame-graph layout: the root spans the full width on row 0 and each
/// row below subdivides its parent proportionally. Normalized coordinates —
/// the renderer scales to pixels. Assumes children pre-sorted by size descending.
/// </summary>
public static class IcicleLayout
{
    public static List<IcicleRect> Compute(FsNode root, int maxDepth = 6, float minWidth = 0.0015f)
    {
        var rects = new List<IcicleRect> { new(root, 0f, 1f, 0) };
        Recurse(root, 0f, 1f, 0);
        return rects;

        void Recurse(FsNode dir, float x0, float x1, int depth)
        {
            if (depth + 1 >= maxDepth || dir.Children is not { Length: > 0 } children)
                return;
            long total = dir.AllocatedSize;
            if (total <= 0)
                return;

            float x = x0;
            float width = x1 - x0;
            foreach (var child in children)
            {
                float childWidth = (float)(width * child.AllocatedSize / (double)total);
                if (childWidth >= minWidth)
                {
                    rects.Add(new IcicleRect(child, x, x + childWidth, depth + 1));
                    if (child.IsDirectory)
                        Recurse(child, x, x + childWidth, depth + 1);
                }
                x += childWidth;
            }
        }
    }
}
