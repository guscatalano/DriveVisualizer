using System.Collections.Concurrent;
using DriveVisualizer.Core;
using Windows.UI;

namespace DriveVisualizer_App.Rendering;

/// <summary>
/// Colors for Core's semantic file categories (classification itself lives in
/// <see cref="FileClassification"/>). Fixed, CVD-validated categorical palette
/// (dark-surface steps); within a category, tiles get a small deterministic
/// lightness jitter per extension for texture without changing identity.
/// </summary>
public static class FileCategories
{
    public static readonly (FileCategory Category, string Name, Color Color)[] All =
    [
        (FileCategory.Apps,        FileClassification.NameOf(FileCategory.Apps),        FromHex(0x3987E5)), // blue
        (FileCategory.Archives,    FileClassification.NameOf(FileCategory.Archives),    FromHex(0xD95926)), // orange
        (FileCategory.Pictures,    FileClassification.NameOf(FileCategory.Pictures),    FromHex(0x199E70)), // aqua
        (FileCategory.Documents,   FileClassification.NameOf(FileCategory.Documents),   FromHex(0xC98500)), // yellow
        (FileCategory.TempAndLogs, FileClassification.NameOf(FileCategory.TempAndLogs), FromHex(0xD55181)), // magenta
        (FileCategory.Code,        FileClassification.NameOf(FileCategory.Code),        FromHex(0x008300)), // green
        (FileCategory.DiskImages,  FileClassification.NameOf(FileCategory.DiskImages),  FromHex(0x9085E9)), // violet
        (FileCategory.Media,       FileClassification.NameOf(FileCategory.Media),       FromHex(0xE66767)), // red
        (FileCategory.Other,       FileClassification.NameOf(FileCategory.Other),       FromHex(0x6B6B68)), // gray
    ];

    private static readonly ConcurrentDictionary<string, Color> TileColorCache = new(StringComparer.OrdinalIgnoreCase);

    public static FileCategory Classify(string fileName) => FileClassification.Classify(fileName);

    public static Color ColorOf(FileCategory category) => All[(int)category].Color;
    public static string NameOf(FileCategory category) => FileClassification.NameOf(category);

    /// <summary>Category color with a per-extension lightness jitter (±10%) for in-category texture.</summary>
    public static Color TileColor(string fileName)
    {
        string ext = Path.GetExtension(fileName);
        if (ext.Length == 0)
            return ColorOf(FileCategory.Other);

        return TileColorCache.GetOrAdd(ext, static e =>
        {
            var baseColor = ColorOf(FileClassification.Classify(e));
            int hash = string.GetHashCode(e, StringComparison.OrdinalIgnoreCase);
            float factor = 0.90f + 0.20f * ((uint)hash % 1000) / 1000f;
            return Color.FromArgb(255,
                (byte)Math.Clamp(baseColor.R * factor, 0, 255),
                (byte)Math.Clamp(baseColor.G * factor, 0, 255),
                (byte)Math.Clamp(baseColor.B * factor, 0, 255));
        });
    }

    private static Color FromHex(int rgb) =>
        Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
}
