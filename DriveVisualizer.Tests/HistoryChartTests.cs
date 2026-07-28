using DriveVisualizer.Core;
using DriveVisualizer.Core.Snapshots;

namespace DriveVisualizer.Tests;

public sealed class HistoryChartTests
{
    private static ScanSnapshot Snap(DateTime dayUtc, long media, long apps)
    {
        var categories = new long[FileClassification.CategoryCount];
        categories[(int)FileCategory.Media] = media;
        categories[(int)FileCategory.Apps] = apps;
        return new ScanSnapshot
        {
            Target = @"C:\x",
            TimestampUtc = dayUtc,
            TotalAllocated = media + apps,
            TotalFiles = 10,
            CategoryBytes = categories,
        };
    }

    [Fact]
    public void ChartContainsBarsLegendAndPerDayTable()
    {
        var history = new List<ScanSnapshot>
        {
            Snap(new DateTime(2026, 7, 24, 12, 0, 0, DateTimeKind.Utc), 5000, 3000),
            Snap(new DateTime(2026, 7, 25, 12, 0, 0, DateTimeKind.Utc), 6000, 3000),
            Snap(new DateTime(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc), 6000, 9000),
        };

        string html = HistoryChart.BuildHtml(history);

        Assert.Contains("<svg", html);
        Assert.Contains("var(--cat0)", html);   // apps bars
        Assert.Contains("var(--cat7)", html);   // media bars
        Assert.Contains("Video &amp; audio", html);
        Assert.Contains("3 snapshots", html);
        Assert.Contains("Per snapshot", html);
        Assert.Contains("+", html);             // day-over-day delta present
        Assert.Contains("prefers-color-scheme: dark", html);
        Assert.Contains("By category, per snapshot", html);
        Assert.Contains("What changed between snapshots", html);
    }

    [Fact]
    public void DailyChangesListFolderMovers()
    {
        var day1 = Snap(new DateTime(2026, 7, 25, 12, 0, 0, DateTimeKind.Utc), 1000, 0);
        day1.Directories.Add(new SnapshotDir { Name = @"C:\x", ParentIndex = -1, AllocatedSize = 1000 });
        day1.Directories.Add(new SnapshotDir { Name = "media", ParentIndex = 0, AllocatedSize = 1000 });

        var day2 = Snap(new DateTime(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc), 5000, 0);
        day2.Directories.Add(new SnapshotDir { Name = @"C:\x", ParentIndex = -1, AllocatedSize = 5000 });
        day2.Directories.Add(new SnapshotDir { Name = "media", ParentIndex = 0, AllocatedSize = 5000 });

        string html = HistoryChart.BuildHtml([day1, day2]);

        Assert.Contains("What changed between snapshots", html);
        Assert.Contains(@"C:\x\media", html);   // the mover is named with its path
        Assert.Contains("Jul 25", html);
        Assert.Contains("Jul 26", html);
    }

    [Fact]
    public void FileMoversTrackGrowthNewAndRemovedFiles()
    {
        var before = Snap(new DateTime(2026, 7, 25, 12, 0, 0, DateTimeKind.Utc), 1000, 0);
        before.TopFiles.Add(new SnapshotFile { Path = @"C:\x\grow.bin", AllocatedSize = 100 });
        before.TopFiles.Add(new SnapshotFile { Path = @"C:\x\gone.bin", AllocatedSize = 500 });

        var after = Snap(new DateTime(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc), 1000, 0);
        after.TopFiles.Add(new SnapshotFile { Path = @"C:\x\grow.bin", AllocatedSize = 900 });
        after.TopFiles.Add(new SnapshotFile { Path = @"C:\x\new.bin", AllocatedSize = 300 });

        var movers = HistoryChart.ComputeFileMovers(before, after, 10);

        Assert.Contains(movers, m => m.Path == @"C:\x\grow.bin" && m.Delta == 800);
        Assert.Contains(movers, m => m.Path.StartsWith(@"C:\x\gone.bin") && m.Delta == -500);
        Assert.Contains(movers, m => m.Path == @"C:\x\new.bin" && m.Delta == 300);
        Assert.Equal(800, movers[0].Delta); // ordered by magnitude
    }

    [Fact]
    public void SnapshotsAreOrderedByTimeRegardlessOfInputOrder()
    {
        var history = new List<ScanSnapshot>
        {
            Snap(new DateTime(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc), 100, 0),
            Snap(new DateTime(2026, 7, 24, 12, 0, 0, DateTimeKind.Utc), 300, 0),
        };

        string html = HistoryChart.BuildHtml(history);

        int first = html.IndexOf("2026-07-24", StringComparison.Ordinal);
        int second = html.IndexOf("2026-07-26", StringComparison.Ordinal);
        Assert.True(first >= 0 && second > first, "table rows should be chronological");
    }
}
