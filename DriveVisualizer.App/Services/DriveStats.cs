using System.Management;
using System.Runtime.InteropServices;

namespace DriveVisualizer_App.Services;

public sealed record DriveDetails(
    string Root,
    string? VolumeLabel,
    string? FileSystem,
    long TotalBytes,
    long FreeBytes,
    long ClusterSize,
    string? Model,
    string? MediaType,
    string? BusType,
    string? Health,
    uint? SpindleSpeedRpm,
    string? SerialNumber,
    string? FirmwareVersion,
    long? PhysicalSizeBytes,
    long? LogicalSectorSize,
    long? PhysicalSectorSize,
    SmartCounters? Smart);

/// <summary>
/// S.M.A.R.T.-derived health counters. Null record = no source could read them
/// for this disk (typically a USB bridge, or a SATA drive in an unelevated
/// process — the NVMe health log works without elevation, WMI's reliability
/// counters usually don't).
/// </summary>
public sealed record SmartCounters(
    string Source,
    string? CriticalWarning,
    int? TemperatureC,
    int? TemperatureMaxC,
    int? WearPercent,
    int? SparePercent,
    long? PowerOnHours,
    long? PowerCycles,
    long? UnsafeShutdowns,
    long? MediaErrors,
    long? DataReadBytes,
    long? DataWrittenBytes);

