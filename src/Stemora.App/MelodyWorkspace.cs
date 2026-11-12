using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Stemora.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using static Stemora.Core.Strings;

namespace Stemora.App;

public sealed class MelodyWorkspace : Grid, IDisposable
{
    private readonly AppPaths _paths;
    private readonly MuScriptorService _service;
    private readonly MelodyDraftStore _drafts;
    private readonly Func<string, IProgress<SeparationProgress>, CancellationToken, Task<string>> _extractVocal;
    private readonly Func<IEnumerable<string>, FileOpenPicker> _picker;
    private readonly Func<bool> _otherInferenceBusy;
    private readonly nint _window;
    private readonly TextBlock _file = new() { FontSize = 16, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _save = new() { FontSize = 12, Opacity = 0.65, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 1) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _modelDetails = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.75 };
    private readonly TextBlock _selection = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.7 };
    private readonly InfoBar _notice = new() { IsClosable = true };
    private readonly ProgressBar _progress = new() { Visibility = Visibility.Collapsed };
    private readonly StackPanel _empty = new() { Spacing = 16, Padding = new Thickness(32, 60, 32, 60), HorizontalAlignment = HorizontalAlignment.Center };
    private readonly StackPanel _ready = new() { Spacing = 16, Visibility = Visibility.Collapsed };
    private readonly Grid _editing = new() { Visibility = Visibility.Collapsed };
    private readonly StackPanel _score = new() { Spacing = 12, Visibility = Visibility.Collapsed };
    private readonly StackPanel _scoreBars = new() { Spacing = 16 };
    private readonly TextBlock _scorePageText = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _previousScorePage;
    private readonly Button _nextScorePage;
    private int _scorePage;
    private const int ScoreBarsPerPage = 8;
    private readonly StackPanel _body = new() { Spacing = 16, Padding = new Thickness(0, 0, 24, 16) };
    private readonly StackPanel _fileSettings = new() { Spacing = 16 };
    private readonly Flyout _fileFlyout = new();
    private readonly Flyout _editFlyout = new();
    private readonly StackPanel _editOptions = new() { Spacing = 16, MaxWidth = 620 };
    private readonly Button _settingsButton;
    private readonly CommandBar _tools;
    private ScrollViewer? _bodyScroll;
    private readonly PianoRollView _roll = new();
    private readonly WaveformView _wave = new() { Height = 40 };
    private readonly Button _run;
    private readonly Button _cancel;
    private readonly Button _manageModels;
    private readonly Button _play;
    private readonly Button _undo;
    private readonly Button _redo;
    private readonly Button _preview;
    private readonly Button _add;
    private readonly Button _open;
    private readonly Button _relocate;
    private readonly Button _chooseMix;
    private readonly ComboBox _model = new() { MinWidth = 140, SelectedIndex = -1 };
    private readonly ComboBox _device = new() { MinWidth = 160 };
    private readonly ToggleButton _currentSource = new();
    private readonly ToggleButton _togetherSource = new();
    private readonly ToggleButton _vocalSource = new();
    private readonly ToggleButton _mixSource = new();
    private readonly ToggleButton _mixTogetherSource = new();
    private readonly TextBlock _sourceVolumeLabel = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _monitorBar = new() { Spacing = 8 };
    private readonly StackPanel _editorHeader = new() { Spacing = 8 };
    private readonly ComboBox _snap = new() { MinWidth = 100 };
    private MelodyListenMode _sourceMode = MelodyListenMode.Together;
    private readonly Slider _pianoVolume = new() { Minimum = 0, Maximum = 100, Value = 80, Width = 140 };
    private readonly Slider _vocalVolume = new() { Minimum = 0, Maximum = 100, Value = 80, Width = 140 };
    private readonly MelodyAudition _notePlayer = new();
    public event Action<string>? ModelLibraryRequested;
    public event Action? ModelAvailabilityChanged;
    private readonly CheckBox _vocal = new();
    private readonly CheckBox _range = new();
    private readonly CheckBox _loop = new();
    private readonly CheckBox _audition = new();
    private readonly CheckBox _rhythm = new();
    private readonly NumberBox _rangeStart = new() { Minimum = 0, Value = 0, Width = 150 };
    private readonly NumberBox _rangeEnd = new() { Minimum = 0, Value = 30, Width = 150 };
    private readonly NumberBox _bpm = new() { Minimum = 20, Maximum = 400, Value = 120, Width = 120 };
    private readonly NumberBox _beats = new() { Minimum = 2, Maximum = 12, Value = 4, Width = 120, SmallChange = 1 };
    private readonly NumberBox _firstBeat = new() { Minimum = 0, Value = 0, Width = 150 };
    private readonly ComboBox _key = new() { ItemsSource = MelodyScore.KeyOptions, SelectedIndex = 0, Width = 100 };
    private readonly Slider _pan = new() { Minimum = 0, Maximum = 1, Height = 24, MinHeight = 24 };
    private readonly TextBlock _time = new() { VerticalAlignment = VerticalAlignment.Center, FontFamily = new FontFamily("Consolas"), Text = "00:00.0 / 00:00.0" };
    private readonly TextBox _log = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Width = 580, MaxHeight = 300 };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly DispatcherTimer _autosave = new() { Interval = TimeSpan.FromMilliseconds(650) };
    private readonly MelodyPlayback _player = new();
    private readonly Queue<string> _logs = new();
    private readonly List<Action> _languageRefresh = [];
    private Func<string> _statusText = () => "";
    private MelodyDocument? _document;
    private CancellationTokenSource? _cancellation;
    private bool _updating, _loopPlaying, _transportRunning, _disposed, _dirty;
    private int _loadVersion;
    private int _saveVersion;
    private string? _lastStage;
    private double _playEnd;
    private static Button Button(string text, RoutedEventHandler action, bool accent = false)
    {
        var button = new Button(); Loc.SetContent(button, text); Loc.SetName(button, text); button.Click += action;
        if (accent) button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        return button;
    }
    private static TextBlock Text(string text, int size = 14)
    {
        var block = new TextBlock { FontSize = size, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center }; Loc.SetText(block, text); return block;
    }
    private static StackPanel Row(params UIElement[] children)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var child in children) panel.Children.Add(child); return panel;
    }
    private static void Content(CheckBox box, string text) { Loc.SetContent(box, text); Loc.SetName(box, text); }
    public MelodyWorkspace(AppPaths paths, nint window, Func<IEnumerable<string>, FileOpenPicker> picker,
        Func<string, IProgress<SeparationProgress>, CancellationToken, Task<string>> extractVocal, Func<bool> otherInferenceBusy)
    {
        _paths = paths; _window = window; _picker = picker; _extractVocal = extractVocal; _otherInferenceBusy = otherInferenceBusy;
        _service = new(paths); _drafts = new(paths);
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowSpacing = 16;
        _add = Button("添加音频", Add_Click); _open = Button("打开草稿", Open_Click);
        _relocate = Button("重新定位音频", Relocate_Click);
        _chooseMix = Button("选择原曲", ChooseMix_Click);
        Loc.SetToolTip(_chooseMix, "选择与人声时长、起点一致的原曲，可同步试听。");
        _run = Button("识别旋律", Run_Click, true); _cancel = Button("取消", (_, _) => _cancellation?.Cancel()); _cancel.Visibility = Visibility.Collapsed;
        _manageModels = Button("管理模型", (_, _) => { _fileFlyout.Hide(); ModelLibraryRequested?.Invoke(Variant); });
        _play = Button("播放", Play_Click);
        _undo = Button("撤销", (_, _) => Edit(e => e.Undo())); _redo = Button("重做", (_, _) => Edit(e => e.Redo()));
        Loc.SetToolTip(_undo, "撤销（Ctrl+Z）"); Loc.SetToolTip(_redo, "重做（Ctrl+Y / Ctrl+Shift+Z）");
        _preview = Button("预览简谱", (_, _) => ShowScore(true), true);
        var header = new Grid { ColumnSpacing = 16 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new StackPanel { Spacing = 4 }; title.Children.Add(Text("旋律转谱", 28)); title.Children.Add(Row(_file, _save)); header.Children.Add(title);
        _settingsButton = Button("文件与模型", (_, _) => { }); _settingsButton.Flyout = _fileFlyout; _settingsButton.Visibility = Visibility.Collapsed;
        _settingsButton.Content = new FontIcon { Glyph = "\uE713", FontSize = 16 }; _settingsButton.Width = 36; Loc.SetToolTip(_settingsButton, "文件与模型");
        var actions = Row(_undo, _redo, _settingsButton, _preview); actions.VerticalAlignment = VerticalAlignment.Center; SetColumn(actions, 1); header.Children.Add(actions); Children.Add(header);
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var body = _body; scroll.Content = body; _bodyScroll = scroll; SetRow(scroll, 1); Children.Add(scroll);
        scroll.SizeChanged += (_, _) => ResizeEditor();
        body.Children.Add(_notice);
        _empty.Children.Add(Text("把歌曲里的人声变成可编辑的旋律", 24)); _empty.Children.Add(Text("拖放音频到此处，或点击添加音频。所有音频均在本机处理。"));
        body.Children.Add(_empty);
        var imports = Row(_add, _open, Button("保存草稿", async (_, _) => await SaveAsync()), _relocate); _fileSettings.Children.Add(imports); body.Children.Add(_fileSettings);
        Content(_vocal, "这是纯人声"); Content(_range, "只做一段"); Content(_loop, "循环选段");
        Content(_audition, "编辑时发声"); _audition.IsChecked = true; Content(_rhythm, "已确认节奏设置");
        Loc.SetHeader(_rangeStart, "开始（秒）"); Loc.SetHeader(_rangeEnd, "结束（秒）");
        _ready.Children.Add(_wave); _fileSettings.Children.Add(Row(_vocal, _range)); _fileSettings.Children.Add(Row(_rangeStart, _rangeEnd));
        _fileSettings.Children.Add(Text("可先选择主唱清楚的 15–30 秒；默认处理整首。", 12));
        body.Children.Add(_ready);
        var setup = new StackPanel { Spacing = 12 };
        foreach (var model in ModelRepository.Catalog.Where(model => model.IsTranscription)) _model.Items.Add(new ComboBoxItem { Content = model.DisplayName, Tag = model.Variant });
        foreach (var label in new[] { "自动（优先 CUDA）", "仅 CPU", "CUDA" }) { var item = new ComboBoxItem(); Loc.SetContent(item, label); _device.Items.Add(item); }
        _device.SelectedIndex = 0; _model.SelectedIndex = 0;
        setup.Children.Add(Row(_model, _device, _manageModels)); setup.Children.Add(_modelDetails);
        setup.Children.Add(Button("选择原曲", ChooseMix_Click));
        _fileSettings.Children.Add(setup);
        foreach (var label in new[] { "自由移动", "吸附 1/8", "吸附 1/16" }) { var item = new ComboBoxItem(); Loc.SetContent(item, label); _snap.Items.Add(item); }
        _snap.SelectedIndex = 0; Loc.SetName(_snap, "音符吸附"); Loc.SetToolTip(_snap, "吸附使用估计节拍；按住 Alt 可临时自由移动。");
        _snap.SelectionChanged += (_, _) => { _roll.SnapDivisions = _snap.SelectedIndex == 1 ? 2 : _snap.SelectedIndex == 2 ? 4 : 0; _roll.Redraw(); };
        body.Children.Add(_editorHeader);
        _editing.Children.Add(new Border { Child = _roll, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 60, 73, 91)) });
        body.Children.Add(_editing); body.Children.Add(_pan);
        var tools = _tools = new CommandBar { DefaultLabelPosition = CommandBarDefaultLabelPosition.Right, IsOpen = false, OverflowButtonVisibility = CommandBarOverflowButtonVisibility.Auto, HorizontalContentAlignment = HorizontalAlignment.Left, VerticalContentAlignment = VerticalAlignment.Center, Content = Row(_snap, _audition) };
        void Tool(string label, Action action, bool secondary = false, string? shortcut = null)
        {
            var item = new AppBarButton { Label = Tr(label), Icon = new FontIcon { Glyph = label switch { "调高一点" => "\uE74A", "调低一点" => "\uE74B", "删除" => "\uE74D", "拆分" => "\uE8C6", "合并" => "\uE8D5", "长一点" => "\uE76C", "短一点" => "\uE76B", _ => "\uE70F" } } };
            AutomationProperties.SetName(item, Tr(label)); item.Click += (_, _) => action();
            if (secondary) tools.SecondaryCommands.Add(item); else tools.PrimaryCommands.Add(item);
            void RefreshTool()
            {
                item.Label = Tr(label); AutomationProperties.SetName(item, Tr(label));
                if (shortcut != null) ToolTipService.SetToolTip(item, $"{Tr(label)} ({shortcut})");
            }
            RefreshTool(); _languageRefresh.Add(RefreshTool);
        }
        Tool("调高一点", () => Edit(e => e.Transform(semitones: 1)), shortcut: "↑"); Tool("调低一点", () => Edit(e => e.Transform(semitones: -1)), shortcut: "↓");
        Tool("长一点", () => _roll.NudgeTime(1, true), shortcut: "Shift+→"); Tool("短一点", () => _roll.NudgeTime(-1, true), shortcut: "Shift+←");
        Tool("拆分", () => Edit(e => e.Split(_document!.Position)), shortcut: "S"); Tool("合并", Merge, shortcut: "M");
        Tool("删除", () => Edit(e => e.Delete()), shortcut: "Del / Backspace");
        Tool("添加音符", () => Edit(e => e.Add(_document!.Position, Math.Min(_document.Duration, _document.Position + 0.3), 60)), true);
        Tool("提前", () => _roll.NudgeTime(-1), true, "←"); Tool("推后", () => _roll.NudgeTime(1), true, "→");
        Tool("放大选中", _roll.FocusSelection, true, "F"); Tool("已核对", () => Edit(e => e.MarkReviewed()), true);
        Tool("下一处待核对", NextReview, true);
        Tool("缩小", () => _roll.Zoom(1.5), true); Tool("放大", () => _roll.Zoom(0.666666), true);
        Tool("适应音域", () => { _roll.FitPitch(); _roll.Redraw(); }, true);
        var exactStart = new NumberBox { Minimum = 0, Width = 160 }; Loc.SetHeader(exactStart, "开始（秒）");
        var exactEnd = new NumberBox { Minimum = 0, Width = 160 }; Loc.SetHeader(exactEnd, "结束（秒）");
        var exact = new Expander
        {
            Content = Row(exactStart, exactEnd, Button("应用起止时间", (_, _) =>
        {
            if (_document?.Selection.Length != 1) return;
            var note = _document.Notes.First(n => n.Id == _document.Selection[0]);
            if (!double.IsFinite(exactStart.Value) || !double.IsFinite(exactEnd.Value) || exactEnd.Value <= exactStart.Value) { ShowError(new ArgumentException(Tr("选段起止时间无效。"))); return; }
            var start = exactStart.Value; var end = exactEnd.Value;
            Edit(editor =>
            {
                if (_document.Notes.Any(n => n.Id != note.Id && start < n.End && end > n.Start)) throw new InvalidOperationException(Tr("已到相邻音符边界。"));
                editor.Commit(_document.Notes.Select(n => n.Id == note.Id ? n with { Start = start, End = end, Edited = true } : n));
            });
        })),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        Loc.SetHeader(exact, "精确修改时间"); _editOptions.Children.Add(exact);
        _roll.Changed += changed =>
        {
            if (_document?.Selection.Length == 1) { var note = _document.Notes.First(n => n.Id == _document.Selection[0]); exactStart.Value = note.Start; exactEnd.Value = note.End; }
            RefreshState(); ScheduleSave();
            if (changed) UpdateEditedAudio();
        };
        _roll.Audition += AuditionPitch;
        _roll.PreviewChanged += notes => { if (_document != null) _player.UpdateNotes(notes ?? _document.Notes); };
        _roll.Seek += Seek; _roll.Status += message => _status.Text = message;
        _score.Children.Add(Row(Button("返回编辑", (_, _) => ShowScore(false)), Button("导出简谱 PDF", Export_Click)));
        _previousScorePage = Button("上一页", (_, _) => { _scorePage--; DrawScore(); });
        _nextScorePage = Button("下一页", (_, _) => { _scorePage++; DrawScore(); });
        Loc.SetHeader(_bpm, "速度（BPM）"); Loc.SetHeader(_beats, "每小节拍数"); Loc.SetHeader(_firstBeat, "首拍（秒）"); Loc.SetHeader(_key, "调性（m 为小调）");
        _score.Children.Add(Row(_key, _beats, _bpm, _firstBeat)); _score.Children.Add(_rhythm); _score.Children.Add(Text("简谱按当前音符整理节奏；节奏与调性是估计值，请试听并确认。", 12));
        _score.Children.Add(Row(_previousScorePage, _scorePageText, _nextScorePage));
        _score.Children.Add(new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Enabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Disabled,
            Content = _scoreBars
        });
        body.Children.Add(_score);
        _bpm.ValueChanged += (_, _) => ScoreSettingsChanged(); _beats.ValueChanged += (_, _) => ScoreSettingsChanged(); _firstBeat.ValueChanged += (_, _) => ScoreSettingsChanged(); _key.SelectionChanged += (_, _) => ScoreSettingsChanged();
        _rhythm.Click += (_, _) => { if (_document != null) { _document.RhythmConfirmed = _rhythm.IsChecked == true; ScheduleSave(); DrawScore(); } };
        _editOptions.Children.Add(Text("上下拖动调音高，左右拖动改位置，拖两端改长短。滚轮移动音域，Shift + 滚轮平移时间，Ctrl + 滚轮缩放。拖空白框选，拖标尺设置循环；空格播放 / 暂停。", 12));
        _editOptions.Children.Add(Text("快捷键：Del / Backspace 删除；↑↓ 调半音，Shift+↑↓ 调八度；←→ 移动一格，Shift+←→ 改时值（自由移动时为 0.05 秒）；S 拆分，M 合并，F 放大选中；Ctrl+A 全选，Ctrl+Z 撤销，Ctrl+Y 重做，Esc 取消选择。", 12));
        var footer = new StackPanel { Spacing = 8, Padding = new Thickness(0, 8, 0, 16) }; SetRow(footer, 2); Children.Add(footer);
        _editorHeader.Children.Add(tools);

        void SourceButton(ToggleButton button, string label, MelodyListenMode mode)
        {
            Loc.SetContent(button, label); Loc.SetName(button, label);
            button.MinWidth = 100; button.Click += (_, _) => SelectSource(mode);
        }
        SourceButton(_currentSource, "钢琴", MelodyListenMode.Piano); SourceButton(_togetherSource, "钢琴＋人声", MelodyListenMode.Together); SourceButton(_vocalSource, "人声", MelodyListenMode.Vocal);
        SourceButton(_mixSource, "原曲", MelodyListenMode.Mix); SourceButton(_mixTogetherSource, "钢琴＋原曲", MelodyListenMode.MixTogether);
        _togetherSource.IsChecked = true;
        Loc.SetToolTip(_currentSource, "使用钢琴音色试听旋律，修改实时生效。");
        Loc.SetToolTip(_togetherSource, "钢琴旋律与原人声同步播放，修改音符实时生效。");
        Loc.SetToolTip(_vocalSource, "播放实际人声，核对唱出的音高与起止。");
        Loc.SetToolTip(_mixSource, "播放原始混音，保留伴奏与人声。"); Loc.SetToolTip(_mixTogetherSource, "钢琴旋律与原始混音同步播放，修改音符实时生效。");
        _monitorBar.Children.Add(Row(_mixTogetherSource, _mixSource, _currentSource, _togetherSource, _vocalSource));
        Loc.SetName(_pianoVolume, "钢琴音量"); Loc.SetName(_vocalVolume, "人声音量");
        _monitorBar.Children.Add(Row(Text("钢琴", 12), _pianoVolume, _sourceVolumeLabel, _vocalVolume, _chooseMix));
        _pianoVolume.ValueChanged += (_, _) => UpdateMixVolumes(); _vocalVolume.ValueChanged += (_, _) => UpdateMixVolumes();
        footer.Children.Add(_monitorBar);
        var transport = new Grid { ColumnSpacing = 12 };
        transport.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        transport.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        transport.Children.Add(Row(_play, Button("试听选中", (_, _) => PlaySelection()), Button("立即重听", (_, _) => Replay()), _loop));
        SetColumn(_time, 1); transport.Children.Add(_time); footer.Children.Add(transport);
        _selection.MinHeight = 20; _selection.VerticalAlignment = VerticalAlignment.Center; footer.Children.Add(_selection);
        Loc.SetName(_pan, "移动可见时间范围");
        _editOptions.Children.Add(Row(Button("回到播放位置", (_, _) => Follow()), Button("放大琴键", (_, _) => _roll.ZoomPitch(0.75)), Button("缩小琴键", (_, _) => _roll.ZoomPitch(1.3333))));
        _editFlyout.Content = _editOptions;
        var moreEdit = new AppBarButton { Icon = new FontIcon { Glyph = "\uE712" }, Flyout = _editFlyout, Label = Tr("更多编辑选项") }; tools.PrimaryCommands.Add(moreEdit);
        _languageRefresh.Add(() => { moreEdit.Label = Tr("更多编辑选项"); AutomationProperties.SetName(moreEdit, Tr("更多编辑选项")); });
        var logButton = Button("运行日志", (_, _) => { }); logButton.Flyout = new Flyout { Content = _log };
        var operation = new Grid { ColumnSpacing = 12 }; operation.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); operation.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        operation.Children.Add(_status); var controls = Row(logButton, _cancel, _run); SetColumn(controls, 1); operation.Children.Add(controls); footer.Children.Add(_progress); footer.Children.Add(operation);
        _pan.ValueChanged += (_, e) => { if (!_updating && _document != null) { _document.ViewStart = e.NewValue; _roll.Redraw(); ScheduleSave(); } };
        _wave.Seek += Seek; _wave.Zoom += factor => _roll.Zoom(1 / factor);
        _audition.Click += (_, _) => { if (_audition.IsChecked != true) _notePlayer.Dispose(); };
        _loop.Click += (_, _) => { if (_document != null) { _document.LoopEnabled = _loop.IsChecked == true; if (_player.IsPlaying) StartPlayback(_player.Position); ScheduleSave(); } };
        _model.SelectionChanged += (_, _) => { if (_document != null) _document.Model = Variant; RefreshModel(); ScheduleSave(); };
        _device.SelectionChanged += (_, _) => RefreshModel();
        _range.Click += (_, _) => { _rangeStart.IsEnabled = _rangeEnd.IsEnabled = _range.IsChecked == true; };
        _rangeStart.IsEnabled = _rangeEnd.IsEnabled = false;
        AllowDrop = true; DragOver += (_, e) => { if (_cancellation == null && e.DataView.Contains(StandardDataFormats.StorageItems)) e.AcceptedOperation = DataPackageOperation.Copy; };
        Drop += async (_, e) =>
        {
            if (_cancellation != null || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            var deferral = e.GetDeferral();
            try { var file = (await e.DataView.GetStorageItemsAsync()).OfType<StorageFile>().FirstOrDefault(); if (file != null) await LoadAudioAsync(file.Path); }
            catch (Exception error) { ShowError(error); }
            finally { deferral.Complete(); }
        };
        _timer.Tick += (_, _) => Tick(); _timer.Start();
        _autosave.Tick += async (_, _) => { _autosave.Stop(); await SaveAsync(); };
        Strings.LanguageChanged += RefreshLanguage;
        RefreshState(); RefreshModel();
        SizeChanged += (_, e) => { _time.FontSize = e.NewSize.Width < 800 ? 11 : 14; };
    }
    private string Variant => (_model.SelectedItem as ComboBoxItem)?.Tag as string ?? "small";
    private ComputeDevice Device => (ComputeDevice)Math.Max(0, _device.SelectedIndex);
    private async void Add_Click(object sender, RoutedEventArgs args)
    {
        try { var file = await _picker(AudioPipeline.Extensions).PickSingleFileAsync(); if (file != null) await LoadAudioAsync(file.Path); } catch (Exception error) { ShowError(error); }
    }
    public async Task LoadAudioAsync(string path, string? vocalPath = null)
    {
        if (_cancellation != null) return;
        await SaveAsync(); Pause();
        var version = ++_loadVersion;
        var wave = await WaveformData.LoadAsync(path);
        var fingerprint = await MelodyDocument.FingerprintAsync(path);
        if (_disposed || version != _loadVersion) return;
        _document = new()
        {
            SourcePath = path,
            SourceSha256 = fingerprint,
            VocalPath = vocalPath,
            IsVocal = vocalPath != null && string.Equals(System.IO.Path.GetFullPath(path), System.IO.Path.GetFullPath(vocalPath), StringComparison.OrdinalIgnoreCase),
            Duration = wave.Duration,
            RangeEnd = wave.Duration,
            ViewDuration = Math.Min(12, wave.Duration)
        };
        _wave.Data = wave; _wave.ViewStart = 0; _wave.ViewDuration = wave.Duration; _wave.Redraw();
        BindDocument(); await SaveAsync();
    }
    private async void Open_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            string? path = null;
            var drafts = await _drafts.ListAsync();
            if (drafts.Count > 0)
            {
                var list = new ListView { ItemsSource = drafts, DisplayMemberPath = nameof(MelodyDraftInfo.Label), SelectedIndex = 0, MaxHeight = 400, MinWidth = 400 };
                var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = Tr("打开草稿"), Content = list, PrimaryButtonText = Tr("打开"), SecondaryButtonText = Tr("浏览文件"), CloseButtonText = Tr("取消") };
                var choice = await dialog.ShowAsync();
                if (choice == ContentDialogResult.Primary) path = (list.SelectedItem as MelodyDraftInfo)?.Path;
                else if (choice != ContentDialogResult.Secondary) return;
            }
            if (path == null) { var file = await _picker([".json"]).PickSingleFileAsync(); if (file == null) return; path = file.Path; }
            await OpenDraftAsync(path);
        }
        catch (Exception error) { ShowError(error); }
    }
    public async Task OpenDraftAsync(string path)
    {
        if (IsBusy) return;
        var document = await MelodyDraftStore.LoadAsync(path);
        await SaveAsync(); Pause(); var version = ++_loadVersion;
        _document = document; _wave.Data = null; _wave.Redraw(); BindDocument();
        if (!File.Exists(document.SourcePath)) { SetStatus("音频已移动；音符可继续编辑，请重新定位音频以试听。"); return; }
        var identity = await MelodyDocument.FingerprintAsync(document.SourcePath);
        if (_disposed || version != _loadVersion) return;
        if (identity != document.SourceSha256)
        {
            document.VocalPath = null; document.IsVocal = false;
            ShowError(new IOException(Tr("源文件内容已改变，请重新定位匹配的音频。"))); return;
        }
        var wave = await WaveformData.LoadAsync(document.SourcePath);
        if (_disposed || version != _loadVersion) return;
        _wave.Data = wave; _wave.ViewStart = 0; _wave.ViewDuration = document.Duration; _wave.Redraw(); UpdateOverview();
    }
    private async void Relocate_Click(object sender, RoutedEventArgs args)
    {
        if (_document == null) return;
        try
        {
            var file = await _picker(AudioPipeline.Extensions).PickSingleFileAsync(); if (file == null) return;
            if (await MelodyDocument.FingerprintAsync(file.Path) != _document.SourceSha256) throw new IOException(Tr("所选音频与草稿源文件不匹配。"));
            _document.SourcePath = file.Path;
            if (_document.IsVocal) _document.VocalPath = file.Path;
            _wave.Data = await WaveformData.LoadAsync(file.Path); _wave.Redraw(); BindDocument(); await SaveAsync();
        }
        catch (Exception error) { ShowError(error); }
    }
    private void BindDocument()
    {
        var d = _document!; _updating = true;
        _scorePage = 0;
        _sourceMode = MelodyListenMode.Together;
        _file.Text = System.IO.Path.GetFileName(d.SourcePath); _vocal.IsChecked = d.IsVocal;
        _rangeStart.Value = d.RangeStart; _rangeEnd.Value = d.RangeEnd;
        _range.IsChecked = d.RangeStart > 0 || d.RangeEnd < d.Duration; _rangeStart.IsEnabled = _rangeEnd.IsEnabled = _range.IsChecked == true;
        _model.SelectedIndex = MuScriptorService.Models.ToList().FindIndex(m => m.Id == d.Model);
        _loop.IsChecked = d.LoopEnabled;
        // Older drafts may contain a failed spectrum stage; it is no longer required.
        d.Spectrum = null; d.ShowSpectrum = false;
        if (d.PendingStage == "spectrum") { d.PendingStage = null; d.Error = null; d.Stage = "completed"; }
        _bpm.Value = d.Bpm; _beats.Value = d.BeatsPerBar; _key.SelectedIndex = d.Key + (d.KeyIsMinor ? 12 : 0); _firstBeat.Value = d.FirstBeat; _rhythm.IsChecked = d.RhythmConfirmed;
        _snap.SelectedIndex = d.MusicAnalysis?.Bpm != null ? 2 : 0;
        _score.Visibility = Visibility.Collapsed; _roll.Document = d; _updating = false;
        RefreshState(); RefreshModel();
        SelectSource(PreferredListenMode);
    }
    private async void Run_Click(object sender, RoutedEventArgs args) => await RunAsync(false);
    public async Task RunAsync(bool prepareOnly = false)
    {
        if (_cancellation != null || _disposed) return;
        if (!prepareOnly && _otherInferenceBusy()) { ShowError(new InvalidOperationException(Tr("请等待当前音频分离完成后再识别旋律。"))); return; }
        var d = _document; var variant = Variant; var device = Device;
        if (!_service.IsModelInstalled(variant)) { SetStatus("请在模型库下载所选转谱模型，已导入的音频会保留。"); ModelLibraryRequested?.Invoke(variant); return; }
        var rangeStart = _range.IsChecked == true ? _rangeStart.Value : 0;
        var rangeEnd = _range.IsChecked == true ? _rangeEnd.Value : d?.Duration ?? 0;
        if (!prepareOnly && d == null) return;
        if (d != null && (!double.IsFinite(rangeStart) || !double.IsFinite(rangeEnd) || rangeStart < 0 || rangeEnd <= rangeStart || rangeEnd > d.Duration)) { ShowError(new ArgumentException(Tr("选段起止时间无效。"))); return; }
        _player.Dispose(); _notePlayer.Dispose(); _loopPlaying = false; _transportRunning = false; _notice.IsOpen = false;
        using var cancellation = new CancellationTokenSource(); _cancellation = cancellation;
        _lastStage = null;
        var activeStage = "prepare";
        IProgress<SeparationProgress> progress = new Progress<SeparationProgress>(p =>
        {
            if (_disposed || !ReferenceEquals(_cancellation, cancellation)) return;
            if (p.Message.Length > 0) { _logs.Enqueue(p.Message); while (_logs.Count > 80) _logs.Dequeue(); _log.Text = string.Join('\n', _logs); }
            if (p.Stage == "log") return;
            _lastStage = p.Stage;
            _statusText = () => MuScriptorProgress.Label(p.Stage) + (p.Stage.StartsWith("runtime-", StringComparison.Ordinal) && p.Message.Length > 0 ? ": " + p.Message : "");
            _status.Text = _statusText();
            _progress.IsIndeterminate = p.Fraction == null; if (p.Fraction is { } f) _progress.Value = f * 100;
        });
        RefreshState();
        try
        {
            await _service.PrepareAsync(variant, device, null, progress, cancellation.Token);
            if (prepareOnly) { SetStatus("模型已就绪，可以识别旋律。"); return; }
            d!.Model = variant; d.RangeStart = rangeStart; d.RangeEnd = rangeEnd;
            if (d.VocalPath == null || !File.Exists(d.VocalPath))
            {
                if (!File.Exists(d.SourcePath)) throw new FileNotFoundException(Tr("原人声不可用，请重新定位音频。"));
                if (await MelodyDocument.FingerprintAsync(d.SourcePath, cancellation.Token) != d.SourceSha256) throw new IOException(Tr("源文件内容已改变，请重新定位匹配的音频。"));
                d.IsVocal = _vocal.IsChecked == true;
                activeStage = d.Stage = d.PendingStage = "separate"; await SaveAsync();
                d.VocalPath = d.IsVocal ? d.SourcePath : await _extractVocal(d.SourcePath, progress, cancellation.Token);
                d.Stage = "vocalReady"; await SaveAsync();
            }
            var firstResult = d.Analyses.Count == 0 && d.OriginalNotes.Length == 0 && d.Undo.Count == 0 && d.Notes.Length == 0;
            var notes = d.Notes;
            {
                activeStage = d.Stage = d.PendingStage = "analyze"; await SaveAsync();
                var analysisPath = d.MixPath != null && File.Exists(d.MixPath) ? d.MixPath : File.Exists(d.SourcePath) ? d.SourcePath : d.VocalPath;
                var music = await _service.AnalyzeAsync(analysisPath, device,
                    rangeStart, rangeEnd, d.Duration, progress, cancellation.Token);
                activeStage = d.Stage = d.PendingStage = "transcribe"; await SaveAsync();
                notes = await _service.TranscribeAsync(d.VocalPath, variant, device, rangeStart, rangeEnd, d.Duration, progress, cancellation.Token);
                var analysis = new MelodyAnalysis(variant, MuScriptorService.GetModel(variant).Revision, DateTimeOffset.Now, notes);
                d.Analyses.Add(analysis);
                if (firstResult)
                {
                    d.Notes = notes; d.OriginalNotes = notes.ToArray(); d.Selection = []; MelodyTiming.Apply(d, music);
                    _snap.SelectedIndex = music.Bpm != null ? 2 : 0;
                    d.ViewStart = d.Position = rangeStart; d.ViewDuration = Math.Min(12, rangeEnd - rangeStart);
                    d.LoopStart = rangeStart; d.LoopEnd = rangeEnd;
                    _roll.Document = d;
                }
                else
                {
                    // New inference is a separate reviewable draft, including its original snapshot.
                    var alternative = new MelodyDocument
                    {
                        SourcePath = d.SourcePath,
                        SourceSha256 = d.SourceSha256,
                        VocalPath = d.VocalPath,
                        MixPath = d.MixPath,
                        IsVocal = d.IsVocal,
                        Duration = d.Duration,
                        RangeStart = rangeStart,
                        RangeEnd = rangeEnd,
                        Model = variant,
                        Notes = notes,
                        OriginalNotes = notes.ToArray(),
                        Analyses = [analysis],
                        Stage = "completed",
                        ViewStart = Math.Clamp(d.ViewStart, rangeStart, Math.Max(rangeStart, rangeEnd - d.ViewDuration)),
                        ViewDuration = Math.Min(d.ViewDuration, rangeEnd - rangeStart),
                        Position = rangeStart,
                        LoopStart = rangeStart,
                        LoopEnd = rangeEnd
                    };
                    MelodyTiming.Apply(alternative, music); await _drafts.SaveAsync(alternative, cancellation.Token);
                    SetStatus("新识别版已另存草稿；当前人工编辑版已保留。");
                    var openNew = new ContentDialog { XamlRoot = XamlRoot, Title = Tr("新识别版已保存"), Content = Tr("当前编辑版已保留。是否打开新识别版？"), PrimaryButtonText = Tr("打开新版本"), CloseButtonText = Tr("保留当前版本") };
                    await SaveAsync();
                    if (await openNew.ShowAsync() == ContentDialogResult.Primary) { _document = alternative; d = alternative; BindDocument(); }
                    else { d.Stage = "completed"; d.PendingStage = null; await SaveAsync(); return; }
                }
            }
            d.Spectrum = null; d.ShowSpectrum = false; d.Stage = "completed"; d.PendingStage = null; d.Error = null;
            if (firstResult) SelectSource(PreferredListenMode);
            _roll.Redraw(); await SaveAsync();
            if (firstResult)
            {
                if (d.MusicAnalysis?.Bpm != null) SetStatus("旋律已就绪并自动吸附，{0} 个音符，{1} 处待核对。", d.Notes.Length, d.Notes.Count(n => n.NeedsReview));
                else SetStatus("旋律已就绪；未找到可用拍点，保留原始音符时间。");
            }
        }
        catch (OperationCanceledException)
        {
            if (d != null) { d.Stage = "cancelled"; await SaveAsync(); }
            SetStatus("已取消，已完成的人声与音符已保留，可重试未完成阶段。");
        }
        catch (Exception error)
        {
            var failedStage = activeStage == "prepare" ? _lastStage ?? "runtime" : activeStage;
            if (d != null) { d.Stage = "failed"; d.Error = failedStage; await SaveAsync(); }
            ShowError(error); SetStatus("阶段失败，可原位重试：{0}", MuScriptorProgress.Label(failedStage));
        }
        finally { _cancellation = null; RefreshState(); RefreshModel(); ModelAvailabilityChanged?.Invoke(); }
    }
    private void Edit(Action<MelodyEditor> action)
    {
        if (_document == null) return;
        try { action(new(_document)); _roll.Redraw(); RefreshState(); ScheduleSave(); UpdateEditedAudio(); }
        catch (Exception error) { ShowError(error); }
    }
    private void AuditionPitch(int pitch)
    {
        if (_audition.IsChecked != true) return;
        // During transport playback, live notes already sound at their original times.
        if (_player.IsPlaying) return;
        try { _notePlayer.Play(pitch); } catch (Exception error) { ShowError(error); }
    }
    private void UpdateEditedAudio()
    {
        if (_document == null) return;
        _player.UpdateNotes(_document.Notes);
        if (!_player.IsPlaying && _document.Notes.FirstOrDefault(n => _document.Selection.Contains(n.Id)) is { } note) AuditionPitch(note.Pitch);
    }
    private void SelectSource(MelodyListenMode mode)
    {
        try
        {
            if (_player.IsPlaying) _player.SetMode(mode);
            _sourceMode = mode;
        }
        catch (Exception error) { ShowError(error); }
        _currentSource.IsChecked = _sourceMode == MelodyListenMode.Piano; _togetherSource.IsChecked = _sourceMode == MelodyListenMode.Together; _vocalSource.IsChecked = _sourceMode == MelodyListenMode.Vocal;
        _mixSource.IsChecked = _sourceMode == MelodyListenMode.Mix; _mixTogetherSource.IsChecked = _sourceMode == MelodyListenMode.MixTogether;
        var mix = _sourceMode is MelodyListenMode.Mix or MelodyListenMode.MixTogether;
        Loc.SetText(_sourceVolumeLabel, mix ? "原曲" : "人声"); Loc.SetName(_vocalVolume, mix ? "原曲音量" : "人声音量");
        _vocalVolume.IsEnabled = _sourceMode != MelodyListenMode.Piano && (mix ? _mixSource.IsEnabled : _vocalSource.IsEnabled);
    }
    private MelodyListenMode PreferredListenMode => _mixSource.IsEnabled
        ? _document?.Notes.Length > 0 ? MelodyListenMode.MixTogether : MelodyListenMode.Mix
        : _vocalSource.IsEnabled ? _document?.Notes.Length > 0 ? MelodyListenMode.Together : MelodyListenMode.Vocal : MelodyListenMode.Piano;
    private async void ChooseMix_Click(object sender, RoutedEventArgs args)
    {
        if (_document == null || IsBusy) return;
        try
        {
            var file = await _picker(AudioPipeline.Extensions).PickSingleFileAsync();
            if (file == null || _document == null) return;
            using (var reader = new NAudio.Wave.AudioFileReader(file.Path))
                if (Math.Abs(reader.TotalTime.TotalSeconds - _document.Duration) > 0.02)
                    throw new ArgumentException(Tr("请选择与人声时长一致、从相同位置开始的完整原曲。"));
            var playing = _player.IsPlaying; var position = playing ? _player.Position : _document.Position;
            _player.Dispose(); _document.MixPath = file.Path; RefreshState(); SelectSource(PreferredListenMode);
            if (playing) _player.Play(_document, _sourceMode, position, _playEnd, (float)_pianoVolume.Value / 100, (float)_vocalVolume.Value / 100);
            await SaveAsync(); SetStatus("原曲已关联，可同步试听。");
        }
        catch (Exception error) { ShowError(error); }
    }
    private void UpdateMixVolumes() => _player.SetVolumes((float)_pianoVolume.Value / 100, (float)_vocalVolume.Value / 100);
    internal bool HandleShortcut(Windows.System.VirtualKey key, bool control, bool shift, bool alt)
    {
        if (key == Windows.System.VirtualKey.Space && !control && !shift && !alt)
        {
            if (_document != null && !IsBusy) Play_Click(_play, new RoutedEventArgs());
            return true;
        }
        if (IsBusy || _score.Visibility == Visibility.Visible || _document == null) return false;
        if (!_roll.IsEditing && !control && !shift && !alt && _document.Selection.Length > 0)
        {
            if (key == Windows.System.VirtualKey.S && _document.Selection.Length == 1) { Edit(editor => editor.Split(_document.Position)); return true; }
            if (key == Windows.System.VirtualKey.M && _document.Selection.Length > 1) { Merge(); return true; }
            if (key == Windows.System.VirtualKey.F) { _roll.FocusSelection(); return true; }
        }
        return _roll.HandleShortcut(key, control, shift, alt);
    }
    internal bool IsPlaying => _player.IsPlaying;
    internal MelodyDocument? DiagnosticDocument => _document;
    internal PianoRollView DiagnosticRoll => _roll;
    internal void RefreshDiagnosticEditor() { _roll.Document = _document; RefreshState(); UpdateEditedAudio(); }
    internal IEnumerable<Control> DiagnosticShortcutTargets => [_play, _mixTogetherSource, _mixSource, _togetherSource, _currentSource, _vocalSource, _run, _pianoVolume];
    private async void Merge()
    {
        if (_document == null) return;
        var notes = _document.Notes.Where(n => _document.Selection.Contains(n.Id)).ToArray(); if (notes.Length < 2) return;
        var pitch = _document.Notes.First(n => n.Id == _document.Selection[0]).Pitch;
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = Tr("合并音符"), Content = Tr("合并范围 {0:F2}–{1:F2} 秒，合并后保持这个音高：{2}。间隔会被填满。", notes.Min(n => n.Start), notes.Max(n => n.End), PianoRollView.NoteName(pitch)), PrimaryButtonText = Tr("合并"), CloseButtonText = Tr("取消") };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) Edit(e => e.Merge(pitch));
    }
    private void NextReview()
    {
        if (_document == null) return;
        var note = _document.Notes.Where(n => n.NeedsReview && n.Start > _document.Position + 0.001).OrderBy(n => n.Start).FirstOrDefault()
            ?? _document.Notes.FirstOrDefault(n => n.NeedsReview);
        if (note == null) { SetStatus("没有待核对标记；所有音符仍可修改。"); return; }
        _document.Selection = [note.Id]; Seek(note.Start); _roll.FocusSelection(); RefreshState();
    }
    private void Play_Click(object sender, RoutedEventArgs args)
    {
        if (_player.IsPlaying || _loopPlaying) { if (_document != null) _document.Position = _player.Position; _loopPlaying = false; _transportRunning = false; _player.Dispose(); _notePlayer.Dispose(); }
        else if (_document != null) StartPlayback(_document.Position);
        RefreshState();
    }
    private void StartPlayback(double position)
    {
        if (_document == null) return;
        try
        {
            var d = _document;
            var looping = _loop.IsChecked == true && d.LoopEnd > d.LoopStart;
            if (looping && (position < d.LoopStart || position >= d.LoopEnd - 0.01)) position = d.LoopStart;
            else if (position >= d.Duration - 0.01) position = 0;
            _playEnd = looping ? d.LoopEnd : d.Duration; _loopPlaying = looping;
            _notePlayer.Dispose(); _player.Play(d, _sourceMode, position, _playEnd, (float)_pianoVolume.Value / 100, (float)_vocalVolume.Value / 100); _transportRunning = true; d.Position = position; FollowPlayback(); RefreshState();
        }
        catch (Exception error) { _loopPlaying = false; ShowError(error); }
    }
    private void Replay()
    {
        if (_document == null) return;
        StartPlayback(_document.LoopEnd > _document.LoopStart ? _document.LoopStart : Math.Max(0, _document.ViewStart));
    }
    private void PlaySelection()
    {
        if (_document == null) return;
        var notes = _document.Notes.Where(n => _document.Selection.Contains(n.Id)).ToArray(); if (notes.Length == 0) return;
        _loopPlaying = false; _notePlayer.Dispose();
        var start = Math.Max(0, notes.Min(n => n.Start) - 0.15); _playEnd = Math.Min(_document.Duration, notes.Max(n => n.End) + 0.15);
        try { _player.Play(_document, _sourceMode, start, _playEnd, (float)_pianoVolume.Value / 100, (float)_vocalVolume.Value / 100); _transportRunning = true; _document.Position = start; FollowPlayback(); RefreshState(); } catch (Exception error) { ShowError(error); }
    }
    private void Seek(double position)
    {
        if (_document == null) return;
        var playing = _player.IsPlaying;
        _document.Position = Math.Clamp(position, 0, _document.Duration);
        if (playing) StartPlayback(_document.Position);
        UpdatePosition(); ScheduleSave();
    }
    private void Tick()
    {
        if (_document == null || _disposed) return;
        if (_player.IsPlaying)
        {
            _document.Position = _player.Position;
            FollowPlayback();
            if (_document.Position >= _playEnd - 0.015) { _player.Dispose(); if (_loopPlaying) StartPlayback(_document.LoopStart); else { _document.Position = _playEnd; _transportRunning = false; } }
        }
        else if (_loopPlaying) StartPlayback(_document.LoopStart);
        else if (_transportRunning) { _document.Position = _playEnd; _transportRunning = false; }
        UpdatePosition();
    }
    private void UpdatePosition()
    {
        if (_document == null) return;
        _time.Text = TimeSpan.FromSeconds(_document.Position).ToString(@"mm\:ss\.f") + " / " + TimeSpan.FromSeconds(_document.Duration).ToString(@"mm\:ss\.f");
        _roll.SetPosition(_document.Position); _wave.SetPosition(_document.Position);
        Loc.SetContent(_play, _player.IsPlaying ? "暂停" : "播放");
    }
    private void FollowPlayback()
    {
        if (_document == null || _roll.IsEditing || _score.Visibility == Visibility.Visible) return;
        var d = _document;
        // Move before the playhead reaches the edge, retaining context on both sides.
        if (d.Position >= d.ViewStart && d.Position < d.ViewStart + d.ViewDuration * 0.85) return;
        var start = Math.Clamp(d.Position - d.ViewDuration * 0.2, 0, Math.Max(0, d.Duration - d.ViewDuration));
        if (Math.Abs(start - d.ViewStart) < 0.001) return;
        d.ViewStart = start; _roll.Redraw(); UpdateOverview();
        _updating = true;
        try { _pan.Maximum = Math.Max(0.001, d.Duration - d.ViewDuration); _pan.Value = Math.Min(start, _pan.Maximum); }
        finally { _updating = false; }
    }
    private void Follow()
    {
        if (_document == null) return;
        _document.ViewStart = Math.Clamp(_document.Position - _document.ViewDuration * 0.25, 0, Math.Max(0, _document.Duration - _document.ViewDuration)); _roll.Redraw(); RefreshState();
    }
    private void ShowScore(bool show)
    {
        if (_document == null) return;
        _score.Visibility = show ? Visibility.Visible : Visibility.Collapsed; _editing.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        if (show)
        {
            _updating = true; _bpm.Value = _document.Bpm; _beats.Value = _document.BeatsPerBar; _key.SelectedIndex = _document.Key + (_document.KeyIsMinor ? 12 : 0); _firstBeat.Value = _document.FirstBeat; _rhythm.IsChecked = _document.RhythmConfirmed; _updating = false;
            DrawScore();
        }
        else ResizeEditor();
        RefreshState();
    }
    private void ScoreSettingsChanged()
    {
        if (_updating || _document == null || !double.IsFinite(_bpm.Value) || !double.IsFinite(_beats.Value) || !double.IsFinite(_firstBeat.Value) || _key.SelectedIndex < 0) return;
        _document.Bpm = Math.Clamp(_bpm.Value, 20, 400); _document.BeatsPerBar = Math.Clamp((int)_beats.Value, 2, 12); _document.Key = _key.SelectedIndex % 12; _document.KeyIsMinor = _key.SelectedIndex >= 12; _document.FirstBeat = Math.Clamp(_firstBeat.Value, 0, _document.Duration);
        _document.RhythmConfirmed = false; _rhythm.IsChecked = false; DrawScore(); ScheduleSave();
    }
    private void DrawScore()
    {
        if (_document == null) return;
        _scoreBars.Children.Clear();
        _scoreBars.Children.Add(Text(_document.RhythmConfirmed ? "简谱" : "草稿 · 节奏待确认", 18));
        var cells = MelodyScore.Layout(_document);
        var bars = cells.GroupBy(c => c.Bar).ToArray();
        var pages = Math.Max(1, (int)Math.Ceiling(bars.Length / (double)ScoreBarsPerPage));
        _scorePage = Math.Clamp(_scorePage, 0, pages - 1);
        _scorePageText.Text = Tr("第 {0} / {1} 页", _scorePage + 1, pages);
        _previousScorePage.IsEnabled = _scorePage > 0; _nextScorePage.IsEnabled = _scorePage < pages - 1;
        // Keep the visual tree bounded. One horizontal viewport serves the current
        // page instead of adding a nested ScrollViewer for every bar in the song.
        foreach (var group in bars.Skip(_scorePage * ScoreBarsPerPage).Take(ScoreBarsPerPage))
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
            row.Children.Add(new TextBlock { Text = $"{group.Key + 1} │", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.6 });
            foreach (var cell in group)
            {
                var button = new Button { Content = MelodyScore.CellLabel(cell), FontSize = 24, MinWidth = 32, Padding = new Thickness(4), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0) };
                AutomationProperties.SetName(button, cell.NoteId is { } id ? Tr("简谱音符 {0}", id) : Tr("休止"));
                button.Click += (_, _) => { if (cell.NoteId is { } noteId) { _document.Selection = [noteId]; ShowScore(false); _roll.FocusSelection(); _roll.Redraw(); RefreshState(); } };
                row.Children.Add(button);
            }
            row.Children.Add(new TextBlock { Text = "│", FontSize = 26 });
            _scoreBars.Children.Add(row);
        }
    }
    private async void Export_Click(object sender, RoutedEventArgs args)
    {
        if (_document == null) return;
        try
        {
            var picker = new FileSavePicker { SuggestedFileName = System.IO.Path.GetFileNameWithoutExtension(_document.SourcePath) + "-melody" };
            picker.FileTypeChoices.Add("PDF", [".pdf"]); WinRT.Interop.InitializeWithWindow.Initialize(picker, _window);
            var file = await picker.PickSaveFileAsync(); if (file == null) return;
            await SaveAsync(); await MelodyScore.ExportPdfAsync(_document, file.Path); SetStatus("简谱 PDF 已导出。");
        }
        catch (Exception error) { ShowError(error); }
    }
    private void RefreshModel()
    {
        var installed = _service.IsModelInstalled(Variant);
        _modelDetails.Text = Tr(installed ? "模型已下载，识别时自动准备所选设备的运行环境。" : "请在模型库下载所选转谱模型，已导入的音频会保留。");
        _manageModels.IsEnabled = _cancellation == null;
    }
    public void RefreshModelAvailability() => RefreshModel();
    public bool SelectModel(string variant, ComputeDevice device)
    {
        if (IsBusy) return false;
        MuScriptorService.GetModel(variant);
        _model.SelectedItem = _model.Items.OfType<ComboBoxItem>().First(item => Equals(item.Tag, variant));
        _device.SelectedIndex = (int)device;
        RefreshModel();
        return true;
    }
    private void RefreshState()
    {
        var busy = _cancellation != null; var d = _document; var hasNotes = d != null && (d.Notes.Length > 0 || d.OriginalNotes.Length > 0 || d.Undo.Count > 0);
        if (hasNotes && _fileFlyout.Content == null) { _body.Children.Remove(_fileSettings); _fileSettings.Width = 620; _fileFlyout.Content = _fileSettings; }
        else if (!hasNotes && _fileFlyout.Content != null) { _fileFlyout.Content = null; _fileSettings.Width = double.NaN; _body.Children.Insert(2, _fileSettings); }
        _settingsButton.Visibility = hasNotes ? Visibility.Visible : Visibility.Collapsed;
        _editorHeader.Visibility = hasNotes && _score.Visibility != Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        _monitorBar.Visibility = hasNotes ? Visibility.Visible : Visibility.Collapsed;
        _tools.Visibility = hasNotes ? Visibility.Visible : Visibility.Collapsed;
        _selection.Visibility = hasNotes ? Visibility.Visible : Visibility.Collapsed;
        _empty.Visibility = d == null ? Visibility.Visible : Visibility.Collapsed; _ready.Visibility = d != null ? Visibility.Visible : Visibility.Collapsed;
        _editing.Visibility = hasNotes && _score.Visibility != Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        _undo.IsEnabled = d?.Undo.Count > 0; _redo.IsEnabled = d?.Redo.Count > 0; _preview.IsEnabled = d?.Notes.Length > 0;
        _run.IsEnabled = d != null && !busy; _run.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        _cancel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed; _progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        _add.IsEnabled = _open.IsEnabled = _relocate.IsEnabled = _model.IsEnabled = _device.IsEnabled = !busy;
        _vocal.IsEnabled = !busy && d?.VocalPath == null; _range.IsEnabled = !busy;
        _rangeStart.IsEnabled = _rangeEnd.IsEnabled = !busy && _range.IsChecked == true;
        _play.IsEnabled = d != null; _pan.Visibility = hasNotes ? Visibility.Visible : Visibility.Collapsed;
        _selection.Text = d == null ? "" : d.Selection.Length == 1 && d.Notes.FirstOrDefault(n => n.Id == d.Selection[0]) is { } selected
            ? Tr("{0} · {1:F2}–{2:F2} 秒 · ↑↓ 调音高 · ←→ 移动 · Del 删除", PianoRollView.NoteName(selected.Pitch), selected.Start, selected.End)
            : d.Selection.Length > 0 ? Tr("已选择 {0} 个音符", d.Selection.Length) : "";
        var vocalPath = d?.VocalPath ?? (d?.IsVocal == true ? d.SourcePath : null);
        _vocalSource.IsEnabled = vocalPath != null && File.Exists(vocalPath);
        _togetherSource.IsEnabled = _vocalSource.IsEnabled && d?.Notes.Length > 0;
        var mixPath = d == null ? null : MelodyPlayback.GetMixPath(d);
        _mixSource.IsEnabled = mixPath != null && File.Exists(mixPath);
        _mixTogetherSource.IsEnabled = _mixSource.IsEnabled && d?.Notes.Length > 0;
        _chooseMix.IsEnabled = d != null && !busy;
        _pianoVolume.IsEnabled = d?.Notes.Length > 0;
        _currentSource.IsEnabled = d?.Notes.Length > 0;
        var available = _sourceMode switch
        {
            MelodyListenMode.Mix => _mixSource.IsEnabled,
            MelodyListenMode.MixTogether => _mixTogetherSource.IsEnabled,
            MelodyListenMode.Vocal => _vocalSource.IsEnabled,
            MelodyListenMode.Together => _togetherSource.IsEnabled,
            _ => _currentSource.IsEnabled
        };
        SelectSource(available ? _sourceMode : PreferredListenMode);
        if (d != null) { _updating = true; _pan.Maximum = Math.Max(0.001, d.Duration - d.ViewDuration); _pan.Value = Math.Min(d.ViewStart, _pan.Maximum); _updating = false; }
        UpdateOverview();
        ResizeEditor();
    }
    private void UpdateOverview()
    {
        if (_document == null) return;
        _wave.HighlightStart = _document.ViewStart; _wave.HighlightEnd = _document.ViewStart + _document.ViewDuration;
        _wave.LoopStart = _document.LoopStart; _wave.LoopEnd = _document.LoopEnd; _wave.Redraw();
    }
    public bool IsBusy => _cancellation != null;
    private void ResizeEditor()
    {
        if (_bodyScroll == null || _score.Visibility == Visibility.Visible) return;
        var height = Math.Clamp(_bodyScroll.ActualHeight - _editorHeader.ActualHeight - 125, 220, 1200);
        _roll.MinHeight = 180;
        if (Math.Abs(_roll.Height - height) > 0.5) _roll.Height = height;
    }
    private void RefreshLanguage()
    {
        if (_disposed) return;
        _updating = true;
        foreach (var combo in new[] { _snap, _device }) { var index = combo.SelectedIndex; combo.SelectedIndex = -1; combo.SelectedIndex = index; }
        _updating = false;
        foreach (var refresh in _languageRefresh) refresh();
        _status.Text = _statusText();
        if (_document != null) _save.Text = _dirty ? Tr("等待自动保存") : Tr("已自动保存 {0:HH:mm:ss}", _document.SavedAt.LocalDateTime);
        RefreshState(); RefreshModel(); if (_score.Visibility == Visibility.Visible) DrawScore(); _roll.Redraw();
    }
    private void SetStatus(string message, params object?[] arguments) { _statusText = () => Tr(message, arguments); _status.Text = _statusText(); }
    private void ScheduleSave() { if (_document == null || _updating || _disposed) return; _dirty = true; _save.Text = Tr("等待自动保存"); _autosave.Stop(); _autosave.Start(); }
    public async Task SaveAsync()
    {
        if (_document == null) return;
        var document = _document; var version = ++_saveVersion; _dirty = false;
        try { await _drafts.SaveAsync(document); if (!_disposed && ReferenceEquals(document, _document) && version == _saveVersion && !_dirty) { _save.Text = Tr("已自动保存 {0:HH:mm:ss}", document.SavedAt.LocalDateTime); ToolTipService.SetToolTip(_save, _drafts.GetPath(document.Id)); } }
        catch (Exception error) { _dirty = true; if (!_disposed) { _save.Text = Tr("自动保存失败，请重试"); ShowError(error); } }
    }
    private void ShowError(Exception error)
    {
        _notice.Title = Tr("请检查后重试");
        _notice.Message = error.Message.Contains("GatedRepoError", StringComparison.Ordinal) || error.Message.Contains("401", StringComparison.Ordinal)
            ? Tr("需要 MuScriptor 模型授权。请前往模型库，展开“MuScriptor 下载与授权”，接受模型许可后重试。") : error.Message;
        _notice.Severity = InfoBarSeverity.Warning; _notice.IsOpen = true;
    }
    public void Pause() { if (_document != null && _player.IsPlaying) _document.Position = _player.Position; _loopPlaying = false; _transportRunning = false; _player.Dispose(); _notePlayer.Dispose(); }
    internal async Task LoadDiagnosticAsync(string folder)
    {
        var file = System.IO.Path.Combine(folder, "melody-ui-fixture.wav");
        var notes = Enumerable.Range(0, 18).Select(i => new MelodyNote(Guid.NewGuid(), 0.5 + i * 0.58, 0.97 + i * 0.58,
            new[] { 60, 62, 64, 67, 69, 67, 64, 62, 60 }[i % 9], i == 4, i is 2 or 9, i is 2 or 9 ? "音符较短，请核对" : null)).ToArray();
        var synth = new MelodySynth(notes, 0, 12);
        using (var writer = new NAudio.Wave.WaveFileWriter(file, synth.WaveFormat))
        {
            var buffer = new float[16384]; int count;
            while ((count = synth.Read(buffer, 0, buffer.Length)) > 0) writer.WriteSamples(buffer, 0, count);
        }
        await LoadAudioAsync(file, file);
        _document!.MixPath = file;
        _document.Notes = notes; _document.OriginalNotes = notes.Select(n => n with { Pitch = n.Edited ? n.Pitch + 1 : n.Pitch }).ToArray();
        _document.Selection = [notes[4].Id]; _document.LoopStart = 2; _document.LoopEnd = 5;
        MelodyScore.Estimate(_document); BindDocument(); await SaveAsync();
    }
    internal void ShowDiagnosticScore() => ShowScore(true);
    internal async Task VerifyDiagnosticScoreAsync()
    {
        if (_document == null) throw new InvalidOperationException("Missing melody draft.");
        var ids = _document.Notes.Select(n => n.Id).ToArray();
        var view = (_document.ViewStart, _document.ViewDuration, _document.Position);
        var selection = _document.Selection.ToArray();
        var pages = Math.Max(1, (int)Math.Ceiling(MelodyScore.Layout(_document).Select(cell => cell.Bar).Distinct().Count() / (double)ScoreBarsPerPage));
        for (var iteration = 0; iteration < 3; iteration++)
        {
            ShowScore(true); _scorePage = pages - 1; DrawScore();
            await Task.Delay(100);
            if (_scoreBars.Children.Count > ScoreBarsPerPage + 1 || _nextScorePage.IsEnabled || _previousScorePage.IsEnabled != (pages > 1))
                throw new InvalidOperationException("Paged notation escaped its visual or page bounds.");
            ShowScore(false); await Task.Delay(100);
        }
        _scorePage = 0; ShowScore(true); await Task.Delay(100);
        if (!_document.Notes.Select(n => n.Id).SequenceEqual(ids) || view != (_document.ViewStart, _document.ViewDuration, _document.Position)
            || !selection.SequenceEqual(_document.Selection)) throw new InvalidOperationException("Notation preview changed the edit or timeline.");
    }
    internal async Task LoadDiagnosticDraftAsync(string path)
    {
        _document = await MelodyDraftStore.LoadAsync(path);
        BindDocument();
    }
    internal Task ExportDiagnosticPdfAsync(string path) => MelodyScore.ExportPdfAsync(_document!, path);
    internal async Task VerifyDiagnosticPlaybackAsync()
    {
        if (_document == null) throw new InvalidOperationException("Missing melody fixture.");
        var first = _document.Notes[0]; _document.Selection = [first.Id];
        _roll.Redraw(); UpdateLayout(); _roll.VerifyDiagnosticInputSurface();
        await Task.Delay(80);
        _document.Selection = [first.Id];
        Edit(editor => editor.Transform(semitones: 1));
        if (_document.Notes[0].Pitch != first.Pitch + 1 || _document.OriginalNotes[0].Pitch != first.Pitch) throw new InvalidOperationException("Editing changed the reference snapshot.");
        Edit(editor => editor.Undo());
        if (_document.Notes[0].Pitch != first.Pitch) throw new InvalidOperationException("Undo did not restore the fixture.");
        SelectSource(MelodyListenMode.Piano); StartPlayback(1);
        await Task.Delay(200);
        if (!_player.IsPlaying || _player.Position < 1.05) throw new InvalidOperationException("Melody output clock did not advance.");
        var livePosition = _player.Position;
        Edit(editor => editor.Transform(semitones: 1));
        if (!_player.IsPlaying || _player.Position < livePosition || _document.OriginalNotes[0].Pitch != first.Pitch)
            throw new InvalidOperationException("Live editing restarted playback or changed the original notes.");
        Edit(editor => editor.Undo());
        var before = _player.Position; SelectSource(MelodyListenMode.Vocal);
        if (Math.Abs(_player.Position - before) > 0.1) throw new InvalidOperationException("A/B switching lost the shared time position.");
        var savedMix = _document.MixPath; _document.MixPath = _document.SourcePath; RefreshState();
        foreach (var mode in new[] { MelodyListenMode.Mix, MelodyListenMode.MixTogether, MelodyListenMode.Vocal })
        {
            before = _player.Position; SelectSource(mode);
            if (!_player.IsPlaying || Math.Abs(_player.Position - before) > 0.1 || _sourceMode != mode)
                throw new InvalidOperationException("Original-mix switching interrupted the shared audio clock.");
        }
        _document.MixPath = savedMix;
        Pause(); SelectSource(MelodyListenMode.Piano); PlaySelection();
        var selectionEnd = _playEnd;
        SelectSource(MelodyListenMode.Together);
        if (_playEnd != selectionEnd || _sourceMode != MelodyListenMode.Together || _loopPlaying || !_player.IsPlaying)
            throw new InvalidOperationException("Mixed playback switching lost the selection playback bounds.");
        await Task.Delay(900);
        Tick();
        if (_player.IsPlaying || Math.Abs(_document.Position - selectionEnd) > 0.001)
            throw new InvalidOperationException("Selection playback did not stop at its endpoint.");
        AuditionPitch(69); await Task.Delay(50); AuditionPitch(71); _notePlayer.Dispose();
        _document.LoopStart = 2; _document.LoopEnd = 2.4; _loop.IsChecked = true; StartPlayback(2.25);
        await Task.Delay(450);
        if (_document.Position < 2 || _document.Position > 2.4) throw new InvalidOperationException("Melody loop escaped its source time range.");
        Pause(); _loop.IsChecked = false; _document.LoopStart = 2; _document.LoopEnd = 5; SelectSource(MelodyListenMode.Piano); Seek(0);
        var savedView = (_document.ViewStart, _document.ViewDuration);
        _document.ViewStart = 0; _document.ViewDuration = 1;
        StartPlayback(0.8); await Task.Delay(280); Tick();
        if (_document.ViewStart <= 0 || _document.Position < _document.ViewStart || _document.Position >= _document.ViewStart + _document.ViewDuration
            || Math.Abs(_pan.Value - _document.ViewStart) > 0.001)
            throw new InvalidOperationException("Playback did not advance the roll and its horizontal scrollbar.");
        _document.Selection = [first.Id]; Edit(editor => editor.Transform(semitones: 1)); Edit(editor => editor.Undo());
        StartPlayback(5);
        if (_document.ViewStart < 4 || _document.Position < _document.ViewStart)
            throw new InvalidOperationException("Editing or seeking disabled playback following.");
        StartPlayback(0.2);
        if (_document.ViewStart > 0.2) throw new InvalidOperationException("Backward playback did not bring the playhead into view.");
        Pause(); _document.ViewStart = savedView.ViewStart; _document.ViewDuration = savedView.ViewDuration; Seek(0); _roll.Redraw(); RefreshState(); SelectSource(MelodyListenMode.Together); SetStatus("");
        await SaveAsync();
    }
    public void Dispose()
    {
        _disposed = true; _cancellation?.Cancel(); _timer.Stop(); _autosave.Stop(); _player.Dispose(); _notePlayer.Dispose(); Strings.LanguageChanged -= RefreshLanguage;
        if (_document != null) { try { _drafts.SaveAsync(_document).GetAwaiter().GetResult(); } catch (Exception) { /* Preserve the previous atomic save on shutdown. */ } }
    }
}
