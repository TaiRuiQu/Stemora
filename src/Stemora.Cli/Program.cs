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
            if (args.Any(a => a is "--help" or "-h")) { PrintHelp(); return 0; }
            var parsed = Arguments.Parse(args);
            if (parsed.Command.Count == 0 || parsed.Command[0] is "help" or "--help" or "-h") { PrintHelp(); return 0; }
            if (parsed.Command[0] is "--version" or "version") { Emit("version", new { version = "0.2.0" }); return 0; }
            var paths = new AppPaths(parsed.Get("home"));
            var settings = paths.LoadSettings();
            var ct = cancellation.Token;
            var downloadProgress = new InlineProgress<DownloadProgress>(p => EmitProgress("download", p.Fraction, p.Message, p.Received, p.Total));
            switch (string.Join(' ', parsed.Command))
            {
                case "models list":
                    var models = ModelRepository.Catalog.Select(m => new
                    {
                        m.Id,
                        m.DisplayName,
                        m.Author,
                        m.Architecture,
                        m.Size,
                        family = m.Profile ?? m.Architecture,
                        stems = m.Stems,
                        repository = m.Repository,
                        installed = new ModelRepository(paths).IsInstalled(m),
                        path = new ModelRepository(paths).GetPath(m)
                    }).ToArray();
                    if (_json) Emit("models", models);
                    else foreach (var m in models) Console.WriteLine($"{m.Id,-20} {m.DisplayName,-40} {m.Size / 1048576d,7:F1} MB  {(m.installed ? "已下载" : "未下载")}");
                    return 0;
                case "models download":
                    var id = parsed.Required("id");
                    var model = ModelRepository.Catalog.FirstOrDefault(m => m.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                        ?? throw new ArgumentException($"未知模型 ID：{id}。运行 models list 查看。");
                    var modelPath = await new ModelRepository(paths).DownloadAsync(model, downloadProgress, ct);
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
                        note = "检查文件与 GGUF 头；实际 GPU 可用性在首次推理时由引擎检测。"
                    });
                    return engine != null && validModel ? 0 : 2;
                case "separate":
                    var input = parsed.Required("input");
                    var selectedModel = parsed.Get("model") ?? settings.ModelPath ?? throw new ArgumentException("需要 --model <模型.gguf>。");
                    var timeout = parsed.Integer("timeout");
                    if (timeout is <= 0) throw new ArgumentException("--timeout 必须大于零。");
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
                default: throw new ArgumentException("未知命令，运行 stemora help 查看用法。");
            }
        }
        catch (OperationCanceledException) { EmitError("cancelled", "操作已取消或超时。"); return 130; }
        catch (ArgumentException ex) { EmitError("invalid_arguments", ex.Message); return 2; }
        catch (Exception ex) { EmitError("operation_failed", ex.Message); return 1; }
    }
    private static ComputeDevice ParseDevice(string? value, ComputeDevice fallback) => value switch
    { null => fallback, "auto" => ComputeDevice.Auto, "cpu" => ComputeDevice.Cpu, "cuda" => ComputeDevice.Cuda, _ => throw new ArgumentException("--device 需要 auto、cpu 或 cuda。") };
    private static WavEncoding ParseEncoding(string? value, WavEncoding fallback) => value switch
    { null => fallback, "float32" => WavEncoding.Float32, "pcm24" => WavEncoding.Pcm24, "pcm16" => WavEncoding.Pcm16, _ => throw new ArgumentException("--format 需要 float32、pcm24 或 pcm16。") };
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
        else Console.Error.WriteLine($"错误：{message}");
    }
    private static void PrintHelp() => Console.WriteLine("""
        Stemora 0.2.0 (多模型 GGUF 本地音频分离)

        stemora runtime install [--flavor cuda|cpu]
        stemora models list
        stemora models download --id anvuew-q8_0
        stemora models download --id bs-roformer-sw-q8_0
        stemora models inspect --model model.gguf
        stemora doctor [--model model.gguf] [--engine bs_roformer-cli.exe]
        stemora separate --input song.mp3 --model model.gguf --output ./stems
          [--device auto|cpu|cuda] [--overlap 2] [--chunk-size 352800]
          [--format float32|pcm24|pcm16] [--no-residual] [--timeout 600]
          [--engine bs_roformer-cli.exe]
          [--profile bs_roformer_sw] [--stems bass,drums,other,vocals,guitar,piano]

        通用参数：--json 输出 UTF-8 NDJSON；--home 指定数据目录（或 STEMORA_HOME）。
        多轨模型按模型音轨名称导出；SW 输出 bass/drums/other/vocals/guitar/piano.wav。
        每次分离创建独立输出文件夹；单轨模型可另导出 residual.wav。
        退出码：0 成功；1 操作失败；2 参数或环境不完整；130 取消/超时。
        自动模式优先 CUDA；CPU 模式仅 CPU；CUDA 模式不可用时明确报错。
        """);
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
    public string Required(string name) => Get(name) ?? throw new ArgumentException($"缺少 --{name}。");
    public int? Integer(string name) => Get(name) is { } value
        ? int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : throw new ArgumentException($"--{name} 需要整数。") : null;
    public static Arguments Parse(string[] args)
    {
        var result = new Arguments();
        string[] flags = ["json", "no-residual"];
        string[] values = ["input", "model", "output", "engine", "device", "chunk-size", "overlap", "format", "id", "flavor", "home", "timeout", "profile", "stems"];
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || args[i] is "--help" or "--version") { result.Command.Add(args[i]); continue; }
            var name = args[i][2..];
            if (result.Has(name)) throw new ArgumentException($"重复参数 --{name}。");
            if (flags.Contains(name)) result._options.Add(name, null);
            else if (values.Contains(name))
            {
                if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"--{name} 缺少值。");
                result._options.Add(name, args[++i]);
            }
            else throw new ArgumentException($"未知参数 --{name}。");
        }
        return result;
    }
}
