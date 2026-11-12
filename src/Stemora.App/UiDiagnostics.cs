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
    internal async Task CaptureUiAsync(string folder, string? input, bool run, string? model = null, string? resultFile = null, string? melodyDraft = null, bool melodyOnly = false)
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
        if (melodyDraft != null || melodyOnly)
        {
            await CaptureMelodyUiAsync(folder, melodyDraft);
            await File.WriteAllTextAsync(System.IO.Path.Combine(folder, "capture-complete.txt"), "Saved melody preview regression completed successfully.\n");
            return;
        }
        Root.RequestedTheme = ElementTheme.Light;
        var originalLanguage = Strings.Language;
        var originalPreference = LanguageCombo.SelectedIndex;
        LanguageCombo.SelectedIndex = 1;
        await _settingsSaveTask;
        if (_paths.LoadSettings().Language != "en-US") throw new InvalidOperationException("English preference was not saved.");
        await CaptureAsync(folder, "17-separation-english.png");
        if ((NavigationItem("separate") as Microsoft.UI.Xaml.Controls.NavigationViewItem)?.Content?.ToString() != "Audio separation")
            throw new InvalidOperationException("The English navigation resource did not update.");
        Navigation.SelectedItem = NavigationItem("models");
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
        if ((NavigationItem("separate") as Microsoft.UI.Xaml.Controls.NavigationViewItem)?.Content?.ToString() != "音频分离")
            throw new InvalidOperationException("The Chinese navigation resource did not update.");
        await CaptureAsync(folder, "21-settings-chinese.png");
        Navigation.SelectedItem = NavigationItem("models");
        await CaptureAsync(folder, "26-models-chinese.png");
        PurposeFilter.SelectedIndex = 2;
        ArchitectureFilter.SelectedIndex = 3;
        ExportFilter.SelectedIndex = 3;
        if (CatalogList.ItemsSource is not List<ModelRow> { Count: 3 } transcriptionRows
            || transcriptionRows.Any(row => !row.Model.IsTranscription))
            throw new InvalidOperationException("MuScriptor models did not appear under the combined library filters.");
        await CaptureAsync(folder, "27-muscriptor-model-library.png");
        StartDownloadStatus("MuScriptor small", true);
        UpdateModelDownload("MuScriptor small", new("runtime-packages", null, "Downloading torch (2.4 GiB)"));
        await Task.Delay(1100);
        if (!DownloadBar.IsIndeterminate || DownloadDetail.Text != "Downloading torch (2.4 GiB)" || _downloadElapsed.Elapsed < TimeSpan.FromSeconds(1))
            throw new InvalidOperationException("Runtime installation did not expose its live detail and elapsed time.");
        await CaptureAsync(folder, "27a-muscriptor-install-progress.png");
        StopDownloadStatus(true);
        Root.RequestedTheme = ElementTheme.Dark;
        await CaptureAsync(folder, "28-muscriptor-model-library-dark.png");
        Strings.SetLanguage("en-US"); RefreshLanguage();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1000, 900));
        await CaptureAsync(folder, "29-muscriptor-model-library-english-compact.png");
        Root.RequestedTheme = ElementTheme.Light;
        Strings.SetLanguage("zh-CN"); RefreshLanguage();
        PurposeFilter.SelectedIndex = ArchitectureFilter.SelectedIndex = ExportFilter.SelectedIndex = 0;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1320, 900));
        Root.UpdateLayout();
        await Task.Delay(100);
        VerifyLocalizedFilterSelections();
        Navigation.SelectedItem = Navigation.SettingsItem;
        LanguageCombo.SelectedIndex = originalPreference;
        await _settingsSaveTask;
        Strings.SetLanguage(originalLanguage); RefreshLanguage();
        Navigation.SelectedItem = NavigationItem("separate");
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
        Navigation.SelectedItem = NavigationItem("models");
        await CaptureAsync(folder, "03-models.png");
        Navigation.SelectedItem = Navigation.SettingsItem;
        await CaptureAsync(folder, "04-settings.png");
        Root.RequestedTheme = ElementTheme.Dark;
        Navigation.SelectedItem = NavigationItem("separate");
        await CaptureAsync(folder, "05-separation-dark.png");
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1000, 900));
        Root.RequestedTheme = ElementTheme.Light;
        await CaptureAsync(folder, "06-compact.png");
        ContentScroll.ChangeView(null, ContentScroll.ScrollableHeight, null, true);
        await CaptureAsync(folder, "07-compact-settings.png");
        Navigation.SelectedItem = NavigationItem("models");
        await CaptureAsync(folder, "11-compact-models.png");
        Navigation.SelectedItem = Navigation.SettingsItem;
        await CaptureAsync(folder, "12-compact-preferences.png");
        Navigation.SelectedItem = NavigationItem("separate");
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
        await CaptureMelodyUiAsync(folder);
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
    private async Task CaptureMelodyUiAsync(string folder, string? draft = null)
    {
        if (_melodyWorkspace == null) throw new InvalidOperationException("Melody workspace was not initialized.");
        Navigation.SelectedItem = NavigationItem("melody");
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1320, 1000));
        Root.RequestedTheme = ElementTheme.Light;
        Strings.SetLanguage("zh-CN"); RefreshLanguage();
        await CaptureAsync(folder, "30-melody-empty.png");
        if (draft == null) { await _melodyWorkspace.LoadDiagnosticAsync(folder); await _melodyWorkspace.VerifyDiagnosticPlaybackAsync(); VerifyMelodyShortcuts(); }
        else await _melodyWorkspace.LoadDiagnosticDraftAsync(draft);
        await _melodyWorkspace.ExportDiagnosticPdfAsync(System.IO.Path.Combine(folder, "melody-fixture.zh-CN.pdf"));
        await CaptureAsync(folder, "31-melody-editor.png");
        Root.RequestedTheme = ElementTheme.Dark;
        await CaptureAsync(folder, "32-melody-dark.png");
        Root.RequestedTheme = ElementTheme.Light;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1000, 900));
        await CaptureAsync(folder, "33-melody-compact.png");
        Strings.SetLanguage("en-US"); RefreshLanguage();
        await CaptureAsync(folder, "34-melody-english.png");
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1320, 1000));
        _melodyWorkspace.ShowDiagnosticScore();
        await CaptureAsync(folder, "35-melody-score.png");
        await _melodyWorkspace.VerifyDiagnosticScoreAsync();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1000, 900));
        Root.RequestedTheme = ElementTheme.Dark;
        Strings.SetLanguage("zh-CN"); RefreshLanguage();
        await CaptureAsync(folder, "36-melody-score-compact-dark.png");
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1320, 1000));
        Root.RequestedTheme = ElementTheme.Light;
        await _melodyWorkspace.VerifyDiagnosticScoreAsync();
        await _melodyWorkspace.ExportDiagnosticPdfAsync(System.IO.Path.Combine(folder, "melody-fixture.pdf"));
        await File.WriteAllTextAsync(System.IO.Path.Combine(folder, "melody-fixture.txt"), draft == null
            ? "UI diagnostics use synthetic notes and audio, not model inference.\n"
            : "Regression uses an existing local melody draft; repeated preview, pagination and resizing preserve notes and the timeline.\n");
    }
    private void VerifyMelodyShortcuts()
    {
        if (_melodyWorkspace == null) throw new InvalidOperationException("Missing melody workspace.");
        _melodyWorkspace.Pause();
        var navigation = NavigationItem("melody"); navigation.Focus(FocusState.Programmatic);
        var focus = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(Root.XamlRoot) as DependencyObject;
        if (!TryMelodyShortcut(Windows.System.VirtualKey.Space, false, false, false, false, focus) || !_melodyWorkspace.IsPlaying)
            throw new InvalidOperationException("Space did not start playback while navigation owned focus.");
        if (!TryMelodyShortcut(Windows.System.VirtualKey.Space, false, false, false, true, focus) || !_melodyWorkspace.IsPlaying)
            throw new InvalidOperationException("Holding Space repeatedly toggled playback.");
        if (!TryMelodyShortcut(Windows.System.VirtualKey.Space, false, false, false, false, focus) || _melodyWorkspace.IsPlaying)
            throw new InvalidOperationException("Space did not pause playback from navigation focus.");
        foreach (var target in _melodyWorkspace.DiagnosticShortcutTargets)
        {
            target.Focus(FocusState.Programmatic);
            var targetFocus = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(Root.XamlRoot) as DependencyObject;
            if (!TryMelodyShortcut(Windows.System.VirtualKey.Space, false, false, false, false, targetFocus) || !_melodyWorkspace.IsPlaying)
                throw new InvalidOperationException("Space did not start playback from " + target.GetType().Name);
            if (!TryMelodyShortcut(Windows.System.VirtualKey.Space, false, false, false, false, targetFocus) || _melodyWorkspace.IsPlaying)
                throw new InvalidOperationException("Space did not pause playback from " + target.GetType().Name);
        }
        foreach (var input in new DependencyObject[] { new Microsoft.UI.Xaml.Controls.TextBox(), new Microsoft.UI.Xaml.Controls.NumberBox(), new Microsoft.UI.Xaml.Controls.ComboBox(), new Microsoft.UI.Xaml.Controls.ContentDialog() })
            if (TryMelodyShortcut(Windows.System.VirtualKey.Space, false, false, false, false, input))
                throw new InvalidOperationException("Playback stole Space from a text field or dialog.");
        VerifyMelodyEditingShortcuts();
        _melodySpacePressed = false;
    }
    private void VerifyMelodyEditingShortcuts()
    {
        var workspace = _melodyWorkspace!;
        var document = workspace.DiagnosticDocument!;
        var roll = workspace.DiagnosticRoll;
        var saved = document with { Notes = document.Notes.ToArray(), Selection = document.Selection.ToArray(), Undo = document.Undo.ToList(), Redo = document.Redo.ToList() };
        var snap = roll.SnapDivisions;
        bool Send(Windows.System.VirtualKey key, bool control = false, bool shift = false, bool repeat = false, DependencyObject? focus = null) =>
            TryMelodyShortcut(key, control, shift, false, repeat, focus ?? Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(Root.XamlRoot) as DependencyObject);
        static bool Near(double a, double b) => Math.Abs(a - b) < 1e-8;
        try
        {
            document.Bpm = 120; document.FirstBeat = 0.2; document.BeatsPerBar = 4;
            var first = new MelodyNote(Guid.NewGuid(), 1.03, 1.43, 60);
            var second = new MelodyNote(Guid.NewGuid(), 2.3, 2.7, 64);
            document.Notes = [first, second]; document.Selection = [first.Id]; document.Undo = []; document.Redo = [];
            roll.SnapDivisions = 4; roll.FocusEditor();
            var editorFocus = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(Root.XamlRoot) as DependencyObject;
            var focusInRoll = false;
            for (var element = editorFocus; element != null; element = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(element))
                if (ReferenceEquals(element, roll)) focusInRoll = true;
            if (!focusInRoll) throw new InvalidOperationException("Selecting the piano roll cannot acquire keyboard focus.");
            if (!Send(Windows.System.VirtualKey.Up) || document.Notes[0].Pitch != 61
                || !Send(Windows.System.VirtualKey.Up, repeat: true) || document.Notes[0].Pitch != 62
                || !Send(Windows.System.VirtualKey.Down) || document.Notes[0].Pitch != 61
                || !Send(Windows.System.VirtualKey.Down, shift: true) || document.Notes[0].Pitch != 49
                || !Send(Windows.System.VirtualKey.Up, shift: true) || document.Notes[0].Pitch != 61)
                throw new InvalidOperationException("Pitch, octave, or held-arrow shortcuts did not edit the selection.");
            document.Notes = [first, second]; document.Undo = []; document.Redo = [];
            workspace.DiagnosticShortcutTargets.First().Focus(FocusState.Programmatic);
            if (!Send(Windows.System.VirtualKey.Right) || !Near(document.Notes[0].Start, 1.075) || !Near(document.Notes[0].End, 1.475))
                throw new InvalidOperationException("An off-grid note did not move to the next grid while a toolbar button owned focus.");
            Send(Windows.System.VirtualKey.Z, control: true);
            if (!Send(Windows.System.VirtualKey.Left) || !Near(document.Notes[0].Start, 0.95))
                throw new InvalidOperationException("Left did not move to the previous grid.");
            Send(Windows.System.VirtualKey.Z, control: true);
            if (!Send(Windows.System.VirtualKey.Right, shift: true) || !Near(document.Notes[0].End, 1.45) || document.Notes[0].Start != first.Start)
                throw new InvalidOperationException("Shift+Right moved the onset instead of extending the note.");
            if (!Send(Windows.System.VirtualKey.Left, shift: true) || !Near(document.Notes[0].End, 1.325))
                throw new InvalidOperationException("Shift+Left did not shorten the note by a grid step.");
            document.Notes = [first, second]; document.Undo = []; document.Redo = []; roll.SnapDivisions = 0;
            if (!Send(Windows.System.VirtualKey.Right) || !Near(document.Notes[0].Start, first.Start + 0.05))
                throw new InvalidOperationException("Free timing did not use 0.05-second nudges.");
            Send(Windows.System.VirtualKey.Z, control: true);
            foreach (var key in new[] { Windows.System.VirtualKey.Delete, Windows.System.VirtualKey.Back })
            {
                if (!Send(key) || document.Notes.Length != 1 || document.Notes[0].Id != second.Id)
                    throw new InvalidOperationException("A delete shortcut did not remove only the selected note.");
                if (!Send(Windows.System.VirtualKey.Z, control: true) || !document.Notes.SequenceEqual(new[] { first, second }))
                    throw new InvalidOperationException("Undo did not restore exact note timing and IDs after deletion.");
            }
            Send(Windows.System.VirtualKey.Z, control: true, shift: true);
            if (document.Notes.Length != 1) throw new InvalidOperationException("Ctrl+Shift+Z did not redo deletion.");
            Send(Windows.System.VirtualKey.Z, control: true);
            Send(Windows.System.VirtualKey.Y, control: true);
            if (document.Notes.Length != 1) throw new InvalidOperationException("Ctrl+Y did not redo deletion.");
            Send(Windows.System.VirtualKey.Z, control: true);
            if (!Send(Windows.System.VirtualKey.A, control: true) || document.Selection.Length != 2)
                throw new InvalidOperationException("Ctrl+A did not select all notes.");
            Send(Windows.System.VirtualKey.Up);
            if (document.Notes[0].Pitch != 61 || document.Notes[1].Pitch != 65)
                throw new InvalidOperationException("Pitch shortcuts did not apply to a multiple selection.");
            Send(Windows.System.VirtualKey.Z, control: true);
            document.Selection = [first.Id]; document.Position = 1.2;
            if (!Send(Windows.System.VirtualKey.S) || document.Notes.Length != 3 || !Near(document.Notes[0].End, 1.2))
                throw new InvalidOperationException("S did not split at the playhead.");
            Send(Windows.System.VirtualKey.Z, control: true);
            if (!Send(Windows.System.VirtualKey.F) || document.ViewStart > first.Start || document.ViewStart + document.ViewDuration < first.End)
                throw new InvalidOperationException("F did not reveal the selected note.");
            if (!Send(Windows.System.VirtualKey.Escape) || document.Selection.Length != 0)
                throw new InvalidOperationException("Esc did not clear the selection.");
            document.Selection = [first.Id];
            foreach (var input in new DependencyObject[] { new Microsoft.UI.Xaml.Controls.TextBox(), new Microsoft.UI.Xaml.Controls.NumberBox(), new Microsoft.UI.Xaml.Controls.ComboBox(), new Microsoft.UI.Xaml.Controls.ContentDialog(), new Microsoft.UI.Xaml.Controls.Slider() })
                foreach (var key in new[] { Windows.System.VirtualKey.Delete, Windows.System.VirtualKey.Up, Windows.System.VirtualKey.Left, Windows.System.VirtualKey.S })
                    if (Send(key, focus: input)) throw new InvalidOperationException("Editing stole a key from an input field, dialog, or slider.");
            if (Send(Windows.System.VirtualKey.Up, focus: NavigationItem("melody")))
                throw new InvalidOperationException("Editing stole navigation's arrow keys.");
            document.ViewStart = 2.7; document.ViewDuration = 4; roll.SnapDivisions = 4;
            var bars = roll.VisibleBars(72).ToArray();
            if (bars.Length < 2 || bars[0].Number != 2 || !Near(bars[0].Time, 2.2) || bars[1].Number != 3 || !Near(bars[1].Time, 4.2))
                throw new InvalidOperationException("The panned bar ruler lost its partial-bar number or first-beat alignment.");
            document.BeatsPerBar = 3;
            if (!Near(roll.VisibleBars(72).First().Time, 1.7)) throw new InvalidOperationException("The bar ruler did not follow the time signature.");
        }
        finally
        {
            document.Notes = saved.Notes; document.Selection = saved.Selection; document.Undo = saved.Undo; document.Redo = saved.Redo;
            document.Bpm = saved.Bpm; document.FirstBeat = saved.FirstBeat; document.BeatsPerBar = saved.BeatsPerBar; document.RhythmConfirmed = saved.RhythmConfirmed;
            document.ViewStart = saved.ViewStart; document.ViewDuration = saved.ViewDuration; document.Position = saved.Position;
            roll.SnapDivisions = snap; workspace.RefreshDiagnosticEditor();
        }
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
        foreach (var combo in new[] { QuantFilter, TrackFilter, PlatformFilter, OutputFilter, ArchitectureFilter, ExportFilter, PurposeFilter })
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
