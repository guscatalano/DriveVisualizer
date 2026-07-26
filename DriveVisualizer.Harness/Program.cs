using DriveVisualizer.Core;
using DriveVisualizer.Core.Scanning;

string target = args.Length > 0 ? args[0] : @"C:\";

Console.WriteLine($"Scanning {target} ...");
var scanner = new ParallelScanner();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var scanTask = scanner.ScanAsync(target, cts.Token);

while (!scanTask.IsCompleted)
{
    await Task.WhenAny(scanTask, Task.Delay(250));
    var s = scanner.Statistics;
    Console.Write($"\r{s.Files:N0} files, {s.Directories:N0} dirs, {FormatBytes(s.LogicalBytes)}   ");
}
Console.WriteLine();

var result = await scanTask;
var root = result.Root;
var stats = scanner.Statistics;

Console.WriteLine();
Console.WriteLine($"Done in {result.Elapsed.TotalSeconds:F1}s{(result.WasCancelled ? " (cancelled — partial results)" : "")}");
Console.WriteLine($"  Files:       {stats.Files:N0}");
Console.WriteLine($"  Directories: {stats.Directories:N0}");
Console.WriteLine($"  Logical:     {FormatBytes(root.LogicalSize)}");
Console.WriteLine($"  Allocated:   {FormatBytes(root.AllocatedSize)}");
Console.WriteLine($"  Access denied: {stats.AccessDenied:N0}, junctions/symlinks skipped: {stats.ReparseSkipped:N0}");

Console.WriteLine();
Console.WriteLine("Top 10 directories by allocated size:");
foreach (var child in (root.Children ?? [])
    .Where(c => c.IsDirectory)
    .OrderByDescending(c => c.AllocatedSize)
    .Take(10))
{
    double pct = root.AllocatedSize > 0 ? 100.0 * child.AllocatedSize / root.AllocatedSize : 0;
    Console.WriteLine($"  {FormatBytes(child.AllocatedSize),12}  {pct,5:F1}%  {child.Name}");
}

static string FormatBytes(long bytes)
{
    string[] units = ["B", "KB", "MB", "GB", "TB"];
    double value = bytes;
    int unit = 0;
    while (value >= 1024 && unit < units.Length - 1)
    {
        value /= 1024;
        unit++;
    }
    return $"{value:F1} {units[unit]}";
}
