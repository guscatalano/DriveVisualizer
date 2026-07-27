using DriveVisualizer.Core;

namespace DriveVisualizer.Tests;

public sealed class CleanupHeuristicsTests
{
    private static FsNode Dir(string name, FsNode? parent)
    {
        return new FsNode { Name = name, Parent = parent, Attributes = FsNode.FILE_ATTRIBUTE_DIRECTORY };
    }

    private static FsNode File(string name, FsNode parent, long size)
    {
        var file = new FsNode { Name = name, Parent = parent };
        file.LogicalSize = size;
        file.AllocatedSize = size;
        return file;
    }

    [Fact]
    public void MarksTempDirContentsLogFilesAndNothingElse()
    {
        var root = Dir(@"C:\x", null);
        var temp = Dir("Temp", root);
        var docs = Dir("docs", root);
        var inTemp = File("data.bin", temp, 1000);       // candidate: inside Temp
        var log = File("app.log", docs, 200);            // candidate: .log extension
        var keep = File("thesis.docx", docs, 5000);      // not a candidate
        temp.Children = [inTemp];
        docs.Children = [log, keep];
        root.Children = [temp, docs];

        var (bytes, files) = CleanupHeuristics.MarkCandidates(root);

        Assert.Equal(1200, bytes);
        Assert.Equal(2, files);
        Assert.True(inTemp.Flags.HasFlag(NodeFlags.CleanupCandidate));
        Assert.True(log.Flags.HasFlag(NodeFlags.CleanupCandidate));
        Assert.False(keep.Flags.HasFlag(NodeFlags.CleanupCandidate));
    }

    [Fact]
    public void ReMarkingClearsStaleFlags()
    {
        var root = Dir(@"C:\x", null);
        var file = File("keep.docx", root, 100);
        file.Flags |= NodeFlags.CleanupCandidate; // stale from a previous run
        root.Children = [file];

        CleanupHeuristics.MarkCandidates(root);

        Assert.False(file.Flags.HasFlag(NodeFlags.CleanupCandidate));
    }

    [Fact]
    public void NestedDisposableDirectoriesInheritDown()
    {
        var root = Dir(@"C:\x", null);
        var nm = Dir("node_modules", root);
        var pkg = Dir("left-pad", nm);
        var deep = File("index.js", pkg, 300);
        pkg.Children = [deep];
        nm.Children = [pkg];
        root.Children = [nm];

        var (bytes, files) = CleanupHeuristics.MarkCandidates(root);

        Assert.Equal(300, bytes);
        Assert.Equal(1, files);
        Assert.True(deep.Flags.HasFlag(NodeFlags.CleanupCandidate));
    }
}
