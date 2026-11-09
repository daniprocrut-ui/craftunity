using System.Windows;
using System.Windows.Controls.Primitives;

namespace CraftunityLauncher;

/// <summary>Плитки, которые сами выбирают число колонок под ширину окна.</summary>
public class ResponsiveUniformGrid : UniformGrid
{
    private double _lastWidth = -1;

    protected override Size MeasureOverride(Size constraint)
    {
        var width = double.IsInfinity(constraint.Width) ? ActualWidth : constraint.Width;
        if (width > 0 && Math.Abs(width - _lastWidth) > 1)
        {
            _lastWidth = width;
            Columns = width < 620 ? 1 : width < 880 ? 2 : 3;
            InvalidateMeasure();
        }
        return base.MeasureOverride(constraint);
    }
}
