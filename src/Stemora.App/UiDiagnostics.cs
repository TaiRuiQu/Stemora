using static Stemora.Core.Strings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using System.Text.Json;
using Stemora.Core;

namespace Stemora.App;

public sealed partial class MainWindow
{
    // Explicit diagnostic mode. Captures only this app's own XAML visual tree, never the desktop.
    internal async Task CaptureUiAsync(string folder, string? input, bool run, string? model = null, string? resultFile = null)
    {
        folder = System.IO.Path.GetFullPath(folder);
        Directory.CreateDirectory(folder);
        if (!Root.IsLoaded)
        {
            var loaded = new TaskCompletionSource();
            void OnLoaded(object sender, RoutedEventArgs args) { Root.Loaded -= OnLoaded; loaded.TrySetResult(); }
            Root.Loaded += OnLoaded;
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(20));
        }
        Root.RequestedTheme = ElementTheme.Light;
        var originalLanguage = Strings.Language;
        var originalPreference = LanguageCombo.SelectedIndex;
        LanguageCombo.SelectedIndex = 1;
        await _settingsSaveTask;
        if (_paths.LoadSettings().Language != "en-US") throw new InvalidOperationException("English preference was not saved.");
        await CaptureAsync(folder, "17-separation-english.png");
        if ((Navigation.MenuItems[0] as Microsoft.UI.Xaml.Controls.NavigationViewItem)?.Content?.ToString() != "Audio separation")
            throw new InvalidOperationException("The English navigation resource did not update.");
        Navigation.SelectedItem = Navigation.MenuItems[1];
        await CaptureAsync(folder, "18-models-english.png");
        VerifyLocalizedFilterSelections();
        QuantFilter.SelectedIndex = 5; OutputFilter.SelectedIndex = 2; TrackFilter.SelectedIndex = 2;
        if (CatalogList.ItemsSource is not List<ModelRow> { Count: 1 } filtered || filtered[0].Model.Id != "becruily-q4_0")
            throw new InvalidOperationException("Combined model filters returned unexpected results.");
        await CaptureAsync(folder, "19-models-filtered-english.png");
        QuantFilter.SelectedIndex = OutputFilter.SelectedIndex = TrackFilter.SelectedIndex = 0;
        Navigation.SelectedItem = Navigation.SettingsItem;
        await CaptureAsync(folder, "20-settings-english.png");
        LanguageCombo.SelectedIndex = 2;
        await _settingsSaveTask;
        if (_paths.LoadSettings().Language != "zh-CN") throw new InvalidOperationException("Chinese preference was not saved.");
        if ((Navigation.MenuItems[0] as Microsoft.UI.Xaml.Controls.NavigationViewItem)?.Content?.ToString() != "音频分离")
            throw new InvalidOperationException("The Chinese navigation resource did not update.");
        await CaptureAsync(folder, "21-settings-chinese.png");
        Navigation.SelectedItem = Navigation.MenuItems[1];
        await CaptureAsync(folder, "26-models-chinese.png");
        VerifyLocalizedFilterSelections();
        Navigation.SelectedItem = Navigation.SettingsItem;
        LanguageCombo.SelectedIndex = originalPreference;
        await _settingsSaveTask;
        Strings.SetLanguage(originalLanguage); RefreshLanguage();
        Navigation.SelectedItem = Navigation.MenuItems[0];
        if (model != null) RefreshModels(System.IO.Path.GetFullPath(model));
        await CaptureAsync(folder, "01-separation-empty.png");
        if (input != null)
        {
            AddFiles([input]);
            while (_timelineCancellation != null) await Task.Delay(100);
            await CaptureAsync(folder, "02-separation-audio.png");
            AdvancedOptions.IsExpanded = true;
            await CaptureAsync(folder, "13-advanced-options.png");
            AdvancedOptions.IsExpanded = false;
        }
        Navigation.SelectedItem = Navigation.MenuItems[1];
        await CaptureAsync(folder, "03-models.png");
        Navigation.SelectedItem = Navigation.SettingsItem;
        await CaptureAsync(folder, "04-settings.png");
        Root.RequestedTheme = ElementTheme.Dark;
        Navigation.SelectedItem = Navigation.MenuItems[0];
        await CaptureAsync(folder, "05-separation-dark.png");
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1000, 900));
        Root.RequestedTheme = ElementTheme.Light;
        await CaptureAsync(folder, "06-compact.png");
        ContentScroll.ChangeView(null, ContentScroll.ScrollableHeight, null, true);
        await CaptureAsync(folder, "07-compact-settings.png");
        Navigation.SelectedItem = Navigation.MenuItems[1];
        await CaptureAsync(folder, "11-compact-models.png");
        Navigation.SelectedItem = Navigation.SettingsItem;
        await CaptureAsync(folder, "12-compact-preferences.png");
        Navigation.SelectedItem = Navigation.MenuItems[0];
        if (resultFile != null && input != null)
        {
            var existing = JsonSerializer.Deserialize<SeparationResult>(await File.ReadAllTextAsync(resultFile), JsonDefaults.Options)
                ?? throw new InvalidDataException("Invalid diagnostic result.");
            var selected = _jobs.First();
            selected.Result = existing;
            selected.SetStatus("已完成 ({0:F1} 秒)", existing.ElapsedSeconds);
            UpdateQueueState();
            _runStatus = () => Tr("已完成 {0} 个文件", 1) + (existing.Backend == null ? "" : " (" + existing.Backend + ")");
            RunStatus.Text = _runStatus();
            AppWindow.Resize(new Windows.Graphics.SizeInt32(1480, 1100));
            SetupExpander.IsExpanded = false;
            await LoadTimelineAsync(selected);
            await CaptureAsync(folder, "09-gui-multitrack.png");
            _trackViews[1].Solo.IsChecked = true; _mixer?.SetSolo(1, true); UpdateTrackEmphasis();
            await CaptureAsync(folder, "14-gui-solo.png");
            _trackViews[1].Solo.IsChecked = false; _mixer?.SetSolo(1, false); UpdateTrackEmphasis();
            ZoomTimeline(4); SeekTimeline(Math.Min(2, _timelineDuration / 2), false);
            await CaptureAsync(folder, "15-gui-zoom.png");
            _timelineTimer?.Stop();
            double[] levels = [0, 0.0000001, 0.001, 0.05, 0.25, 0.75, 1];
            for (var i = 0; i < _trackViews.Count; i++)
            {
                var meter = _trackViews[i].Meter;
                foreach (var invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -1d })
                {
                    meter.SetLevel(invalid);
                    if (meter.FillWidth != 0) throw new InvalidOperationException("An invalid meter level left a visible fill.");
                }
                meter.SetLevel(levels[i % levels.Length]);
                if (meter.FillWidth < 0 || meter.FillWidth > meter.ActualWidth || !double.IsFinite(meter.FillWidth))
                    throw new InvalidOperationException("Meter fill escaped its layout bounds.");
            }
            await CaptureAsync(folder, "22-meter-levels.png");
            ContentScroll.ChangeView(null, ContentScroll.ScrollableHeight, null, true);
            await CaptureAsync(folder, "23-scroll-gutter.png");
            var right = TracksPanel.TransformToVisual(ContentScroll).TransformPoint(new Windows.Foundation.Point(TracksPanel.ActualWidth, 0)).X;
            if (ContentScroll.ActualWidth - right < 23)
                throw new InvalidOperationException("The scroll bar gutter overlaps track content.");
            Strings.SetLanguage("en-US"); RefreshLanguage();
            await CaptureAsync(folder, "24-tracks-english.png");
            Strings.SetLanguage("zh-CN"); RefreshLanguage();
            await CaptureAsync(folder, "25-tracks-chinese.png");
            _timelineTimer?.Start();
        }
        if (run && input != null)
        {
            AppWindow.Resize(new Windows.Graphics.SizeInt32(1480, 1100));
            ContentScroll.ChangeView(null, 0, null, true);
            var originalOutput = OutputBox.Text;
            OutputBox.Text = System.IO.Path.Combine(folder, "inference-output");
            var inference = RunQueueAsync();
            await CaptureAsync(folder, "10-gui-inference-running.png");
            await inference;
            if (_jobs.FirstOrDefault()?.Result == null) throw new InvalidOperationException("GUI inference did not complete. " + LogText.Text);
            await CaptureAsync(folder, "08-gui-inference-completed.png");
            while (_timelineCancellation != null) await Task.Delay(100);
            await CaptureAsync(folder, "09-gui-multitrack.png");
            if (_trackViews.Count > 2)
            {
                _trackViews[1].Solo.IsChecked = true;
                _mixer?.SetSolo(1, true); UpdateTrackEmphasis();
                await CaptureAsync(folder, "14-gui-solo.png");
                _trackViews[1].Solo.IsChecked = false;
                _mixer?.SetSolo(1, false); UpdateTrackEmphasis();
                ZoomTimeline(4);
                SeekTimeline(Math.Min(0.5, _timelineDuration), false);
                await CaptureAsync(folder, "15-gui-zoom.png");
                if (_mixer != null)
                {
                    _mixer.Play(); await Task.Delay(350); _mixer.Pause();
                    if (_mixer.Position.TotalSeconds < 0.1) throw new InvalidOperationException("Audio device clock did not advance.");
                    UpdatePlaybackPosition();
                    await CaptureAsync(folder, "16-gui-playback.png");
                }
            }
            OutputBox.Text = originalOutput;
            await PersistSettingsAsync();
        }
        await File.WriteAllTextAsync(System.IO.Path.Combine(folder, "capture-complete.txt"), "WinUI capture completed successfully.\n");
        await File.WriteAllTextAsync(System.IO.Path.Combine(folder, "capture-environment.json"),
            JsonSerializer.Serialize(new
            {
                dataRoot = _paths.Root,
                language = _paths.LoadSettings().Language,
                engine = new EngineManager(_paths).Find(),
                model = _selectedDescriptor?.Name
            }, JsonDefaults.Options));
    }
    private void VerifyLocalizedFilterSelections()
    {
        static bool DisplaysText(DependencyObject element, string expected)
        {
            if (element is Microsoft.UI.Xaml.Controls.TextBlock { Text: var text } && text == expected) return true;
            for (var i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(element); i++)
                if (DisplaysText(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(element, i), expected)) return true;
            return false;
        }
        foreach (var combo in new[] { QuantFilter, TrackFilter, PlatformFilter, OutputFilter, ArchitectureFilter, ExportFilter })
        {
            var expected = (combo.SelectedItem as Microsoft.UI.Xaml.Controls.ComboBoxItem)?.Content?.ToString();
            if (expected == null || !DisplaysText(combo, expected))
                throw new InvalidOperationException("A selected filter label did not update after changing language.");
        }
    }
    private async Task CaptureAsync(string folder, string name)
    {
        // Let layout, composition, and media-control initialization finish without blocking the UI thread.
        await Task.Delay(700);
        Root.UpdateLayout();
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(Root);
        var pixels = await bitmap.GetPixelsAsync();
        using var reader = DataReader.FromBuffer(pixels);
        var bytes = new byte[pixels.Length]; reader.ReadBytes(bytes);
        var file = await StorageFile.GetFileFromPathAsync(CreateFile(System.IO.Path.Combine(folder, name)));
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, bytes);
        await encoder.FlushAsync();
    }
    private static string CreateFile(string path) { File.WriteAllBytes(path, []); return path; }
}
