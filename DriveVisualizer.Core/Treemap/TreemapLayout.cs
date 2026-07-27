using System.Drawing;

namespace DriveVisualizer.Core.Treemap;

/// <summary>A leaf rectangle in the treemap: a file, or an aggregate of items too small to show.</summary>
public readonly record struct Tile(FsNode Node, RectangleF Rect, bool IsAggregate);

public sealed record TreemapResult(List<Tile> Tiles, Dictionary<FsNode, RectangleF> DirectoryBounds);

/// <summary>
/// Squarified treemap layout (Bruls, Huizing &amp; van Wijk): lays out children
/// in rows along the shorter side of the remaining rectangle, keeping tiles as
/// close to square as possible. Pure function of the tree — no UI dependencies.
/// Assumes each directory's children are pre-sorted by AllocatedSize descending
/// (the squarified algorithm requires descending order).
/// </summary>
public static class TreemapLayout
{
    public static TreemapResult Compute(FsNode root, float width, float height, float minTileArea = 3f)
    {
        var tiles = new List<Tile>();
        var dirBounds = new Dictionary<FsNode, RectangleF>();

        if (root.AllocatedSize > 0 && width > 1 && height > 1)
        {
            var rootRect = new RectangleF(0, 0, width, height);
            dirBounds[root] = rootRect;

            var pending = new Stack<(FsNode Dir, RectangleF Rect)>();
            pending.Push((root, rootRect));

            while (pending.Count > 0)
            {
                var (dir, rect) = pending.Pop();
                LayoutDirectory(dir, rect, minTileArea, tiles, dirBounds, pending);
            }
        }

        return new TreemapResult(tiles, dirBounds);
    }

    private static void LayoutDirectory(
        FsNode dir, RectangleF rect, float minTileArea,
        List<Tile> tiles, Dictionary<FsNode, RectangleF> dirBounds,
        Stack<(FsNode, RectangleF)> pending)
    {
        var children = dir.Children;
        if (children is null || children.Length == 0)
        {
            // Unreadable or empty directory occupying visible space: show as an aggregate tile.
            tiles.Add(new Tile(dir, rect, IsAggregate: true));
            return;
        }

        long total = 0;
        foreach (var c in children)
            total += c.AllocatedSize;
        if (total <= 0)
            return;

        double scale = (double)rect.Width * rect.Height / total;

        // Children are sorted descending, so once one falls below the minimum
        // tile area every remaining one does too — collapse them into a single
        // pseudo-item laid out as one aggregate tile.
        var items = new List<(FsNode? Node, double Area)>(children.Length);
        double restArea = 0;
        foreach (var c in children)
        {
            if (c.AllocatedSize <= 0)
                continue;
            double area = c.AllocatedSize * scale;
            if (area < minTileArea && items.Count > 0)
                restArea += area;
            else
                items.Add((c, area));
        }
        if (restArea > 0)
            items.Add((null, restArea));

        Squarify(dir, items, rect, minTileArea, tiles, dirBounds, pending);
    }

    private static void Squarify(
        FsNode dir, List<(FsNode? Node, double Area)> items, RectangleF rect, float minTileArea,
        List<Tile> tiles, Dictionary<FsNode, RectangleF> dirBounds,
        Stack<(FsNode, RectangleF)> pending)
    {
        int start = 0;
        while (start < items.Count)
        {
            double shortSide = Math.Min(rect.Width, rect.Height);
            if (shortSide < 1e-3)
                break;

            // Grow the row while doing so improves (does not worsen) the worst aspect ratio.
            double rowArea = items[start].Area;
            double rowMax = rowArea, rowMin = rowArea;
            int end = start + 1;
            double worst = Worst(rowArea, rowMax, rowMin, shortSide);
            while (end < items.Count)
            {
                double a = items[end].Area;
                double newArea = rowArea + a;
                double newMax = Math.Max(rowMax, a);
                double newMin = Math.Min(rowMin, a);
                double newWorst = Worst(newArea, newMax, newMin, shortSide);
                if (newWorst > worst)
                    break;
                rowArea = newArea; rowMax = newMax; rowMin = newMin; worst = newWorst;
                end++;
            }

            // Lay the row along the short side, then shrink the remaining rect.
            bool horizontal = rect.Width >= rect.Height; // row occupies a vertical strip on the left when true
            float thickness = (float)(rowArea / shortSide);
            float offset = 0;
            for (int i = start; i < end; i++)
            {
                float length = (float)(items[i].Area / rowArea * shortSide);
                var tileRect = horizontal
                    ? new RectangleF(rect.X, rect.Y + offset, thickness, length)
                    : new RectangleF(rect.X + offset, rect.Y, length, thickness);
                offset += length;

                Emit(items[i].Node, dir, tileRect, minTileArea, tiles, dirBounds, pending);
            }

            rect = horizontal
                ? new RectangleF(rect.X + thickness, rect.Y, rect.Width - thickness, rect.Height)
                : new RectangleF(rect.X, rect.Y + thickness, rect.Width, rect.Height - thickness);
            start = end;
        }
    }

    private static void Emit(
        FsNode? node, FsNode dir, RectangleF tileRect, float minTileArea,
        List<Tile> tiles, Dictionary<FsNode, RectangleF> dirBounds,
        Stack<(FsNode, RectangleF)> pending)
    {
        if (node is null)
        {
            tiles.Add(new Tile(dir, tileRect, IsAggregate: true));
        }
        else if (node.IsDirectory)
        {
            dirBounds[node] = tileRect;
            if (tileRect.Width * tileRect.Height >= minTileArea)
                pending.Push((node, tileRect));
            else
                tiles.Add(new Tile(node, tileRect, IsAggregate: true));
        }
        else
        {
            tiles.Add(new Tile(node, tileRect, IsAggregate: false));
        }
    }

    /// <summary>Worst (largest) aspect ratio in a row, per the squarified treemap paper.</summary>
    private static double Worst(double rowArea, double maxArea, double minArea, double side)
    {
        double s2 = side * side;
        double r2 = rowArea * rowArea;
        return Math.Max(s2 * maxArea / r2, r2 / (s2 * minArea));
    }
}
