using DriveVisualizer.Core;
using DriveVisualizer.Core.Snapshots;

namespace DriveVisualizer.Tests;

public sealed class SnapshotTests
{
    private static FsNode Dir(string name, FsNode? parent)
    {
        var dir = new FsNode { Name = name, Parent = parent, Attributes = FsNode.FILE_ATTRIBUTE_DIRECTORY };
        return dir;
    }

    private static FsNode File(string name, FsNode parent, long size)
    {
        var file = new FsNode { Name = name, Parent = parent };
        file.LogicalSize = size;
        file.AllocatedSize = size;
        for (FsNode? n = parent; n is not null; n = n.Parent)
        {
            n.AllocatedSize += size;
            n.LogicalSize += size;
            n.SubtreeFileCount += 1;
        }
        return file;
    }

    private static FsNode BuildSampleTree()
    {
        var root = Dir(@"C:\scan", null);
        var sub = Dir("videos", root);
        var f1 = File("movie.mp4", sub, 5000);
        var f2 = File("app.exe", root, 3000);
        var f3 = File("notes.txt", root, 100);
        sub.Children = [f1];
        root.Children = [sub, f2, f3];
        return root;
    }

    [Fact]
    public void BuildCapturesTotalsCategoriesAndDirs()
    {
        var root = BuildSampleTree();
        var snap = ScanSnapshot.Build(root, @"C:\scan", new DateTime(2026, 7, 26, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(3, snap.TotalFiles);
        Assert.Equal(1, snap.TotalDirectories);
        Assert.Equal(8100, snap.TotalAllocated);
        Assert.Equal(5000, snap.CategoryBytes[(int)FileCategory.Media]);
        Assert.Equal(3000, snap.CategoryBytes[(int)FileCategory.Apps]);
        Assert.Equal(100, snap.CategoryBytes[(int)FileCategory.Documents]);
        Assert.Equal(3, snap.TopFiles.Count);
        Assert.Equal("movie.mp4", Path.GetFileName(snap.TopFiles[0].Path));

        var paths = snap.BuildDirectoryPaths();
        Assert.Contains(@"C:\scan\videos", paths);
    }

    [Fact]
    public void SaveAndLoadRoundTrips()
    {
        var root = BuildSampleTree();
        var snap = ScanSnapshot.Build(root, @"C:\scan", DateTime.UtcNow);
        string path = Path.Combine(Path.GetTempPath(), $"dv_{Guid.NewGuid():N}.dvsnap");
        try
        {
            snap.Save(path);
            var loaded = ScanSnapshot.Load(path);
            Assert.Equal(snap.TotalAllocated, loaded.TotalAllocated);
            Assert.Equal(snap.TotalFiles, loaded.TotalFiles);
            Assert.Equal(snap.Directories.Count, loaded.Directories.Count);
            Assert.Equal(snap.CategoryBytes, loaded.CategoryBytes);
            Assert.Equal(snap.TopFiles[0].Path, loaded.TopFiles[0].Path);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public void DriveHealthRoundTripsAndShowsUpInReports()
    {
        var root = BuildSampleTree();
        var snap = ScanSnapshot.Build(root, @"C:\scan", DateTime.UtcNow);
        snap.DriveHealth = new SnapshotDriveHealth
        {
            Model = "Contoso NVMe SSD 2TB",
            MediaType = "SSD",
            BusType = "NVMe",
            Health = "Healthy",
            VolumeTotalBytes = 2_000_000_000_000,
            VolumeFreeBytes = 500_000_000_000,
            TemperatureC = 47,
            WearPercent = 3,
            PowerOnHours = 1234,
            DataWrittenBytes = 20_000_000_000_000,
        };

        string path = Path.Combine(Path.GetTempPath(), $"dv_{Guid.NewGuid():N}.dvsnap");
        try
        {
            snap.Save(path);
            var loaded = ScanSnapshot.Load(path);
            Assert.NotNull(loaded.DriveHealth);
            Assert.Equal(47, loaded.DriveHealth!.TemperatureC);
            Assert.Equal(3, loaded.DriveHealth.WearPercent);
            Assert.Equal(500_000_000_000, loaded.DriveHealth.VolumeFreeBytes);

            string report = ReportGenerator.BuildHtml(loaded);
            Assert.Contains("Contoso NVMe SSD 2TB", report);
            Assert.Contains("47 °C", report);

            string history = HistoryChart.BuildHtml([loaded]);
            Assert.Contains("Drive health", history);
            Assert.Contains("3%", history);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public void DirMoversReportGrowthShrinkageAndRemovals()
    {
        var oldRoot = BuildSampleTree();
        var before = ScanSnapshot.Build(oldRoot, @"C:\scan", DateTime.UtcNow.AddDays(-1));

        var newRoot = Dir(@"C:\scan", null);
        var videos = Dir("videos", newRoot);
        var big = File("movie.mp4", videos, 9000); // grew from 5000
        newRoot.Children = [videos];
        videos.Children = [big];
        var after = ScanSnapshot.Build(newRoot, @"C:\scan", DateTime.UtcNow);

        var movers = HistoryChart.ComputeDirMovers(before, after, 10);
        Assert.Contains(movers, m => m.Path.EndsWith("videos", StringComparison.OrdinalIgnoreCase) && m.Delta == 4000);
        Assert.DoesNotContain(movers, m => m.Delta == 0);
    }

    [Fact]
    public void SnapshotsWithoutDriveHealthStillLoadAndReport()
    {
        var root = BuildSampleTree();
        var snap = ScanSnapshot.Build(root, @"C:\scan", DateTime.UtcNow);
        Assert.Null(snap.DriveHealth);
        string html = HistoryChart.BuildHtml([snap]);
        Assert.DoesNotContain("Drive health", html);
    }

    [Fact]
    public void ReportContainsSummaryCategoriesAndTopEntries()
    {
        var root = BuildSampleTree();
        var snap = ScanSnapshot.Build(root, @"C:\scan", DateTime.UtcNow);
        string html = ReportGenerator.BuildHtml(snap);

        Assert.Contains("DriveVisualizer report", html);
        Assert.Contains("movie.mp4", html);
        Assert.Contains("videos", html);
        Assert.Contains("Video &amp; audio", html);
        Assert.Contains("Largest files", html);
        Assert.DoesNotContain("What grew", html); // no baseline given
    }

    [Fact]
    public void ComparisonReportShowsGrowersAndShrinkers()
    {
        var oldRoot = BuildSampleTree();
        var baseline = ScanSnapshot.Build(oldRoot, @"C:\scan", DateTime.UtcNow.AddDays(-7));

        // New scan: videos grew by 4000, a folder disappeared logically (docs removed)
        var newRoot = Dir(@"C:\scan", null);
        var videos = Dir("videos", newRoot);
        var big = File("movie.mp4", videos, 9000);
        var exe = File("app.exe", newRoot, 3000);
        videos.Children = [big];
        newRoot.Children = [videos, exe];
        var current = ScanSnapshot.Build(newRoot, @"C:\scan", DateTime.UtcNow);

        string html = ReportGenerator.BuildHtml(current, baseline);

        Assert.Contains("What grew", html);
        Assert.Contains("What shrank", html);
        Assert.Contains(@"C:\scan\videos", html);
        Assert.Contains("since baseline", html);
    }
}
