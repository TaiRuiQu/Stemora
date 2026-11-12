using static Stemora.Core.Strings;
using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Input;
using Windows.System;
using Windows.UI.Core;
using Microsoft.UI.Xaml.Shapes;
using Stemora.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Media.Core;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI;

namespace Stemora.App;

public sealed partial class MainWindow : Window
{
    private readonly AppPaths _paths = new();
    private readonly ObservableCollection<AudioJob> _jobs = [];
    private readonly List<ModelRow> _catalog = [];
    private AppSettings _settings = new();
    private CancellationTokenSource? _runCancellation;
    private CancellationTokenSource? _downloadCancellation;
    private bool _ready;
    private bool _closed;
    private readonly Queue<string> _logs = new();
    private MelodyWorkspace? _melodyWorkspace;
    private bool _melodySpacePressed;

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1320, 900));
        AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "Stemora.ico"));
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.PreferredMinimumWidth = 900;
        SystemBackdrop = new MicaBackdrop();
        QueueList.ItemsSource = _jobs;
        if (Navigation.SettingsItem is NavigationViewItem settingsItem) settingsItem.Content = Tr("设置");
        Navigation.Loaded += (_, _) => { if (Navigation.SettingsItem is NavigationViewItem item) item.Content = Tr("设置"); };
        Root.Loaded += (_, _) => AddButton.Focus(FocusState.Programmatic);
        try { _settings = _paths.LoadSettings(); }
        catch (Exception e) { ShowNotice(Tr("无法读取设置"), e.Message, InfoBarSeverity.Warning); }
        ApplySettings();
        _catalog.AddRange(ModelRepository.Catalog.OrderByDescending(m => m.Quantization == "Q8_0")
            .ThenBy(m => m.Size).Select(m => new ModelRow(m, new ModelRepository(_paths).IsInstalled(m))));
        _ready = true;
        RefreshModels(_settings.ModelPath);
        FilterModels();
        UpdateEngineStatus();
        DataPathLabel.Text = Tr("数据目录：{0}", _paths.Root);
        Closed += (_, _) =>
        {
            _closed = true;
            _runCancellation?.Cancel();
            _downloadCancellation?.Cancel();
            _downloadTimer?.Stop();
            _exportCancellation?.Cancel();
        };
        Root.ActualThemeChanged += (_, _) => { UpdateCaptionColors(); DrawTimelineRuler(); };
        UpdateCaptionColors();
        InitializeTimeline();
        _melodyWorkspace = new MelodyWorkspace(_paths, WinRT.Interop.WindowNative.GetWindowHandle(this), NewFilePicker, ExtractMelodyVocalAsync, () => _runCancellation != null || _downloadCancellation != null);
        _melodyWorkspace.ModelLibraryRequested += OpenTranscriptionModels;
        _melodyWorkspace.ModelAvailabilityChanged += RefreshCatalogAvailability;
        MelodyHost.Child = _melodyWorkspace;
        Root.PreviewKeyDown += MelodyShortcut_KeyDown;
        Root.PreviewKeyUp += (_, e) =>
        {
            if (e.Key == VirtualKey.Space && _melodySpacePressed) { _melodySpacePressed = false; e.Handled = true; }
        };
        Activated += (_, e) => { if (e.WindowActivationState == WindowActivationState.Deactivated) _melodySpacePressed = false; };
        Closed += (_, _) => _melodyWorkspace.Dispose();
        RefreshLanguage();
    }

    private void MelodyShortcut_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        static bool Down(VirtualKey key) => (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) != 0;
        if (e.Handled) return;
        e.Handled = TryMelodyShortcut(e.Key, Down(VirtualKey.Control), Down(VirtualKey.Shift), Down(VirtualKey.Menu),
            e.KeyStatus.WasKeyDown, FocusManager.GetFocusedElement(Root.XamlRoot) as DependencyObject);
    }
    private bool TryMelodyShortcut(VirtualKey key, bool control, bool shift, bool alt, bool repeat, DependencyObject? focus)
    {
        if (MelodyHost.Visibility != Visibility.Visible || _melodyWorkspace == null) return false;
        if (VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot).Count > 0) return false;
        for (var element = focus; element != null; element = VisualTreeHelper.GetParent(element))
        {
            if (element is TextBox or RichEditBox or PasswordBox or NumberBox or AutoSuggestBox or ComboBox or ContentDialog)
                return false;
            if (key != VirtualKey.Space && element is Slider) return false;
            if (element is NavigationViewItem && key is (VirtualKey.Up or VirtualKey.Down or VirtualKey.Left or VirtualKey.Right)) return false;
        }
        if (key == VirtualKey.Space && !control && !shift && !alt)
        {
            // Consume both down and up before a focused Button can run its last action.
            _melodySpacePressed = true;
            if (!repeat) _melodyWorkspace.HandleShortcut(key, false, false, false);
            return true;
        }
        return _melodyWorkspace.HandleShortcut(key, control, shift, alt);
    }

    private void ApplySettings()
    {
        Strings.SetLanguage(_settings.Language);
        LanguageCombo.SelectedIndex = _settings.Language switch { "en-US" => 1, "zh-CN" => 2, _ => 0 };
        Root.Language = Strings.Language;
        OutputBox.Text = _settings.OutputDirectory;
        EngineBox.Text = _settings.EnginePath ?? "";
        DeviceCombo.SelectedIndex = (int)_settings.Device;
        FormatCombo.SelectedIndex = (int)_settings.Encoding;
        ResidualCheck.IsChecked = _settings.ExportResidual;
        OverlapBox.Value = _settings.Overlap ?? double.NaN;
        ChunkBox.Value = _settings.ChunkSize ?? double.NaN;
        ThemeCombo.SelectedIndex = _settings.Theme switch { "Light" => 1, "Dark" => 2, _ => 0 };
        Root.RequestedTheme = _settings.Theme switch { "Light" => ElementTheme.Light, "Dark" => ElementTheme.Dark, _ => ElementTheme.Default };
    }
    private AppSettings ReadSettings() => new()
    {
        ModelPath = (ModelCombo.SelectedItem as ModelChoice)?.Path,
        EnginePath = string.IsNullOrWhiteSpace(EngineBox.Text) ? null : EngineBox.Text.Trim(),
        OutputDirectory = OutputBox.Text.Trim(),
        Device = (ComputeDevice)DeviceCombo.SelectedIndex,
        Encoding = (WavEncoding)FormatCombo.SelectedIndex,
        ExportResidual = ResidualCheck.IsChecked == true,
        Overlap = ToInteger(OverlapBox.Value),
        ChunkSize = ToInteger(ChunkBox.Value),
        Theme = ThemeCombo.SelectedIndex switch { 1 => "Light", 2 => "Dark", _ => "Default" },
        Language = LanguageCombo.SelectedIndex switch { 1 => "en-US", 2 => "zh-CN", _ => null }
    };
    private static int? ToInteger(double value)
    {
        if (double.IsNaN(value)) return null;
        if (!double.IsFinite(value) || value != Math.Truncate(value) || value > int.MaxValue || value < int.MinValue)
            throw new ArgumentException(Tr("高级参数需要填写整数。"));
        return (int)value;
    }
    private async Task PersistSettingsAsync()
    {
        var settings = ReadSettings();
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.OutputDirectory);
        _ = System.IO.Path.GetFullPath(settings.OutputDirectory);
        _settings = settings;
        await SaveSettingsInOrderAsync(_settings);
    }

    private void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (!_ready) return;
        var tag = args.IsSettingsSelected ? "settings" : (args.SelectedItem as NavigationViewItem)?.Tag?.ToString();
        ShowPage(tag ?? "separate");
    }
    private void ShowPage(string tag)
    {
        if (tag == "models") RefreshCatalogAvailability();
        if (tag == "melody") _melodyWorkspace?.RefreshModelAvailability();
        ContentScroll.Visibility = tag == "melody" ? Visibility.Collapsed : Visibility.Visible;
        MelodyHost.Visibility = tag == "melody" ? Visibility.Visible : Visibility.Collapsed;
        if (tag == "melody") _mixer?.Pause();
        else _melodyWorkspace?.Pause();
        SeparatePage.Visibility = tag == "separate" ? Visibility.Visible : Visibility.Collapsed;
        ModelsPage.Visibility = tag == "models" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = tag == "settings" ? Visibility.Visible : Visibility.Collapsed;
        RunFooter.Visibility = tag == "separate" ? Visibility.Visible : Visibility.Collapsed;
        TimelineTransport.Visibility = tag == "separate" && _trackViews.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ContentScroll.ChangeView(null, 0, null, true);
    }
    private NavigationViewItem NavigationItem(string tag) => Navigation.MenuItems.OfType<NavigationViewItem>().First(item => Equals(item.Tag, tag));
    internal async Task OpenMelodyDraftAsync(string path)
    {
        Navigation.SelectedItem = NavigationItem("melody");
        try { if (_melodyWorkspace != null) await _melodyWorkspace.OpenDraftAsync(path); }
        catch (Exception error) { ShowError(error); }
    }
    private void ManageModels_Click(object sender, RoutedEventArgs e) => Navigation.SelectedItem = NavigationItem("models");
    private void OpenTranscriptionModels(string variant)
    {
        foreach (var combo in new[] { QuantFilter, TrackFilter, PlatformFilter, OutputFilter, ArchitectureFilter, ExportFilter }) combo.SelectedIndex = 0;
        MuScriptorService.GetModel(variant);
        ModelSearch.Text = "MuScriptor";
        PurposeFilter.SelectedIndex = 2;
        Navigation.SelectedItem = NavigationItem("models");
    }
    private async void MuScriptorAccess_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string variant }) return;
        try { await Windows.System.Launcher.LaunchUriAsync(new Uri("https://huggingface.co/" + MuScriptorService.GetModel(variant).Repository)); }
        catch (Exception error) { ShowError(error); }
    }

    private async Task<string> ExtractMelodyVocalAsync(string input, IProgress<SeparationProgress> progress, CancellationToken ct)
    {
        if (_runCancellation != null) throw new InvalidOperationException(Tr("请等待当前音频分离完成后再识别旋律。"));
        if (new EngineManager(_paths).Find(_settings.EnginePath) == null)
            throw new FileNotFoundException(Tr("未找到 Stemora Infra。请使用完整发布包，或先运行 scripts/Build-Native.ps1 构建后端。"));
        var repository = new ModelRepository(_paths);
        var model = ModelRepository.SeparationCatalog.Where(m => m.Stems?.Contains("vocals") == true)
            .OrderByDescending(m => repository.IsInstalled(m)).ThenByDescending(m => m.Id == "bs-roformer-sw-q8_0").First();
        if (!repository.IsInstalled(model))
        {
            progress.Report(new("download", null, Tr("正在下载人声分离模型：{0}", model.SizeLabel)));
            await repository.DownloadAsync(model, new Progress<DownloadProgress>(p => progress.Report(new("download", p.Fraction, Tr("正在下载人声分离模型")))), ct);
            RefreshModels(_settings.ModelPath);
        }
        var result = await new SeparationService(_paths).SeparateAsync(new(input, repository.GetPath(model), _settings.OutputDirectory,
            _settings.Device, Encoding: WavEncoding.Float32, ExportResidual: false, EnginePath: _settings.EnginePath),
            new Progress<SeparationProgress>(p => progress.Report(new("separate", p.Fraction, p.Message))), ct);
        var vocal = result.Tracks?.FirstOrDefault(track => track.Id == "vocals");
        return vocal?.Path ?? throw new InvalidDataException(Tr("分离结果没有已知人声音轨，请试听选择人声。"));
    }

    private async void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = NewFilePicker(AudioPipeline.Extensions);
            var files = await picker.PickMultipleFilesAsync();
            AddFiles(files.Select(f => f.Path));
        }
        catch (Exception ex) { ShowError(ex); }
    }
    private FileOpenPicker NewFilePicker(IEnumerable<string> extensions)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.MusicLibrary, ViewMode = PickerViewMode.List };
        foreach (var extension in extensions) picker.FileTypeFilter.Add(extension);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        return picker;
    }
    private void AddFiles(IEnumerable<string> files)
    {
        if (_runCancellation != null) return;
        var rejected = new List<string>();
        foreach (var raw in files)
        {
            var path = System.IO.Path.GetFullPath(raw);
            if (!AudioPipeline.Extensions.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase) || !File.Exists(path)) { rejected.Add(System.IO.Path.GetFileName(path)); continue; }
            if (!_jobs.Any(j => j.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) _jobs.Add(new(path));
        }
        UpdateQueueState();
        if (_jobs.Count > 0) SetRunStatus("待处理 {0} 个文件", _jobs.Count(j => j.Result == null));
        if (QueueList.SelectedItem == null && _jobs.Count > 0) QueueList.SelectedIndex = 0;
        if (rejected.Count > 0) ShowNotice(Tr("已跳过不支持的文件"), string.Join("、", rejected), InfoBarSeverity.Warning);
    }
    private void Input_DragOver(object sender, DragEventArgs e)
    {
        if (_runCancellation == null && e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = Tr("添加到分离队列");
        }
    }
    private async void Input_Drop(object sender, DragEventArgs e)
    {
        if (_runCancellation != null || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var deferral = e.GetDeferral();
        try { AddFiles((await e.DataView.GetStorageItemsAsync()).OfType<StorageFile>().Select(f => f.Path)); }
        catch (Exception ex) { ShowError(ex); }
        finally { deferral.Complete(); }
    }
    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _timelineCancellation?.Cancel();
        _mixer?.Dispose(); _mixer = null;
        _jobs.Clear();
        UpdateQueueState();
        TracksPanel.Children.Clear(); _trackViews.Clear();
        PreviewSection.Visibility = Visibility.Collapsed;
        TimelineTransport.Visibility = Visibility.Collapsed;
        PreviewLabel.Text = "";
        SetRunStatus("添加音频以开始");
        RunProgress.Visibility = Visibility.Collapsed;
        RunProgress.Value = 0;
    }
    private void UpdateQueueState()
    {
        QueueCount.Text = _jobs.Count == 0 ? "" : Tr("{0} 个文件", _jobs.Count);
        DropArea.Visibility = _jobs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        QueueList.Visibility = _jobs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        StartButton.IsEnabled = _runCancellation == null && _jobs.Any(j => j.Result == null);
        ClearButton.IsEnabled = _runCancellation == null && _jobs.Count > 0;
    }
    private void Queue_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || QueueList.SelectedItem is not AudioJob job) return;
        RefreshPreview(job);
    }
    private async void RefreshPreview(AudioJob job)
    {
        SetupExpander.IsExpanded = false;
        await LoadTimelineAsync(job);
    }
    private void Workspace_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ConfigColumn == null) return;
        var narrow = e.NewSize.Width < 780;
        Workspace.ColumnDefinitions[1].Width = new GridLength(narrow ? 0 : 300);
        Grid.SetColumn(ConfigColumn, narrow ? 0 : 1); Grid.SetRow(ConfigColumn, narrow ? 1 : 0);
        ConfigColumn.Margin = new Thickness(0, narrow ? 32 : 0, 0, 0);
    }

    private async void Start_Click(object sender, RoutedEventArgs e) => await RunQueueAsync();
    private async Task RunQueueAsync()
    {
        if (_runCancellation != null) return;
        if (_melodyWorkspace?.IsBusy == true) { ShowNotice(Tr("正在识别旋律"), Tr("请等待旋律处理完成后再开始分离。"), InfoBarSeverity.Informational); return; }
        var pending = _jobs.Where(j => j.Result == null).ToArray();
        if (pending.Length == 0) { ShowNotice(Tr("添加音频"), Tr("请添加尚未分离的音频。"), InfoBarSeverity.Informational); return; }
        CancellationTokenSource? cancellation = null;
        try
        {
            await PersistSettingsAsync();
            if (_settings.ModelPath == null) { Navigation.SelectedItem = NavigationItem("models"); ShowNotice(Tr("先选择一个模型"), Tr("下载仓库模型，或导入本地 GGUF 文件。"), InfoBarSeverity.Informational); return; }
            if (new EngineManager(_paths).Find(_settings.EnginePath) == null)
            { Navigation.SelectedItem = Navigation.SettingsItem; ShowNotice(Tr("未找到推理引擎"), Tr("请检查 Stemora Infra 文件，或选择已有引擎。"), InfoBarSeverity.Informational); return; }
            var snapshot = _settings;
            var options = new SeparationOptions(pending[0].Path, snapshot.ModelPath!, snapshot.OutputDirectory,
                snapshot.Device, snapshot.ChunkSize, snapshot.Overlap, snapshot.Encoding, snapshot.ExportResidual, snapshot.EnginePath);
            SeparationService.ValidateOptions(options);
            cancellation = new CancellationTokenSource();
            _runCancellation = cancellation;
            SetRunning(true); Notice.IsOpen = false;
            var succeeded = 0; var failed = 0;
            var service = new SeparationService(_paths);
            for (var i = 0; i < pending.Length; i++)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var job = pending[i];
                job.IsRunning = true; QueueList.SelectedItem = job;
                var current = i;
                var progress = new Progress<SeparationProgress>(p =>
                {
                    if (_closed || job.Result != null) return;
                    job.SetStatus(p.Stage switch { "prepare" => "准备音频", "inference" => "推理中", "export" => "导出中", "completed" => "已完成", _ => "正在处理" });
                    var step = p.Stage switch { "prepare" => "正在准备音频", "inference" => "正在分离", "export" => "正在导出", "completed" => "分离完成", _ => "正在处理" };
                    _runStatus = () => $"{Tr(step)} {current + 1}/{pending.Length}" + (p.Fraction.HasValue ? $" ({p.Fraction.Value:P0})" : "");
                    RunStatus.Text = _runStatus();
                    RunProgress.IsIndeterminate = p.Fraction == null;
                    if (p.Fraction.HasValue) RunProgress.Value = p.Fraction.Value * 100;
                    AddLog($"{job.Name} ({p.Message})");
                });
                try
                {
                    job.Result = await service.SeparateAsync(options with { InputPath = job.Path }, progress, cancellation.Token);
                    succeeded++;
                    job.SetStatus("已完成 ({0:F1} 秒)", job.Result.ElapsedSeconds);
                    if (QueueList.SelectedItem == job) RefreshPreview(job);
                }
                catch (OperationCanceledException) { job.SetStatus("已取消 (可重新开始)"); throw; }
                catch (Exception ex) { failed++; job.SetStatus("失败 (可重新开始)"); ShowError(ex); }
                finally { job.IsRunning = false; }
            }
            RunProgress.IsIndeterminate = false; RunProgress.Value = 100;
            var backends = pending.Where(j => j.Result?.Backend != null).Select(j => j.Result!.Backend!).Distinct().ToArray();
            _runStatus = () => Tr("已完成 {0} 个文件", succeeded) + (failed > 0 ? Tr("，{0} 个失败", failed) : "")
                + (backends.Length > 0 ? " (" + string.Join(" / ", backends) + ")" : "");
            RunStatus.Text = _runStatus();
        }
        catch (OperationCanceledException) { SetRunStatus("已取消，未完成的音频可重新开始"); AddLog(Tr("用户取消了处理。")); }
        catch (Exception ex) { ShowError(ex); SetRunStatus("请检查设置后重试"); }
        finally
        {
            if (cancellation != null) { _runCancellation = null; cancellation.Dispose(); SetRunning(false); }
            RunProgress.IsIndeterminate = false;
        }
    }
    private void SetRunning(bool running)
    {
        AddButton.IsEnabled = !running;
        UpdateQueueState();
        ModelCombo.IsEnabled = !running; DeviceCombo.IsEnabled = !running; FormatCombo.IsEnabled = !running;
        OverlapBox.IsEnabled = !running; ChunkBox.IsEnabled = !running; ResidualCheck.IsEnabled = !running && _selectedDescriptor?.IsMultiStem != true;
        CancelButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        StartButton.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        if (running) { RunProgress.Visibility = Visibility.Visible; RunProgress.Value = 0; }
        else RunProgress.Visibility = Visibility.Collapsed;
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => _runCancellation?.Cancel();

    private void RefreshModels(string? selected = null)
    {
        selected ??= (ModelCombo.SelectedItem as ModelChoice)?.Path;
        var repository = new ModelRepository(_paths);
        var choices = ModelRepository.SeparationCatalog.Where(repository.IsInstalled)
            .OrderByDescending(m => m.Quantization == "Q8_0").Select(m => new ModelChoice(m.DisplayName, repository.GetPath(m))).ToList();
        foreach (var path in new[] { selected, _settings.ModelPath }.Where(p => p != null).Distinct())
            if (File.Exists(path) && !choices.Any(m => m.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
                choices.Add(new(System.IO.Path.GetFileName(path!), path!));
        ModelCombo.ItemsSource = choices;
        ModelCombo.SelectedItem = choices.FirstOrDefault(m => m.Path.Equals(selected, StringComparison.OrdinalIgnoreCase)) ?? choices.FirstOrDefault();
        RefreshCatalogAvailability();
    }
    private void RefreshCatalogAvailability()
    {
        var repository = new ModelRepository(_paths);
        foreach (var row in _catalog) row.Installed = repository.IsInstalled(row.Model);
        _melodyWorkspace?.RefreshModelAvailability();
    }
    private int _modelSelectionVersion;
    private ModelDescriptor? _selectedDescriptor;
    private async void ModelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || ModelCombo.SelectedItem is not ModelChoice choice) return;
        var version = ++_modelSelectionVersion;
        try
        {
            var descriptor = await ModelRepository.InspectAsync(choice.Path);
            if (version != _modelSelectionVersion) return;
            _selectedDescriptor = descriptor;
            UpdateModelSummary();
            ResidualCheck.IsEnabled = !descriptor.IsMultiStem && _runCancellation == null;
            ToolTipService.SetToolTip(ResidualCheck, descriptor.IsMultiStem ? Tr("多轨模型直接导出每种乐器，不额外生成首轨残差。") : Tr("原音频减去模型首个输出。"));
        }
        catch (Exception error) { if (version == _modelSelectionVersion) ShowError(error); }
    }
    private void ModelSearch_TextChanged(object sender, TextChangedEventArgs e) { if (_ready) FilterModels(); }
    private void QuantFilter_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (_ready) FilterModels(); }
    private void FilterModels()
    {
        static string? Tag(ComboBox combo) => (combo.SelectedItem as ComboBoxItem)?.Tag as string;
        var filters = new ModelFilters(ModelSearch.Text.Trim(), Tag(QuantFilter), Tag(OutputFilter),
            TrackFilter.SelectedIndex switch { 1 => 1, 2 => -1, _ => null }, Tag(PlatformFilter),
            Tag(ArchitectureFilter), Tag(ExportFilter), PurposeFilter.SelectedIndex switch { 1 => ModelPurpose.Separation, 2 => ModelPurpose.Transcription, _ => null });
        var rows = _catalog.Where(row => filters.Matches(row.Model)).ToList();
        CatalogList.ItemsSource = rows;
        CatalogCount.Text = Tr("{0} / {1} 个模型", rows.Count, _catalog.Count);
        CatalogEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private async void ImportModel_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var file = await NewFilePicker([".gguf"]).PickSingleFileAsync();
            if (file == null) return;
            ModelRepository.ValidateGguf(file.Path);
            RefreshModels(file.Path);
            await PersistSettingsAsync();
            ShowNotice(Tr("已导入模型"), file.Name, InfoBarSeverity.Success);
        }
        catch (Exception ex) { ShowError(ex); }
    }
    private async void DownloadModel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;
        var row = _catalog.Single(m => m.Model.Id == id);
        if (row.Model.IsTranscription && _melodyWorkspace?.IsBusy == true)
        { ShowNotice(Tr("正在识别旋律"), Tr("请等待当前旋律处理完成后再更换或下载模型。"), InfoBarSeverity.Informational); return; }
        var device = (ComputeDevice)MuScriptorDeviceCombo.SelectedIndex;
        if (row.Installed)
        {
            if (row.Model.IsTranscription)
            {
                if (_melodyWorkspace?.SelectModel(row.Model.Variant!, device) == true) Navigation.SelectedItem = NavigationItem("melody");
                return;
            }
            RefreshModels(new ModelRepository(_paths).GetPath(row.Model));
            try { await PersistSettingsAsync(); Navigation.SelectedItem = NavigationItem("separate"); }
            catch (Exception ex) { ShowError(ex); }
            return;
        }
        if (_downloadCancellation != null) { ShowNotice(Tr("正在下载"), Tr("请等待当前下载完成，或先取消。"), InfoBarSeverity.Informational); return; }
        using var cancellation = new CancellationTokenSource();
        _downloadCancellation = cancellation;
        StartDownloadStatus(row.Model.DisplayName, row.Model.IsTranscription);
        var completed = false;
        var token = row.Model.IsTranscription ? MuScriptorToken.Password : null;
        if (row.Model.IsTranscription) MuScriptorToken.Password = "";
        MuScriptorDeviceCombo.IsEnabled = MuScriptorToken.IsEnabled = false;
        try
        {
            var path = await new ModelRepository(_paths).DownloadAsync(row.Model, new Progress<DownloadProgress>(p =>
            {
                if (_closed || !ReferenceEquals(_downloadCancellation, cancellation)) return;
                DownloadLabel.Text = $"{row.Model.DisplayName}: {p.Message} ({p.Received / 1048576d:F1} / {row.Model.SizeLabel})";
                DownloadBar.IsIndeterminate = p.Fraction == null;
                if (p.Fraction.HasValue) DownloadBar.Value = p.Fraction.Value * 100;
            }), cancellation.Token, device, token, new Progress<SeparationProgress>(p =>
            {
                if (_closed || !ReferenceEquals(_downloadCancellation, cancellation)) return;
                UpdateModelDownload(row.Model.DisplayName, p);
            }));
            if (row.Model.IsTranscription)
            {
                RefreshCatalogAvailability();
                _melodyWorkspace?.SelectModel(row.Model.Variant!, device);
                ShowNotice(Tr("模型已就绪"), Tr("{0} 已下载，可点击“使用”返回旋律转谱。", row.Model.DisplayName), InfoBarSeverity.Success);
                completed = true;
                return;
            }
            RefreshModels(path); await PersistSettingsAsync();
            ShowNotice(Tr("模型已就绪"), Tr("{0}，已设为当前模型。", row.Model.DisplayName), InfoBarSeverity.Success);
            completed = true;
        }
        catch (OperationCanceledException) { DownloadLabel.Text = Tr("下载已取消"); ShowNotice(Tr("下载已取消"), Tr("已保留断点，再次下载会继续。"), InfoBarSeverity.Informational); }
        catch (Exception ex)
        {
            DownloadLabel.Text = Tr("下载或安装失败，可重试"); DownloadDetail.Text = ex.Message;
            if (row.Model.IsTranscription && (ex.Message.Contains("GatedRepoError", StringComparison.Ordinal) || ex.Message.Contains("401", StringComparison.Ordinal)))
            {
                MuScriptorDownloadOptions.IsExpanded = true;
                ShowNotice(Tr("需要模型授权"), Tr("需要 MuScriptor 模型授权。请前往模型库，展开“MuScriptor 下载与授权”，接受模型许可后重试。"), InfoBarSeverity.Warning);
            }
            else ShowError(ex);
        }
        finally { _downloadCancellation = null; StopDownloadStatus(completed); MuScriptorDeviceCombo.IsEnabled = MuScriptorToken.IsEnabled = true; RefreshCatalogAvailability(); }
    }
    private void CancelDownload_Click(object sender, RoutedEventArgs e) => _downloadCancellation?.Cancel();
    private async void PickEngine_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var file = await NewFilePicker([".exe"]).PickSingleFileAsync();
            if (file == null) return;
            EngineBox.Text = file.Path; await PersistSettingsAsync(); UpdateEngineStatus();
        }
        catch (Exception ex) { ShowError(ex); }
    }
    private void UpdateEngineStatus()
    {
        var engine = new EngineManager(_paths).Find(string.IsNullOrWhiteSpace(EngineBox.Text) ? null : EngineBox.Text.Trim());
        RuntimeStatus.Text = engine == null ? Tr("尚未安装推理引擎。") : Tr("引擎已安装 (Stemora Infra {0})", EngineManager.InfraVersion);
        var bundled = engine != null && EngineManager.IsInfrastructure(engine);
        LocateEngineButton.Visibility = bundled ? Visibility.Collapsed : Visibility.Visible;
        ToolTipService.SetToolTip(RuntimeStatus, engine);
    }
    private async void PickOutput_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.MusicLibrary };
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var folder = await picker.PickSingleFolderAsync();
            if (folder != null) { OutputBox.Text = folder.Path; await PersistSettingsAsync(); }
        }
        catch (Exception ex) { ShowError(ex); }
    }
    private async void OpenOutput_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = (QueueList.SelectedItem as AudioJob)?.Result?.OutputDirectory ?? System.IO.Path.GetFullPath(OutputBox.Text);
            Directory.CreateDirectory(path);
            await Windows.System.Launcher.LaunchFolderAsync(await StorageFolder.GetFolderFromPathAsync(path));
        }
        catch (Exception ex) { ShowError(ex); }
    }
    private void Theme_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        Root.RequestedTheme = ThemeCombo.SelectedIndex switch { 1 => ElementTheme.Light, 2 => ElementTheme.Dark, _ => ElementTheme.Default };
        DrawTimelineRuler(); UpdateCaptionColors();
    }
    private void UpdateCaptionColors()
    {
        if (_closed) return;
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonForegroundColor = Root.ActualTheme == ElementTheme.Dark ? Colors.White : Colors.Black;
    }
    private async void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        try { await PersistSettingsAsync(); UpdateEngineStatus(); ShowNotice(Tr("设置已保存"), Tr("GUI 与 CLI 将共用这些偏好。"), InfoBarSeverity.Success); }
        catch (Exception ex) { ShowError(ex); }
    }
    private void ShowNotice(string title, string message, InfoBarSeverity severity)
    {
        if (_closed) return;
        Notice.Title = title; Notice.Message = message.Length > 400 ? message[..400] + Tr("…（完整信息见日志）") : message;
        Notice.Severity = severity; Notice.IsOpen = true;
    }
    private void ShowError(Exception ex)
    {
        AddLog(ex.ToString()); ShowNotice(Tr("操作未完成"), ex.Message, InfoBarSeverity.Error);
    }
    private void AddLog(string line)
    {
        if (_closed) return;
        _logs.Enqueue($"{DateTime.Now:HH:mm:ss}  {line}");
        while (_logs.Count > 120) _logs.Dequeue();
        LogText.Text = string.Join(Environment.NewLine, _logs);
    }
}
