using System.Windows;
using System.Windows.Controls.Primitives;

namespace CraftunityLauncher;

/// <summary>Плитки сами выбирают число колонок под ширину окна.</summary>
public class ResponsiveUniformGrid : UniformGrid
{
    private double _lastWidth = -1;
    private int _naturalColumns = 3;

    static ResponsiveUniformGrid()
    {
        WidthProperty.OverrideMetadata(
            typeof(ResponsiveUniformGrid),
            new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsMeasure));
    }

    protected override Size MeasureOverride(Size constraint)
    {
        var width = double.IsInfinity(constraint.Width) ? ActualWidth : constraint.Width;
        if (width > 0 && Math.Abs(width - _lastWidth) > 1)
        {
            _lastWidth = width;
            var columns = width < 620 ? 1 : width < 880 ? 2 : _naturalColumns;
            if (Columns != columns)
            {
                Columns = columns;
                InvalidateMeasure();
            }
        }
        return base.MeasureOverride(constraint);
    }
}
