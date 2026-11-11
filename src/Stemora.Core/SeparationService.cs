using static Stemora.Core.Strings;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Stemora.Core;

public sealed partial class SeparationService(AppPaths paths)
{
    public async Task<SeparationResult> SeparateAsync(SeparationOptions options,
        IProgress<SeparationProgress>? progress = null, CancellationToken ct = default)
    {
        ValidateOptions(options);
        var input = Path.GetFullPath(options.InputPath);
        var model = Path.GetFullPath(options.ModelPath);
        if (!File.Exists(input)) throw new FileNotFoundException(Tr("输入音频不存在。"), input);
        if (!File.Exists(model)) throw new FileNotFoundException(Tr("模型文件不存在。"), model);
        ModelRepository.ValidateGguf(model);
        var engine = new EngineManager(paths).Find(options.EnginePath)
            ?? throw new FileNotFoundException(Tr("尚未安装推理引擎。请在「设置」安装，或运行 stemora runtime install。"));
        var native = EngineManager.IsInfrastructure(engine);
        ModelDescriptor? description = native ? await ModelRepository.InspectAsync(model, ct) : null;
        if (description != null)
        {
            if (options.StemNames != null)
            {
                GgufInspector.ValidateStems(options.StemNames, description.Stems.Count);
                description = description with { Stems = options.StemNames.Select(id => new StemDefinition(id, GgufInspector.StemLabel(id))).ToArray() };
            }
            if (options.Profile != null) description = description with { Family = options.Profile };
        }
        ct.ThrowIfCancellationRequested();
        paths.EnsureCreated();
        var job = Path.Combine(paths.Jobs, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(job);
        var outputRoot = Path.GetFullPath(options.OutputDirectory);
        Directory.CreateDirectory(outputRoot);
        var name = Path.GetFileNameWithoutExtension(input);
        if (name.Length > 70) name = name[..70];
        var id = $"{name}_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..8]}";
        var stage = Path.Combine(outputRoot, ".stemora-" + Guid.NewGuid().ToString("N"));
        var final = Path.Combine(outputRoot, id);
        var timer = Stopwatch.StartNew();
        try
        {
            Directory.CreateDirectory(stage);
            progress?.Report(new("prepare", 0, Tr("正在解码音频")));
            var wav = Path.Combine(job, "input.wav");
            var duration = await Task.Run(() => AudioPipeline.Prepare(input, wav, progress, ct), ct);
            // Upstream uses narrow fopen on Windows. ASCII relative names also work when the user profile,
            // model, source, or output path contains Unicode. Hard-linking avoids copying large models.
            var stagedModel = Path.Combine(job, "model.gguf");
            if (!CreateHardLink(stagedModel, model, IntPtr.Zero))
                await CopyAsync(model, stagedModel, ct);
            await RunEngineAsync(engine, job, options, description, progress, ct);
            var nativeOutputs = native ? ReadNativeOutputs(job, description!) : null;
            var raw = nativeOutputs?.Stems ?? Directory.GetFiles(job, "output*.wav").Order(StringComparer.Ordinal)
                .Select((file, i) => new RawStem(i == 0 ? "primary" : $"stem_{i}", file)).ToArray();
            var stems = raw.Select(s => s.Path).ToArray();
            if (stems.Length == 0) throw new InvalidDataException(Tr("引擎退出后没有生成音轨，请检查引擎版本和日志。"));
            progress?.Report(new("export", 0.92, Tr("正在导出分离音轨")));
            var names = new List<string>();
            for (var i = 0; i < stems.Length; i++)
            {
                AudioPipeline.ValidateStem(wav, stems[i]);
                var filename = raw[i].Id + ".wav";
                await Task.Run(() => AudioPipeline.Export(stems[i], Path.Combine(stage, filename), options.Encoding, ct), ct);
                names.Add(filename);
            }
            string? residualName = null;
            if (options.ExportResidual && stems.Length == 1)
            {
                progress?.Report(new("export", 0.96, Tr("正在计算残差伴奏")));
                residualName = "residual.wav";
                await Task.Run(() => AudioPipeline.ExportResidual(wav, stems[0], Path.Combine(stage, residualName), options.Encoding, ct), ct);
            }
            var tracks = raw.Select((stem, i) => new StemResult(stem.Id, GgufInspector.StemLabel(stem.Id), Path.Combine(final, names[i]))).ToList();
            if (residualName != null) tracks.Add(new("residual", GgufInspector.StemLabel("residual"), Path.Combine(final, residualName)));
            var result = new SeparationResult(input, model, final, Path.Combine(final, names[0]),
                residualName == null ? null : Path.Combine(final, residualName),
                names.Select(n => Path.Combine(final, n)).ToArray(), duration, timer.Elapsed.TotalSeconds, DateTimeOffset.Now,
                tracks, description?.Family, Backend: nativeOutputs?.Backend);
            await File.WriteAllTextAsync(Path.Combine(stage, "result.json"), JsonSerializer.Serialize(result, JsonDefaults.Options), ct);
            ct.ThrowIfCancellationRequested();
            Directory.Move(stage, final);
            progress?.Report(new("completed", 1, Tr("分离完成")));
            return result;
        }
        finally
        {
            TryRemove(job);
            TryRemove(stage);
        }
    }

    public static void ValidateOptions(SeparationOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.InputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ModelPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.OutputDirectory);
        if (options.Overlap is < 1 or > 32) throw new ArgumentException(Tr("重叠次数需要在 1–32 之间。"));
        if (options.ChunkSize is < 8192 or > 3528000) throw new ArgumentException(Tr("分块大小需要在 8192–3528000 采样点之间。"));
        if (options.Overlap.HasValue && options.ChunkSize.HasValue && options.Overlap.Value >= options.ChunkSize.Value)
            throw new ArgumentException(Tr("重叠次数必须小于分块采样数。"));
        if (!Enum.IsDefined(options.Device) || !Enum.IsDefined(options.Encoding)) throw new ArgumentException(Tr("设备或导出格式无效。"));
        if (options.StemNames != null) GgufInspector.ValidateStems(options.StemNames, options.StemNames.Count);
    }

