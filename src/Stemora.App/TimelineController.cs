using static Stemora.Core.Strings;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Stemora.Core;
using Windows.UI;

namespace Stemora.App;

public sealed partial class MainWindow
{
    private sealed class TrackUi
    {
        public required MixerTrack Track { get; init; }
        public required WaveformView Wave { get; init; }
        public required ToggleButton Mute { get; init; }
        public required ToggleButton Solo { get; init; }
        public required Slider Volume { get; init; }
        public required LevelMeter Meter { get; init; }
        public required TextBlock Label { get; init; }
        public required TextBlock PeakLabel { get; init; }
        public required Grid Row { get; init; }
    }
    private readonly List<TrackUi> _trackViews = [];
    private AudioMixerSession? _mixer;
    private CancellationTokenSource? _timelineCancellation;
    private DispatcherTimer? _timelineTimer;
    private double _timelineDuration, _timelineStart, _timelineZoom = 1, _timelinePosition;
    private bool _updatingPosition, _seeking, _wasPlaying, _updatingPan;
    private static readonly Color[] TrackColors = [Color.FromArgb(255, 0, 103, 192), Color.FromArgb(255, 120, 94, 195),
        Color.FromArgb(255, 0, 135, 118), Color.FromArgb(255, 182, 111, 23), Color.FromArgb(255, 197, 73, 112),
        Color.FromArgb(255, 44, 129, 168), Color.FromArgb(255, 136, 106, 78)];
    private void InitializeTimeline()
    {
        _timelineTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _timelineTimer.Tick += (_, _) => UpdatePlaybackPosition();
        _timelineTimer.Start();
        TimelineRuler.SizeChanged += (_, _) => DrawTimelineRuler();
        PositionSlider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) =>
        {
            _seeking = true; _wasPlaying = _mixer?.IsPlaying == true; _mixer?.Pause();
        }), true);
        PositionSlider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) =>
        {
            if (!_seeking) return; _seeking = false; SeekTimeline(PositionSlider.Value, _wasPlaying);
        }), true);
        Closed += (_, _) => { _timelineTimer?.Stop(); _timelineCancellation?.Cancel(); _mixer?.Dispose(); };
    }
    private async Task LoadTimelineAsync(AudioJob job)
    {
        _timelineCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _timelineCancellation = cancellation;
        _mixer?.Dispose(); _mixer = null;
        TracksPanel.Children.Clear(); _trackViews.Clear();
        _timelineStart = _timelinePosition = 0; _timelineZoom = 1;
        _timelineDuration = 0; PlaybackButton.IsEnabled = false;
        ExportTracksButton.IsEnabled = job.Result != null && _exportCancellation == null;
        PlaybackGlyph.Glyph = "\uE768";
        var tracks = new List<MixerTrack>();
        if (job.Result is { } result)
        {
            tracks.Add(new("original", Tr("原始混音"), job.Path, true));
            if (result.Tracks is { Count: > 0 }) tracks.AddRange(result.Tracks.Select(t => new MixerTrack(t.Id, t.Label, t.Path)));
            else
            {
                tracks.AddRange(result.StemPaths.Select((path, i) => new MixerTrack(i == 0 ? "primary" : $"stem_{i}", i == 0 ? Tr("主音轨") : Tr("音轨 {0}", i + 1), path)));
                if (result.ResidualPath != null) tracks.Add(new("residual", Tr("残差伴奏"), result.ResidualPath));
            }
        }
        else tracks.Add(new("original", Tr("原始混音"), job.Path));
        PreviewSection.Visibility = Visibility.Visible;
        TimelineTransport.Visibility = Visibility.Visible;
        PreviewLabel.Text = job.Name;
        var definitions = tracks.ToArray();
        for (var i = 0; i < definitions.Length; i++) CreateTrackRow(definitions[i], i);
        try
        {
            for (var i = 0; i < definitions.Length; i++)
            {
                var data = await WaveformData.LoadAsync(definitions[i].Path, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                if (_closed) return;
                _timelineDuration = Math.Max(_timelineDuration, data.Duration);
                _trackViews[i].Wave.Data = data;
                UpdateTrackDetails(_trackViews[i]);
                _trackViews[i].Wave.Redraw();
                UpdateTimelineViewport();
            }
            cancellation.Token.ThrowIfCancellationRequested();
            try
            {
                _mixer = new AudioMixerSession(definitions);
                for (var i = 0; i < _trackViews.Count; i++)
                {
                    _mixer.SetMute(i, _trackViews[i].Mute.IsChecked == true);
                    _mixer.SetSolo(i, _trackViews[i].Solo.IsChecked == true);
                    _mixer.SetVolume(i, (float)(_trackViews[i].Volume.Value / 100));
                }
                PlaybackButton.IsEnabled = true;
            }
            catch (Exception error) { ShowNotice(Tr("无法打开音频设备"), error.Message, InfoBarSeverity.Warning); }
            PositionSlider.Maximum = Math.Max(0.001, _timelineDuration);
            PositionSlider.Value = 0;
            UpdatePlaybackPosition();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!_closed && !cancellation.IsCancellationRequested) ShowError(error); }
        finally { if (_timelineCancellation == cancellation) _timelineCancellation = null; cancellation.Dispose(); }
    }
    private void CreateTrackRow(MixerTrack track, int index)
    {
        var label = new TextBlock { Text = Strings.StemLabel(track.Id, track.Label), TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        var mute = new ToggleButton { Content = "M", IsChecked = track.Muted, Width = 40, Height = 32, Padding = new Thickness(0) };
        var solo = new ToggleButton { Content = "S", Width = 40, Height = 32, Padding = new Thickness(0) };
        AutomationProperties.SetName(mute, Tr("{0} 静音", label.Text)); AutomationProperties.SetName(solo, Tr("{0} 独听", label.Text));
        ToolTipService.SetToolTip(mute, Tr("静音 (Mute)")); ToolTipService.SetToolTip(solo, Tr("独听 (Solo)"));
        var volume = new Slider { Minimum = 0, Maximum = 100, Value = 100, Width = 120, Height = 32, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(volume, Tr("{0} 音量", label.Text));
        var meter = new LevelMeter(TrackColors[index % TrackColors.Length]);
        var peak = new TextBlock { Text = Tr("分析中"), Opacity = 0.7 };
        var toggles = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        toggles.Children.Add(mute); toggles.Children.Add(solo); toggles.Children.Add(volume);
        var control = new StackPanel { Spacing = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 24, 0) };
        var trackTitle = new Grid();
        trackTitle.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        trackTitle.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(peak, 1); trackTitle.Children.Add(label); trackTitle.Children.Add(peak);
        label.FontSize = 16;
        peak.FontSize = 14;
        control.Children.Add(trackTitle); control.Children.Add(toggles); control.Children.Add(meter);
        if (track.Id is not "original" and not "instrumental" and not "residual" and not "bass" and not "drums" and not "guitar" and not "piano" and not "other")
        {
            var transcribe = new HyperlinkButton { Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Left };
            Loc.SetContent(transcribe, "转成旋律");
            transcribe.Click += async (_, _) =>
            {
                if (_melodyWorkspace == null) return;
                try
                {
                    if (track.Id != "vocals")
                    {
                        var confirm = new ContentDialog { XamlRoot = Root.XamlRoot, Title = Tr("选择人声音轨"), Content = Tr("请先试听确认此音轨是人声，再继续转谱。"), PrimaryButtonText = Tr("这是人声，继续"), CloseButtonText = Tr("取消") };
                        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
                    }
                    Navigation.SelectedItem = NavigationItem("melody");
                    var original = (QueueList.SelectedItem as AudioJob)?.Path ?? track.Path;
                    await _melodyWorkspace.LoadAudioAsync(original, track.Path);
                }
                catch (Exception error) { ShowError(error); }
            };
            control.Children.Add(transcribe);
        }
        var wave = new WaveformView { TrackColor = TrackColors[index % TrackColors.Length], Opacity = track.Muted ? 0.4 : 1, NormalizeDisplay = NormalizeWaveform.IsChecked == true };
        var row = new Grid
        {
            ColumnSpacing = 0,
            Padding = new Thickness(0, 4, 0, 4),
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(20, 127, 127, 127))
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(272) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(wave, 1); row.Children.Add(control); row.Children.Add(wave);
        var ui = new TrackUi { Track = track, Wave = wave, Mute = mute, Solo = solo, Volume = volume, Meter = meter, Label = label, PeakLabel = peak, Row = row };
        _trackViews.Add(ui); TracksPanel.Children.Add(row);
        mute.Click += (_, _) => { _mixer?.SetMute(index, mute.IsChecked == true); UpdateTrackEmphasis(); };
        solo.Click += (_, _) => { _mixer?.SetSolo(index, solo.IsChecked == true); UpdateTrackEmphasis(); };
        volume.ValueChanged += (_, args) => _mixer?.SetVolume(index, (float)(args.NewValue / 100));
        wave.Seek += value => SeekTimeline(value, _mixer?.IsPlaying == true);
        wave.Zoom += factor => ZoomTimeline(factor);
    }
    private void UpdateTrackEmphasis()
    {
        var hasSolo = _trackViews.Any(t => t.Solo.IsChecked == true);
        foreach (var track in _trackViews) track.Wave.Opacity = track.Mute.IsChecked == true || hasSolo && track.Solo.IsChecked != true ? 0.35 : 1;
    }
    private void Playback_Click(object sender, RoutedEventArgs e)
    {
        try { if (_mixer == null) return; if (_mixer.IsPlaying) _mixer.Pause(); else _mixer.Play(); UpdatePlaybackPosition(); }
        catch (Exception error) { ShowError(error); }
    }
    private void ReturnToStart_Click(object sender, RoutedEventArgs e) => SeekTimeline(0, _mixer?.IsPlaying == true);
    private void Position_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updatingPosition) return;
        _timelinePosition = e.NewValue;
        UpdatePlayheads();
        if (!_seeking) SeekTimeline(e.NewValue, _mixer?.IsPlaying == true);
    }
    private void SeekTimeline(double seconds, bool resume)
    {
        try
        {
            _timelinePosition = Math.Clamp(seconds, 0, _timelineDuration);
            _mixer?.Seek(TimeSpan.FromSeconds(_timelinePosition), resume);
            _updatingPosition = true; PositionSlider.Value = _timelinePosition; _updatingPosition = false;
            UpdatePlayheads();
        }
        catch (Exception error) { ShowError(error); }
    }
    private void UpdatePlaybackPosition()
    {
        if (_closed || _seeking) return;
        if (_mixer != null) _timelinePosition = _mixer.Position.TotalSeconds;
        _updatingPosition = true; PositionSlider.Value = Math.Clamp(_timelinePosition, 0, PositionSlider.Maximum); _updatingPosition = false;
        PlaybackGlyph.Glyph = _mixer?.IsPlaying == true ? "\uE769" : "\uE768";
        if (_mixer != null) for (var i = 0; i < _trackViews.Count; i++)
        {
            _trackViews[i].Meter.SetLevel(_mixer.IsPlaying ? _mixer.Channels[i].Peak : 0);
        }
        AutomationProperties.SetName(PlaybackButton, _mixer?.IsPlaying == true ? Tr("暂停") : Tr("播放"));
        var span = ViewDuration;
        if (_mixer?.IsPlaying == true && _timelineZoom > 1 && (_timelinePosition > _timelineStart + span || _timelinePosition < _timelineStart))
        {
            _timelineStart = Math.Clamp(_timelinePosition, 0, Math.Max(0, _timelineDuration - span)); UpdateTimelineViewport();
        }
        UpdatePlayheads();
    }
    private static string TimeLabel(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return time.TotalHours >= 1 ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds / 100}"
            : $"{(int)time.TotalMinutes:00}:{time.Seconds:00}.{time.Milliseconds / 100}";
    }
    private double ViewDuration => Math.Max(0.001, _timelineDuration / _timelineZoom);
    private void UpdatePlayheads()
    {
        PositionText.Text = $"{TimeLabel(_timelinePosition)} / {TimeLabel(_timelineDuration)}";
        foreach (var track in _trackViews) track.Wave.SetPosition(_timelinePosition);
    }
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => ZoomTimeline(2);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => ZoomTimeline(0.5);
    private void ZoomFit_Click(object sender, RoutedEventArgs e) { _timelineZoom = 1; _timelineStart = 0; UpdateTimelineViewport(); }
    private void NormalizeWaveform_Click(object sender, RoutedEventArgs e)
    {
        foreach (var track in _trackViews) { track.Wave.NormalizeDisplay = NormalizeWaveform.IsChecked == true; track.Wave.Redraw(); }
    }
    private void ZoomTimeline(double factor)
    {
        _timelineZoom = Math.Clamp(_timelineZoom * factor, 1, 128);
        _timelineStart = Math.Clamp(_timelinePosition - ViewDuration * 0.3, 0, Math.Max(0, _timelineDuration - ViewDuration));
        UpdateTimelineViewport();
    }
    private void TimelinePan_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updatingPan) return;
        _timelineStart = e.NewValue; UpdateTimelineViewport();
    }
    private void UpdateTimelineViewport()
    {
        _timelineStart = Math.Clamp(_timelineStart, 0, Math.Max(0, _timelineDuration - ViewDuration));
        foreach (var track in _trackViews)
        {
            track.Wave.ViewStart = _timelineStart; track.Wave.ViewDuration = ViewDuration; track.Wave.Redraw();
        }
        ZoomLabel.Text = $"{_timelineZoom:0}×";
        _updatingPan = true;
        TimelinePan.Maximum = Math.Max(0.001, _timelineDuration - ViewDuration); TimelinePan.Value = _timelineStart;
        TimelinePan.Visibility = _timelineZoom > 1 ? Visibility.Visible : Visibility.Collapsed;
        _updatingPan = false;
        DrawTimelineRuler(); UpdatePlayheads();
    }
    private void DrawTimelineRuler()
    {
        TimelineRuler.Children.Clear();
        var width = TimelineRuler.ActualWidth;
        if (width <= 0 || _timelineDuration <= 0) return;
        double[] steps = [0.01, 0.02, 0.05, 0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1800];
        var desired = ViewDuration / Math.Max(2, width / 100);
        var step = steps.FirstOrDefault(s => s >= desired, steps[^1]);
        var brush = new SolidColorBrush(Root.ActualTheme == ElementTheme.Dark ? Colors.LightGray : Colors.DimGray);
        for (var tick = Math.Ceiling(_timelineStart / step) * step; tick <= _timelineStart + ViewDuration + step / 100; tick += step)
        {
            var x = (tick - _timelineStart) / ViewDuration * width;
            TimelineRuler.Children.Add(new Line { X1 = x, X2 = x, Y1 = 21, Y2 = 27, Stroke = brush });
            var text = new TextBlock { Text = TimeLabel(tick), FontSize = 12, Foreground = brush };
            Canvas.SetLeft(text, Math.Clamp(x, 0, Math.Max(0, width - 64))); Canvas.SetTop(text, 0);
            TimelineRuler.Children.Add(text);
        }
    }
}
