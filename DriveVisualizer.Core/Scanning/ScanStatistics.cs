namespace DriveVisualizer.Core.Scanning;

/// <summary>
/// Live counters mutated by scanner workers and safe to poll from the UI thread.
/// </summary>
public sealed class ScanStatistics
{
    private long _files;
    private long _directories;
    private long _logicalBytes;
    private long _allocatedBytes;
    private long _accessDenied;
    private long _reparseSkipped;

    public long Files => Interlocked.Read(ref _files);
    public long Directories => Interlocked.Read(ref _directories);
    public long LogicalBytes => Interlocked.Read(ref _logicalBytes);
    public long AllocatedBytes => Interlocked.Read(ref _allocatedBytes);
    public long AccessDenied => Interlocked.Read(ref _accessDenied);
    public long ReparseSkipped => Interlocked.Read(ref _reparseSkipped);

    internal void AddFile(long logical, long allocated)
    {
        Interlocked.Increment(ref _files);
        Interlocked.Add(ref _logicalBytes, logical);
        Interlocked.Add(ref _allocatedBytes, allocated);
    }

    internal void AddDirectory() => Interlocked.Increment(ref _directories);
    internal void AddAccessDenied() => Interlocked.Increment(ref _accessDenied);
    internal void AddReparseSkipped() => Interlocked.Increment(ref _reparseSkipped);
}
