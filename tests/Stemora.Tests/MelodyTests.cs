using System.Text;
using NAudio.Wave;
using Stemora.Core;

internal static class MelodyTests
{
    public static async Task RunAsync(Action<bool, string> check, string root, string input, AppPaths paths)
    {
        await MelodyTimingTests.RunAsync(check, input, paths);
        MelodyNote Note(double start, double end, int pitch = 60) => new(Guid.NewGuid(), start, end, pitch);
        var a = Note(1, 2); var b = Note(2.1, 3, 62); var c = Note(5, 6, 65);
        var document = new MelodyDocument
        {
            SourcePath = input,
            SourceSha256 = await MelodyDocument.FingerprintAsync(input),
            Duration = 12,
            RangeEnd = 12,
            Notes = [a, b, c],
            OriginalNotes = [a, b, c],
            Selection = [a.Id],
            ViewStart = 0.5,
            ViewDuration = 8
        };
        var editor = new MelodyEditor(document);
        editor.Transform(semitones: 1);
        check(document.Notes[0].Pitch == 61 && document.Notes[0].Id == a.Id && document.OriginalNotes[0].Pitch == 60, "melody edits retain IDs and immutable original notes");
        editor.Undo(); check(document.Notes[0] == a && document.ViewStart == 0.5 && document.Selection.SequenceEqual([a.Id]), "melody undo restores notes, viewport and selection");
        editor.Redo(); check(document.Notes[0].Pitch == 61, "melody redo restores edits");
        editor.Transform(move: 100);
        check(Math.Abs(document.Notes[0].End - b.Start) < 0.00001 && document.Notes[1] == b, "moving a note stops at the next note without shifting it");
        editor.Undo(); editor.Transform(resizeEnd: 10);
        check(document.Notes[0].End == b.Start && document.Notes[1] == b, "lengthening stops at adjacent boundary");
        editor.Undo(); editor.Split(1.4);
        check(document.Notes.Length == 4 && document.Notes[0].End == 1.4 && document.Notes[1].Start == 1.4 && document.Notes[0].Id == a.Id, "split retains original seconds and one stable source ID");
        editor.Undo(); document.Selection = [b.Id, a.Id]; editor.Merge();
        check(document.Notes.Length == 2 && document.Notes[0].Pitch == 62 && document.Notes[0].Start == 1 && document.Notes[0].End == 3 && document.Notes[0].SourceIds!.Length == 2, "merge uses first selected pitch with explicit source links");
        editor.Undo(); document.Selection = [a.Id, c.Id];
        try { editor.Merge(); throw new Exception("Merged across rest"); } catch (InvalidOperationException) { check(true, "merge rejects long rests and nonconsecutive selection"); }
        document.Selection = [b.Id]; editor.Delete();
        check(document.Notes.Length == 2 && document.Notes[1] == c, "deletion leaves later notes at their original positions");
        editor.Add(3.2, 3.8, 67);
        check(document.Notes.Any(n => n.Start == 3.2 && n.Pitch == 67 && n.Edited), "manual note addition uses absolute seconds");
        try { editor.Add(1.2, 1.8, 60); throw new Exception("Added overlapping note"); } catch (InvalidOperationException) { check(true, "adding notes cannot overwrite adjacent notes"); }
        var before = document.Notes.ToArray(); document.Selection = [a.Id];
        var preview = editor.Transform(semitones: 2, commit: false);
        check(document.Notes.SequenceEqual(before) && preview.First(n => n.Id == a.Id).Pitch == before.First(n => n.Id == a.Id).Pitch + 2, "drag previews do not create history or mutate the saved notes");
        document.Notes = [a with { NeedsReview = true, ReviewReason = "short" }, b]; document.Selection = [a.Id]; editor.MarkReviewed();
        check(!document.Notes[0].NeedsReview && document.Notes[0].Edited, "review confirmation persists separately from model output");
        editor.Undo(); document.Selection = [a.Id, b.Id]; editor.Transform(resizeEnd: 5);
        check(document.Notes[0].End <= document.Notes[1].Start, "resizing a selection preserves internal neighbor boundaries");
        var store = new MelodyDraftStore(paths);
        await store.SaveAsync(document);
        var loaded = await MelodyDraftStore.LoadAsync(store.GetPath(document.Id));
        check(loaded.Notes.SequenceEqual(document.Notes) && loaded.Undo.Count == document.Undo.Count && loaded.SourceSha256 == document.SourceSha256,
            "atomic melody drafts restore notes, undo history and content identity");
        check((await store.ListAsync()).Any(draft => draft.Path == store.GetPath(document.Id)), "saved drafts are discoverable from the in-app recent draft list");
        loaded.Selection = [Guid.NewGuid()];
        try { loaded.Validate(); throw new Exception("Invalid selection accepted"); } catch (InvalidDataException) { check(true, "draft validation rejects stale selection IDs"); }
        loaded.Selection = []; loaded.Notes = null!;
        try { loaded.Validate(); throw new Exception("Null notes accepted"); } catch (InvalidDataException) { check(true, "malformed null note collections produce a recoverable draft error"); }
        var movedSource = Path.Combine(root, "moved-source.wav"); File.Copy(input, movedSource);
        check(await MelodyDocument.FingerprintAsync(movedSource) == document.SourceSha256, "source identity survives relocation");
        await File.AppendAllTextAsync(movedSource, "changed");
        check(await MelodyDocument.FingerprintAsync(movedSource) != document.SourceSha256, "source cache detects changed content at the same path");
        document.Notes = [a with { End = double.NaN }];
        try { await store.SaveAsync(document); throw new Exception("Saved NaN"); } catch (InvalidDataException) { check(File.Exists(store.GetPath(document.Id)), "invalid note data never replaces the previous draft"); }
        document.Notes = [a, b, c]; document.Bpm = 120; document.FirstBeat = 1;
        var score = MelodyScore.Layout(document);
        check(score.Any(cell => cell.Number == "0") && score.Any(cell => cell.NoteId == a.Id) && score.All(cell => cell.Length is >= 1 and <= 4), "numbered score retains rests, durations and stable note links");
        check(MelodyScore.Number(60, 0) == ("1", 0) && MelodyScore.Number(72, 0) == ("1", 1) && MelodyScore.Number(59, 0) == ("7", -1), "numbered notation maps tonic, semitones and octave marks");
        document.FirstBeat = 5;
        check(MelodyScore.Layout(document).Where(cell => cell.NoteId == a.Id).First().Bar < MelodyScore.Layout(document).Where(cell => cell.NoteId == c.Id).First().Bar,
            "changing first beat preserves pickup-note timing rather than collapsing early notes");
        document.FirstBeat = 1;
        var pdf = Path.Combine(root, "旋律简谱.pdf"); await MelodyScore.ExportPdfAsync(document, pdf);
        var pdfText = Encoding.ASCII.GetString(await File.ReadAllBytesAsync(pdf));
        check(pdfText.StartsWith("%PDF-1.4") && pdfText.EndsWith("%%EOF\n") && pdfText.Contains("/Type /Page ") && pdfText.Contains("xref"), "PDF export produces complete pages and cross-reference table");
        var cropped = Path.Combine(root, "range.wav"); MelodyAudio.PrepareRange(input, cropped, 0.05, 0.2, default);
        using (var wav = new WaveFileReader(cropped))
            check(wav.WaveFormat.SampleRate == 16000 && wav.WaveFormat.Channels == 1 && Math.Abs(wav.TotalTime.TotalSeconds - 0.15) < 0.001, "MuScriptor input crops and resamples without altering the original seconds");
        var synth = new MelodySynth([Note(0.1, 0.2, 69)], 0, 0.25); var samples = new float[11025]; var read = synth.Read(samples, 0, samples.Length);
        check(read == 11025 && samples.Take(4410).All(x => x == 0) && samples.Skip(4500).Take(3000).Any(x => Math.Abs(x) > 0.1) && synth.Read(samples, 0, 1) == 0,
            "synthesized melody uses source seconds with silent gaps and bounded playback");
        var sustained = Note(0, 1, 69);
        var changedNote = sustained with { Pitch = 81 };
        var live = new MelodySynth([sustained], 0, 0.5);
        var block = new float[4410]; live.Read(block, 0, block.Length);
        live.UpdateNotes([changedNote]); live.Read(block, 0, block.Length);
        var expected = new MelodySynth([changedNote], 0.1, 0.2); var reference = new float[4410]; expected.Read(reference, 0, reference.Length);
        check(block.Skip(441).Zip(reference.Skip(441)).All(pair => Math.Abs(pair.First - pair.Second) < 0.0001),
            "live pitch changes take effect within the next audio buffer without resetting source time");
        live.UpdateNotes([sustained]); live.Read(block, 0, block.Length);
        expected = new MelodySynth([sustained], 0.2, 0.3); expected.Read(reference, 0, reference.Length);
        check(block.Skip(441).Zip(reference.Skip(441)).All(pair => Math.Abs(pair.First - pair.Second) < 0.0001),
            "cancelled pitch previews and undo immediately restore the audible original at the current time");
        live.UpdateNotes([]); live.Read(block, 0, block.Length);
        check(block.Skip(441).All(value => value == 0), "deleting a sounding note releases it without waiting for a loop");
        var monitor = new MelodyMonitor(new ClockSamples(), [sustained], 0, 0.4, MelodyListenMode.Piano);
        monitor.SetVolumes(1, 1); monitor.Read(block, 0, block.Length);
        monitor.UpdateNotes([changedNote]); monitor.SetMode(MelodyListenMode.Together); monitor.Read(block, 0, block.Length);
        expected = new MelodySynth([changedNote], 0.1, 0.2); expected.Read(reference, 0, reference.Length);
        check(block.Skip(441).Select((value, index) => Math.Abs(value - (reference[index + 441] + (4410 + index + 441) / 44100d * 0.1) * 0.65)).All(error => error < 0.0001),
            "mixed playback contains both the live edited piano and the time-aligned vocal");
        monitor.SetMode(MelodyListenMode.Vocal); monitor.Read(block, 0, block.Length);
        check(Math.Abs(block[1000] - (9820 / 44100d * 0.1 * 0.65)) < 0.00001,
            "switching to vocal-only keeps the continuously advanced source clock");
        monitor.SetMode(MelodyListenMode.Piano); monitor.Read(block, 0, block.Length);
        expected = new MelodySynth([changedNote], 0.3, 0.4); expected.Read(reference, 0, reference.Length);
        check(block.Skip(441).Zip(reference.Skip(441)).All(pair => Math.Abs(pair.First - pair.Second * 0.65) < 0.0001) && monitor.Read(block, 0, block.Length) == 0,
            "switching to piano-only preserves live edits and the playback endpoint");
        var offlineMonitor = new MelodyMonitor(null, [sustained], 0, 1, MelodyListenMode.Piano);
        foreach (var mode in new[] { MelodyListenMode.Vocal, MelodyListenMode.Together })
        {
            try { offlineMonitor.SetMode(mode); throw new Exception("Missing vocal accepted"); }
            catch (FileNotFoundException) { check(offlineMonitor.Read(block, 0, block.Length) > 0, "unavailable vocals leave piano playback usable: " + mode); }
        }
        var volumeMonitor = new MelodyMonitor(new ClockSamples(), [sustained], 0, 1, MelodyListenMode.Together);
        volumeMonitor.SetVolumes(0, 1); volumeMonitor.Read(block, 0, block.Length);
        check(Math.Abs(block[1000] - 1000 / 44100d * 0.1 * 0.65) < 0.00001, "piano volume can be muted independently in the mix");
        volumeMonitor.SetVolumes(1, 0); volumeMonitor.Read(block, 0, block.Length);
        expected = new MelodySynth([sustained], 0.1, 0.2); expected.Read(reference, 0, reference.Length);
        check(block.Skip(441).Zip(reference.Skip(441)).All(pair => Math.Abs(pair.First - pair.Second * 0.65) < 0.0001), "vocal volume can be muted without losing piano timing");
        var mixMonitor = new MelodyMonitor(new ClockSamples(), [sustained], 0, 0.5, MelodyListenMode.Piano, new ClockSamples(-1));
        mixMonitor.SetVolumes(1, 1); mixMonitor.Read(block, 0, block.Length);
        mixMonitor.SetMode(MelodyListenMode.Mix); mixMonitor.Read(block, 0, block.Length);
        check(Math.Abs(block[1000] + 5410 / 44100d * 0.1 * 0.65) < 0.00001,
            "switching to the original mix uses its continuously advanced clock and excludes piano and separated vocals");
        mixMonitor.UpdateNotes([changedNote]); mixMonitor.SetMode(MelodyListenMode.MixTogether); mixMonitor.Read(block, 0, block.Length);
        expected = new MelodySynth([changedNote], 0.2, 0.3); expected.Read(reference, 0, reference.Length);
        check(block.Skip(441).Select((value, index) => Math.Abs(value - (reference[index + 441] - (8820 + index + 441) / 44100d * 0.1) * 0.65)).All(error => error < 0.0001),
            "piano plus original mix includes live edits and the original track without summing the separated vocal twice");
        mixMonitor.SetMode(MelodyListenMode.Vocal); mixMonitor.Read(block, 0, block.Length);
        check(Math.Abs(block[1000] - 14230 / 44100d * 0.1 * 0.65) < 0.00001,
            "switching from the mix back to vocals preserves source position");
        mixMonitor.SetMode(MelodyListenMode.MixTogether); mixMonitor.SetVolumes(0, 1); mixMonitor.Read(block, 0, block.Length);
        check(Math.Abs(block[1000] + 18640 / 44100d * 0.1 * 0.65) < 0.00001 && mixMonitor.Read(block, 0, block.Length) == 0,
            "original mix playback shares volume ramps and the selection endpoint");
        var vocalOnly = new MelodyMonitor(new ClockSamples(), [sustained], 0, 1, MelodyListenMode.Vocal);
        try { vocalOnly.SetMode(MelodyListenMode.Mix); throw new Exception("Missing original mix accepted"); }
        catch (FileNotFoundException) { check(vocalOnly.Read(block, 0, block.Length) > 0, "missing original mix leaves existing vocal playback usable"); }
        var mixOnly = new MelodyMonitor(null, [sustained], 0, 1, MelodyListenMode.Mix, new ClockSamples());
        check(mixOnly.Read(block, 0, block.Length) > 0, "original mix can play when separated vocals are unavailable");
        var mixDocument = document with { SourcePath = "mix.wav", IsVocal = false, MixPath = null };
        check(MelodyPlayback.GetMixPath(mixDocument) == "mix.wav" && MelodyPlayback.GetMixPath(mixDocument with { IsVocal = true }) == null
            && MelodyPlayback.GetMixPath(mixDocument with { IsVocal = true, MixPath = "linked.wav" }) == "linked.wav",
            "original-mix selection reuses mixed sources and supports separately linked mixes for vocal-only drafts");
        mixDocument = document with { MixPath = input };
        await store.SaveAsync(mixDocument);
        check((await MelodyDraftStore.LoadAsync(store.GetPath(mixDocument.Id))).MixPath == input,
            "drafts retain the separately linked original mix across sessions");
        foreach (var pitch in Enumerable.Range(0, 128))
        {
            var voice = new PianoVoice(pitch);
            var signal = Enumerable.Range(0, 4410).Select(i => voice.Sample(i / 44100d, 0.1)).ToArray();
            if (!signal.All(float.IsFinite) || !signal.Any(x => Math.Abs(x) > 0.001)) throw new Exception("Invalid piano sample: " + pitch);
        }
        check(true, "bundled grand-piano recordings render finite audible notes over the full MIDI range");
        var piano = new PianoVoice(69);
        check(piano.Sample(-0.01, 0.4) == 0 && piano.Sample(0.53, 0.4) == 0,
            "sampled piano has bounded attack and damped note release");
        var longPiano = new MelodySynth([Note(0, 10, 69)], 0, 10); var longSignal = new float[441000]; longPiano.Read(longSignal, 0, longSignal.Length);
        double Rms(IEnumerable<float> values) => Math.Sqrt(values.Average(x => (double)x * x));
        check(Rms(longSignal.Skip(4410).Take(8820)) > Rms(longSignal.Skip(220500).Take(8820)) * 2,
            "grand-piano recordings decay naturally instead of sustaining a fixed sine tone");
        check(MuScriptorService.Models.Count == 3 && MuScriptorService.Models.All(m => m.Sha256.Length == 64 && m.Revision.Length == 40 && m.Size > 0), "MuScriptor variants pin official revisions, sizes and SHA-256");
        var unifiedCatalog = ModelRepository.Catalog;
        var transcriptionModels = unifiedCatalog.Where(m => m.IsTranscription).ToArray();
        check(transcriptionModels.Select(m => m.Id).SequenceEqual(new[] { "muscriptor-small", "muscriptor-medium", "muscriptor-large" })
            && transcriptionModels.All(m => m.FileFormat == "Safetensors" && m.Stems == null && m.ExportFormats!.SequenceEqual(["PDF"])), "MuScriptor models share the library without posing as GGUF separation models");
        check(unifiedCatalog.Count(m => new ModelFilters(Purpose: ModelPurpose.Transcription, Architecture: "MuScriptor", ExportFormat: "PDF", Platform: "cpu").Matches(m)) == 3
            && unifiedCatalog.Count(m => new ModelFilters(Purpose: ModelPurpose.Separation).Matches(m)) == 22,
            "model purpose combines with architecture, platform and export filters");
        check(!transcriptionModels.Any(m => new ModelFilters(Stem: "vocals").Matches(m) || new ModelFilters(StemCount: 1).Matches(m)),
            "transcription models never appear as vocal separation outputs");
        check(ModelRepository.SeparationCatalog.All(m => !m.IsTranscription), "separation selection excludes all MuScriptor models");
        var repository = new ModelRepository(paths);
        foreach (var model in transcriptionModels)
            check(repository.GetPath(model) == Path.Combine(paths.Models, "muscriptor", model.Variant!, "model.safetensors") && !repository.IsInstalled(model),
                "library and transcription workspace share the canonical model path: " + model.Variant);
        var modelRow = new Stemora.App.ModelRow(transcriptionModels[0], false);
        var savedLanguage = Strings.Language; Strings.SetLanguage("en-US");
        check(modelRow.TrackType == "Melody transcription" && modelRow.QuantizationLabel == "Original weights" && modelRow.ButtonLabel == "Download"
            && !modelRow.OutputSummary.Contains("Vocals", StringComparison.Ordinal) && modelRow.LicenseSummary.Contains("CC BY-NC 4.0", StringComparison.Ordinal),
            "MuScriptor library rows localize purpose, weights, outputs and license");
        modelRow.Installed = true; check(modelRow.ButtonLabel == "Use" && modelRow.State == "Downloaded", "library actions follow the shared installed state");
        Strings.SetLanguage(savedLanguage);
        var processMessages = new List<SeparationProgress>();
        await MuScriptorService.RunProcessAsync(Environment.ProcessPath!, ["--melody-process", "progress"], new ImmediateProgress<SeparationProgress>(processMessages.Add), default);
        check(processMessages.Any(p => p.Stage == "transcribe" && p.Fraction == 0.5), "MuScriptor subprocess protocol reports real progress");
        check(processMessages.Any(p => p.Stage == "loading" && p.Fraction == null), "MuScriptor null progress stays indeterminate while loading or downloading");
        var runtimeMessages = new List<SeparationProgress>();
        await MuScriptorService.RunProcessAsync(Environment.ProcessPath!, ["--melody-process", "stages"], new ImmediateProgress<SeparationProgress>(runtimeMessages.Add), default);
        check(runtimeMessages.Select(p => p.Stage).SequenceEqual(new[] { "runtime-download", "runtime-python", "runtime-packages", "runtime-verify", "runtime-ready", "download" })
            && runtimeMessages.Take(5).Select(p => MuScriptorProgress.Label(p.Stage)).Distinct().Count() == 5,
            "runtime setup exposes each real installation phase before model download");
        using (var carriageCancellation = new CancellationTokenSource())
        {
            var firstUpdate = new TaskCompletionSource<SeparationProgress>(TaskCreationOptions.RunContinuationsAsynchronously);
            var process = MuScriptorService.RunProcessAsync(Environment.ProcessPath!, ["--melody-process", "carriage"],
                new ImmediateProgress<SeparationProgress>(p => firstUpdate.TrySetResult(p)), carriageCancellation.Token, outputStage: "runtime-packages");
            try
            {
                var update = await firstUpdate.Task.WaitAsync(TimeSpan.FromSeconds(3));
                check(!process.IsCompleted && update.Stage == "runtime-packages" && update.Message.Contains("Downloading torch", StringComparison.Ordinal),
                    "carriage-return package updates reach the UI before the installer exits");
            }
            finally
            {
                carriageCancellation.Cancel();
                try { await process; } catch (OperationCanceledException) { }
            }
        }
        var tailMessages = new List<SeparationProgress>();
        await MuScriptorService.RunProcessAsync(Environment.ProcessPath!, ["--melody-process", "tail"], new ImmediateProgress<SeparationProgress>(tailMessages.Add), default, outputStage: "runtime-packages");
        check(tailMessages.Any(p => p.Stage == "runtime-packages" && p.Message == "Finished installing dependencies"), "installer output without a final newline remains visible");
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        try { await MuScriptorService.RunProcessAsync(Environment.ProcessPath!, ["--melody-process", "wait"], null, cancel.Token); throw new Exception("Process cancellation ignored"); }
        catch (OperationCanceledException) { check(true, "MuScriptor cancellation terminates the process tree"); }
        try { await MuScriptorService.RunProcessAsync(Environment.ProcessPath!, ["--melody-process", "fail"], null, default, "test-private-token"); throw new Exception("Process failure ignored"); }
        catch (InvalidOperationException error) { check(!error.Message.Contains("test-private-token") && error.Message.Contains("[redacted]"), "MuScriptor errors redact ephemeral Hugging Face credentials"); }
    }
    private sealed class ClockSamples(float gain = 1) : ISampleProvider
    {
        private int _frame;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(44100, 1);
        public int Read(float[] buffer, int offset, int count)
        {
            for (var i = 0; i < count; i++) buffer[offset + i] = (float)(_frame++ / 44100d * 0.1 * gain);
            return count;
        }
    }
    private sealed class ImmediateProgress<T>(Action<T> action) : IProgress<T> { public void Report(T value) => action(value); }
}