/// <summary>
/// Volume + physical-disk facts for the drive hosting a path: capacity and
/// filesystem from DriveInfo, SSD/HDD + bus + model + health + S.M.A.R.T.
/// counters from the Windows Storage WMI namespace. Everything is best-effort —
/// nulls where unknown.
/// </summary>
public static class DriveStats
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetDiskFreeSpaceW(
        string lpRootPathName, out uint sectorsPerCluster, out uint bytesPerSector,
        out uint freeClusters, out uint totalClusters);

    public static DriveDetails? Get(string path)
    {
        string? root = Path.GetPathRoot(Path.GetFullPath(path));
        if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal))
            return null; // UNC paths have no local physical disk

        var drive = new DriveInfo(root);
        if (!drive.IsReady)
            return null;

        long cluster = 4096;
        if (GetDiskFreeSpaceW(root, out uint spc, out uint bps, out _, out _))
            cluster = (long)spc * bps;

        string? model = null, media = null, bus = null, health = null, serial = null, firmware = null;
        uint? rpm = null;
        long? physicalSize = null, logicalSector = null, physicalSector = null;
        SmartCounters? smart = null;
        try
        {
            char letter = char.ToUpperInvariant(root[0]);
            var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
            scope.Connect();

            uint? diskNumber = null;
            using (var partitions = new ManagementObjectSearcher(scope,
                new ObjectQuery($"SELECT DiskNumber, DriveLetter FROM MSFT_Partition WHERE DriveLetter='{letter}'")))
            {
                foreach (ManagementObject p in partitions.Get())
                {
                    diskNumber = (uint)p["DiskNumber"];
                    break;
                }
            }

            if (diskNumber is { } n)
            {
                using var disks = new ManagementObjectSearcher(scope,
                    new ObjectQuery("SELECT FriendlyName, MediaType, BusType, HealthStatus, SpindleSpeed, SerialNumber, FirmwareVersion, Size, LogicalSectorSize, PhysicalSectorSize " +
                                    $"FROM MSFT_PhysicalDisk WHERE DeviceId='{n}'"));
                foreach (ManagementObject d in disks.Get())
                {
                    model = d["FriendlyName"] as string;
                    serial = (d["SerialNumber"] as string)?.Trim();
                    firmware = (d["FirmwareVersion"] as string)?.Trim();
                    physicalSize = ToLong(d["Size"]);
                    logicalSector = ToLong(d["LogicalSectorSize"]);
                    physicalSector = ToLong(d["PhysicalSectorSize"]);
                    media = (ushort?)(d["MediaType"] as ushort?) switch
                    {
                        3 => "HDD",
                        4 => "SSD",
                        5 => "SCM",
                        _ => "Unspecified",
                    };
                    bus = (ushort?)(d["BusType"] as ushort?) switch
                    {
                        1 => "SCSI",
                        2 => "ATAPI",
                        3 => "ATA",
                        5 => "1394",
                        6 => "SSA",
                        7 => "USB",
                        8 => "RAID",
                        9 => "iSCSI",
                        10 => "SAS",
                        11 => "SATA",
                        12 => "SD",
                        13 => "MMC",
                        15 => "File-backed virtual",
                        16 => "Storage Spaces",
                        17 => "NVMe",
                        _ => "Unknown",
                    };
                    health = (ushort?)(d["HealthStatus"] as ushort?) switch
                    {
                        0 => "Healthy",
                        1 => "Warning",
                        2 => "Unhealthy",
                        _ => null,
                    };
                    if (d["SpindleSpeed"] is uint s && s > 0 && s != uint.MaxValue)
                        rpm = s;
                    break;
                }

                smart = NvmeHealth.Read(n) ?? ReadSmart(scope, n);
            }
        }
        catch
        {
            // WMI can fail on exotic volumes (VHD, dev drives) — volume facts still useful.
        }

        return new DriveDetails(
            root, drive.VolumeLabel, drive.DriveFormat,
            drive.TotalSize, drive.AvailableFreeSpace, cluster,
            model, media, bus, health, rpm,
            serial, firmware, physicalSize, logicalSector, physicalSector, smart);
    }

    /// <summary>
    /// Reliability counters need elevation on many systems and are absent for
    /// most USB bridges — a null return means "unavailable", not "unhealthy".
    /// </summary>
    private static SmartCounters? ReadSmart(ManagementScope scope, uint diskNumber)
    {
        try
        {
            using var counters = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT Temperature, TemperatureMax, PowerOnHours, Wear, StartStopCycleCount, ReadErrorsTotal, WriteErrorsTotal " +
                                $"FROM MSFT_StorageReliabilityCounter WHERE DeviceId='{diskNumber}'"));
            foreach (ManagementObject c in counters.Get())
            {
                // 0 for temperature/hours means "not reported" on most firmware; wear 0 is a real value.
                int? temp = ToInt(c["Temperature"]) is int t and > 0 ? t : null;
                int? tempMax = ToInt(c["TemperatureMax"]) is int tm and > 0 ? tm : null;
                long? hours = ToLong(c["PowerOnHours"]) is long h and > 0 ? h : null;
                long? readErrors = ToLong(c["ReadErrorsTotal"]);
                long? writeErrors = ToLong(c["WriteErrorsTotal"]);
                return new SmartCounters(
                    Source: "Windows reliability counters",
                    CriticalWarning: null,
                    TemperatureC: temp,
                    TemperatureMaxC: tempMax,
                    WearPercent: ToInt(c["Wear"]),
                    SparePercent: null,
                    PowerOnHours: hours,
                    PowerCycles: ToLong(c["StartStopCycleCount"]),
                    UnsafeShutdowns: null,
                    MediaErrors: readErrors is null && writeErrors is null
                        ? null
                        : (readErrors ?? 0) + (writeErrors ?? 0),
                    DataReadBytes: null,
                    DataWrittenBytes: null);
            }
        }
        catch
        {
        }
        return null;
    }

    private static long? ToLong(object? value) =>
        value is null ? null : Convert.ToInt64(value);

    private static int? ToInt(object? value) =>
        value is null ? null : Convert.ToInt32(value);

    /// <summary>Drive health block for a snapshot of <paramref name="target"/>; null if no local drive.</summary>
    public static DriveVisualizer.Core.Snapshots.SnapshotDriveHealth? GetSnapshotHealth(string target)
    {
        try
        {
            var d = Get(target);
            if (d is null)
                return null;
            return new DriveVisualizer.Core.Snapshots.SnapshotDriveHealth
            {
                Model = d.Model,
                MediaType = d.MediaType,
                BusType = d.BusType,
                Health = d.Health,
                VolumeTotalBytes = d.TotalBytes,
                VolumeFreeBytes = d.FreeBytes,
                TemperatureC = d.Smart?.TemperatureC,
                WearPercent = d.Smart?.WearPercent,
                SparePercent = d.Smart?.SparePercent,
                PowerOnHours = d.Smart?.PowerOnHours,
                PowerCycles = d.Smart?.PowerCycles,
                UnsafeShutdowns = d.Smart?.UnsafeShutdowns,
                MediaErrors = d.Smart?.MediaErrors,
                DataReadBytes = d.Smart?.DataReadBytes,
                DataWrittenBytes = d.Smart?.DataWrittenBytes,
                CriticalWarning = d.Smart?.CriticalWarning,
            };
        }
        catch
        {
            return null;
        }
    }
}
