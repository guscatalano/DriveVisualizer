using System.Collections.Concurrent;
using Windows.UI;

namespace DriveVisualizer_App.Rendering;

/// <summary>
/// Stable extension → color mapping for treemap tiles and the (future) file
/// types legend. Common extensions get fixed colors; everything else hashes
/// into the palette so a given extension always renders the same color.
/// </summary>
public static class TileColors
{
    public static readonly Color Aggregate = Color.FromArgb(255, 90, 90, 90);
    public static readonly Color NoExtension = Color.FromArgb(255, 130, 130, 130);

    private static readonly Color[] Palette =
    [
        Color.FromArgb(255, 64, 132, 214),   // blue
        Color.FromArgb(255, 214, 77, 77),    // red
        Color.FromArgb(255, 76, 175, 80),    // green
        Color.FromArgb(255, 230, 180, 60),   // yellow
        Color.FromArgb(255, 156, 99, 214),   // purple
        Color.FromArgb(255, 66, 190, 200),   // cyan
        Color.FromArgb(255, 235, 140, 66),   // orange
        Color.FromArgb(255, 200, 90, 170),   // magenta
        Color.FromArgb(255, 0, 150, 136),    // teal
        Color.FromArgb(255, 150, 200, 70),   // lime
        Color.FromArgb(255, 160, 120, 90),   // brown
        Color.FromArgb(255, 235, 130, 150),  // pink
        Color.FromArgb(255, 100, 130, 160),  // steel
        Color.FromArgb(255, 140, 140, 60),   // olive
        Color.FromArgb(255, 92, 107, 192),   // indigo
        Color.FromArgb(255, 240, 110, 90),   // coral
    ];

    private static readonly ConcurrentDictionary<string, Color> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static Color ForFileName(string fileName)
    {
        string ext = Path.GetExtension(fileName);
        if (ext.Length == 0)
            return NoExtension;
        return Cache.GetOrAdd(ext, static e =>
        {
            int hash = string.GetHashCode(e, StringComparison.OrdinalIgnoreCase);
            return Palette[(int)((uint)hash % Palette.Length)];
        });
    }
}
