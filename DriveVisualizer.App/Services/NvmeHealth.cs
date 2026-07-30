using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DriveVisualizer_App.Services;

/// <summary>
/// Reads the NVMe SMART / Health Information log (page 02h) via
/// IOCTL_STORAGE_QUERY_PROPERTY. Works without elevation because the device
/// handle is opened with no data access — this is the only SMART source
/// available to a normal-privilege process on NVMe drives.
/// </summary>
public static class NvmeHealth
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string name, uint access, uint share, nint securityAttributes,
        uint disposition, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle handle, uint code, byte[] inBuffer, int inLength,
        byte[] outBuffer, int outLength, out int bytesReturned, nint overlapped);

    private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
    private const uint FILE_SHARE_READ_WRITE = 3;
    private const uint OPEN_EXISTING = 3;

    private const uint StorageDeviceProtocolSpecificProperty = 50;
    private const uint ProtocolTypeNvme = 3;
    private const uint NVMeDataTypeLogPage = 2;
    private const uint SmartHealthLogPage = 0x02;

    public static SmartCounters? Read(uint diskNumber)
    {
        try
        {
            using var handle = CreateFileW($@"\\.\PhysicalDrive{diskNumber}",
                0 /* query metadata only — no admin needed */, FILE_SHARE_READ_WRITE,
                0, OPEN_EXISTING, 0, 0);
            if (handle.IsInvalid)
                return null;

            const int dataLength = 4096;
            const int protocolDataOffset = 40; // sizeof(STORAGE_PROTOCOL_SPECIFIC_DATA)
            byte[] buffer = new byte[8 + protocolDataOffset + dataLength];
            void Write(int offset, uint value) => BitConverter.GetBytes(value).CopyTo(buffer, offset);
            Write(0, StorageDeviceProtocolSpecificProperty); // STORAGE_PROPERTY_QUERY.PropertyId
            Write(4, 0);                                     // PropertyStandardQuery
            Write(8, ProtocolTypeNvme);                      // STORAGE_PROTOCOL_SPECIFIC_DATA…
            Write(12, NVMeDataTypeLogPage);
            Write(16, SmartHealthLogPage);
            Write(20, 0);
            Write(24, protocolDataOffset);
            Write(28, dataLength);

            if (!DeviceIoControl(handle, IOCTL_STORAGE_QUERY_PROPERTY,
                    buffer, buffer.Length, buffer, buffer.Length, out _, 0))
                return null; // not an NVMe disk, or the bridge doesn't pass the log through

            // STORAGE_PROTOCOL_DATA_DESCRIPTOR: 8-byte header, then the
            // protocol-specific block whose ProtocolDataOffset locates the log.
            int log = 8 + (int)BitConverter.ToUInt32(buffer, 8 + 16);
            if (log + 512 > buffer.Length)
                return null;

            byte critical = buffer[log + 0];
            int temperatureC = BitConverter.ToUInt16(buffer, log + 1) - 273;
            int sparePercent = buffer[log + 3];
            int usedPercent = buffer[log + 5];
            long ReadCounter(int offset) => (long)Math.Min(BitConverter.ToUInt64(buffer, log + offset), long.MaxValue);
            long dataUnitsRead = ReadCounter(32);
            long dataUnitsWritten = ReadCounter(48);
            long powerCycles = ReadCounter(112);
            long powerOnHours = ReadCounter(128);
            long unsafeShutdowns = ReadCounter(144);
            long mediaErrors = ReadCounter(160);

            return new SmartCounters(
                Source: "NVMe health log",
                CriticalWarning: DecodeCriticalWarning(critical),
                TemperatureC: temperatureC is > -60 and < 200 ? temperatureC : null,
                TemperatureMaxC: null,
                WearPercent: usedPercent,
                SparePercent: sparePercent,
                PowerOnHours: powerOnHours,
                PowerCycles: powerCycles,
                UnsafeShutdowns: unsafeShutdowns,
                MediaErrors: mediaErrors,
                DataReadBytes: dataUnitsRead * 512_000,     // NVMe data units are 1000 × 512 B
                DataWrittenBytes: dataUnitsWritten * 512_000);
        }
        catch
        {
            return null;
        }
    }

    private static string? DecodeCriticalWarning(byte flags)
    {
        if (flags == 0)
            return null;
        var reasons = new List<string>(3);
        if ((flags & 0x01) != 0) reasons.Add("spare capacity low");
        if ((flags & 0x02) != 0) reasons.Add("temperature out of range");
        if ((flags & 0x04) != 0) reasons.Add("media degraded");
        if ((flags & 0x08) != 0) reasons.Add("read-only mode");
        if ((flags & 0x10) != 0) reasons.Add("volatile memory backup failed");
        return reasons.Count > 0 ? string.Join(", ", reasons) : $"0x{flags:X2}";
    }
}
