using static Stemora.Core.Strings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Stemora.Core;
using Windows.Foundation;
using Windows.UI;

namespace Stemora.App;

// A true min/max envelope (both channels), with RMS overlay and shared time coordinates.
public sealed class WaveformView : Grid
{
    private readonly Canvas _canvas = new() { IsHitTestVisible = false };
    private readonly Line _playhead = new() { StrokeThickness = 1.5, IsHitTestVisible = false };
    private readonly TextBlock _empty = new() { Text = Tr("正在分析波形…"), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
    private bool _dragging;
    public WaveformData? Data { get; set; }
    public double ViewStart { get; set; }
    public double ViewDuration { get; set; } = 1;
    public Color TrackColor { get; set; } = Color.FromArgb(255, 0, 103, 192);
    public bool NormalizeDisplay { get; set; } = true;
    public double? HighlightStart { get; set; }
    public double? HighlightEnd { get; set; }
    public double? LoopStart { get; set; }
    public double? LoopEnd { get; set; }
    public event Action<double>? Seek;
    public event Action<double>? Zoom;
    public WaveformView()
    {
        Height = 104; HorizontalAlignment = HorizontalAlignment.Stretch;
        Background = new SolidColorBrush(Color.FromArgb(9, 127, 127, 127));
        CornerRadius = new CornerRadius(4);
        Children.Add(_canvas); Children.Add(_empty); Children.Add(_playhead);
        SizeChanged += (_, _) => Redraw();
        ActualThemeChanged += (_, _) => Redraw();
        PointerPressed += (_, e) => { _dragging = CapturePointer(e.Pointer); RequestSeek(e); e.Handled = true; };
        PointerMoved += (_, e) => { if (_dragging) { RequestSeek(e); e.Handled = true; } };
        PointerReleased += (_, e) => { _dragging = false; ReleasePointerCapture(e.Pointer); };
        PointerCanceled += (_, _) => _dragging = false;
        PointerWheelChanged += (_, e) => { Zoom?.Invoke(e.GetCurrentPoint(this).Properties.MouseWheelDelta > 0 ? 2 : 0.5); e.Handled = true; };
        Loc.SetName(this, "音轨波形，点击或拖动定位，滚轮缩放");
        Loc.SetText(_empty, "正在分析波形…");
    }
    private void RequestSeek(PointerRoutedEventArgs e)
    {
        if (ActualWidth > 0) Seek?.Invoke(ViewStart + Math.Clamp(e.GetCurrentPoint(this).Position.X / ActualWidth, 0, 1) * ViewDuration);
    }
    public void SetPosition(double seconds)
    {
        var x = (seconds - ViewStart) / ViewDuration * ActualWidth;
        _playhead.X1 = _playhead.X2 = x; _playhead.Y1 = 0; _playhead.Y2 = ActualHeight;
        _playhead.Visibility = x >= 0 && x <= ActualWidth ? Visibility.Visible : Visibility.Collapsed;
    }
    public void Redraw()
    {
        _canvas.Children.Clear();
        _canvas.Clip = new RectangleGeometry { Rect = new Rect(0, 0, Math.Max(0, ActualWidth), Math.Max(0, ActualHeight)) };
        _empty.Visibility = Data == null ? Visibility.Visible : Visibility.Collapsed;
        _playhead.Stroke = new SolidColorBrush(ActualTheme == ElementTheme.Dark ? Color.FromArgb(255, 255, 255, 255) : Color.FromArgb(255, 32, 32, 32));
        if (Data == null || ActualWidth < 1 || ActualHeight < 1) return;
        void Highlight(double? start, double? end, byte alpha, bool outline)
        {
            if (start == null || end == null || end <= start) return;
            var left = Math.Max(0, (start.Value - ViewStart) / ViewDuration * ActualWidth);
            var right = Math.Min(ActualWidth, (end.Value - ViewStart) / ViewDuration * ActualWidth);
            if (right <= left) return;
            var area = new Rectangle { Width = right - left, Height = Math.Max(1, ActualHeight - 2), Fill = new SolidColorBrush(Color.FromArgb(alpha, 0, 103, 192)), Stroke = outline ? new SolidColorBrush(TrackColor) : null, StrokeThickness = 1, IsHitTestVisible = false };
            Canvas.SetLeft(area, left); Canvas.SetTop(area, 1); _canvas.Children.Add(area);
        }
        Highlight(LoopStart, LoopEnd, 35, false); Highlight(HighlightStart, HighlightEnd, 8, true);
        var columns = Math.Max(1, (int)ActualWidth);
        var channels = Data.Channels;
        var axisBrush = new SolidColorBrush(Color.FromArgb(55, 127, 127, 127));
        for (var channel = 0; channel < channels; channel++)
        {
            var lane = ActualHeight / channels;
            var center = lane * (channel + 0.5);
            _canvas.Children.Add(new Line { X1 = 0, X2 = ActualWidth, Y1 = center, Y2 = center, Stroke = axisBrush, StrokeThickness = 1 });
            var envelope = Data.View(channel, ViewStart, ViewDuration, columns);
            var scale = (lane - 10) / 2 / (NormalizeDisplay && Data.Peak > 0.000001 ? Data.Peak : 1);
            var peakFigure = new PathFigure { StartPoint = new Point(0, center), IsClosed = true };
            var rmsFigure = new PathFigure { StartPoint = new Point(0, center), IsClosed = true };
            var peakLine = new PolyLineSegment(); var rmsLine = new PolyLineSegment();
            for (var x = 0; x < columns; x++)
            {
                peakLine.Points.Add(new(x, center - Math.Clamp(envelope[x].Maximum, -1, 1) * scale));
                rmsLine.Points.Add(new(x, center - Math.Min(1, envelope[x].Rms) * scale));
            }
            for (var x = columns - 1; x >= 0; x--)
            {
                peakLine.Points.Add(new(x, center - Math.Clamp(envelope[x].Minimum, -1, 1) * scale));
                rmsLine.Points.Add(new(x, center + Math.Min(1, envelope[x].Rms) * scale));
            }
            peakFigure.Segments.Add(peakLine); rmsFigure.Segments.Add(rmsLine);
            var peaks = new PathGeometry(); peaks.Figures.Add(peakFigure);
            var rms = new PathGeometry(); rms.Figures.Add(rmsFigure);
            _canvas.Children.Add(new Microsoft.UI.Xaml.Shapes.Path { Data = peaks, Fill = new SolidColorBrush(TrackColor), Opacity = 0.5 });
            _canvas.Children.Add(new Microsoft.UI.Xaml.Shapes.Path { Data = rms, Fill = new SolidColorBrush(TrackColor), Opacity = 0.9 });
            var channelLabel = new TextBlock { Text = channels == 2 ? channel == 0 ? "L" : "R" : "M", FontSize = 12, Opacity = 0.5 };
            Canvas.SetLeft(channelLabel, 4); Canvas.SetTop(channelLabel, lane * channel + 1); _canvas.Children.Add(channelLabel);
        }
    }
}
