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
    public partial bool ShowFiles { get; set; }

    [ObservableProperty]
    public partial bool ShowModified { get; set; }

    private ColumnPrefs()
    {
        ShowPercent = true;
        ShowSize = true;
        ShowFiles = true;
        ShowModified = true;
    }
}
