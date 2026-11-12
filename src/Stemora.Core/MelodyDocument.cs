using System.Security.Cryptography;
using System.Text.Json;
using static Stemora.Core.Strings;

namespace Stemora.Core;

public sealed record MelodyNote(Guid Id, double Start, double End, int Pitch,
    bool Edited = false, bool NeedsReview = false, string? ReviewReason = null, Guid[]? SourceIds = null);
public sealed record MelodySnapshot(MelodyNote[] Notes, Guid[] Selection, double ViewStart, double ViewDuration);
public sealed record MelodyAnalysis(string Model, string Revision, DateTimeOffset CreatedAt, MelodyNote[] Notes);
public sealed record MelodySpectrum(int Columns, int MinPitch, int MaxPitch, double Start, double Duration, byte[] Values);
public sealed record MelodyDocument
{
    public int SchemaVersion { get; init; } = 1;
    public Guid Id { get; init; } = Guid.NewGuid();
    public string SourcePath { get; set; } = "";
    public string SourceSha256 { get; set; } = "";
    public string? VocalPath { get; set; }
    public string? MixPath { get; set; }
    public bool IsVocal { get; set; }
    public double Duration { get; set; }
    public double RangeStart { get; set; }
    public double RangeEnd { get; set; }
    public string Model { get; set; } = "small";
    public List<MelodyAnalysis> Analyses { get; set; } = [];
    public MelodyNote[] Notes { get; set; } = [];
    public MelodyNote[] OriginalNotes { get; set; } = [];
    public List<MelodySnapshot> Undo { get; set; } = [];
    public List<MelodySnapshot> Redo { get; set; } = [];
    public Guid[] Selection { get; set; } = [];
    public double ViewStart { get; set; }
    public double ViewDuration { get; set; } = 12;
    public double Position { get; set; }
    public double LoopStart { get; set; }
    public double LoopEnd { get; set; }
    public bool LoopEnabled { get; set; }
    // Retained only to read schema v1 drafts created before the piano-roll redesign.
    public bool ShowSpectrum { get; set; }
    public MelodySpectrum? Spectrum { get; set; }
    public double Bpm { get; set; } = 120;
    public int BeatsPerBar { get; set; } = 4;
    public int Key { get; set; }
    public bool KeyIsMinor { get; set; }
    public MelodyMusicAnalysis? MusicAnalysis { get; set; }
    public double FirstBeat { get; set; }
    public bool RhythmConfirmed { get; set; }
    public string Stage { get; set; } = "ready";
    public string? PendingStage { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset SavedAt { get; set; }

    public static async Task<string> FingerprintAsync(string path, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
    }
    public void Validate()
    {
        if (SchemaVersion != 1 || Id == Guid.Empty || string.IsNullOrWhiteSpace(SourcePath) || SourceSha256 == null || SourceSha256.Length != 64 || !SourceSha256.All(Uri.IsHexDigit)
            || Notes == null || OriginalNotes == null || Selection == null || Undo == null || Redo == null || Analyses == null
            || !double.IsFinite(Duration) || Duration <= 0 || Duration > 86400
            || !double.IsFinite(RangeStart) || !double.IsFinite(RangeEnd) || RangeStart < 0 || RangeEnd <= RangeStart || RangeEnd > Duration + 0.01
            || !double.IsFinite(Bpm) || Bpm is < 20 or > 400 || BeatsPerBar is < 2 or > 12 || Key is < 0 or > 11
            || !double.IsFinite(FirstBeat) || FirstBeat < 0 || FirstBeat > Duration
            || !double.IsFinite(ViewStart) || ViewStart < 0 || ViewStart > Duration || !double.IsFinite(ViewDuration) || ViewDuration <= 0
            || !double.IsFinite(Position) || Position < 0 || Position > Duration || !double.IsFinite(LoopStart) || !double.IsFinite(LoopEnd) || LoopStart < 0 || LoopEnd < LoopStart || LoopEnd > Duration
            || Notes.Length > 100000 || Undo.Count > 100 || Redo.Count > 100)
            throw new InvalidDataException(Tr("旋律草稿格式无效。"));
        MusicAnalysis?.Validate(Duration);
        ValidateNotes(Notes, Duration); ValidateNotes(OriginalNotes, Duration);
        var noteIds = Notes.Select(n => n.Id).ToHashSet();
        if (Selection.Any(id => !noteIds.Contains(id))) throw new InvalidDataException(Tr("旋律草稿格式无效。"));
        foreach (var state in Undo.Concat(Redo))
        {
            ValidateNotes(state.Notes, Duration);
            var stateIds = state.Notes.Select(n => n.Id).ToHashSet();
            if (!double.IsFinite(state.ViewStart) || !double.IsFinite(state.ViewDuration) || state.ViewStart < 0 || state.ViewStart > Duration || state.ViewDuration <= 0
                || state.Selection == null || state.Selection.Any(id => !stateIds.Contains(id))) throw new InvalidDataException(Tr("旋律草稿格式无效。"));
        }
        foreach (var analysis in Analyses) ValidateNotes(analysis.Notes, Duration);
        if (Spectrum is { } s && (s.Columns is < 1 or > 2400 || s.MinPitch < 0 || s.MaxPitch > 127 || s.MaxPitch < s.MinPitch
            || s.Values == null || s.Values.Length != s.Columns * (s.MaxPitch - s.MinPitch + 1) || !double.IsFinite(s.Start) || s.Start < 0 || !double.IsFinite(s.Duration) || s.Duration <= 0 || s.Start + s.Duration > Duration + 0.02))
            throw new InvalidDataException(Tr("旋律草稿格式无效。"));
    }
    public static void ValidateNotes(IEnumerable<MelodyNote> notes, double duration)
    {
        if (notes == null) throw new InvalidDataException(Tr("音符时间或音高无效。"));
        var ids = new HashSet<Guid>();
        foreach (var n in notes)
            if (n.Id == Guid.Empty || !ids.Add(n.Id) || !double.IsFinite(n.Start) || !double.IsFinite(n.End)
                || n.Start < 0 || n.End <= n.Start || n.End > duration + 0.01 || n.Pitch is < 0 or > 127)
                throw new InvalidDataException(Tr("音符时间或音高无效。"));
    }
}

public sealed class MelodyDraftStore(AppPaths paths)
{
    private readonly SemaphoreSlim _gate = new(1);
    public string DirectoryPath => Path.Combine(paths.Root, "melodies");
    public string GetPath(Guid id) => Path.Combine(DirectoryPath, id.ToString("N") + ".stemora.json");
    public async Task<IReadOnlyList<MelodyDraftInfo>> ListAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(DirectoryPath)) return [];
        var result = new List<MelodyDraftInfo>();
        foreach (var file in Directory.EnumerateFiles(DirectoryPath, "*.stemora.json").OrderByDescending(File.GetLastWriteTimeUtc).Take(30))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (new FileInfo(file).Length > 64 * 1024 * 1024) continue;
                using var json = JsonDocument.Parse(await File.ReadAllTextAsync(file, ct));
                var source = json.RootElement.GetProperty("sourcePath").GetString() ?? file;
                result.Add(new(file, Path.GetFileName(source), File.GetLastWriteTime(file)));
            }
            catch (Exception error) when (error is IOException or JsonException or KeyNotFoundException or InvalidOperationException) { /* A malformed external draft does not hide other saved work. */ }
        }
        return result;
    }
    public async Task SaveAsync(MelodyDocument document, CancellationToken ct = default)
    {
        document.Validate();
        document.SavedAt = DateTimeOffset.Now;
        // Serialize before yielding: subsequent edits cannot race this snapshot.
        var json = JsonSerializer.Serialize(document, new JsonSerializerOptions(JsonDefaults.Options) { WriteIndented = false });
        if (System.Text.Encoding.UTF8.GetByteCount(json) > 64 * 1024 * 1024) throw new InvalidDataException(Tr("旋律草稿过大。"));
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        var destination = GetPath(document.Id);
        var temp = destination + ".tmp";
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            await File.WriteAllTextAsync(temp, json, ct).ConfigureAwait(false);
            File.Move(temp, destination, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); _gate.Release(); }
    }
    public static async Task<MelodyDocument> LoadAsync(string path, CancellationToken ct = default)
    {
        if (new FileInfo(path).Length > 64 * 1024 * 1024) throw new InvalidDataException(Tr("旋律草稿过大。"));
        var document = JsonSerializer.Deserialize<MelodyDocument>(await File.ReadAllTextAsync(path, ct), JsonDefaults.Options)
            ?? throw new InvalidDataException(Tr("旋律草稿格式无效。"));
        document.Validate();
        return document;
    }
}

