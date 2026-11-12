using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Stemora.Core;
using Windows.Foundation;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using static Stemora.Core.Strings;

namespace Stemora.App;

public sealed class PianoRollView : Grid
{
    // The stable parent owns pointer input; rebuilding visuals must never remove the capture owner.
    private readonly Canvas _canvas = new() { IsHitTestVisible = false };
    private bool _redrawing, _redrawQueued;
    private readonly Canvas _cursor = new() { IsHitTestVisible = false };
    private readonly Line _line = new() { StrokeThickness = 1.5 };
    private readonly Button _keyboardFocus = new() { Width = 1, Height = 1, Opacity = 0, IsTabStop = true, IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    private MelodyDocument? _document;
    private Point _press;
    private string? _gesture;
    private int _pitchDelta;
    private double _timeDelta;
    private bool _moved;
    private Guid? _lastSelected;
    private double _savedLoopStart, _savedLoopEnd, _savedViewStart;
    private Guid[] _savedSelection = [];
    private int _low = 48, _high = 84;
    private const double KeysWidth = 72, RulerHeight = 48;
    private int? _previewPitch;
    public int SnapDivisions { get; set; }
    public event Action<int>? Audition;
    public event Action<MelodyNote[]?>? PreviewChanged;
    public event Action<bool>? Changed;
    public event Action<double>? Seek;
    public event Action<string>? Status;
    public bool IsEditing => _gesture != null;
    public MelodyDocument? Document { get => _document; set { _document = value; FitPitch(); Redraw(); } }
    private double PlotWidth => Math.Max(1, ActualWidth - KeysWidth);
    private double RowHeight => Math.Max(1, (ActualHeight - RulerHeight) / (_high - _low + 1));
    private double X(double time) => KeysWidth + (time - _document!.ViewStart) / _document.ViewDuration * PlotWidth;
    private double Y(int pitch) => RulerHeight + (_high - pitch) * RowHeight;
    private double Time(double x) => Math.Clamp(_document!.ViewStart + (x - KeysWidth) / PlotWidth * _document.ViewDuration, 0, _document.Duration);
    internal void FocusEditor() => _keyboardFocus.Focus(FocusState.Programmatic);
    internal double EditStep => _document != null && SnapDivisions > 0 ? 60 / _document.Bpm / SnapDivisions : 0.05;
    internal void NudgeTime(int direction, bool resize = false)
    {
        if (_document == null) return;
        var first = _document.Notes.Where(note => _document.Selection.Contains(note.Id)).MinBy(note => note.Start);
        if (first == null) return;
        var delta = direction * EditStep;
        if (SnapDivisions > 0)
        {
            var anchor = resize ? first.End : first.Start;
            var tick = (anchor - _document.FirstBeat) / EditStep;
            var target = direction > 0 ? Math.Floor(tick + 1e-8) + 1 : Math.Ceiling(tick - 1e-8) - 1;
            delta = _document.FirstBeat + target * EditStep - anchor;
        }
        TryEdit(() => new MelodyEditor(_document).Transform(move: resize ? 0 : delta, resizeEnd: resize ? delta : 0));
    }
    private int Pitch(double y) => Math.Clamp(_high - (int)((y - RulerHeight) / RowHeight), 0, 127);
    private static bool Down(VirtualKey key) => (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) != 0;
    public PianoRollView()
    {
        MinHeight = 320; Height = 420; Background = Ink(25, 29, 36);
        Children.Add(_canvas); Children.Add(_cursor); Children.Add(_keyboardFocus); _cursor.Children.Add(_line);
        Loc.SetName(_keyboardFocus, "旋律编辑区：上下是音高，左右是时间");
        SizeChanged += (_, _) => Redraw(); ActualThemeChanged += (_, _) => DispatcherQueue.TryEnqueue(() => Redraw());
        AddHandler(PointerPressedEvent, new PointerEventHandler(Pressed), true);
        PointerMoved += Moved; PointerReleased += Released;
        PointerCanceled += (_, e) => { if (ReferenceEquals(e.OriginalSource, this)) CancelGesture(); };
        PointerCaptureLost += (_, e) => { if (ReferenceEquals(e.OriginalSource, this)) CancelGesture(); };
        DoubleTapped += (_, e) =>
        {
            if (_document == null) return; var point = e.GetPosition(this);
            if (point.X <= KeysWidth || point.Y <= RulerHeight || Hit(point) != null) return;
            TryEdit(() => new MelodyEditor(_document).Add(Snap(Time(point.X)), Math.Min(_document.Duration, Snap(Time(point.X)) + (SnapDivisions > 0 ? 60 / _document.Bpm / SnapDivisions : 0.3)), Pitch(point.Y))); e.Handled = true;
        };
        KeyDown += OnKeyDown;
        PointerWheelChanged += (_, e) =>
        {
            if (_document == null) return;
            var point = e.GetCurrentPoint(this); var direction = point.Properties.MouseWheelDelta > 0 ? 1 : -1;
            if (Down(VirtualKey.Control)) Zoom(direction > 0 ? 0.75 : 1.333333, Time(point.Position.X));
            else if (Down(VirtualKey.Shift)) { _document.ViewStart = Math.Clamp(_document.ViewStart - direction * _document.ViewDuration * 0.12, 0, Math.Max(0, _document.Duration - _document.ViewDuration)); Redraw(); Changed?.Invoke(false); }
            else { var delta = Math.Clamp(direction * 3, -_low, 127 - _high); _low += delta; _high += delta; Redraw(); }
            e.Handled = true;
        };
        Loc.SetName(this, "旋律编辑区：上下是音高，左右是时间");
    }
    private static SolidColorBrush Ink(byte r, byte g, byte b, byte a = 255) => new(Color.FromArgb(a, r, g, b));
    private double Snap(double time) => _document == null || SnapDivisions == 0 || Down(VirtualKey.Menu) ? time
        : Math.Clamp(_document.FirstBeat + Math.Round((time - _document.FirstBeat) / (60 / _document.Bpm / SnapDivisions)) * (60 / _document.Bpm / SnapDivisions), 0, _document.Duration);
    private void PreviewPitch(int pitch)
    {
        if (_previewPitch == pitch) return;
        _previewPitch = pitch; Audition?.Invoke(pitch);
    }
    public static string NoteName(int pitch) { string[] names = ["C", "C♯", "D", "D♯", "E", "F", "F♯", "G", "G♯", "A", "A♯", "B"]; return names[pitch % 12] + (pitch / 12 - 1); }
    public void FitPitch()
    {
        var notes = _document?.Notes ?? [];
        _low = notes.Length == 0 ? 48 : Math.Max(0, notes.Min(n => n.Pitch) - 2);
        _high = notes.Length == 0 ? 84 : Math.Min(127, Math.Max(_low + 13, notes.Max(n => n.Pitch) + 2));
    }
    public void ZoomPitch(double factor)
    {
        var count = Math.Clamp((int)Math.Round((_high - _low + 1) * factor), 8, 128);
        var center = (_high + _low) / 2;
        _low = Math.Clamp(center - count / 2, 0, 128 - count); _high = _low + count - 1;
        Redraw();
    }
    public void Zoom(double factor, double? anchor = null)
    {
        if (_document == null) return;
        var center = anchor ?? _document.Position; var fraction = Math.Clamp((center - _document.ViewStart) / _document.ViewDuration, 0, 1);
        _document.ViewDuration = Math.Clamp(_document.ViewDuration * factor, Math.Min(0.5, _document.Duration), _document.Duration);
        _document.ViewStart = Math.Clamp(center - fraction * _document.ViewDuration, 0, Math.Max(0, _document.Duration - _document.ViewDuration));
        Redraw(); Changed?.Invoke(false);
    }
    public void FocusSelection()
    {
        if (_document == null) return;
        var selected = _document.Notes.Where(n => _document.Selection.Contains(n.Id)).ToArray();
        if (selected.Length == 0) return;
        _document.ViewStart = Math.Max(0, selected.Min(n => n.Start) - 0.3);
        _document.ViewDuration = Math.Min(_document.Duration, Math.Max(1.5, selected.Max(n => n.End) - _document.ViewStart + 0.3));
        Redraw(); Changed?.Invoke(false);
    }
    public void SetPosition(double position)
    {
        if (_document == null) return;
        _line.X1 = _line.X2 = X(position); _line.Y1 = RulerHeight; _line.Y2 = ActualHeight;
        _line.Stroke = Ink(112, 207, 255);
        _line.Visibility = X(position) >= KeysWidth && X(position) <= ActualWidth ? Visibility.Visible : Visibility.Collapsed;
    }
    private MelodyNote? Hit(Point p) => _document?.Notes.Reverse().FirstOrDefault(n => p.X >= X(n.Start) - 2 && p.X <= Math.Max(X(n.Start) + 8, X(n.End)) + 2 && p.Y >= Y(n.Pitch) && p.Y < Y(n.Pitch) + RowHeight);
    public void Redraw(MelodyNote[]? preview = null)
    {
        // WinUI may deliver focus/capture notifications while children are removed.
        // Never mutate the same UIElementCollection recursively.
        if (_redrawing)
        {
            if (!_redrawQueued)
            {
                _redrawQueued = DispatcherQueue.TryEnqueue(() => { _redrawQueued = false; Redraw(); });
            }
            return;
        }
        _redrawing = true;
        try { DrawVisuals(preview); }
        finally { _redrawing = false; }
    }
    private void DrawVisuals(MelodyNote[]? preview)
    {
        _canvas.Children.Clear();
        _canvas.Clip = new RectangleGeometry { Rect = new Rect(0, 0, Math.Max(0, ActualWidth), Math.Max(0, ActualHeight)) };
        if (_document == null || ActualWidth < 1 || ActualHeight < 1) return;
        var d = _document;
        for (var pitch = _low; pitch <= _high; pitch++)
        {
            var black = new[] { 1, 3, 6, 8, 10 }.Contains(pitch % 12);
            Place(new Rectangle { Width = PlotWidth, Height = RowHeight, Fill = black ? Ink(28, 33, 41) : Ink(34, 40, 49) }, KeysWidth, Y(pitch));
            Place(new Rectangle { Width = PlotWidth, Height = 1, Fill = pitch % 12 == 0 ? Ink(73, 86, 104) : Ink(43, 50, 61) }, KeysWidth, Y(pitch) + RowHeight - 1);
        }
        var beat = 60 / d.Bpm;
        var step = beat / (SnapDivisions > 0 ? SnapDivisions : 2);
        while (step / d.ViewDuration * PlotWidth < 18) step *= 2;
        for (var time = d.FirstBeat + Math.Ceiling((d.ViewStart - d.FirstBeat) / step) * step; time < d.ViewStart + d.ViewDuration; time += step)
        {
            var beats = (time - d.FirstBeat) / beat;
            var wholeBeat = Math.Abs(beats - Math.Round(beats)) < 0.001;
            Place(new Rectangle { Width = 1, Height = ActualHeight - RulerHeight, Fill = wholeBeat ? Ink(59, 70, 85) : Ink(42, 50, 61), IsHitTestVisible = false }, X(time), RulerHeight);
        }
        foreach (var bar in VisibleBars(18).Where(bar => bar.Time >= d.ViewStart))
            Place(new Rectangle { Width = 1.5, Height = ActualHeight - RulerHeight, Fill = Ink(88, 108, 135), IsHitTestVisible = false }, X(bar.Time), RulerHeight);
        if (d.LoopEnd > d.LoopStart)
        {
            var left = Math.Max(KeysWidth, X(d.LoopStart)); var right = Math.Min(ActualWidth, X(d.LoopEnd));
            if (right > left) Place(new Rectangle { Width = right - left, Height = ActualHeight - RulerHeight, Fill = Ink(76, 160, 239, 12), IsHitTestVisible = false }, left, RulerHeight);
        }
        foreach (var note in preview ?? d.Notes)
        {
            if (note.End <= d.ViewStart || note.Start >= d.ViewStart + d.ViewDuration || note.Pitch < _low || note.Pitch > _high) continue;
            var selected = d.Selection.Contains(note.Id);
            var left = Math.Max(KeysWidth, X(note.Start)); var right = Math.Min(ActualWidth, X(note.End));
            var width = Math.Max(3, right - left);
            var button = new Button
            {
                Width = width,
                Height = Math.Max(6, RowHeight - 4),
                Padding = new Thickness(5, 0, 3, 0),
                MinWidth = 0,
                MinHeight = 0,
                Background = selected ? Ink(134, 209, 255) : Ink(67, 128, 178),
                Foreground = selected ? Ink(13, 35, 54) : Ink(239, 248, 255),
                BorderBrush = note.NeedsReview ? Ink(243, 191, 107) : Ink(192, 232, 255),
                BorderThickness = new Thickness(selected || note.NeedsReview ? 1 : 0),
                CornerRadius = new CornerRadius(3),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Content = width > 38 ? (note.NeedsReview ? "? " : "") + NoteName(note.Pitch) : note.NeedsReview ? "?" : "",
                FontSize = Math.Clamp(RowHeight - 7, 9, 12)
            };
            AutomationProperties.SetName(button, Tr("音符 {0}，{1:F2} 至 {2:F2} 秒", NoteName(note.Pitch), note.Start, note.End) + (note.NeedsReview ? ", " + Tr(note.ReviewReason ?? "待核对") : ""));
            ToolTipService.SetToolTip(button, Tr("音符 {0}，{1:F2} 至 {2:F2} 秒", NoteName(note.Pitch), note.Start, note.End));
            button.Click += (_, _) => { if (_gesture == null) { _keyboardFocus.Focus(FocusState.Programmatic); d.Selection = [note.Id]; _lastSelected = note.Id; Audition?.Invoke(note.Pitch); Redraw(); Changed?.Invoke(false); } };
            Place(button, left, Y(note.Pitch) + 2);
            if (selected && width > 16)
                foreach (var x in new[] { left + 2, right - 4 })
                    Place(new Rectangle { Width = 2, Height = Math.Max(3, RowHeight - 12), Fill = Ink(225, 245, 255), IsHitTestVisible = false }, x, Y(note.Pitch) + 6);
        }
        // Paint the keyboard and ruler last so clipped notes cannot cover either surface.
        Place(new Rectangle { Width = ActualWidth, Height = RulerHeight, Fill = Ink(22, 26, 33) }, 0, 0);
        var lastLabelRight = KeysWidth;
        foreach (var bar in VisibleBars(72))
        {
            var left = Math.Max(KeysWidth + 5, X(bar.Time) + 5);
            if (left < lastLabelRight + 4) continue;
            var label = new TextBlock { Text = bar.Number.ToString(Strings.Culture), FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Ink(218, 231, 248) };
            AutomationProperties.SetName(label, Tr("第 {0} 小节", bar.Number));
            Place(label, left, 3);
            if (bar.Time >= d.ViewStart) Place(new Rectangle { Width = 1.5, Height = 18, Fill = Ink(88, 108, 135) }, X(bar.Time), 0);
            lastLabelRight = left + label.Text.Length * 8 + 8;
        }
        var seconds = d.ViewDuration > 30 ? 5d : d.ViewDuration > 12 ? 2d : d.ViewDuration > 4 ? 1d : 0.25;
        while (seconds / d.ViewDuration * PlotWidth < 64) seconds *= 2;
        for (var time = Math.Ceiling(d.ViewStart / seconds) * seconds; time < d.ViewStart + d.ViewDuration; time += seconds)
            Place(new TextBlock { Text = $"{time:0.##}s", FontSize = 10, Foreground = Ink(151, 168, 189) }, X(time) + 5, 25);
        if (d.LoopEnd > d.LoopStart)
        {
            var left = Math.Max(KeysWidth, X(d.LoopStart)); var right = Math.Min(ActualWidth, X(d.LoopEnd));
            if (right > left) Place(new Rectangle { Width = right - left, Height = 3, Fill = Ink(112, 207, 255) }, left, RulerHeight - 3);
        }
        Place(new TextBlock { Text = Tr("小节"), FontSize = 11, Foreground = Ink(218, 231, 248) }, 20, 3);
        Place(new TextBlock { Text = Tr("时间"), FontSize = 10, Foreground = Ink(151, 168, 189) }, 20, 25);
        for (var pitch = _low; pitch <= _high; pitch++)
        {
            var black = new[] { 1, 3, 6, 8, 10 }.Contains(pitch % 12);
            var active = _previewPitch == pitch;
            var key = new Button
            {
                Width = KeysWidth - (black ? 15 : 3),
                Height = Math.Max(1, RowHeight - 1),
                MinHeight = 0,
                MinWidth = 0,
                Padding = new Thickness(0, 0, 7, 0),
                CornerRadius = new CornerRadius(0, 2, 2, 0),
                HorizontalContentAlignment = HorizontalAlignment.Right,
                Background = active ? Ink(112, 207, 255) : black ? Ink(18, 22, 29) : Ink(207, 217, 228),
                Foreground = black && !active ? Ink(152, 168, 188) : Ink(35, 44, 57),
                FontSize = Math.Clamp(RowHeight - 5, 8, 12),
                Content = RowHeight >= 12 || pitch % 12 == 0 ? NoteName(pitch) : "",
                BorderThickness = new Thickness(0),
                IsTabStop = pitch % 12 == 0
            };
            var keyPitch = pitch;
            AutomationProperties.SetName(key, Tr("试听 {0}", NoteName(pitch)));
            key.Click += (_, _) => Audition?.Invoke(keyPitch);
            Place(key, 0, Y(pitch));
        }
        SetPosition(d.Position);
    }
    private void Place(UIElement element, double x, double y) { Canvas.SetLeft(element, x); Canvas.SetTop(element, y); _canvas.Children.Add(element); }
    internal IEnumerable<(int Number, double Time)> VisibleBars(double minimumSpacing)
    {
        if (_document == null) yield break;
        var d = _document;
        var origin = MelodyScore.BarOrigin(d);
        var duration = d.BeatsPerBar * 60 / d.Bpm;
        var stride = 1;
        while (stride < 1 << 20 && stride * duration / d.ViewDuration * PlotWidth < minimumSpacing) stride *= 2;
        var end = Math.Min(d.Duration, d.ViewStart + d.ViewDuration);
        var first = Math.Max(0, (int)Math.Floor((d.ViewStart - origin) / duration + 1e-8));
        if (origin + first * duration < end) yield return (first + 1, origin + first * duration);
        for (var index = (first / stride + 1) * stride; origin + index * duration < end; index += stride)
            yield return (index + 1, origin + index * duration);
    }
    private void Pressed(object sender, PointerRoutedEventArgs e)
    {
        if (_document == null) return;
        var current = e.GetCurrentPoint(this); _press = current.Position;
        if (_press.X < KeysWidth)
        {
            if (_press.Y < RulerHeight || !current.Properties.IsLeftButtonPressed) return;
            _gesture = "keyboard"; _previewPitch = null; PreviewPitch(Pitch(_press.Y));
            _keyboardFocus.Focus(FocusState.Programmatic); CapturePointer(e.Pointer); Redraw(); e.Handled = true; return;
        }
        _previewPitch = null;
        _keyboardFocus.Focus(FocusState.Programmatic);
        _savedLoopStart = _document.LoopStart; _savedLoopEnd = _document.LoopEnd; _savedViewStart = _document.ViewStart; _savedSelection = _document.Selection.ToArray();
        _moved = false; _timeDelta = 0; _pitchDelta = 0;
        if (current.Properties.IsMiddleButtonPressed) _gesture = "pan";
        else if (!current.Properties.IsLeftButtonPressed) return;
        else if (_press.Y < RulerHeight) { _gesture = "loop"; Seek?.Invoke(Time(_press.X)); }
        else if (Hit(_press) is { } note)
        {
            if (Down(VirtualKey.Control))
            {
                _document.Selection = _document.Selection.Contains(note.Id) ? _document.Selection.Where(id => id != note.Id).ToArray() : _document.Selection.Append(note.Id).ToArray();
                Redraw(); Changed?.Invoke(false); e.Handled = true; return;
            }
            if (Down(VirtualKey.Shift) && _lastSelected is { } anchor && _document.Notes.FirstOrDefault(n => n.Id == anchor) is { } first)
                _document.Selection = _document.Notes.Where(n => n.Start >= Math.Min(first.Start, note.Start) && n.Start <= Math.Max(first.Start, note.Start)).Select(n => n.Id).ToArray();
            else if (!_document.Selection.Contains(note.Id)) _document.Selection = [note.Id];
            _lastSelected = note.Id; PreviewPitch(note.Pitch);
            var width = X(note.End) - X(note.Start);
            _gesture = width > 22 && Math.Abs(_press.X - X(note.Start)) < 7 ? "left" : width > 22 && Math.Abs(_press.X - X(note.End)) < 7 ? "right" : "move";
        }
        else { _gesture = "select"; if (!Down(VirtualKey.Control)) _document.Selection = []; }
        CapturePointer(e.Pointer); Redraw(); Changed?.Invoke(false); e.Handled = true;
    }
    private void Moved(object sender, PointerRoutedEventArgs e)
    {
        if (_gesture == null || _document == null) return;
        var point = e.GetCurrentPoint(this).Position;
        if (_gesture == "keyboard") { PreviewPitch(Pitch(Math.Clamp(point.Y, RulerHeight, ActualHeight - 1))); Redraw(); e.Handled = true; return; }
        var dx = point.X - _press.X; var dy = point.Y - _press.Y;
        if (Math.Abs(dx) + Math.Abs(dy) < 4 && !_moved) return;
        _moved = true;
        if (_gesture == "pan")
        {
            _document.ViewStart = Math.Clamp(_document.ViewStart - dx / PlotWidth * _document.ViewDuration, 0, Math.Max(0, _document.Duration - _document.ViewDuration)); _press = point; Redraw(); return;
        }
        if (_gesture == "loop") { _document.LoopStart = Math.Min(Time(_press.X), Time(point.X)); _document.LoopEnd = Math.Max(Time(_press.X), Time(point.X)); Redraw(); return; }
        if (_gesture == "select")
        {
            var minT = Math.Min(Time(_press.X), Time(point.X)); var maxT = Math.Max(Time(_press.X), Time(point.X));
            var lo = Math.Min(Pitch(_press.Y), Pitch(point.Y)); var hi = Math.Max(Pitch(_press.Y), Pitch(point.Y));
            _document.Selection = _document.Notes.Where(n => n.End >= minT && n.Start <= maxT && n.Pitch >= lo && n.Pitch <= hi).Select(n => n.Id).ToArray();
            Redraw();
            Place(new Rectangle { Width = Math.Abs(dx), Height = Math.Abs(dy), Stroke = new SolidColorBrush(Colors.DodgerBlue), Fill = new SolidColorBrush(Color.FromArgb(30, 0, 103, 192)), IsHitTestVisible = false }, Math.Min(point.X, _press.X), Math.Min(point.Y, _press.Y));
            return;
        }
        if (_gesture == "move") _gesture = Math.Abs(dy) > Math.Abs(dx) ? "pitch" : "time";
        var free = Down(VirtualKey.Menu);
        _pitchDelta = _gesture == "pitch" || free ? -(int)Math.Round(dy / RowHeight) : 0;
        _timeDelta = _gesture != "pitch" || free ? dx / PlotWidth * _document.ViewDuration : 0;
        if (SnapDivisions > 0 && !free && _gesture != "pitch" && _document.Notes.FirstOrDefault(n => n.Id == _lastSelected) is { } anchor)
        {
            var edge = _gesture == "right" ? anchor.End : anchor.Start;
            _timeDelta = Snap(edge + _timeDelta) - edge;
        }
        try
        {
            var editor = new MelodyEditor(_document);
            var preview = editor.Transform(_pitchDelta, _gesture is "time" or "pitch" ? _timeDelta : 0, _gesture == "left" ? _timeDelta : 0, _gesture == "right" ? _timeDelta : 0, false);
            PreviewChanged?.Invoke(preview);
            if (preview.FirstOrDefault(n => n.Id == _lastSelected) is { } sounding) PreviewPitch(sounding.Pitch);
            Redraw(preview); Status?.Invoke(Tr(_gesture == "pitch" ? "正在改音高" : _gesture is "left" or "right" ? "正在改长短" : "正在改位置"));
        }
        catch (InvalidOperationException error) { Status?.Invoke(error.Message); }
        e.Handled = true;
    }
    private void Released(object sender, PointerRoutedEventArgs e)
    {
        if (_document == null || _gesture == null) return;
        var gesture = _gesture; _gesture = null; _previewPitch = null; ReleasePointerCapture(e.Pointer);
        if (_moved && gesture is "pitch" or "time" or "left" or "right")
            TryEdit(() => new MelodyEditor(_document).Transform(_pitchDelta, gesture is "time" or "pitch" ? _timeDelta : 0, gesture == "left" ? _timeDelta : 0, gesture == "right" ? _timeDelta : 0));
        else { Redraw(); Changed?.Invoke(false); }
        PreviewChanged?.Invoke(null);
        e.Handled = true;
    }
    private void CancelGesture()
    {
        if (_gesture == null) return;
        if (_gesture != "keyboard" && _document != null) { _document.LoopStart = _savedLoopStart; _document.LoopEnd = _savedLoopEnd; _document.ViewStart = _savedViewStart; _document.Selection = _savedSelection; }
        _gesture = null; _previewPitch = null; ReleasePointerCaptures(); PreviewChanged?.Invoke(null); Redraw(); Status?.Invoke(Tr("已取消拖动"));
    }
    internal void VerifyDiagnosticInputSurface()
    {
        if (_document == null || ActualWidth < KeysWidth || _document.Selection.Length == 0)
            throw new InvalidOperationException("Missing piano-roll diagnostic layout.");
        var note = _document.Notes.First(n => n.Id == _document.Selection[0]);
        var point = TransformToVisual(XamlRoot.Content).TransformPoint(new Point(
            Math.Clamp((X(note.Start) + X(note.End)) / 2, KeysWidth + 1, ActualWidth - 1), Y(note.Pitch) + RowHeight / 2));
        var hits = VisualTreeHelper.FindElementsInHostCoordinates(point, this).ToArray();
        if (!hits.Contains(this) || hits.Any(element => element is Button))
            throw new InvalidOperationException("A rebuilt note visual can still intercept and capture pointer input.");
        // Exercise keyboard/accessibility activation as well as the pointer surface.
        var button = _canvas.Children.OfType<Button>().First(b => Canvas.GetLeft(b) >= KeysWidth);
        var peer = new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(button);
        var invoke = (Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)peer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke);
        invoke.Invoke();
        for (var i = 0; i < 20; i++)
        {
            _savedSelection = _document.Selection.ToArray(); _savedLoopStart = _document.LoopStart;
            _savedLoopEnd = _document.LoopEnd; _savedViewStart = _document.ViewStart;
            _gesture = "move";
            Redraw();
            if (_gesture != "move") throw new InvalidOperationException("Redrawing cancelled an active note gesture.");
            CancelGesture(); CancelGesture();
        }
    }
    private void TryEdit(Action action)
    {
        try { action(); Redraw(); Changed?.Invoke(true); } catch (Exception error) when (error is ArgumentException or InvalidOperationException) { Redraw(); Status?.Invoke(error.Message); }
    }
    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!e.Handled) e.Handled = HandleShortcut(e.Key, Down(VirtualKey.Control), Down(VirtualKey.Shift), Down(VirtualKey.Menu));
    }
    internal bool HandleShortcut(VirtualKey key, bool control, bool shift, bool alt)
    {
        if (_document == null || alt) return false;
        var editor = new MelodyEditor(_document);
        if (key == VirtualKey.Escape && IsEditing) { CancelGesture(); return true; }
        if (IsEditing) return false;
        if (key == VirtualKey.Escape && !control) { _document.Selection = []; Redraw(); Changed?.Invoke(false); return true; }
        if (control && key == VirtualKey.A) { _document.Selection = _document.Notes.Select(n => n.Id).ToArray(); Redraw(); Changed?.Invoke(false); }
        else if (control && key == VirtualKey.Z) TryEdit(shift ? editor.Redo : editor.Undo);
        else if (control && key == VirtualKey.Y) TryEdit(editor.Redo);
        else if (control || _document.Selection.Length == 0) return false;
        else if (key is VirtualKey.Delete or VirtualKey.Back) TryEdit(editor.Delete);
        else if (key == VirtualKey.Up) TryEdit(() => editor.Transform(semitones: shift ? 12 : 1));
        else if (key == VirtualKey.Down) TryEdit(() => editor.Transform(semitones: shift ? -12 : -1));
        else if (key == VirtualKey.Left) NudgeTime(-1, shift);
        else if (key == VirtualKey.Right) NudgeTime(1, shift);
        else return false;
        return true;
    }
}
