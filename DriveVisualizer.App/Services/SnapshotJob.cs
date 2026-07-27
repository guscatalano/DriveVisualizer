using DriveVisualizer.Core.Scanning;
using DriveVisualizer.Core.Snapshots;
using DriveVisualizer_App.ViewModels;

namespace DriveVisualizer_App.Services;

/// <summary>
/// Headless snapshot run for the --snapshot command line / scheduled task:
/// scan the target, write a history entry with the configured granularity,
/// prune per retention, log the outcome. No UI is created.
/// </summary>
public static class SnapshotJob
{
    public static async Task RunAsync(string target)
    {
        string logDir = MainViewModel.GetHistoryRootDirectory();
        string logPath = Path.Combine(logDir, "scheduled-run.log");
        try
        {
            Directory.CreateDirectory(logDir);
            var scanner = new ParallelScanner();
            var result = await scanner.ScanAsync(target);
            var snapshot = ScanSnapshot.Build(result.Root, target, DateTime.UtcNow);

            string historyDir = MainViewModel.GetHistoryDirectory(target);
            Directory.CreateDirectory(historyDir);
            snapshot.Save(Path.Combine(historyDir, MainViewModel.SnapshotFileName(AppSettings.SnapshotFrequency)));
            MainViewModel.ApplyRetention(historyDir, AppSettings.SnapshotRetention);

            File.AppendAllText(logPath,
                $"[{DateTime.Now:O}] OK {target}: {snapshot.TotalFiles:N0} files, " +
                $"{snapshot.TotalAllocated:N0} bytes in {result.Elapsed.TotalSeconds:F1}s\n");
        }
        catch (Exception ex)
        {
            try
            {
                Directory.CreateDirectory(logDir);
                File.AppendAllText(logPath, $"[{DateTime.Now:O}] FAIL {target}: {ex.Message}\n");
            }
            catch { }
        }
    }
}
