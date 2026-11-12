using System.Diagnostics;
using Microsoft.UI.Xaml;
using Stemora.Core;
using static Stemora.Core.Strings;

namespace Stemora.App;

public sealed partial class MainWindow
{
    private readonly Stopwatch _downloadElapsed = new();
    private readonly Queue<string> _downloadLogs = new();
    private DispatcherTimer? _downloadTimer;

    private void StartDownloadStatus(string name, bool transcription)
    {
        _downloadLogs.Clear(); DownloadLogText.Text = "";
        DownloadLabel.Text = Tr("正在准备下载：{0}", name);
        DownloadDetail.Text = transcription ? Tr("首次安装会下载 Python 和 PyTorch，可能需要几分钟。下面会显示当前步骤和下载详情。") : "";
        DownloadBar.IsIndeterminate = true; DownloadBar.Value = 0;
        CancelModelDownloadButton.IsEnabled = true;
        _downloadElapsed.Restart();
        _downloadTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _downloadTimer.Tick -= DownloadTimer_Tick; _downloadTimer.Tick += DownloadTimer_Tick;
        DownloadTimer_Tick(null, null); _downloadTimer.Start();
        DownloadPanel.Visibility = Visibility.Visible;
        DispatcherQueue.TryEnqueue(() => DownloadPanel.StartBringIntoView());
    }
    private void DownloadTimer_Tick(object? sender, object? args) => DownloadElapsed.Text = Tr("已用时 {0}", _downloadElapsed.Elapsed.ToString(@"mm\:ss"));
    private void UpdateModelDownload(string name, SeparationProgress progress)
    {
        if (progress.Message.Length > 0)
        {
            AddLog(progress.Message);
            _downloadLogs.Enqueue($"{DateTime.Now:HH:mm:ss}  {progress.Message}");
            while (_downloadLogs.Count > 60) _downloadLogs.Dequeue();
            DownloadLogText.Text = string.Join(Environment.NewLine, _downloadLogs);
            DownloadDetail.Text = progress.Message;
        }
        if (progress.Stage == "log") return;
        DownloadLabel.Text = name + ": " + MuScriptorProgress.Label(progress.Stage);
        DownloadBar.IsIndeterminate = progress.Fraction == null;
        if (progress.Fraction is { } fraction) DownloadBar.Value = fraction * 100;
    }
    private void StopDownloadStatus(bool completed)
    {
        _downloadTimer?.Stop(); _downloadElapsed.Stop();
        CancelModelDownloadButton.IsEnabled = false;
        DownloadBar.IsIndeterminate = false;
        DownloadPanel.Visibility = completed ? Visibility.Collapsed : Visibility.Visible;
    }
}
