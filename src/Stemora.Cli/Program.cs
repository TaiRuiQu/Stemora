using static Stemora.Core.Strings;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Stemora.Core;

namespace Stemora.Cli;

internal static class Program
{
    private static readonly object OutputLock = new();
    private static bool _json;
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        Console.InputEncoding = Encoding.UTF8;
        _json = args.Contains("--json", StringComparer.Ordinal);
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        try
        {
            var parsed = Arguments.Parse(args);
            var paths = new AppPaths(parsed.Get("home"));
            var help = args.Any(a => a is "--help" or "-h") || parsed.Command.Count == 0 || parsed.Command[0] == "help";
            if (help)
            {
                try { Strings.SetLanguage(paths.LoadSettings().Language); }
                catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { }
                PrintHelp(); return 0;
            }
            if (parsed.Command[0] is "--version" or "version") { Emit("version", new { version = "0.4.0" }); return 0; }
            var settings = paths.LoadSettings();
            Strings.SetLanguage(settings.Language);
            var ct = cancellation.Token;
            var downloadProgress = new InlineProgress<DownloadProgress>(p => EmitProgress("download", p.Fraction, p.Message, p.Received, p.Total));
            switch (string.Join(' ', parsed.Command))
            {
                case "melody models":
                    Emit("models", MuScriptorService.Models.Select(m => new { m.Id, m.Repository, m.Size, m.Revision, m.Sha256, installed = new MuScriptorService(paths).IsModelInstalled(m.Id) }));
                    return 0;
                case "melody prepare":
                    await new MuScriptorService(paths).PrepareAsync(parsed.Get("model") ?? "small", ParseDevice(parsed.Get("device"), ComputeDevice.Auto), null,
                        new InlineProgress<SeparationProgress>(p => EmitProgress(p.Stage, p.Fraction, p.Message)), ct);
                    Emit("completed", new { model = parsed.Get("model") ?? "small" });
                    return 0;
                case "melody transcribe":
                    var vocalInput = Path.GetFullPath(parsed.Required("input"));
                    using (var vocalReader = new NAudio.Wave.AudioFileReader(vocalInput))
                    {
                        var melody = new MelodyDocument
                        {
                            SourcePath = vocalInput,
                            VocalPath = vocalInput,
                            IsVocal = true,
                            Model = parsed.Get("model") ?? "small",
                            SourceSha256 = await MelodyDocument.FingerprintAsync(vocalInput, ct),
                            Duration = vocalReader.TotalTime.TotalSeconds,
                            RangeStart = parsed.Number("start") ?? 0,
                            RangeEnd = parsed.Number("end") ?? vocalReader.TotalTime.TotalSeconds,
                            ViewDuration = Math.Min(12, vocalReader.TotalTime.TotalSeconds)
                        };
                        melody.Validate();
                        var melodyDevice = ParseDevice(parsed.Get("device"), ComputeDevice.Auto);
                        var transcriber = new MuScriptorService(paths);
                        var melodyProgress = new InlineProgress<SeparationProgress>(p => EmitProgress(p.Stage, p.Fraction, p.Message));
                        await transcriber.PrepareAsync(melody.Model, melodyDevice, null, melodyProgress, ct);
                        var music = await transcriber.AnalyzeAsync(vocalInput, melodyDevice, melody.RangeStart, melody.RangeEnd, melody.Duration, melodyProgress, ct);
                        melody.Notes = await transcriber.TranscribeAsync(vocalInput, melody.Model, melodyDevice, melody.RangeStart, melody.RangeEnd, melody.Duration, melodyProgress, ct);
                        melody.OriginalNotes = melody.Notes.ToArray();
                        melody.Analyses.Add(new(melody.Model, MuScriptorService.GetModel(melody.Model).Revision, DateTimeOffset.Now, melody.Notes.ToArray()));
                        MelodyTiming.Apply(melody, music); melody.Stage = "completed";
                        var drafts = new MelodyDraftStore(paths); await drafts.SaveAsync(melody, ct);
                        var pdfRoot = Path.GetFullPath(parsed.Get("output") ?? settings.OutputDirectory); Directory.CreateDirectory(pdfRoot);
                        var pdfPath = Path.Combine(pdfRoot, $"melody-{melody.Id:N}.pdf");
                        await MelodyScore.ExportPdfAsync(melody, pdfPath, ct);
                        Emit("completed", new
                        {
                            draft = drafts.GetPath(melody.Id),
                            pdf = pdfPath,
                            notes = melody.Notes.Length,
                            bpm = music.Bpm,
                            key = music.Key is { } key ? MelodyScore.Keys[key] + (music.IsMinor ? "m" : "") : null,
                            autoSnapped = music.Bpm != null,
                            rhythmConfirmed = false
                        });
                    }
                    return 0;
                case "models list":
                    var models = ModelRepository.Catalog.Select(m => new
                    {
                        m.Id,
                        m.DisplayName,
                        m.Author,
                        m.Architecture,
                        m.Quantization,
                        m.Size,
                        m.Hardware,
                        m.ExportFormats,
                        m.Purpose,
                        m.FileFormat,
                        family = m.Profile ?? m.Architecture,
                        stems = m.Stems,
                        repository = m.Repository,
                        installed = new ModelRepository(paths).IsInstalled(m),
                        path = new ModelRepository(paths).GetPath(m)
                    }).ToArray();
                    if (_json) Emit("models", models);
                    else foreach (var m in models) Console.WriteLine($"{m.Id,-20} {m.DisplayName,-40} {m.Size / 1048576d,7:F1} MB  {(m.installed ? Tr("已下载") : Tr("未下载"))}");
                    return 0;
                case "models download":
                    var id = parsed.Required("id");
                    var model = ModelRepository.Catalog.FirstOrDefault(m => m.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                        ?? throw new ArgumentException(Tr("未知模型 ID：{0}。运行 models list 查看。", id));
                    var modelPath = await new ModelRepository(paths).DownloadAsync(model, downloadProgress, ct,
                        ParseDevice(parsed.Get("device"), ComputeDevice.Auto), stageProgress: new InlineProgress<SeparationProgress>(p => EmitProgress(p.Stage, p.Fraction, p.Message)));
                    Emit("completed", new { modelPath });
                    return 0;
                case "models inspect":
                    Emit("model", await ModelRepository.InspectAsync(parsed.Required("model"), ct));
                    return 0;
                case "runtime install":
                    var enginePath = await new EngineManager(paths).InstallAsync(parsed.Get("flavor") ?? "cuda", downloadProgress, ct);
                    Emit("completed", new { enginePath });
                    return 0;
                case "doctor":
                    var engine = new EngineManager(paths).Find(parsed.Get("engine") ?? settings.EnginePath);
                    var selected = parsed.Get("model") ?? settings.ModelPath;
                    var validModel = false;
                    string? modelError = null;
                    if (selected != null)
                    {
                        try { ModelRepository.ValidateGguf(selected); validModel = true; }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { modelError = e.Message; }
                    }
                    Emit("doctor", new
                    {
                        home = paths.Root,
                        enginePath = engine,
                        engineExists = engine != null,
                        modelPath = selected,
                        modelValid = validModel,
                        modelError,
                        note = Tr("检查文件与 GGUF 头；实际 GPU 可用性在首次推理时由引擎检测。")
                    });
                    return engine != null && validModel ? 0 : 2;
                case "separate":
                    var input = parsed.Required("input");
                    var selectedModel = parsed.Get("model") ?? settings.ModelPath ?? throw new ArgumentException(Tr("需要 --model <模型.gguf>。"));
                    var timeout = parsed.Integer("timeout");
                    if (timeout is <= 0) throw new ArgumentException(Tr("--timeout 必须大于零。"));
                    if (timeout.HasValue) cancellation.CancelAfter(TimeSpan.FromSeconds(timeout.Value));
                    var options = new SeparationOptions(input, selectedModel, parsed.Get("output") ?? settings.OutputDirectory,
                        ParseDevice(parsed.Get("device"), settings.Device), parsed.Integer("chunk-size") ?? settings.ChunkSize,
                        parsed.Integer("overlap") ?? settings.Overlap, ParseEncoding(parsed.Get("format"), settings.Encoding),
                        !parsed.Has("no-residual") && settings.ExportResidual, parsed.Get("engine") ?? settings.EnginePath,
                        parsed.Get("profile"), parsed.Get("stems")?.Split(',', StringSplitOptions.TrimEntries));
                    var result = await new SeparationService(paths).SeparateAsync(options,
                        new InlineProgress<SeparationProgress>(p => EmitProgress(p.Stage, p.Fraction, p.Message)), ct);
                    Emit("completed", result);
                    return 0;
                default: throw new ArgumentException(Tr("未知命令，运行 stemora help 查看用法。"));
            }
        }
        catch (OperationCanceledException) { EmitError("cancelled", Tr("操作已取消或超时。")); return 130; }
        catch (ArgumentException ex) { EmitError("invalid_arguments", ex.Message); return 2; }
        catch (Exception ex) { EmitError("operation_failed", ex.Message); return 1; }
    }
    private static ComputeDevice ParseDevice(string? value, ComputeDevice fallback) => value switch
    { null => fallback, "auto" => ComputeDevice.Auto, "cpu" => ComputeDevice.Cpu, "cuda" => ComputeDevice.Cuda, _ => throw new ArgumentException(Tr("--device 需要 auto、cpu 或 cuda。")) };
    private static WavEncoding ParseEncoding(string? value, WavEncoding fallback) => value switch
    { null => fallback, "float32" => WavEncoding.Float32, "pcm24" => WavEncoding.Pcm24, "pcm16" => WavEncoding.Pcm16, _ => throw new ArgumentException(Tr("--format 需要 float32、pcm24 或 pcm16。")) };
    private static void EmitProgress(string stage, double? fraction, string message, long? received = null, long? total = null)
    {
        lock (OutputLock)
        {
            if (_json) Console.WriteLine(JsonSerializer.Serialize(new { type = "progress", stage, fraction, message, received, total }));
            else Console.Error.WriteLine($"[{stage}] {(fraction.HasValue ? fraction.Value.ToString("P0", CultureInfo.InvariantCulture) + " " : "")}{message}");
        }
    }
    private static void Emit(string type, object data)
    {
        lock (OutputLock) Console.WriteLine(_json
            ? JsonSerializer.Serialize(new { type, data }, new JsonSerializerOptions(JsonDefaults.Options) { WriteIndented = false })
            : JsonSerializer.Serialize(data, JsonDefaults.Options));
    }
    private static void EmitError(string code, string message)
    {
        if (_json) Emit("error", new { code, message });
        else Console.Error.WriteLine(Tr("错误：{0}", message));
    }
    private static void PrintHelp()
    {
        Console.WriteLine(Tr("Stemora 0.4.0 (本地音频分离与旋律转谱)"));
        Console.WriteLine("""

            stemora runtime install [--flavor cuda|cpu]
            stemora models list
            stemora models download --id anvuew-q8_0
            stemora models download --id bs-roformer-sw-q8_0
            stemora models inspect --model model.gguf
            stemora melody models
            stemora melody prepare --model small [--device auto|cpu|cuda]
            stemora melody transcribe --input vocals.wav --model small --output ./scores
              [--start 15.5] [--end 45.5] [--device auto|cpu|cuda]
            stemora doctor [--model model.gguf] [--engine stemora-infra.exe]
            stemora separate --input song.mp3 --model model.gguf --output ./stems
              [--device auto|cpu|cuda] [--overlap 2] [--chunk-size 352800]
              [--format float32|pcm24|pcm16] [--no-residual] [--timeout 600]
              [--engine stemora-infra.exe]
              [--profile bs_roformer_sw] [--stems bass,drums,other,vocals,guitar,piano]

            """);
        foreach (var line in new[]
        {
            Tr("通用参数：--json 输出 UTF-8 NDJSON；--home 指定数据目录（或 STEMORA_HOME）。"),
            Tr("多轨模型按模型音轨名称导出；SW 输出 bass/drums/other/vocals/guitar/piano.wav。"),
            Tr("每次分离创建独立输出文件夹；单轨模型可另导出 residual.wav。"),
            Tr("退出码：0 成功；1 操作失败；2 参数或环境不完整；130 取消/超时。"),
            Tr("自动模式优先 CUDA；CPU 模式仅 CPU；CUDA 模式不可用时明确报错。")
        }) Console.WriteLine(line);
    }

}

