using DriveVisualizer.Core;
using DriveVisualizer.Core.Treemap;

namespace DriveVisualizer.Tests;

public sealed class AltLayoutTests
{
    private static FsNode Dir(string name, FsNode? parent, params FsNode[] children)
    {
        var dir = new FsNode { Name = name, Parent = parent, Attributes = FsNode.FILE_ATTRIBUTE_DIRECTORY };
        dir.Children = children;
        foreach (var c in children)
            dir.AllocatedSize += c.AllocatedSize;
        Array.Sort(children, static (a, b) => b.AllocatedSize.CompareTo(a.AllocatedSize));
        return dir;
    }

    private static FsNode File(string name, long size)
    {
        var f = new FsNode { Name = name };
        f.AllocatedSize = size;
        f.LogicalSize = size;
        return f;
    }

    private static FsNode SampleTree()
    {
        var inner = Dir("inner", null, File("x", 600), File("y", 400));
        return Dir("root", null, inner, File("z", 1000), File("w", 500));
    }

    [Fact]
    public void SunburstSweepsAreProportionalAndNested()
    {
        var root = SampleTree();
        var arcs = SunburstLayout.Compute(root);

        // Ring 1 sweeps sum to a full circle (within float tolerance).
        float ring1 = arcs.Where(a => a.Depth == 1).Sum(a => a.SweepAngle);
        Assert.True(Math.Abs(ring1 - MathF.Tau) < 0.01f, $"ring1 sums to {ring1}");

        // z gets 1000/2500 of the circle.
        var z = arcs.Single(a => a.Node.Name == "z");
        Assert.True(Math.Abs(z.SweepAngle - MathF.Tau * 0.4f) < 0.01f);

        // Children of inner sit inside inner's angular range, one ring deeper.
        var inner = arcs.Single(a => a.Node.Name == "inner");
        foreach (var child in arcs.Where(a => a.Depth == 2))
        {
            Assert.True(child.StartAngle >= inner.StartAngle - 0.001f);
            Assert.True(child.StartAngle + child.SweepAngle <= inner.StartAngle + inner.SweepAngle + 0.001f);
        }
    }

    [Fact]
    public void SunburstRespectsMaxDepthAndMinSweep()
    {
        var root = SampleTree();
        var arcs = SunburstLayout.Compute(root, maxDepth: 1);
        Assert.All(arcs, a => Assert.Equal(1, a.Depth));

        var arcsFiltered = SunburstLayout.Compute(root, minSweep: MathF.Tau * 0.3f);
        Assert.DoesNotContain(arcsFiltered, a => a.Node.Name == "w"); // 20% < 30% threshold
    }

    [Fact]
    public void IcicleRowsNestWithinParents()
    {
        var root = SampleTree();
        var rects = IcicleLayout.Compute(root);

        var rootRect = rects.Single(r => r.Depth == 0);
        Assert.Equal(0f, rootRect.X0);
        Assert.Equal(1f, rootRect.X1);

        var inner = rects.Single(r => r.Node.Name == "inner");
        foreach (var child in rects.Where(r => r.Depth == 2))
        {
            Assert.True(child.X0 >= inner.X0 - 0.001f);
            Assert.True(child.X1 <= inner.X1 + 0.001f);
        }

        // Row 1 widths are proportional: z = 1000/2500 = 0.4
        var z = rects.Single(r => r.Node.Name == "z");
        Assert.True(Math.Abs((z.X1 - z.X0) - 0.4f) < 0.005f);
    }

    [Fact]
    public void DominantCategoryReflectsSubtreeBytes()
    {
        var media = Dir("media", null, File("movie.mp4", 900), File("song.mp3", 50));
        var root = Dir("root", null, media, File("app.exe", 400));

        var dominant = DominantCategories.Compute(root);

        Assert.Equal(FileCategory.Media, dominant[media]);
        Assert.Equal(FileCategory.Media, dominant[root]); // 950 media vs 400 apps
    }

    [Fact]
    public void IcicleSiblingsDoNotOverlap()
    {
        var root = SampleTree();
        var rects = IcicleLayout.Compute(root);
        var row1 = rects.Where(r => r.Depth == 1).OrderBy(r => r.X0).ToList();
        for (int i = 1; i < row1.Count; i++)
            Assert.True(row1[i].X0 >= row1[i - 1].X1 - 0.001f);
    }
}
