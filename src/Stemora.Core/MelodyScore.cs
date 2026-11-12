using System.Globalization;
using System.Text;
using static Stemora.Core.Strings;

namespace Stemora.Core;

public sealed record ScoreCell(Guid? NoteId, int Bar, int Tick, int Length, string Number, int Octave, bool Continuation, bool NeedsReview);

public static class MelodyScore
{
    public static readonly string[] Keys = ["C", "C#", "D", "Eb", "E", "F", "F#", "G", "Ab", "A", "Bb", "B"];
    public static readonly string[] KeyOptions = Keys.Concat(Keys.Select(key => key + "m")).ToArray();
    // Minor keys use the conventional la-based numbered notation (e.g. A minor: 6=A, 1=C).
    public static int NotationKey(MelodyDocument document) => (document.Key + (document.KeyIsMinor ? 3 : 0)) % 12;
    public static string KeyLabel(MelodyDocument document) => $"{(document.KeyIsMinor ? 6 : 1)} = {Keys[document.Key]}";
    public static double BarOrigin(MelodyDocument document)
    {
        var origin = document.FirstBeat;
        var firstNote = document.Notes.Select(note => note.Start).DefaultIfEmpty(origin).Min();
        var barSeconds = document.BeatsPerBar * 60 / document.Bpm;
        return firstNote < origin ? origin - Math.Ceiling((origin - firstNote) / barSeconds) * barSeconds : origin;
    }
    public static (string Number, int Octave) Number(int pitch, int key)
    {
        var relative = pitch - (60 + key); var octave = (int)Math.Floor(relative / 12d);
        string[] degrees = ["1", "#1", "2", "#2", "3", "4", "#4", "5", "#5", "6", "#6", "7"];
        return (degrees[(relative % 12 + 12) % 12], octave);
    }
    public static void Estimate(MelodyDocument document)
    {
        if (document.Notes.Length == 0) return;
        // A transparent, provisional estimate. A cappella timing has no reliable beat guarantee.
        var gaps = document.Notes.Zip(document.Notes.Skip(1), (a, b) => b.Start - a.Start).Where(d => d is > 0.18 and < 1.5).Order().ToArray();
        if (gaps.Length > 0)
        {
            var seconds = gaps[gaps.Length / 2]; var bpm = 60 / seconds;
            while (bpm > 160) bpm /= 2; while (bpm < 65) bpm *= 2;
            document.Bpm = Math.Round(bpm);
        }
        double[] major = [6.35, 2.23, 3.48, 2.33, 4.38, 4.09, 2.52, 5.19, 2.39, 3.66, 2.29, 2.88];
        document.Key = Enumerable.Range(0, 12).MaxBy(key => document.Notes.Sum(n => (n.End - n.Start) * major[(n.Pitch - key + 120) % 12]));
        document.FirstBeat = document.Notes.Min(n => n.Start); document.RhythmConfirmed = false;
    }
    public static ScoreCell[] Layout(MelodyDocument document)
    {
        var cells = new List<ScoreCell>();
        var ticksPerBar = document.BeatsPerBar * 4;
        var notes = document.Notes.OrderBy(n => n.Start).ToArray();
        var origin = BarOrigin(document);
        var cursor = 0;
        foreach (var note in notes)
        {
            var start = Math.Max(0, (int)Math.Round((note.Start - origin) * document.Bpm / 60 * 4));
            var end = Math.Max(start + 1, (int)Math.Round((note.End - origin) * document.Bpm / 60 * 4));
            if (start > cursor) Add(cursor, start, null, "0", 0, false);
            var degree = Number(note.Pitch, NotationKey(document));
            Add(start, end, note.Id, degree.Number, degree.Octave, note.NeedsReview);
            cursor = Math.Max(cursor, end);
        }
        return cells.ToArray();

        void Add(int from, int to, Guid? id, string number, int octave, bool review)
        {
            var continuation = false;
            while (from < to)
            {
                var available = Math.Min(to - from, Math.Min(4, ticksPerBar - from % ticksPerBar));
                var length = available >= 4 ? 4 : available >= 2 ? 2 : 1;
                cells.Add(new(id, from / ticksPerBar, from % ticksPerBar, length, number, octave, continuation, review));
                continuation = id.HasValue; from += length;
            }
        }
    }
    public static string CellLabel(ScoreCell cell) => (cell.Continuation ? "−" : cell.Number)
        + (cell.Continuation || cell.Octave == 0 ? "" : cell.Octave > 0 ? new string('˙', Math.Min(4, cell.Octave)) : new string('̣', Math.Min(4, -cell.Octave)))
        + (cell.Length < 4 ? cell.Length <= 1 ? "̳" : "̲" : "") + (cell.NeedsReview ? "?" : "");