internal sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

internal sealed class Arguments
{
    private readonly Dictionary<string, string?> _options = new(StringComparer.Ordinal);
    public List<string> Command { get; } = [];
    public string? Get(string name) => _options.GetValueOrDefault(name);
    public bool Has(string name) => _options.ContainsKey(name);
    public string Required(string name) => Get(name) ?? throw new ArgumentException(Tr("缺少 --{0}。", name));
    public int? Integer(string name) => Get(name) is { } value
        ? int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : throw new ArgumentException(Tr("--{0} 需要整数。", name)) : null;
    public double? Number(string name) => Get(name) is { } value
        ? double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) ? n : throw new ArgumentException(Tr("--{0} 需要有限数值。", name)) : null;
    public static Arguments Parse(string[] args)
    {
        var result = new Arguments();
        string[] flags = ["json", "no-residual"];
        string[] values = ["input", "model", "output", "engine", "device", "chunk-size", "overlap", "format", "id", "flavor", "home", "timeout", "profile", "stems", "start", "end"];
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || args[i] is "--help" or "--version") { result.Command.Add(args[i]); continue; }
            var name = args[i][2..];
            if (result.Has(name)) throw new ArgumentException(Tr("重复参数 --{0}。", name));
            if (flags.Contains(name)) result._options.Add(name, null);
            else if (values.Contains(name))
            {
                if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException(Tr("--{0} 缺少值。", name));
                result._options.Add(name, args[++i]);
            }
            else throw new ArgumentException(Tr("未知参数 --{0}。", name));
        }
        return result;
    }
}
