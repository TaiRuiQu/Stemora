using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Stemora.App;

public sealed class LevelMeter : Grid
{
    private readonly Microsoft.UI.Xaml.Shapes.Rectangle _fill;
    private double _level;
    public double FillWidth => _fill.Width;
    public LevelMeter(Color color)
    {
        Height = 4;
        Background = new SolidColorBrush(Color.FromArgb(24, 127, 127, 127));
        CornerRadius = new CornerRadius(2);
        _fill = new()
        {
            Fill = new SolidColorBrush(color),
            Width = 0,
            Height = 4,
            RadiusX = 2,
            RadiusY = 2,
            HorizontalAlignment = HorizontalAlignment.Left,
            Visibility = Visibility.Collapsed
        };
        Children.Add(_fill);
        SizeChanged += (_, _) => UpdateFill();
    }
    public void SetLevel(double level)
    {
        _level = double.IsFinite(level) ? Math.Clamp(level, 0, 1) : 0;
        UpdateFill();
    }
    private void UpdateFill()
    {
        // Resize the geometry instead of scaling a full-width surface down to subpixel size.
        // Snap to physical pixels so silence and very low levels cannot leave composition streaks.
        var scale = XamlRoot?.RasterizationScale ?? 1;
        var width = Math.Floor(ActualWidth * _level * scale) / scale;
        _fill.Width = Math.Clamp(width, 0, ActualWidth);
        _fill.Visibility = width > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
