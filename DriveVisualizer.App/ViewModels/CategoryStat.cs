using Microsoft.UI.Xaml.Media;

namespace DriveVisualizer_App.ViewModels;

/// <summary>One legend entry: category swatch, name, and its share of the scan.
/// Opacity drops when the category is filtered out so the legend mirrors the map.</summary>
public sealed record CategoryStat(string Name, Brush Swatch, string SizeText, string PercentText, double Opacity);
