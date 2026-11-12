using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Stemora.Core;
using static Stemora.Core.Strings;

namespace Stemora.App;

public sealed partial class MainWindow
{
    private Func<string> _runStatus = () => Tr("添加音频以开始");
    private Task _settingsSaveTask = Task.CompletedTask;
    private Task SaveSettingsInOrderAsync(AppSettings settings)
    {
        var previous = _settingsSaveTask;
        async Task WriteAsync()
        {
            // Each caller reports its own failure; a failed write must not block later saves.
            try { await previous; } catch { }
            await _paths.SaveSettingsAsync(settings);
        }
        return _settingsSaveTask = WriteAsync();
    }
    private void SetRunStatus(string message, params object?[] arguments)
    {
        _runStatus = () => Tr(message, arguments);
        RunStatus.Text = _runStatus();
    }
    private void RefreshLanguage()
    {
        Root.Language = Strings.Language;
        // WinUI caches the selected item's display separately from ComboBoxItem.Content.
        // Refresh selection without firing configuration, filter or persistence handlers.
        var wasReady = _ready;
        _ready = false;
        try
        {
            foreach (var combo in new[] { QuantFilter, TrackFilter, PlatformFilter, OutputFilter,
                ArchitectureFilter, ExportFilter, PurposeFilter, MuScriptorDeviceCombo, DeviceCombo, FormatCombo, ThemeCombo, LanguageCombo })
            {
                var selected = combo.SelectedIndex;
                if (selected < 0) continue;
                combo.SelectedIndex = -1;
                combo.SelectedIndex = selected;
            }
        }
        finally { _ready = wasReady; }
        if (Navigation.SettingsItem is NavigationViewItem settingsItem) settingsItem.Content = Tr("设置");
        foreach (var job in _jobs) job.RefreshLanguage();
        foreach (var row in _catalog) row.RefreshLanguage();
        UpdateQueueState();
        RunStatus.Text = _runStatus();
        DataPathLabel.Text = Tr("数据目录：{0}", _paths.Root);
        UpdateEngineStatus(); UpdateModelSummary();
        foreach (var ui in _trackViews)
        {
            var label = Strings.StemLabel(ui.Track.Id, ui.Track.Label);
            ui.Label.Text = label;
            AutomationProperties.SetName(ui.Mute, Tr("{0} 静音", label));
            AutomationProperties.SetName(ui.Solo, Tr("{0} 独听", label));
            AutomationProperties.SetName(ui.Volume, Tr("{0} 音量", label));
            ToolTipService.SetToolTip(ui.Mute, Tr("静音 (Mute)"));
            ToolTipService.SetToolTip(ui.Solo, Tr("独听 (Solo)"));
            UpdateTrackDetails(ui);
        }
        UpdatePlaybackPosition();
        if (_ready) FilterModels();
    }
    private void UpdateModelSummary()
    {
        if (_selectedDescriptor is not { } descriptor) return;
        ModelStemSummary.Text = Tr("{0} ({1} 轨)", GgufInspector.FamilyLabel(descriptor.Family), descriptor.Stems.Count)
            + "\n" + string.Join(" / ", descriptor.Stems.Select(s => Strings.StemLabel(s.Id, s.Label)));
        ToolTipService.SetToolTip(ResidualCheck, descriptor.IsMultiStem
            ? Tr("多轨模型直接导出每种乐器，不额外生成首轨残差。") : Tr("原音频减去模型首个输出。"));
    }
    private void UpdateTrackDetails(TrackUi ui)
    {
        if (ui.Wave.Data is not { } data) { ui.PeakLabel.Text = Tr("分析中"); return; }
        ui.PeakLabel.Text = Tr("峰值 {0}", WaveformData.Decibels(data.Peak));
        ToolTipService.SetToolTip(ui.PeakLabel, Tr("采样峰值 {0} (均方根 {1})", WaveformData.Decibels(data.Peak), WaveformData.Decibels(data.Rms)));
        TimelineDetails.Text = Tr("{0:N0} Hz ({1} 声道，{2} 轨)", data.SampleRate, data.Channels, _trackViews.Count);
    }
    private async void Language_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        var language = LanguageCombo.SelectedIndex switch { 1 => "en-US", 2 => "zh-CN", _ => null };
        Strings.SetLanguage(language);
        RefreshLanguage();
        // Persist the language without implicitly committing partially edited advanced parameters.
        _settings = _settings with { Language = language };
        try { await SaveSettingsInOrderAsync(_settings); }
        catch (Exception error) { ShowError(error); }
    }
}
