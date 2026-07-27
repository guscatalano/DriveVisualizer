using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace DriveVisualizer_App.Controls;

/// <summary>Minimal horizontal wrap panel (WinUI ships none) for the legend.</summary>
public partial class WrapPanel : Panel
{
    public double HorizontalSpacing { get; set; } = 14;
    public double VerticalSpacing { get; set; } = 4;

    protected override Size MeasureOverride(Size availableSize)
    {
        double lineWidth = 0, lineHeight = 0, totalWidth = 0, totalHeight = 0;
        foreach (var child in Children)
        {
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var d = child.DesiredSize;
            if (lineWidth > 0 && lineWidth + HorizontalSpacing + d.Width > availableSize.Width)
            {
                totalWidth = Math.Max(totalWidth, lineWidth);
                totalHeight += lineHeight + VerticalSpacing;
                lineWidth = 0;
                lineHeight = 0;
            }
            lineWidth += (lineWidth > 0 ? HorizontalSpacing : 0) + d.Width;
            lineHeight = Math.Max(lineHeight, d.Height);
        }
        totalWidth = Math.Max(totalWidth, lineWidth);
        totalHeight += lineHeight;
        return new Size(
            double.IsInfinity(availableSize.Width) ? totalWidth : Math.Min(totalWidth, availableSize.Width),
            totalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0, y = 0, lineHeight = 0;
        foreach (var child in Children)
        {
            var d = child.DesiredSize;
            if (x > 0 && x + HorizontalSpacing + d.Width > finalSize.Width)
            {
                y += lineHeight + VerticalSpacing;
                x = 0;
                lineHeight = 0;
            }
            if (x > 0)
                x += HorizontalSpacing;
            child.Arrange(new Rect(x, y, d.Width, d.Height));
            x += d.Width;
            lineHeight = Math.Max(lineHeight, d.Height);
        }
        return finalSize;
    }
}
