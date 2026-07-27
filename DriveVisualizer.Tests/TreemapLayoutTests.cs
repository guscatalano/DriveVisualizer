using System.Drawing;
using DriveVisualizer.Core;
using DriveVisualizer.Core.Treemap;

namespace DriveVisualizer.Tests;

public sealed class TreemapLayoutTests
{
    private static FsNode Dir(string name, FsNode? parent, params FsNode[] children)
    {
        var dir = new FsNode { Name = name, Parent = parent, Attributes = FsNode.FILE_ATTRIBUTE_DIRECTORY };
        dir.Children = children;
        foreach (var c in children)
            dir.AllocatedSize += c.AllocatedSize;
        // keep the descending-size invariant the layout expects
        Array.Sort(children, static (a, b) => b.AllocatedSize.CompareTo(a.AllocatedSize));
        return dir;
    }

    private static FsNode File(string name, long size) =>
        new() { Name = name, AllocatedSize = size, LogicalSize = size };

    [Fact]
    public void TileAreasAreProportionalToSizes()
    {
        var root = Dir("root", null, File("a", 500), File("b", 300), File("c", 200));
        var result = TreemapLayout.Compute(root, 100, 100);

        Assert.Equal(3, result.Tiles.Count);
        foreach (var tile in result.Tiles)
        {
            double expected = 10000.0 * tile.Node.AllocatedSize / 1000.0;
            double actual = tile.Rect.Width * tile.Rect.Height;
            Assert.True(Math.Abs(actual - expected) < 1.0,
                $"{tile.Node.Name}: expected area {expected}, got {actual}");
        }
    }

    [Fact]
    public void TilesStayInBoundsAndDoNotOverlap()
    {
        var files = Enumerable.Range(1, 25).Select(i => File($"f{i}", i * 37L)).ToArray();
        var root = Dir("root", null, files);
        var result = TreemapLayout.Compute(root, 400, 300);

        var bounds = new RectangleF(0, 0, 400, 300);
        for (int i = 0; i < result.Tiles.Count; i++)
        {
            var a = result.Tiles[i].Rect;
            Assert.True(bounds.Contains(a) || RectanglesNearlyContained(bounds, a), $"tile {i} out of bounds: {a}");
            for (int j = i + 1; j < result.Tiles.Count; j++)
            {
                var b = result.Tiles[j].Rect;
                var overlap = RectangleF.Intersect(a, b);
                Assert.True(overlap.Width * overlap.Height < 0.5,
                    $"tiles {i} and {j} overlap by {overlap.Width * overlap.Height}");
            }
        }
    }

    [Fact]
    public void NestedDirectoriesAreSubdivided()
    {
        var inner = Dir("inner", null, File("x", 600), File("y", 400));
        var root = Dir("root", null, inner, File("z", 1000));
        var result = TreemapLayout.Compute(root, 200, 100);

        // Leaf tiles are x, y, z; inner has recorded bounds containing x and y.
        Assert.Equal(3, result.Tiles.Count(t => !t.IsAggregate));
        Assert.True(result.DirectoryBounds.ContainsKey(inner));

        var innerBounds = result.DirectoryBounds[inner];
        foreach (var tile in result.Tiles.Where(t => t.Node.Name is "x" or "y"))
            Assert.True(RectanglesNearlyContained(innerBounds, tile.Rect));
    }

    [Fact]
    public void TinyFilesCollapseIntoAggregateTile()
    {
        var files = new[] { File("big", 1_000_000) }
            .Concat(Enumerable.Range(1, 200).Select(i => File($"tiny{i}", 1)))
            .ToArray();
        var root = Dir("root", null, files);
        var result = TreemapLayout.Compute(root, 100, 100, minTileArea: 3f);

        Assert.Contains(result.Tiles, t => t.IsAggregate);
        // Far fewer tiles than files: the 200 tiny ones merge into one aggregate.
        Assert.True(result.Tiles.Count < 10, $"expected aggregation, got {result.Tiles.Count} tiles");
    }

    [Fact]
    public void AspectRatiosAreReasonable()
    {
        var files = Enumerable.Range(1, 12).Select(i => File($"f{i}", 100)).ToArray();
        var root = Dir("root", null, files);
        var result = TreemapLayout.Compute(root, 300, 300);

        foreach (var tile in result.Tiles)
        {
            double ratio = Math.Max(tile.Rect.Width / tile.Rect.Height, tile.Rect.Height / tile.Rect.Width);
            Assert.True(ratio < 3.0, $"{tile.Node.Name} aspect ratio {ratio:F2} too elongated");
        }
    }

    [Fact]
    public void EmptyRootProducesNoTiles()
    {
        var root = Dir("root", null);
        var result = TreemapLayout.Compute(root, 100, 100);
        Assert.Empty(result.Tiles);
    }

    private static bool RectanglesNearlyContained(RectangleF outer, RectangleF inner) =>
        inner.Left >= outer.Left - 0.01f && inner.Top >= outer.Top - 0.01f &&
        inner.Right <= outer.Right + 0.01f && inner.Bottom <= outer.Bottom + 0.01f;
}
