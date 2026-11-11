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
            selected.Status = $"已完成 ({existing.ElapsedSeconds:F1} 秒)";
            UpdateQueueState(); RunStatus.Text = "已完成 1 个文件" + (existing.Backend == null ? "" : " (" + existing.Backend + ")");
            AppWindow.Resize(new Windows.Graphics.SizeInt32(1480, 1100));
            SetupExpander.IsExpanded = false;
            await LoadTimelineAsync(selected);
            await CaptureAsync(folder, "09-gui-multitrack.png");
            _trackViews[1].Solo.IsChecked = true; _mixer?.SetSolo(1, true); UpdateTrackEmphasis();
            await CaptureAsync(folder, "14-gui-solo.png");
            _trackViews[1].Solo.IsChecked = false; _mixer?.SetSolo(1, false); UpdateTrackEmphasis();
            ZoomTimeline(4); SeekTimeline(Math.Min(2, _timelineDuration / 2), false);
            await CaptureAsync(folder, "15-gui-zoom.png");
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
