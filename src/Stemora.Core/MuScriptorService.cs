using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using static Stemora.Core.Strings;

namespace Stemora.Core;

public sealed record MuScriptorModel(string Id, long Size, string Revision, string Sha256)
{
    public string Repository => "MuScriptor/muscriptor-" + Id;
    public string SizeLabel => Size < 1073741824 ? $"{Size / 1048576d:F0} MB" : $"{Size / 1073741824d:F2} GB";
    public ModelInfo ToCatalogEntry() => new("muscriptor-" + Id, "MuScriptor " + Id, "MuScriptor", "MuScriptor", "原始权重",
        "model.safetensors", Size, Sha256, Revision, Repository: Repository,
        Hardware: new(true, true, "cuda"), ExportFormats: ["PDF"],
        Purpose: ModelPurpose.Transcription, FileFormat: "Safetensors", Variant: Id);
}

public sealed class MuScriptorService(AppPaths paths)
{
    public const string PackageVersion = "0.4.0";
    public const string AnalysisRevision = "bffead9be61857fd44b2e0d3a2510d9d94c8f2d2";
    private string AnalysisDirectory => Path.Combine(paths.Models, "music-analysis");
    private string AnalysisMarker(ComputeDevice device) => Path.Combine(RuntimeDirectory(device), "analysis-ready-" + AnalysisRevision);
    public static IReadOnlyList<MuScriptorModel> Models { get; } = [
        new("small", 411888600, "8c127f603b807520fa465c838e9bfee8a91ada4e", "bbd482c786b895cf7d8f44185073d951adae2ebb8a66f82ca84cd1f84569549c"),
        new("medium", 1228144472, "f32236969308476e01fd3aae67357de5feb05a2d", "ac80adbdf85d87231735fd948af7013441c0afced316c4e9067fd5d8a7fb97ec"),
        new("large", 5465642136, "8809fdfbed2affa7ade94a7059e746e3880720e7", "ac4eb6ea87dfc26b6ca6b954c6b967ab87ad4c7d08e078b25214f13ed051f397")];
    public string ModelDirectory(string variant) => Path.Combine(paths.Models, "muscriptor", GetModel(variant).Id);
    public static MuScriptorModel GetModel(string variant) => Models.FirstOrDefault(m => m.Id == variant)
        ?? throw new ArgumentException(Tr("不支持的 MuScriptor 模型。"));
    private string RuntimeDirectory(ComputeDevice device) => Path.Combine(paths.Runtime, "muscriptor-" + PackageVersion + (device == ComputeDevice.Cpu ? "-cpu" : "-cu128"));
    private string Python(ComputeDevice device) => Path.Combine(RuntimeDirectory(device), "Scripts", "python.exe");
    private bool RuntimeReady(ComputeDevice device)
    {
        var runtime = RuntimeDirectory(device); var marker = Path.Combine(runtime, "ready");
        if (!File.Exists(Python(device)) || !File.Exists(marker) || File.ReadAllText(marker) != Path.GetFullPath(runtime)) return false;
        var config = Path.Combine(runtime, "pyvenv.cfg");
        var home = File.Exists(config) ? File.ReadLines(config).FirstOrDefault(line => line.StartsWith("home = ", StringComparison.Ordinal))?[7..] : null;
        return home != null && Directory.Exists(home);
    }
    public bool IsReady(string variant, ComputeDevice device) => RuntimeReady(device) && File.Exists(AnalysisMarker(device)) && IsModelInstalled(variant);
    public bool IsModelInstalled(string variant)
    {
        var dir = ModelDirectory(variant);
        var model = GetModel(variant);
        return File.Exists(Path.Combine(dir, "verified-" + model.Revision))
            && File.Exists(Path.Combine(dir, "config.json"))
            && File.Exists(Path.Combine(dir, "model.safetensors")) && new FileInfo(Path.Combine(dir, "model.safetensors")).Length == model.Size;
    }
    public async Task PrepareAsync(string variant, ComputeDevice device, string? token,
        IProgress<SeparationProgress>? progress, CancellationToken ct)
    {
        var model = GetModel(variant);
        paths.EnsureCreated();
        var runtime = RuntimeDirectory(device);
        Directory.CreateDirectory(runtime);
        // An OS file lock also prevents the CLI and another app instance from installing concurrently.
        await using var installLock = new FileStream(runtime + ".lock", FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        if (!RuntimeReady(device))
        {
            progress?.Report(new("runtime-download", null, Tr("正在下载环境安装工具")));
            var uv = await EnsureUvAsync(progress, ct);
            var environment = new Dictionary<string, string> { ["UV_PYTHON_INSTALL_DIR"] = Path.Combine(paths.Runtime, "python"), ["UV_CACHE_DIR"] = Path.Combine(paths.Runtime, "uv-cache") };
            progress?.Report(new("runtime-python", null, Tr("正在安装 Python 3.12")));
            await RunProcessAsync(uv, ["venv", "--clear", "--python", "3.12", "--managed-python", runtime], progress, ct, environment: environment, outputStage: "runtime-python");
            progress?.Report(new("runtime-packages", null, Tr(device == ComputeDevice.Cpu ? "正在安装 MuScriptor 和 PyTorch（CPU）" : "正在安装 MuScriptor 和 PyTorch（CUDA）")));
            await RunProcessAsync(uv, ["pip", "install", "--python", Python(device), "--torch-backend", device == ComputeDevice.Cpu ? "cpu" : "cu128", "muscriptor==" + PackageVersion], progress, ct, environment: environment, outputStage: "runtime-packages");
            progress?.Report(new("runtime-verify", null, Tr("正在检查 MuScriptor 运行环境")));
            await RunProcessAsync(Python(device), ["-c", "from muscriptor import TranscriptionModel; import soundfile, torch"], progress, ct, outputStage: "runtime-verify");
            await File.WriteAllTextAsync(Path.Combine(runtime, "ready"), Path.GetFullPath(runtime), ct);
            progress?.Report(new("runtime-ready", 1, Tr("MuScriptor 运行环境已就绪")));
        }
        if (!File.Exists(AnalysisMarker(device)) || !Directory.Exists(AnalysisDirectory))
        {
            progress?.Report(new("analysis-packages", null, Tr("正在安装节奏与调性分析依赖")));
            var uv = await EnsureUvAsync(progress, ct);
            await RunProcessAsync(uv, ["pip", "install", "--python", Python(device), "--torch-backend", device == ComputeDevice.Cpu ? "cpu" : "cu128",
                $"madmom-infer[torch] @ https://github.com/openmirlab/madmom-infer/archive/{AnalysisRevision}.zip"], progress, ct,
                environment: new Dictionary<string, string> { ["UV_CACHE_DIR"] = Path.Combine(paths.Runtime, "uv-cache") }, outputStage: "analysis-packages");
        }
        // Verify the small analysis checkpoints on preparation, including existing installations.
        await RunBridgeAsync(device, ["prepare-analysis", "--analysis-dir", AnalysisDirectory], null, progress, ct);
        await File.WriteAllTextAsync(AnalysisMarker(device), AnalysisRevision, ct);
        if (!IsModelInstalled(variant))
        {
            progress?.Report(new("download", null, Tr("正在下载 MuScriptor 模型：{0}", model.SizeLabel)));
            await RunBridgeAsync(device, ["download", "--model-dir", ModelDirectory(variant), "--variant", variant,
                "--revision", model.Revision, "--sha256", model.Sha256, "--size", model.Size.ToString(System.Globalization.CultureInfo.InvariantCulture)], token, progress, ct);
        }
    }
    private async Task<string> EnsureUvAsync(IProgress<SeparationProgress>? progress, CancellationToken ct)
    {
        var target = Path.Combine(paths.Runtime, "uv", "uv.exe");
        if (File.Exists(target)) return target;
        var archive = Path.Combine(paths.Runtime, "uv-0.13.1.zip");
        await Downloads.FetchAsync(new("https://github.com/astral-sh/uv/releases/download/0.13.1/uv-x86_64-pc-windows-msvc.zip"),
            archive, "3b2661a463a5baea7f5076ea2b25ab71d77f36a6c0d809e5350651792f80a672", 15308685,
            new RelayProgress<DownloadProgress>(p => progress?.Report(new("runtime-download", p.Fraction,
                Tr("正在下载环境安装工具：{0:F1} / {1:F1} MB", p.Received / 1048576d, p.Total.GetValueOrDefault() / 1048576d)))), ct);
        using var zip = ZipFile.OpenRead(archive);
        var entry = zip.Entries.Single(e => e.Name == "uv.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        entry.ExtractToFile(target + ".tmp", true); File.Move(target + ".tmp", target, true);
        return target;
    }
    public async Task<MelodyNote[]> TranscribeAsync(string vocalPath, string variant, ComputeDevice device,
        double start, double end, double duration, IProgress<SeparationProgress>? progress, CancellationToken ct)
    {
        if (!IsReady(variant, device)) throw new InvalidOperationException(Tr("请先下载并准备 MuScriptor 模型。"));
        var job = Path.Combine(paths.Jobs, "melody-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(job);
        try
        {
            var wav = Path.Combine(job, "vocal.wav");
            await Task.Run(() => MelodyAudio.PrepareRange(vocalPath, wav, start, end, ct), ct);
            var output = Path.Combine(job, "notes.json");
            await RunBridgeAsync(device, ["transcribe", "--model-dir", ModelDirectory(variant), "--input", wav, "--output", output,
                "--device", device switch { ComputeDevice.Cpu => "cpu", ComputeDevice.Cuda => "cuda", _ => "auto" }], null, progress, ct);
            var raw = JsonSerializer.Deserialize<BridgeNote[]>(await File.ReadAllTextAsync(output, ct), JsonDefaults.Options)
                ?? throw new InvalidDataException(Tr("MuScriptor 没有返回有效音符。"));
            var notes = raw.Where(n => n.Start >= 0 && n.End > n.Start && n.Start < end - start)
                .Select(n => new MelodyNote(Guid.NewGuid(), n.Start + start, Math.Min(end, n.End + start), n.Pitch))
                .OrderBy(n => n.Start).ToArray();
            MelodyDocument.ValidateNotes(notes, duration);
            if (notes.Length == 0) throw new InvalidDataException(Tr("该范围没有识别到可用人声旋律，请调整选段。"));
            var previousEnd = 0d;
            return notes.Select((n, i) =>
            {
                var overlap = previousEnd > n.Start + 0.03 || i + 1 < notes.Length && notes[i + 1].Start < n.End - 0.03;
                previousEnd = Math.Max(previousEnd, n.End);
                var brief = n.End - n.Start < 0.09;
                return n with { NeedsReview = overlap || brief, ReviewReason = overlap ? "音符重叠，可能存在和声" : brief ? "音符较短，请核对" : null };
            }).ToArray();
        }
        finally { Directory.Delete(job, true); }
    }
    public async Task<MelodyMusicAnalysis> AnalyzeAsync(string sourcePath, ComputeDevice device,
        double start, double end, double duration, IProgress<SeparationProgress>? progress, CancellationToken ct)
    {
        var job = Path.Combine(paths.Jobs, "music-analysis-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(job);
        try
        {
            progress?.Report(new("analyze", null, Tr("正在分析节奏与调性")));
            var wav = Path.Combine(job, "source.wav");
            await Task.Run(() => MelodyAudio.PrepareRange(sourcePath, wav, start, end, ct, 44100), ct);
            var output = Path.Combine(job, "analysis.json");
            await RunBridgeAsync(device, ["analyze", "--analysis-dir", AnalysisDirectory, "--input", wav, "--output", output,
                "--device", device switch { ComputeDevice.Cpu => "cpu", ComputeDevice.Cuda => "cuda", _ => "auto" }], null, progress, ct);
            var analysis = JsonSerializer.Deserialize<MelodyMusicAnalysis>(await File.ReadAllTextAsync(output, ct), JsonDefaults.Options)
                ?? throw new InvalidDataException(Tr("节奏与调性分析结果无效。"));
            analysis = analysis with { FirstBeat = analysis.FirstBeat + start };
            analysis.Validate(duration);
            return analysis;
        }
        finally { Directory.Delete(job, true); }
    }
    private sealed record BridgeNote(double Start, double End, int Pitch);
    private async Task RunBridgeAsync(ComputeDevice device, string[] arguments, string? token, IProgress<SeparationProgress>? progress, CancellationToken ct)
    {
        var script = Path.Combine(paths.Runtime, "muscriptor-bridge-" + PackageVersion + ".py");
        var temp = script + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using var resource = typeof(MuScriptorService).Assembly.GetManifestResourceStream("Stemora.Core.Data.muscriptor_bridge.py")!;
            using var reader = new StreamReader(resource);
            await File.WriteAllTextAsync(temp, await reader.ReadToEndAsync(ct), ct);
            File.Move(temp, script, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        await RunProcessAsync(Python(device), new[] { "-u", script }.Concat(arguments), progress, ct, token);
    }
    public static async Task RunProcessAsync(string executable, IEnumerable<string> arguments, IProgress<SeparationProgress>? progress,
        CancellationToken ct, string? hfToken = null, IReadOnlyDictionary<string, string>? environment = null, string? outputStage = null)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in arguments) info.ArgumentList.Add(arg);
        info.Environment["PYTHONUTF8"] = "1"; info.Environment["PYTHONIOENCODING"] = "utf-8";
        if (environment != null) foreach (var entry in environment) info.Environment[entry.Key] = entry.Value;
        if (!string.IsNullOrWhiteSpace(hfToken)) info.Environment["HF_TOKEN"] = hfToken;
        using var process = Process.Start(info) ?? throw new IOException(Tr("无法启动 MuScriptor。"));
        using var cancellation = ct.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { } });
        var errors = new Queue<string>();
        var sync = new object();
        void OnLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            if (!string.IsNullOrWhiteSpace(hfToken)) line = line.Replace(hfToken, "[redacted]", StringComparison.Ordinal);
            if (line.StartsWith('{'))
            {
                try
                {
                    using var json = JsonDocument.Parse(line);
                    var root = json.RootElement;
                    if (root.TryGetProperty("stage", out var stage))
                    {
                        var fraction = root.TryGetProperty("fraction", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var f) && double.IsFinite(f) ? (double?)Math.Clamp(f, 0, 1) : null;
                        progress?.Report(new(stage.GetString()!, fraction, root.TryGetProperty("message", out var message) ? message.GetString() ?? "" : "")); return;
                    }
                }
                catch (JsonException) { }
            }
            lock (sync) { errors.Enqueue(line); while (errors.Count > 15) errors.Dequeue(); }
            progress?.Report(new(outputStage ?? "log", null, line));
        }
        async Task ReadAsync(StreamReader reader)
        {
            // Installers and download clients also update progress with carriage returns.
            var line = new StringBuilder(); var buffer = new char[1024]; int count;
            while ((count = await reader.ReadAsync(buffer)) > 0)
                for (var i = 0; i < count; i++)
                    if (buffer[i] is '\r' or '\n')
                    {
                        if (line.Length > 0) { OnLine(line.ToString()); line.Clear(); }
                    }
                    else line.Append(buffer[i]);
            if (line.Length > 0) OnLine(line.ToString());
        }
        await Task.WhenAll(ReadAsync(process.StandardOutput), ReadAsync(process.StandardError), process.WaitForExitAsync(CancellationToken.None));
        ct.ThrowIfCancellationRequested();
        if (process.ExitCode != 0) throw new InvalidOperationException(Tr("MuScriptor 运行失败 ({0})。{1}", process.ExitCode, string.Join('\n', errors)));
    }
}

internal sealed class RelayProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
