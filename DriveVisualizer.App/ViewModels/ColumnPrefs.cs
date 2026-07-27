using CommunityToolkit.Mvvm.ComponentModel;

namespace DriveVisualizer_App.ViewModels;

/// <summary>
/// Shared column-visibility switches. A singleton so both the header and every
/// row template can bind to the same instance.
/// </summary>
public partial class ColumnPrefs : ObservableObject
{
    public static ColumnPrefs Instance { get; } = new();

    [ObservableProperty]
    public partial bool ShowPercent { get; set; }

    [ObservableProperty]
    public partial bool ShowSize { get; set; }

    [ObservableProperty]
    public partial bool ShowChange { get; set; }

    [ObservableProperty]
    public partial bool ShowFiles { get; set; }

    [ObservableProperty]
    public partial bool ShowModified { get; set; }

    /// <summary>Computed from the widest value on screen so byte-precision never clips.</summary>
    [ObservableProperty]
    public partial double SizeColumnWidth { get; set; }

    [ObservableProperty]
    public partial double ChangeColumnWidth { get; set; }

    private ColumnPrefs()
    {
        ShowPercent = true;
        ShowSize = true;
        ShowChange = true;
        ShowFiles = true;
        ShowModified = true;
        SizeColumnWidth = 90;
        ChangeColumnWidth = 90;
    }

    /// <summary>Fits the Size/Change columns to the largest value they can display.</summary>
    public void FitToLargestValue(long maxBytes)
    {
        string sample = DriveVisualizer.Core.ByteFormatter.Format(maxBytes);
        // ~7.5 px per character of tabular Segoe UI at 14px, plus padding.
        SizeColumnWidth = Math.Max(90, 16 + sample.Length * 7.5);
        ChangeColumnWidth = Math.Max(90, 26 + sample.Length * 7.5); // room for the +/− sign
    }
}
