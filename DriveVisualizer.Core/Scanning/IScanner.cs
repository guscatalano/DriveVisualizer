namespace DriveVisualizer.Core.Scanning;

public sealed record ScanResult(FsNode Root, TimeSpan Elapsed, bool WasCancelled);

/// <summary>
/// Abstraction over scan strategies so an NTFS MFT fast-path can slot in later
/// without touching the UI.
/// </summary>
public interface IScanner
{
    /// <summary>Counters that may be polled while a scan is running.</summary>
    ScanStatistics Statistics { get; }

    /// <summary>
    /// Scans <paramref name="rootPath"/> and returns the aggregated tree.
    /// Cancellation returns the partial tree (aggregated) rather than throwing.
    /// </summary>
    Task<ScanResult> ScanAsync(string rootPath, CancellationToken cancellationToken = default);
}
