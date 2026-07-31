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
    SmartCounters? Smart,
    IReadOnlyList<PartitionInfo> Partitions);

/// <summary>One partition on the physical disk hosting the scanned volume.</summary>
public sealed record PartitionInfo(
    int Number,
    char? DriveLetter,
    long SizeBytes,
    long OffsetBytes,
    string TypeName,
    bool IsBoot,
    bool IsSystem,
    string? VolumeLabel,
    string? FileSystem,
    long? FreeBytes);

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
        var partitionList = new List<PartitionInfo>();
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
                partitionList = ReadPartitions(scope, n);
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
            serial, firmware, physicalSize, logicalSector, physicalSector, smart, partitionList);
    }

    /// <summary>All partitions on the disk, in on-disk order, with volume facts for mounted ones.</summary>
    private static List<PartitionInfo> ReadPartitions(ManagementScope scope, uint diskNumber)
    {
        var result = new List<PartitionInfo>();
        try
        {
            using var searcher = new ManagementObjectSearcher(scope,
                new ObjectQuery("SELECT PartitionNumber, DriveLetter, Size, Offset, GptType, MbrType, IsBoot, IsSystem " +
                                $"FROM MSFT_Partition WHERE DiskNumber='{diskNumber}'"));
            foreach (ManagementObject p in searcher.Get())
            {
                char? letter = p["DriveLetter"] is char c && c != '\0' ? char.ToUpperInvariant(c) : null;
                string type = PartitionTypeName(p["GptType"] as string, ToInt(p["MbrType"]));

                string? label = null, fs = null;
                long? free = null;
                if (letter is { } l)
                {
                    try
                    {
                        var vol = new DriveInfo($"{l}:\\");
                        if (vol.IsReady)
                        {
                            label = vol.VolumeLabel;
                            fs = vol.DriveFormat;
                            free = vol.AvailableFreeSpace;
                        }
                    }
                    catch { }
                }

                result.Add(new PartitionInfo(
                    ToInt(p["PartitionNumber"]) ?? 0, letter,
                    ToLong(p["Size"]) ?? 0, ToLong(p["Offset"]) ?? 0,
                    type,
                    p["IsBoot"] as bool? ?? false,
                    p["IsSystem"] as bool? ?? false,
                    label, fs, free));
            }
        }
        catch
        {
        }
        return [.. result.OrderBy(p => p.OffsetBytes)];
    }

    private static string PartitionTypeName(string? gptType, int? mbrType)
    {
        if (gptType is not null)
        {
            return gptType.Trim('{', '}').ToLowerInvariant() switch
            {
                "c12a7328-f81f-11d2-ba4b-00a0c93ec93b" => "EFI System",
                "e3c9e316-0b5c-4db8-817d-f92df00215ae" => "Microsoft Reserved",
                "ebd0a0a2-b9e5-4433-87c0-68b6b72699c7" => "Basic data",
                "de94bba4-06d1-4d40-a16a-bfd50179d6ac" => "Recovery",
                "5808c8aa-7e8f-42e0-85d2-e1e90434cfb3" => "LDM metadata",
                "af9b60a0-1431-4f62-bc68-3311714a69ad" => "LDM data",
                "0fc63daf-8483-4772-8e79-3d69d8477de4" => "Linux filesystem",
                _ => "GPT partition",
            };
        }
        return mbrType switch
        {
            7 => "NTFS/exFAT",
            11 or 12 => "FAT32",
            14 => "FAT16",
            0x27 => "Recovery",
            0x82 => "Linux swap",
            0x83 => "Linux",
            null => "Partition",
            _ => $"MBR type {mbrType}",
        };
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
