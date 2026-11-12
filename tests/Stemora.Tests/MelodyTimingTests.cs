using Stemora.Core;

internal static class MelodyTimingTests
{
    public static async Task RunAsync(Action<bool, string> check, string input, AppPaths paths)
    {
        MelodyNote Note(double start, double end, int pitch = 60) => new(Guid.NewGuid(), start, end, pitch);
        bool Near(double a, double b) => Math.Abs(a - b) < 1e-8;
        var raw = new[] { Note(0.23, 0.48), Note(0.57, 0.98, 62), Note(1.08, 1.13, 64) };
        var snapped = MelodyTiming.Quantize(raw, 120, 0.2, 0, 2);
        check(Near(snapped[0].Start, 0.2) && Near(snapped[0].End, 0.45) && Near(snapped[1].Start, 0.575),
            "automatic quantization uses the neural beat phase, including offbeat sixteenths and rests");
        check(snapped.Select(n => n.Id).SequenceEqual(raw.Select(n => n.Id)) && snapped.Select(n => n.Pitch).SequenceEqual(raw.Select(n => n.Pitch))
            && raw[0].Start == 0.23 && snapped.All(n => !n.Edited), "automatic snapping preserves raw notes, IDs, pitches and manual-edit flags");
        check(snapped.All(n => new[] { 1d, 2, 4, 8, 16 }.Any(ticks => Near((n.End - n.Start) / 0.125, ticks)))
            && snapped.Zip(snapped.Skip(1)).All(pair => pair.First.End <= pair.Second.Start + 1e-8),
            "snapping restricts note values to regular durations and preserves neighbor boundaries");
        var collision = MelodyTiming.Quantize([Note(0.23, 0.27), Note(0.24, 0.29, 62)], 120, 0.2, 0, 2);
        check(collision.Length == 2 && collision.All(n => n.NeedsReview && n.ReviewReason != null) && collision[0].Start == collision[1].Start,
            "same-grid collisions retain both notes and flag them for review");
        var cropped = MelodyTiming.Quantize([Note(5.08, 5.13), Note(5.29, 5.4)], 120, 5.2, 5.07, 5.41);
        check(cropped.All(n => n.Start >= 5.07 && n.End <= 5.41 && n.End > n.Start)
            && cropped.All(n => Near((n.Start - 5.2) / 0.125, Math.Round((n.Start - 5.2) / 0.125))),
            "nonzero crops keep absolute source time and grid alignment inside both boundaries");
        var brief = new[] { Note(5.071, 5.09) };
        check(MelodyTiming.Quantize(brief, 120, 5.2, 5.07, 5.1).SequenceEqual(brief),
            "a crop shorter than one grid cell preserves valid notes instead of creating zero lengths");
        var document = new MelodyDocument
        {
            SourcePath = input,
            SourceSha256 = await MelodyDocument.FingerprintAsync(input),
            Duration = 2,
            RangeEnd = 2,
            Notes = raw,
            OriginalNotes = raw.ToArray()
        };
        var analysis = new MelodyMusicAnalysis(120, 0.2, 9, true, MuScriptorService.AnalysisRevision);
        MelodyTiming.Apply(document, analysis);
        check(document.Bpm == 120 && document.FirstBeat == 0.2 && document.BeatsPerBar == 4 && document.Key == 9 && document.KeyIsMinor
            && document.OriginalNotes.SequenceEqual(raw) && document.Undo.Count == 1 && !document.RhythmConfirmed,
            "music analysis applies BPM, bar phase and minor tonic while retaining a reversible raw snapshot");
        check(MelodyScore.Number(69, MelodyScore.NotationKey(document)) == ("6", 0) && MelodyScore.KeyLabel(document) == "6 = A",
            "A minor uses la-based numbered notation without being mislabeled as A major");
        var store = new MelodyDraftStore(paths); await store.SaveAsync(document);
        var loaded = await MelodyDraftStore.LoadAsync(store.GetPath(document.Id));
        check(loaded.MusicAnalysis == analysis && loaded.KeyIsMinor && loaded.Notes.SequenceEqual(document.Notes),
            "draft round trips preserve neural analysis provenance, key mode and snapped notes");
        new MelodyEditor(document).Undo();
        check(document.Notes.SequenceEqual(raw), "one undo restores exact pre-quantization recognition times");
        MelodyTiming.Apply(document, new(null, null, 0, false, MuScriptorService.AnalysisRevision));
        check(document.Notes.SequenceEqual(raw) && !document.KeyIsMinor, "missing beats keep raw timing and still permit a neural key result");
        document.Notes = [Note(0.2, 0.575)]; document.FirstBeat = 0.2;
        check(MelodyScore.Layout(document).All(cell => cell.Length is 1 or 2 or 4),
            "notation decomposes odd tick counts using regular tied values without dotted-note cells");
        document.FirstBeat = 1.2; document.Notes = [Note(0.1, 0.4)];
        check(Near(MelodyScore.BarOrigin(document), -0.8) && MelodyScore.Layout(document)[0].Bar == 0,
            "bar ruler and notation share a stable pickup-bar origin before the first full downbeat");
        foreach (var invalid in new[] { analysis with { Bpm = double.NaN }, analysis with { FirstBeat = 3 }, analysis with { Key = 12 }, analysis with { FirstBeat = null } })
        {
            try { invalid.Validate(2); throw new Exception("Invalid music analysis accepted"); }
            catch (InvalidDataException) { check(true, "draft validation rejects malformed neural analysis"); }
        }
    }
}