    private sealed record RawStem(string Id, string Path);
    private sealed record NativeOutputs(RawStem[] Stems, string? Backend);
    private static NativeOutputs ReadNativeOutputs(string job, ModelDescriptor expected)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(job, "native-result.json")));
        var stems = document.RootElement.GetProperty("stems").EnumerateArray().Select(item =>
        {
            var id = item.GetProperty("id").GetString() ?? "";
            var filename = item.GetProperty("file").GetString() ?? "";
            if (Path.GetFileName(filename) != filename || !filename.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(Tr("原生后端返回了无效的输出路径。"));
            return new RawStem(id, Path.Combine(job, filename));
        }).ToArray();
        GgufInspector.ValidateStems(stems.Select(s => s.Id).ToArray(), expected.Stems.Count);
        if (!stems.Select(s => s.Id).SequenceEqual(expected.Stems.Select(s => s.Id))) throw new InvalidDataException(Tr("后端返回的音轨顺序与模型描述不符。"));
        var model = document.RootElement.GetProperty("model");
        var backend = model.TryGetProperty("backend", out var value) ? value.GetString() : null;
        return new(stems, backend);
    }

    private static async Task RunEngineAsync(string engine, string workingDirectory, SeparationOptions options,
        ModelDescriptor? description, IProgress<SeparationProgress>? progress, CancellationToken ct)
    {
        var start = new ProcessStartInfo(engine)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        if (description != null)
        {
            foreach (var value in new[] { "separate", "--model", "model.gguf", "--input", "input.wav", "--output-dir", ".", "--device", options.Device switch { ComputeDevice.Cpu => "cpu", ComputeDevice.Cuda => "cuda", _ => "auto" },
                         "--profile", description.Family, "--stems", string.Join(',', description.Stems.Select(s => s.Id)) }) start.ArgumentList.Add(value);
        }
        else foreach (var value in new[] { "model.gguf", "input.wav", "output.wav" }) start.ArgumentList.Add(value);
        if (description == null && options.Device == ComputeDevice.Cuda) throw new NotSupportedException(Tr("CUDA 模式需要 Stemora Infra 后端。"));
        if (options.ChunkSize is { } chunk) { start.ArgumentList.Add("--chunk-size"); start.ArgumentList.Add(chunk.ToString(CultureInfo.InvariantCulture)); }
        if (options.Overlap is { } overlap) { start.ArgumentList.Add("--overlap"); start.ArgumentList.Add(overlap.ToString(CultureInfo.InvariantCulture)); }
        // Infra selects its device per request. Only the explicit legacy protocol uses this override.
        if (description == null && options.Device == ComputeDevice.Cpu) start.Environment["BSR_FORCE_CPU"] = "1";
        else start.Environment.Remove("BSR_FORCE_CPU");
        using var process = new Process { StartInfo = start };
        var tail = new Queue<string>();
        var sync = new object();
        var fraction = 0d;
        void OnLine(string line)
        {
            line = line.Trim();
            if (line.Length == 0) return;
            lock (sync)
            {
                if (description != null && line.StartsWith('{'))
                {
                    try
                    {
                        using var document = JsonDocument.Parse(line);
                        var root = document.RootElement;
                        var type = root.GetProperty("type").GetString();
                        if (type == "progress" && root.TryGetProperty("fraction", out var percent) && percent.TryGetDouble(out var nativeFraction))
                        {
                            fraction = Math.Max(fraction, Math.Clamp(nativeFraction, 0, 1));
                            progress?.Report(new("inference", 0.1 + fraction * 0.8, Tr("推理中 ({0:P0})", fraction)));
                            return;
                        }
                        if (type is "model" or "completed") return;
                        if (type == "error" && root.TryGetProperty("message", out var message)) line = message.GetString() ?? line;
                    }
                    catch (JsonException) { }
                }
                if (NativeOutputParser.ParseFraction(line) is { } value)
                {
                    fraction = Math.Max(fraction, value);
                    progress?.Report(new("inference", 0.1 + fraction * 0.8, Tr("推理中 ({0:P0})", fraction)));
                }
                else
                {
                    tail.Enqueue(line);
                    while (tail.Count > 100) tail.Dequeue();
                    progress?.Report(new("inference", null, line));
                }
            }
        }
        progress?.Report(new("inference", null, Tr("正在加载模型与计算后端")));
        if (!process.Start()) throw new IOException(Tr("无法启动推理引擎。"));
        using var cancellation = ct.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
        });
        var stdout = ReadOutputAsync(process.StandardOutput, OnLine);
        var stderr = ReadOutputAsync(process.StandardError, OnLine);
        await process.WaitForExitAsync(CancellationToken.None);
        await Task.WhenAll(stdout, stderr);
        ct.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(Tr("推理引擎失败（退出码 {0}）。\n{1}", process.ExitCode, string.Join('\n', tail)));
    }

    private static async Task ReadOutputAsync(StreamReader reader, Action<string> onLine)
    {
        var text = new StringBuilder();
        var buffer = new char[1024];
        int count;
        while ((count = await reader.ReadAsync(buffer)) > 0)
        {
            for (var i = 0; i < count; i++)
            {
                var c = buffer[i];
                if (c is '\r' or '\n' || text.Length >= 4096)
                {
                    if (text.Length > 0) { onLine(text.ToString()); text.Clear(); }
                }
                else text.Append(c);
            }
        }
        if (text.Length > 0) onLine(text.ToString());
    }

    private static async Task CopyAsync(string source, string destination, CancellationToken ct)
    {
        await using var input = File.OpenRead(source);
        await using var output = File.Create(destination);
        await input.CopyToAsync(output, ct);
    }
    private static void TryRemove(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);
}
