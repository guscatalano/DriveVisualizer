namespace DriveVisualizer.Core.Treemap;

/// <summary>
/// Computes, for every directory, which file category owns the most bytes in
/// its subtree — so radial/icicle views can tint folders by what's inside them.
/// One bottom-up pass; transient per-directory tallies are discarded as parents
/// consume them.
/// </summary>
public static class DominantCategories
{
    public static Dictionary<FsNode, FileCategory> Compute(FsNode root)
    {
        // Pre-order flatten (parents before children), then reverse-iterate so
        // every directory sees its children's finished tallies.
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

        var tallies = new Dictionary<FsNode, long[]>();
        var result = new Dictionary<FsNode, FileCategory>();

        for (int i = order.Count - 1; i >= 0; i--)
        {
            var node = order[i];
            if (!node.IsDirectory)
                continue;

            var t = new long[FileClassification.CategoryCount];
            if (node.Children is { } children)
            {
                foreach (var child in children)
                {
                    if (child.IsDirectory)
                    {
                        if (tallies.Remove(child, out var ct))
                            for (int c = 0; c < t.Length; c++)
                                t[c] += ct[c];
                    }
                    else
                    {
                        t[(int)FileClassification.Classify(child.Name)] += child.AllocatedSize;
                    }
                }
            }
            tallies[node] = t;

            int best = (int)FileCategory.Other;
            long bestBytes = 0;
            for (int c = 0; c < t.Length; c++)
                if (t[c] > bestBytes)
                {
                    bestBytes = t[c];
                    best = c;
                }
            result[node] = (FileCategory)best;
        }

        return result;
    }
}
