namespace DriveVisualizer.Core;

public enum SizeDetail
{
    /// <summary>One decimal in the natural unit: 251.3 GB.</summary>
    Compact = 0,
    /// <summary>Three decimals in the natural unit: 251.327 GB — small changes stay visible.</summary>
    Detailed = 1,
    /// <summary>Exact byte count: 269,853,412,352 B.</summary>
    Bytes = 2,
}

public static class ByteFormatter
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    /// <summary>Process-wide display precision; the app sets this from user settings.</summary>
    public static SizeDetail Detail { get; set; } = SizeDetail.Compact;

    public static string Format(long bytes) => Detail switch
    {
        SizeDetail.Bytes => bytes.ToString("N0") + " B",
        SizeDetail.Detailed => FormatUnits(bytes, "F3"),
        _ => FormatUnits(bytes, "F1"),
    };

    private static string FormatUnits(long bytes, string decimals)
    {
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{value.ToString(decimals)} {Units[unit]}";
    }
}
