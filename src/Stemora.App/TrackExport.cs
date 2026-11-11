using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Stemora.Core;
using Windows.Storage.Pickers;

namespace Stemora.App;

public sealed partial class MainWindow
{
    private CancellationTokenSource? _exportCancellation;

    private async void ExportTracks_Click(object sender, RoutedEventArgs e)
    {
        if (_exportCancellation != null || QueueList.SelectedItem is not AudioJob { Result: { } result }) return;
        var tracks = result.Tracks is { Count: > 0 } ? result.Tracks.ToList()
            : result.StemPaths.Select((path, i) => new StemResult(i == 0 ? "primary" : $"stem_{i}",
                i == 0 ? "主音轨" : $"音轨 {i + 1}", path)).ToList();
        if (result.ResidualPath is { } residual && !tracks.Any(track => track.Path.Equals(residual, StringComparison.OrdinalIgnoreCase)))
            tracks.Add(new("residual", "残差伴奏", residual));
        if (tracks.Count == 0) return;

        using var cancellation = new CancellationTokenSource();
        _exportCancellation = cancellation;
        ExportTracksButton.IsEnabled = false;
        try
        {
            var format = new ComboBox
            {
                Header = "导出格式",
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            foreach (var label in new[] { "WAV · 32 位浮点", "WAV · 24 位 PCM", "WAV · 16 位 PCM", "MP3 · 320 kbps" })
                format.Items.Add(new ComboBoxItem { Content = label });
            format.SelectedIndex = (int)_settings.Encoding;
            var content = new StackPanel { Spacing = 12, MinWidth = 300 };
            content.Children.Add(new TextBlock { Text = "选择要导出的音轨" });
            var choices = tracks.Select(track => new CheckBox { Content = track.Label, IsChecked = true }).ToArray();
            var list = new StackPanel { Spacing = 4 };
            foreach (var choice in choices) list.Children.Add(choice);
            content.Children.Add(new ScrollViewer { Content = list, MaxHeight = 260 });
            content.Children.Add(format);
            content.Children.Add(new TextBlock
            {
                Text = "导出完整分离音轨，试听的静音、独听和音量不影响文件。",
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 380,
                Opacity = 0.7
            });
            var dialog = new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                Title = "导出音轨",
                Content = content,
                PrimaryButtonText = "选择保存位置",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary
            };
            foreach (var choice in choices)
            {
                choice.Checked += (_, _) => dialog.IsPrimaryButtonEnabled = choices.Any(item => item.IsChecked == true);
                choice.Unchecked += (_, _) => dialog.IsPrimaryButtonEnabled = choices.Any(item => item.IsChecked == true);
            }
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            var selected = tracks.Where((_, i) => choices[i].IsChecked == true).ToArray();
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.MusicLibrary };
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var folder = await picker.PickSingleFolderAsync();
            if (folder == null || _closed) return;
            var name = System.IO.Path.GetFileName(result.OutputDirectory.TrimEnd(System.IO.Path.DirectorySeparatorChar));
            if (name.Length > 70) name = name[..70];
            var destination = System.IO.Path.Combine(folder.Path, $"{name}_export_{Guid.NewGuid().ToString("N")[..8]}");
            var mp3 = format.SelectedIndex == 3;
            var encoding = mp3 ? WavEncoding.Pcm16 : (WavEncoding)format.SelectedIndex;
            ExportStatus.Visibility = Visibility.Visible;
            CancelExportButton.Visibility = Visibility.Visible;
            var progress = new Progress<SeparationProgress>(value =>
            {
                if (!_closed && _exportCancellation == cancellation) ExportStatus.Text = value.Message;
            });
            await AudioExporter.ExportAsync(selected, destination, mp3 ? AudioExportFormat.Mp3 : AudioExportFormat.Wav,
                encoding, progress, cancellation.Token);
            ShowNotice("音轨已导出", $"{selected.Length} 条 {(mp3 ? "MP3" : "WAV")} 音轨已保存到 {destination}", InfoBarSeverity.Success);
        }
        catch (OperationCanceledException) { ShowNotice("导出已取消", "可以重新选择音轨和格式后导出。", InfoBarSeverity.Informational); }
        catch (Exception error) { ShowError(error); }
        finally
        {
            _exportCancellation = null;
            if (!_closed)
            {
                ExportStatus.Visibility = Visibility.Collapsed;
                CancelExportButton.Visibility = Visibility.Collapsed;
                ExportTracksButton.IsEnabled = (QueueList.SelectedItem as AudioJob)?.Result != null;
            }
        }
    }

    private void CancelExport_Click(object sender, RoutedEventArgs e) => _exportCancellation?.Cancel();
}
