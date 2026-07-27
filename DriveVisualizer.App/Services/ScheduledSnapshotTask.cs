using System.Diagnostics;

namespace DriveVisualizer_App.Services;

/// <summary>
/// Manages the per-user Task Scheduler entry that runs a headless snapshot
/// (drivevisualizer.exe --snapshot &lt;target&gt;) even when the app is closed.
/// Uses schtasks.exe; no elevation needed for current-user tasks.
/// </summary>
public static class ScheduledSnapshotTask
{
    public const string TaskName = "DriveVisualizer Snapshot";

    /// <summary>The command-line entry point: execution alias when packaged, own exe otherwise.</summary>
    private static string GetExecutablePath()
    {
        try
        {
            _ = Windows.ApplicationModel.Package.Current; // throws when unpackaged
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WindowsApps", "drivevisualizer.exe");
        }
        catch
        {
            return Environment.ProcessPath ?? "DriveVisualizer.App.exe";
        }
    }

    private static int RunSchtasks(string arguments, out string output)
    {
        var psi = new ProcessStartInfo("schtasks.exe", arguments)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(psi)!;
        output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit(15000);
        return process.ExitCode;
    }

    public static bool IsRegistered() =>
        RunSchtasks($"/Query /TN \"{TaskName}\"", out _) == 0;

    /// <summary>Registers (or replaces) the task at the given cadence for the given target.</summary>
    public static (bool Ok, string Message) Register(string target, int frequency)
    {
        string schedule = frequency switch
        {
            1 => "/SC HOURLY",
            3 => "/SC WEEKLY /D MON",
            _ => "/SC DAILY",
        };
        string exe = GetExecutablePath();
        // /TR value: "exe" --snapshot "target"  (inner quotes escaped for the argument string)
        string action = $"\\\"{exe}\\\" --snapshot \\\"{target}\\\"";
        int exit = RunSchtasks($"/Create /F /TN \"{TaskName}\" /TR \"{action}\" {schedule} /ST 12:00", out string output);
        return (exit == 0, output.Trim());
    }

    public static (bool Ok, string Message) Unregister()
    {
        int exit = RunSchtasks($"/Delete /F /TN \"{TaskName}\"", out string output);
        return (exit == 0, output.Trim());
    }
}
