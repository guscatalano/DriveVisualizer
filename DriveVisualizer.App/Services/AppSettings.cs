using Windows.Storage;

namespace DriveVisualizer_App.Services;

/// <summary>Small typed wrapper over the packaged app's LocalSettings.</summary>
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

    private static bool GetBool(string key, bool fallback) =>
        Container?.Values[key] is bool value ? value : fallback;

    private static void SetBool(string key, bool value)
    {
        if (Container is { } c)
            c.Values[key] = value;
    }

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

    /// <summary>Display precision for sizes (DriveVisualizer.Core.SizeDetail as int).</summary>
    public static int SizeDetail
    {
        get => Container?.Values[nameof(SizeDetail)] is int value ? value : 0;
        set
        {
            if (Container is { } c)
                c.Values[nameof(SizeDetail)] = value;
        }
    }

}