public sealed record MelodyDraftInfo(string Path, string Name, DateTime SavedAt)
{
    public string Label => $"{Name} · {SavedAt:g}";
}

public sealed class MelodyEditor(MelodyDocument document)
{
    public MelodyDocument Document { get; } = document;
    public bool CanUndo => Document.Undo.Count > 0;
    public bool CanRedo => Document.Redo.Count > 0;
    private MelodySnapshot Snapshot() => new(Document.Notes.ToArray(), Document.Selection.ToArray(), Document.ViewStart, Document.ViewDuration);
    private void Restore(MelodySnapshot state)
    {
        Document.Notes = state.Notes.ToArray(); Document.Selection = state.Selection.ToArray();
        Document.ViewStart = state.ViewStart; Document.ViewDuration = state.ViewDuration;
    }
    public void Commit(IEnumerable<MelodyNote> notes, Guid[]? selection = null, bool timingChanged = true)
    {
        var next = notes.OrderBy(n => n.Start).ThenBy(n => n.Pitch).ToArray();
        MelodyDocument.ValidateNotes(next, Document.Duration);
        if (next.SequenceEqual(Document.Notes)) return;
        Document.Undo.Add(Snapshot());
        if (Document.Undo.Count > 100) Document.Undo.RemoveAt(0);
        Document.Redo.Clear(); Document.Notes = next;
        Document.Selection = selection ?? Document.Selection.Where(id => next.Any(n => n.Id == id)).ToArray();
        if (timingChanged) Document.RhythmConfirmed = false;
    }
    public void Undo()
    {
        if (!CanUndo) return;
        Document.Redo.Add(Snapshot()); var state = Document.Undo[^1]; Document.Undo.RemoveAt(Document.Undo.Count - 1); Restore(state);
        Document.RhythmConfirmed = false;
    }
    public void Redo()
    {
        if (!CanRedo) return;
        Document.Undo.Add(Snapshot()); var state = Document.Redo[^1]; Document.Redo.RemoveAt(Document.Redo.Count - 1); Restore(state);
        Document.RhythmConfirmed = false;
    }
    public MelodyNote[] Transform(int semitones = 0, double move = 0, double resizeStart = 0, double resizeEnd = 0, bool commit = true)
    {
        var chosen = Document.Notes.Where(n => Document.Selection.Contains(n.Id)).ToArray();
        if (chosen.Length == 0) return Document.Notes;
        var others = Document.Notes.Except(chosen).ToArray();
        var minMove = -chosen.Min(n => n.Start); var maxMove = Document.Duration - chosen.Max(n => n.End);
        foreach (var n in chosen)
        {
            var previous = others.Where(o => o.End <= n.Start + 0.0001).Select(o => o.End).DefaultIfEmpty(0).Max();
            var next = others.Where(o => o.Start >= n.End - 0.0001).Select(o => o.Start).DefaultIfEmpty(Document.Duration).Min();
            minMove = Math.Max(minMove, previous - n.Start); maxMove = Math.Min(maxMove, next - n.End);
        }
        move = Math.Clamp(move, minMove, Math.Max(minMove, maxMove));
        var pitchDelta = Math.Clamp(semitones, -chosen.Min(n => n.Pitch), 127 - chosen.Max(n => n.Pitch));
        var updated = chosen.Select(n =>
        {
            var start = n.Start + move; var end = n.End + move;
            var resizeNeighbors = Document.Notes.Where(o => o.Id != n.Id).ToArray();
            if (resizeStart != 0) start = Math.Clamp(start + resizeStart,
                resizeNeighbors.Where(o => o.End <= n.Start + 0.0001).Select(o => o.End).DefaultIfEmpty(0).Max(), end - Math.Min(0.03, end - start));
            if (resizeEnd != 0) end = Math.Clamp(end + resizeEnd, start + Math.Min(0.03, end - start),
                resizeNeighbors.Where(o => o.Start >= n.End - 0.0001).Select(o => o.Start).DefaultIfEmpty(Document.Duration).Min());
            return start == n.Start && end == n.End && pitchDelta == 0 ? n : n with { Start = start, End = end, Pitch = n.Pitch + pitchDelta, Edited = true };
        }).ToArray();
        // Existing overlaps are reviewable. Editing may not introduce new overlaps.
        if (updated.Any(n => others.Any(o => n.Start < o.End - 0.0001 && n.End > o.Start + 0.0001
            && !chosen.Any(old => old.Id == n.Id && old.Start < o.End - 0.0001 && old.End > o.Start + 0.0001))))
            throw new InvalidOperationException(Tr("已到相邻音符边界。"));
        var result = others.Concat(updated).OrderBy(n => n.Start).ToArray();
        if (commit) Commit(result, timingChanged: move != 0 || resizeStart != 0 || resizeEnd != 0);
        return result;
    }
    public void Delete() => Commit(Document.Notes.Where(n => !Document.Selection.Contains(n.Id)));
    public void Add(double start, double end, int pitch)
    {
        start = Math.Clamp(start, 0, Document.Duration - 0.03); end = Math.Clamp(end, start + 0.03, Document.Duration);
        if (Document.Notes.Any(n => start < n.End && end > n.Start)) throw new InvalidOperationException(Tr("该位置已有音符，请先选择空白区域。"));
        var note = new MelodyNote(Guid.NewGuid(), start, end, Math.Clamp(pitch, 0, 127), true);
        Commit(Document.Notes.Append(note), [note.Id]);
    }
    public void Split(double position)
    {
        if (Document.Selection.Length != 1) return;
        var n = Document.Notes.First(n => n.Id == Document.Selection[0]);
        var cut = position > n.Start + 0.03 && position < n.End - 0.03 ? position : (n.Start + n.End) / 2;
        var second = n with { Id = Guid.NewGuid(), Start = cut, Edited = true, SourceIds = n.SourceIds ?? [n.Id] };
        Commit(Document.Notes.Where(o => o.Id != n.Id).Concat([n with { End = cut, Edited = true }, second]), [n.Id, second.Id]);
    }
    public void Merge(int? targetPitch = null)
    {
        var chosen = Document.Notes.Where(n => Document.Selection.Contains(n.Id)).OrderBy(n => n.Start).ToArray();
        if (chosen.Length < 2) return;
        for (var i = 1; i < chosen.Length; i++)
            if (chosen[i].Start - chosen[i - 1].End > 0.35 || Document.Notes.Any(n => !Document.Selection.Contains(n.Id) && n.Start < chosen[i].Start && n.End > chosen[i - 1].End))
                throw new InvalidOperationException(Tr("请选择相邻音符；不能跨越明显休止合并。"));
        var pitch = targetPitch ?? Document.Notes.First(n => n.Id == Document.Selection[0]).Pitch;
        var merged = chosen[0] with { End = chosen.Max(n => n.End), Pitch = pitch, Edited = true, NeedsReview = chosen.Any(n => n.NeedsReview), SourceIds = chosen.SelectMany(n => n.SourceIds ?? [n.Id]).Distinct().ToArray() };
        Commit(Document.Notes.Where(n => !Document.Selection.Contains(n.Id)).Append(merged), [merged.Id]);
    }
    public void MarkReviewed() => Commit(Document.Notes.Select(n => Document.Selection.Contains(n.Id)
        ? n with { NeedsReview = false, ReviewReason = null, Edited = true } : n), timingChanged: false);
}
