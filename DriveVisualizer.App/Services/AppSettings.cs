using System.Text.Json;
using Windows.Storage;

namespace DriveVisualizer_App.Services;

/// <summary>
/// Typed settings. Packaged (MSIX) builds use LocalSettings; unpackaged (MSI)
/// builds fall back to a JSON file in %LocalAppData%\DriveVisualizer.
/// </summary>
public static class AppSettings
{
    private static ApplicationDataContainer? Container
    {
        get
        {
            try { return ApplicationData.Current.LocalSettings; }
            catch { return null; }
        }
    }

    // ---- JSON fallback for unpackaged builds ----

    private static readonly object FileLock = new();
    private static Dictionary<string, JsonElement>? _fileValues;

    private static string FallbackPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DriveVisualizer", "settings.json");

    private static Dictionary<string, JsonElement> FileValues
    {
        get
        {
            if (_fileValues is null)
            {
                lock (FileLock)
                {
                    try
                    {
                        _fileValues = File.Exists(FallbackPath)
                            ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(FallbackPath)) ?? []
                            : [];
                    }
                    catch
                    {
                        _fileValues = [];
                    }
                }
            }
            return _fileValues;
        }
    }

    private static void SaveFileValues()
    {
        lock (FileLock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FallbackPath)!);
                File.WriteAllText(FallbackPath, JsonSerializer.Serialize(_fileValues));
            }
            catch { }
        }
    }

    private static bool GetBool(string key, bool fallback)
    {
        if (Container is { } c)
            return c.Values[key] is bool value ? value : fallback;
        return FileValues.TryGetValue(key, out var e) && e.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? e.GetBoolean() : fallback;
    }

    private static void SetBool(string key, bool value)
    {
        if (Container is { } c)
        {
            c.Values[key] = value;
            return;
        }
        FileValues[key] = JsonSerializer.SerializeToElement(value);
        SaveFileValues();
    }

    private static int GetInt(string key, int fallback)
    {
        if (Container is { } c)
            return c.Values[key] is int value ? value : fallback;
        return FileValues.TryGetValue(key, out var e) && e.ValueKind == JsonValueKind.Number
            ? e.GetInt32() : fallback;
    }

    private static void SetInt(string key, int value)
    {
        if (Container is { } c)
        {
            c.Values[key] = value;
            return;
        }
        FileValues[key] = JsonSerializer.SerializeToElement(value);
        SaveFileValues();
    }

    // ---- Settings ----

    /// <summary>Auto-save each completed scan so "diff vs last scan" has a baseline.</summary>
    public static bool AutoSaveSnapshots
    {
        get => GetBool(nameof(AutoSaveSnapshots), true);
        set => SetBool(nameof(AutoSaveSnapshots), value);
    }

    /// <summary>Start with the treemap beside the tree instead of below it.</summary>
    public static bool SideBySideLayout
    {
        get => GetBool(nameof(SideBySideLayout), false);
        set => SetBool(nameof(SideBySideLayout), value);
    }

    /// <summary>App theme: 0 system, 1 light, 2 dark.</summary>
    public static int Theme
    {
        get => GetInt(nameof(Theme), 0);
        set => SetInt(nameof(Theme), value);
    }

    /// <summary>Visualization style: 0 treemap, 1 sunburst, 2 icicle.</summary>
    public static int VizMode
    {
        get => GetInt(nameof(VizMode), 0);
        set => SetInt(nameof(VizMode), value);
    }

    /// <summary>Display precision for sizes (DriveVisualizer.Core.SizeDetail as int).</summary>
    public static int SizeDetail
    {
        get => GetInt(nameof(SizeDetail), 0);
        set => SetInt(nameof(SizeDetail), value);
    }
}
