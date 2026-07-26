using DriveVisualizer.Core;
using DriveVisualizer.Core.Scanning;

namespace DriveVisualizer.Tests;

public sealed class ParallelScannerTests : IDisposable
{
    private readonly string _root;

    public ParallelScannerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "DriveVisualizerTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private void CreateFile(string relativePath, int size)
    {
        string path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[size]);
    }

    [Fact]
    public async Task ComputesCorrectLogicalTotalsAndCounts()
    {
        CreateFile("a.bin", 1000);
        CreateFile(@"sub1\b.bin", 2500);
        CreateFile(@"sub1\sub2\c.bin", 10);

        var scanner = new ParallelScanner();
        var result = await scanner.ScanAsync(_root);
        var root = result.Root;

        Assert.False(result.WasCancelled);
        Assert.Equal(3510, root.LogicalSize);
        Assert.Equal(3, root.SubtreeFileCount);
        Assert.Equal(3, scanner.Statistics.Files);
        Assert.Equal(2, scanner.Statistics.Directories);

        var sub1 = Assert.Single(root.Children!, c => c.Name == "sub1");
        Assert.Equal(2510, sub1.LogicalSize);
        Assert.Equal(2, sub1.SubtreeFileCount);
    }

    [Fact]
    public async Task AllocatedSizeIsClusterRoundedAndZeroForEmptyFiles()
    {
        CreateFile("tiny.bin", 1);
        CreateFile("empty.bin", 0);

        var scanner = new ParallelScanner();
        var result = await scanner.ScanAsync(_root);

        var tiny = Assert.Single(result.Root.Children!, c => c.Name == "tiny.bin");
        var empty = Assert.Single(result.Root.Children!, c => c.Name == "empty.bin");

        Assert.True(tiny.AllocatedSize >= tiny.LogicalSize, "allocated must be at least logical");
        Assert.True(tiny.AllocatedSize >= 512, "1-byte file should occupy at least one cluster/sector");
        Assert.Equal(0, empty.AllocatedSize);
    }

    [Fact]
    public async Task GetFullPathRoundTrips()
    {
        CreateFile(@"sub1\sub2\c.bin", 10);

        var scanner = new ParallelScanner();
        var result = await scanner.ScanAsync(_root);

        var node = result.Root.Children!.Single(c => c.Name == "sub1")
            .Children!.Single(c => c.Name == "sub2")
            .Children!.Single(c => c.Name == "c.bin");

        Assert.Equal(Path.Combine(_root, "sub1", "sub2", "c.bin"), node.GetFullPath());
        Assert.True(File.Exists(node.GetFullPath()));
    }

    [Fact]
    public async Task LongPathsBeyond260CharactersAreScanned()
    {
        // Build a nested path well past MAX_PATH.
        string relative = string.Join('\\', Enumerable.Repeat("longdirectoryname_0123456789", 12));
        CreateFile(relative + @"\deep.bin", 42);

        var scanner = new ParallelScanner();
        var result = await scanner.ScanAsync(_root);

        Assert.Equal(42, result.Root.LogicalSize);
        Assert.Equal(1, result.Root.SubtreeFileCount);
    }

    [Fact]
    public async Task PreCancelledScanReturnsCancelledResult()
    {
        CreateFile("a.bin", 100);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var scanner = new ParallelScanner();
        var result = await scanner.ScanAsync(_root, cts.Token);

        Assert.True(result.WasCancelled);
    }

    [Fact]
    public async Task EmptyDirectoryScansToZero()
    {
        var scanner = new ParallelScanner();
        var result = await scanner.ScanAsync(_root);

        Assert.Equal(0, result.Root.LogicalSize);
        Assert.Equal(0, result.Root.SubtreeFileCount);
        Assert.NotNull(result.Root.Children);
        Assert.Empty(result.Root.Children!);
    }

    [Fact]
    public async Task DirectoryJunctionIsRecordedButNotRecursed()
    {
        CreateFile(@"real\payload.bin", 5000);
        string junction = Path.Combine(_root, "junction");

        // mklink /J works without elevation.
        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe",
            $"/c mklink /J \"{junction}\" \"{Path.Combine(_root, "real")}\"")
        { CreateNoWindow = true, UseShellExecute = false };
        using (var p = System.Diagnostics.Process.Start(psi)!)
            p.WaitForExit();
        Assert.True(Directory.Exists(junction), "junction creation failed");

        var scanner = new ParallelScanner();
        var result = await scanner.ScanAsync(_root);

        // Payload counted exactly once despite being reachable two ways.
        Assert.Equal(5000, result.Root.LogicalSize);
        Assert.Equal(1, result.Root.SubtreeFileCount);
        Assert.Equal(1, scanner.Statistics.ReparseSkipped);

        var junctionNode = Assert.Single(result.Root.Children!, c => c.Name == "junction");
        Assert.True(junctionNode.Flags.HasFlag(NodeFlags.ReparseSkipped));
    }
}
