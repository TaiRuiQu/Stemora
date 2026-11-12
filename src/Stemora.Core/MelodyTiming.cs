using static Stemora.Core.Strings;

namespace Stemora.Core;

public sealed record MelodyMusicAnalysis(double? Bpm, double? FirstBeat, int? Key, bool IsMinor, string Revision)
{
    public void Validate(double duration)
    {
        if (Bpm is { } bpm && (!double.IsFinite(bpm) || bpm is < 20 or > 400)
            || FirstBeat is { } beat && (!double.IsFinite(beat) || beat < 0 || beat > duration)
            || Bpm.HasValue != FirstBeat.HasValue || Key is < 0 or > 11 || string.IsNullOrWhiteSpace(Revision))
            throw new InvalidDataException(Tr("节奏与调性分析结果无效。"));
    }
}

public static class MelodyTiming
{
    // One quarter-note beat contains four sixteenth-note ticks. No dotted notes or tuplets.
    private static readonly int[] Lengths = [1, 2, 4, 8, 16];
    public static void Apply(MelodyDocument document, MelodyMusicAnalysis analysis)
    {
        analysis.Validate(document.Duration);
        document.MusicAnalysis = analysis;
        if (analysis.Key is { } key) { document.Key = key; document.KeyIsMinor = analysis.IsMinor; }
        document.RhythmConfirmed = false;
        if (analysis.Bpm is not { } bpm || analysis.FirstBeat is not { } firstBeat) return;
        document.Bpm = bpm; document.FirstBeat = firstBeat; document.BeatsPerBar = 4;
        new MelodyEditor(document).Commit(Quantize(document.Notes, bpm, firstBeat, document.RangeStart, document.RangeEnd));
    }

    public static MelodyNote[] Quantize(MelodyNote[] notes, double bpm, double firstBeat, double start, double end)
    {
        if (!double.IsFinite(bpm) || bpm is < 20 or > 400 || !double.IsFinite(firstBeat)
            || !double.IsFinite(start) || !double.IsFinite(end) || start < 0 || end <= start)
            throw new ArgumentException(Tr("选段起止时间无效。"));
        var step = 60 / bpm / 4;
        var min = (long)Math.Ceiling((start - firstBeat) / step - 1e-8);
        var max = (long)Math.Floor((end - firstBeat) / step + 1e-8);
        if (max <= min) return notes.ToArray();
        var groups = notes.GroupBy(n => Math.Clamp((long)Math.Round((n.Start - firstBeat) / step, MidpointRounding.AwayFromZero), min, max - 1))
            .OrderBy(g => g.Key).ToArray();
        var result = new List<MelodyNote>(notes.Length);
        for (var i = 0; i < groups.Length; i++)
        {
            var group = groups[i];
            var available = (i + 1 < groups.Length ? groups[i + 1].Key : max) - group.Key;
            foreach (var note in group)
            {
                var target = (note.End - note.Start) / step;
                var length = Lengths.Where(value => value <= available).MinBy(value => Math.Abs(value - target));
                var collision = group.Count() > 1;
                result.Add(note with
                {
                    Start = Math.Clamp(firstBeat + group.Key * step, start, end),
                    End = Math.Clamp(firstBeat + (group.Key + length) * step, start, end),
                    NeedsReview = note.NeedsReview || collision,
                    ReviewReason = collision ? "多个音符吸附到同一位置，请核对" : note.ReviewReason
                });
            }
        }
        return result.OrderBy(n => n.Start).ThenBy(n => n.Pitch).ToArray();
    }
}
