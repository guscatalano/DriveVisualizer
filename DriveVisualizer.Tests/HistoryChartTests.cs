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
        Assert.Contains("3 daily snapshots", html);
        Assert.Contains("Per day", html);
        Assert.Contains("+", html);             // day-over-day delta present
        Assert.Contains("prefers-color-scheme: dark", html);
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