    public static async Task ExportPdfAsync(MelodyDocument document, string destination, CancellationToken ct = default)
    {
        document.Validate();
        var cells = Layout(document); var bars = cells.Select(c => c.Bar).Distinct().ToArray();
        if (bars.Length == 0) throw new InvalidOperationException(Tr("请先识别或添加音符。"));
        const int rowsPerPage = 9;
        var barsPerRow = Math.Clamp(12 / document.BeatsPerBar, 1, 3);
        var barWidth = 507d / barsPerRow;
        var pages = Math.Max(1, (int)Math.Ceiling((bars.Max() + 1) / (double)(barsPerRow * rowsPerPage)));
        var objects = new List<string> { "", "", "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Type /Font /Subtype /Type0 /BaseFont /STSong-Light /Encoding /UniGB-UCS2-H /DescendantFonts [5 0 R] /ToUnicode 7 0 R >>",
            "<< /Type /Font /Subtype /CIDFontType0 /BaseFont /STSong-Light /CIDSystemInfo << /Registry (Adobe) /Ordering (GB1) /Supplement 4 >> /FontDescriptor 6 0 R >>",
            "<< /Type /FontDescriptor /FontName /STSong-Light /Flags 6 /FontBBox [0 -200 1000 900] /ItalicAngle 0 /Ascent 880 /Descent -120 /CapHeight 700 /StemV 80 >>" };
        const string unicodeMap = "/CIDInit /ProcSet findresource begin\n12 dict begin begincmap\n/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n/CMapName /StemoraUnicode def /CMapType 2 def\n1 begincodespacerange <0000> <FFFF> endcodespacerange\n1 beginbfrange <0000> <FFFF> <0000> endbfrange\nendcmap CMapName currentdict /CMap defineresource pop end end\n";
        objects.Add($"<< /Length {unicodeMap.Length} >>\nstream\n{unicodeMap}endstream");
        var pageRefs = new List<int>();
        for (var p = 0; p < pages; p++)
        {
            ct.ThrowIfCancellationRequested();
            var content = new StringBuilder();
            void Text(double x, double y, double size, string text, bool unicode = false)
            {
                if (text.All(c => c <= 127 || c == '·')) { text = text.Replace('·', '-'); unicode = false; }
                var value = unicode ? "<" + Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(text)) + ">" : "(" + text.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)") + ")";
                content.Append(CultureInfo.InvariantCulture, $"BT /{(unicode ? "CJK" : "F1")} {size} Tf {x:F2} {y:F2} Td {value} Tj ET\n");
            }
            void Line(double x1, double y1, double x2, double y2) => content.Append(CultureInfo.InvariantCulture, $"{x1:F2} {y1:F2} m {x2:F2} {y2:F2} l S\n");
            var title = Path.GetFileNameWithoutExtension(document.SourcePath);
            var titleLimit = title.Any(c => c > 127) ? 52 : 92;
            if (title.Length > titleLimit) title = title[..(titleLimit - 3)] + "...";
            var titleSize = Math.Clamp(480 / Math.Max(1, title.Length * (title.Any(c => c > 127) ? 1 : 0.55)), 9, 19);
            Text(44, 791, titleSize, title, true);
            Text(44, 765, 11, $"{KeyLabel(document)}     {document.BeatsPerBar}/4     BPM {document.Bpm:0.##}     {p + 1}/{pages}");
            Text(44, 743, 10, document.RhythmConfirmed ? Tr("当前编辑版 · 简谱") : Tr("草稿 · 节奏待确认"), true);
            Text(44, 726, 9, Tr("? 待核对音符；连线表示延续；空白处保留休止。"), true);
            for (var row = 0; row < rowsPerPage; row++)
            {
                var firstBar = (p * rowsPerPage + row) * barsPerRow; if (firstBar > bars.Max()) break;
                var y = 678 - row * 65;
                for (var col = 0; col < barsPerRow && firstBar + col <= bars.Max(); col++)
                {
                    var bar = firstBar + col; var x = 44 + col * barWidth;
                    Text(x, y + 25, 8, (bar + 1).ToString(CultureInfo.InvariantCulture)); Line(x, y - 16, x, y + 16);
                    foreach (var cell in cells.Where(c => c.Bar == bar))
                    {
                        var nx = x + 9 + cell.Tick / (document.BeatsPerBar * 4d) * (barWidth - 18);
                        Text(nx, y, cell.Number.Length > 1 ? 11 : 13, cell.Continuation ? "-" : cell.Number);
                        if (!cell.Continuation && cell.Octave != 0) Text(nx + 2, cell.Octave > 0 ? y + 12 : y - 10, 11, new string('.', Math.Min(4, Math.Abs(cell.Octave))));
                        if (cell.Length < 4) { Line(nx, y - 3, nx + 8, y - 3); if (cell.Length == 1) Line(nx, y - 6, nx + 8, y - 6); }
                        if (cell.NeedsReview) Text(nx + 8, y + 13, 8, "?");
                        if (cell.Continuation) Line(nx - 7, y + 15, nx + 6, y + 15);
                    }
                    Line(x + barWidth, y - 16, x + barWidth, y + 16);
                }
            }
            var stream = content.ToString();
            var contentId = objects.Count + 1; objects.Add($"<< /Length {Encoding.ASCII.GetByteCount(stream)} >>\nstream\n{stream}endstream");
            var pageId = objects.Count + 1; pageRefs.Add(pageId);
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 3 0 R /CJK 4 0 R >> >> /Contents {contentId} 0 R >>");
        }
        objects[0] = "<< /Type /Catalog /Pages 2 0 R >>";
        objects[1] = $"<< /Type /Pages /Count {pageRefs.Count} /Kids [{string.Join(' ', pageRefs.Select(p => $"{p} 0 R"))}] >>";
        using var memory = new MemoryStream();
        void Write(string text) => memory.Write(Encoding.ASCII.GetBytes(text));
        Write("%PDF-1.4\n"); var offsets = new List<long> { 0 };
        for (var i = 0; i < objects.Count; i++) { offsets.Add(memory.Position); Write($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"); }
        var xref = memory.Position; Write($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1)) Write(offset.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        Write($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        var temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllBytesAsync(temp, memory.ToArray(), ct); File.Move(temp, destination, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
