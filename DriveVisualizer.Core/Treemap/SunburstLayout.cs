namespace DriveVisualizer.Core.Treemap;

/// <summary>One ring segment: node, start angle, sweep (radians), ring depth (1-based).</summary>
public readonly record struct SunburstArc(FsNode Node, float StartAngle, float SweepAngle, int Depth);

/// <summary>
/// Radial partition ("sunburst") layout: the root is the center disc, each ring
/// outward is one folder level, and every child gets an arc proportional to its
/// share of the parent. Pure math — pixel radii are the renderer's business.
/// Assumes children are pre-sorted by size descending.
/// </summary>
public static class SunburstLayout
{
    public static List<SunburstArc> Compute(FsNode root, int maxDepth = 5, float minSweep = 0.008f)
    {
        var arcs = new List<SunburstArc>();
        Recurse(root, -MathF.PI / 2f, MathF.Tau, 0);
        return arcs;

        void Recurse(FsNode dir, float start, float sweep, int depth)
        {
            if (depth >= maxDepth || dir.Children is not { Length: > 0 } children)
                return;
            long total = dir.AllocatedSize;
            if (total <= 0)
                return;

            float angle = start;
            foreach (var child in children)
            {
                float childSweep = (float)(sweep * child.AllocatedSize / (double)total);
                if (childSweep >= minSweep)
                {
                    arcs.Add(new SunburstArc(child, angle, childSweep, depth + 1));
                    if (child.IsDirectory)
                        Recurse(child, angle, childSweep, depth + 1);
                }
                angle += childSweep;
            }
        }
    }
}
