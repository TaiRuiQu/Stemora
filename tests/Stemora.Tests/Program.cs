using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NAudio.Wave;
using Stemora.Core;

if (args.FirstOrDefault() == "--verify-real")
{
    var result = JsonSerializer.Deserialize<SeparationResult>(File.ReadAllText(args[1]), JsonDefaults.Options)!;
    var normalized = Path.Combine(Path.GetTempPath(), "stemora-verify-" + Guid.NewGuid().ToString("N") + ".wav");
    try
    {
        AudioPipeline.Prepare(result.InputPath, normalized, null, default);
        if (result.Tracks is { Count: > 1 } tracks && result.ResidualPath == null)
        {
            using var inputReader = new AudioFileReader(normalized);
            var sources = tracks.Select(t => new AudioFileReader(t.Path)).ToArray();
            try
            {
                foreach (var track in tracks) AudioPipeline.ValidateStem(normalized, track.Path);
                var buffer = new float[32768]; var other = new float[32768]; var sum = new float[32768];
                double dot = 0, energyInput = 0, energySum = 0, errorEnergy = 0;
                var energies = new double[tracks.Count]; long samples = 0;
                int count;
                while ((count = inputReader.Read(buffer, 0, buffer.Length)) > 0)
                {
                    Array.Clear(sum);
                    for (var stem = 0; stem < sources.Length; stem++)
                    {
                        if (sources[stem].Read(other, 0, count) != count) throw new Exception("Multitrack length mismatch");
                        for (var i = 0; i < count; i++)
                        {
                            if (!float.IsFinite(other[i])) throw new Exception("Non-finite multitrack output");
                            sum[i] += other[i]; energies[stem] += other[i] * other[i];
                        }
                    }
                    for (var i = 0; i < count; i++) { dot += buffer[i] * sum[i]; energyInput += buffer[i] * buffer[i]; energySum += sum[i] * sum[i]; errorEnergy += (buffer[i] - sum[i]) * (buffer[i] - sum[i]); }
                    samples += count;
                }
                var correlation = dot / Math.Sqrt(energyInput * energySum);
                var errorDb = 10 * Math.Log10(errorEnergy / energyInput);
                if (sources.Length != 6 || tracks.Select(t => t.Id).Distinct().Count() != 6 || energies.Any(e => e <= 0) || correlation < 0.99 || errorDb > -20)
                    throw new Exception($"Six-stem quality/reconstruction check failed: correlation {correlation}, relative error {errorDb} dB");
                Console.WriteLine($"PASS actual SW six-stem inference: {result.DurationSeconds:F2}s audio, {result.ElapsedSeconds:F2}s elapsed, {samples} samples/track, sum cosine similarity {correlation:F6}, relative reconstruction error {errorDb:F2} dB");
                return 0;
            }
            finally { foreach (var source in sources) source.Dispose(); }
        }
        AudioPipeline.ValidateStem(normalized, result.PrimaryPath);
        using var mixture = new AudioFileReader(normalized);
        using var primary = new AudioFileReader(result.PrimaryPath);
        using var residual = new AudioFileReader(result.ResidualPath!);
        var a = new float[32768]; var b = new float[32768]; var c = new float[32768];
        double error = 0, energy = 0; long total = 0;
        int n;
        while ((n = mixture.Read(a, 0, a.Length)) > 0)
        {
            if (primary.Read(b, 0, n) != n || residual.Read(c, 0, n) != n) throw new Exception("Real output truncated");
            for (var i = 0; i < n; i++)
            {
                if (!float.IsFinite(b[i]) || !float.IsFinite(c[i])) throw new Exception("Non-finite real output");
                error = Math.Max(error, Math.Abs(a[i] - b[i] - c[i]));
                energy += b[i] * b[i]; total++;
            }
        }
        if (error > 1e-6 || total == 0 || energy <= 0) throw new Exception("Real reconstruction or energy check failed");
        Console.WriteLine($"PASS real GGUF inference: {result.DurationSeconds:F2}s audio, {result.ElapsedSeconds:F2}s elapsed, {total} samples, max reconstruction error {error:E3}, primary RMS {Math.Sqrt(energy / total):E3}");
        return 0;
    }
    finally { File.Delete(normalized); }
}

if (args.FirstOrDefault() == "model.gguf")
{
    var mode = Environment.GetEnvironmentVariable("STEMORA_TEST_ENGINE_MODE");
    if (mode == "wait") { await Task.Delay(TimeSpan.FromSeconds(30)); return 0; }
    if (mode == "fail") { Console.Error.WriteLine("Intentional test engine failure"); return 7; }
    using (var reader = new AudioFileReader("input.wav"))
    using (var writer = new WaveFileWriter(mode == "multi" ? "output_stem_0.wav" : "output.wav", reader.WaveFormat))
    {
        var samples = new float[4096];
        int count;
        while ((count = reader.Read(samples, 0, samples.Length)) > 0)
        {
            for (var i = 0; i < count; i++) samples[i] *= 0.3f;
            writer.WriteSamples(samples, 0, count);
        }
    }
    if (mode == "multi") File.Copy("output_stem_0.wav", "output_stem_1.wav");
    Console.Write("[===> ] 10 %\r[====>] 100 %\r"); Console.Out.Flush();
    return 0;
}

var root = Path.Combine(Path.GetTempPath(), "StemoraTests_中文_" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var passed = 0;
void Check(bool condition, string label)
{
    if (!condition) throw new Exception("FAIL: " + label);
    passed++;
    Console.WriteLine("PASS: " + label);
}
try
{
    var paths = new AppPaths(Path.Combine(root, "数据")); paths.EnsureCreated();
    var model = Path.Combine(root, "模型.gguf");
    using (var writer = new BinaryWriter(File.Create(model))) { writer.Write(0x46554747u); writer.Write(3u); writer.Write(0L); writer.Write(0L); }
    var input = Path.Combine(root, "测试 ' & 音频.wav");
    using (var writer = new WaveFileWriter(input, WaveFormat.CreateIeeeFloatWaveFormat(48000, 1)))
    {
        var samples = Enumerable.Range(0, 12000).Select(i => (float)(Math.Sin(i * Math.Tau * 440 / 48000) * 0.4)).ToArray();
        writer.WriteSamples(samples, 0, samples.Length);
    }
    Check(NativeOutputParser.ParseFraction("[==>] 42 %") == 0.42, "native carriage-return progress parser");
    Check(NativeOutputParser.ParseFraction("tensor size: 256") == null && NativeOutputParser.ParseFraction("144 %") == null, "ignore unrelated numbers and invalid progress");
    Check(ModelRepository.Catalog.Count == 22 && ModelRepository.Catalog.All(m => m.Sha256.Length == 64 && m.Revision.Length == 40), "pinned catalog and SHA-256 coverage");
    // A loopback HTTP fixture exercises the real streaming/resume/hash path without downloading model files.
    var payload = Enumerable.Range(0, 65536).Select(i => (byte)(i % 251)).ToArray();
    var downloadPath = Path.Combine(root, "download.gguf");
    await File.WriteAllBytesAsync(downloadPath + ".partial", payload[..8192]);
    var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    var sawRange = false;
    var server = Task.Run(async () =>
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, true);
        string? line;
        while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
            if (line.StartsWith("Range: bytes=8192-", StringComparison.OrdinalIgnoreCase)) sawRange = true;
        var offset = sawRange ? 8192 : 0;
        var headers = $"HTTP/1.1 {(sawRange ? "206 Partial Content" : "200 OK")}\r\nContent-Length: {payload.Length - offset}\r\n";
        if (sawRange) headers += $"Content-Range: bytes {offset}-{payload.Length - 1}/{payload.Length}\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers + "Connection: close\r\n\r\n"));
        await stream.WriteAsync(payload.AsMemory(offset));
    });
    try
    {
        await Downloads.FetchAsync(new($"http://127.0.0.1:{port}/model"), downloadPath, Convert.ToHexString(SHA256.HashData(payload)), payload.Length);
        await server;
        Check(sawRange && File.ReadAllBytes(downloadPath).SequenceEqual(payload), "HTTP Range download resumes and passes SHA-256");
        Check(!await Downloads.IsValidAsync(downloadPath, new string('0', 64), payload.Length), "reject checksum mismatch");
    }
    finally { listener.Stop(); }
    var settings = new AppSettings { ModelPath = model, Overlap = 4, Device = ComputeDevice.Cpu };
    await paths.SaveSettingsAsync(settings);
    Check(paths.LoadSettings() == settings, "settings round trip");
    var portableSettings = settings with { ModelPath = Path.Combine(paths.Models, "test.gguf"), EnginePath = Path.Combine(paths.Runtime, "engine.exe") };
    await paths.SaveSettingsAsync(portableSettings);
    var moved = new AppPaths(Path.Combine(root, "移动的数据")); moved.EnsureCreated();
    File.Copy(paths.SettingsFile, moved.SettingsFile);
    Check(moved.LoadSettings().ModelPath == Path.Combine(moved.Models, "test.gguf")
        && moved.LoadSettings().EnginePath == Path.Combine(moved.Runtime, "engine.exe"), "portable settings survive moving the data directory");
    await File.WriteAllTextAsync(paths.SettingsFile, "malformed");
    try { paths.LoadSettings(); throw new Exception("Expected malformed settings failure"); } catch (JsonException) { Check(true, "malformed settings reported"); }
    await paths.SaveSettingsAsync(settings);
    try { ModelRepository.ValidateGguf(input); throw new Exception("Expected invalid GGUF failure"); } catch (InvalidDataException) { Check(true, "reject invalid GGUF"); }
    var normalized = Path.Combine(root, "expected.wav");
    var duration = AudioPipeline.Prepare(input, normalized, null, default);
    Check(Math.Abs(duration - 0.25) < 0.01, "48 kHz mono resamples to 44.1 kHz stereo");
    var options = new SeparationOptions(input, model, Path.Combine(root, "输出"), EnginePath: Environment.ProcessPath);
    var progress = new List<SeparationProgress>();
    var result = await new SeparationService(paths).SeparateAsync(options, new SynchronousProgress<SeparationProgress>(progress.Add));
    Check(File.Exists(result.PrimaryPath) && File.Exists(result.ResidualPath) && File.Exists(Path.Combine(result.OutputDirectory, "result.json")), "complete native process pipeline with Unicode paths");
    using (var original = new AudioFileReader(normalized))
    using (var primary = new AudioFileReader(result.PrimaryPath))
    using (var residual = new AudioFileReader(result.ResidualPath!))
    {
        var a = new float[original.Length / 4]; var b = new float[a.Length]; var c = new float[a.Length];
        var n = original.Read(a, 0, a.Length); var nb = primary.Read(b, 0, b.Length); var nc = residual.Read(c, 0, c.Length);
        Check(n == nb && n == nc, "sample-accurate stem lengths");
        Check(a.Zip(b.Zip(c), (x, yz) => Math.Abs(x - yz.First - yz.Second)).Max() < 1e-6, "primary plus residual reconstructs normalized input");
    }
    Check(progress.Any(p => p.Stage == "inference" && p.Fraction >= 0.89), "streaming progress captured before process exit");
    Check(!Directory.EnumerateFileSystemEntries(paths.Jobs).Any(), "job working files cleaned on success");
    foreach (var encoding in new[] { WavEncoding.Pcm16, WavEncoding.Pcm24 })
    {
        var file = Path.Combine(root, encoding + ".wav"); AudioPipeline.Export(result.PrimaryPath, file, encoding, default);
        using var reader = new WaveFileReader(file);
        Check(reader.WaveFormat.BitsPerSample == (encoding == WavEncoding.Pcm16 ? 16 : 24), encoding + " export");
    }
    var exportTracks = result.Tracks!.ToArray();
    foreach (var encoding in Enum.GetValues<WavEncoding>())
    {
        var exportDirectory = Path.Combine(root, "另存音轨-" + encoding);
        await AudioExporter.ExportAsync(exportTracks, exportDirectory, encoding: encoding);
        using var wave = new WaveFileReader(Path.Combine(exportDirectory, "primary.wav"));
        Check(wave.WaveFormat.SampleRate == 44100 && wave.WaveFormat.Channels == 2
            && wave.WaveFormat.BitsPerSample == (encoding == WavEncoding.Float32 ? 32 : encoding == WavEncoding.Pcm24 ? 24 : 16)
            && File.Exists(Path.Combine(exportDirectory, "residual.wav")), "batch WAV export with Unicode paths: " + encoding);
    }
    var mp3Directory = Path.Combine(root, "MP3 导出");
    await AudioExporter.ExportAsync([exportTracks[0]], mp3Directory, AudioExportFormat.Mp3);
    using (var mp3 = new AudioFileReader(Path.Combine(mp3Directory, "primary.mp3")))
    {
        var samples = new float[32768];
        var count = mp3.Read(samples, 0, samples.Length);
        Check(mp3.WaveFormat.SampleRate == 44100 && mp3.WaveFormat.Channels == 2
            && Math.Abs(mp3.TotalTime.TotalSeconds - result.DurationSeconds) < 0.1
            && count > 0 && samples.Take(count).Any(sample => Math.Abs(sample) > 0.001f)
            && Directory.GetFiles(mp3Directory).Length == 1, "selected track MP3 export decodes with correct audio and duration");
    }
    using (var stream = File.OpenRead(Path.Combine(mp3Directory, "primary.mp3")))
    {
        var frame = Mp3Frame.LoadFromStream(stream);
        Check(frame != null && frame.BitRate == 320000, "MP3 export uses 320 kbps");
    }
    try { await AudioExporter.ExportAsync(exportTracks, mp3Directory); throw new Exception("Existing export overwritten"); }
    catch (IOException) { Check(File.Exists(Path.Combine(mp3Directory, "primary.mp3")), "export preserves an existing output folder"); }
    using (var cancellation = new CancellationTokenSource())
    {
        var exportDirectory = Path.Combine(root, "cancelled-export");
        var cancelProgress = new SynchronousProgress<SeparationProgress>(value => { if (value.Fraction > 0) cancellation.Cancel(); });
        try { await AudioExporter.ExportAsync(exportTracks, exportDirectory, progress: cancelProgress, ct: cancellation.Token); throw new Exception("Export cancellation ignored"); }
        catch (OperationCanceledException)
        {
            Check(!Directory.Exists(exportDirectory) && !Directory.EnumerateDirectories(root, ".stemora-export-*").Any(),
                "cancelled batch export cleans all partial tracks");
        }
    }
    try { await AudioExporter.ExportAsync([exportTracks[0] with { Id = "../escape" }], Path.Combine(root, "unsafe-export")); throw new Exception("Unsafe export name accepted"); }
    catch (InvalidDataException) { Check(true, "export rejects unsafe track filenames"); }
    Check(File.Exists(result.PrimaryPath) && File.Exists(result.ResidualPath), "additional exports preserve original separation files");
    Environment.SetEnvironmentVariable("STEMORA_TEST_ENGINE_MODE", "multi");
    var multi = await new SeparationService(paths).SeparateAsync(options with { ExportResidual = false });
    Check(multi.StemPaths.Count == 2 && multi.ResidualPath == null, "multiple model stems retained");
    Environment.SetEnvironmentVariable("STEMORA_TEST_ENGINE_MODE", "fail");
    try { await new SeparationService(paths).SeparateAsync(options); throw new Exception("Expected process failure"); }
    catch (InvalidOperationException e) { Check(e.Message.Contains("7") && e.Message.Contains("Intentional"), "native exit code and stderr surfaced"); }
    Check(!Directory.EnumerateDirectories(options.OutputDirectory, ".stemora-*").Any(), "failed operation leaves no partial exports");
    Environment.SetEnvironmentVariable("STEMORA_TEST_ENGINE_MODE", "wait");
    using (var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1)))
    {
        var timer = Stopwatch.StartNew();
        try { await new SeparationService(paths).SeparateAsync(options, ct: cancellation.Token); throw new Exception("Expected cancellation"); }
        catch (OperationCanceledException) { Check(timer.Elapsed < TimeSpan.FromSeconds(8), "cancellation terminates the native child process"); }
    }
    Check(!Directory.EnumerateFileSystemEntries(paths.Jobs).Any(), "job working files cleaned on cancellation");
    Check(Directory.GetDirectories(options.OutputDirectory).Length == 2, "successful outputs preserved across later failures");
    var waveform = await WaveformData.LoadAsync(normalized);
    Check(waveform.Channels == 2 && waveform.Frames == (long)(duration * 44100) && waveform.Levels.Count > 1, "multiresolution waveform retains channel and duration metadata");
    var envelope = waveform.View(0, 0, waveform.Duration, 100);
    Check(envelope.Min(b => b.Minimum) < -0.35 && envelope.Max(b => b.Maximum) > 0.35 && waveform.Rms > 0.2, "waveform retains signed amplitude and RMS, not repeated decorative bars");
    var zoomed = waveform.View(1, 0.05, 0.02, 160);
    Check(zoomed.Length == 160 && zoomed.Any(b => b.Maximum > 0), "waveform supports channel-specific zoomed time windows");
    var aSamples = Enumerable.Range(0, 100).Select(i => i % 2 == 0 ? 0.1f : -0.1f).ToArray();
    var bSamples = Enumerable.Repeat(0.2f, 100).ToArray();
    var channelA = new MixerChannel(new("a", "A", ""), new ArraySamples(aSamples));
    var channelB = new MixerChannel(new("b", "B", ""), new ArraySamples(bSamples));
    var mixer = new StemMixerGraph([channelA, channelB]);
    var mixed = new float[20]; mixer.Read(mixed, 0, 20);
    Check(Math.Abs(mixed[0] - 0.3) < 1e-6 && Math.Abs(mixed[1] - 0.1) < 1e-6, "one sample clock sums aligned tracks");
    mixer.SetMute(1, true); mixer.Read(mixed, 0, 20);
    Check(Math.Abs(mixed[0] - aSamples[20]) < 1e-6 && channelB.Peak == 0, "Mute silences a track while consuming aligned samples");
    mixer.SetMute(1, false); mixer.SetSolo(1, true); mixer.Read(mixed, 0, 20);
    Check(mixed.All(v => Math.Abs(v - 0.2) < 1e-6) && channelA.Peak == 0, "Solo isolates the selected track without seeking others");
    mixer.SetSolo(1, false); mixer.SetVolume(1, 0.5f); mixer.Read(mixed, 0, 20);
    Check(Math.Abs(mixed[0] - 0.2) < 1e-6 && Math.Abs(mixed[1]) < 1e-6, "per-track volume and M/S restoration preserve phase");
    mixer.SetSolo(0, true); mixer.SetSolo(1, true); mixer.Read(mixed, 0, 20);
    Check(Math.Abs(mixed[0] - 0.2) < 1e-6, "multiple Solo tracks are mixed together");
    var gguf = Path.Combine(root, "metadata.gguf");
    using (var writer = new BinaryWriter(File.Create(gguf)))
    {
        void Text(string value) { var bytes = Encoding.UTF8.GetBytes(value); writer.Write((ulong)bytes.Length); writer.Write(bytes); }
        writer.Write(0x46554747u); writer.Write(3u); writer.Write(0UL); writer.Write(5UL);
        Text("general.architecture"); writer.Write(8u); Text("bs_roformer");
        Text("bs_roformer.num_stems"); writer.Write(4u); writer.Write(6u);
        Text("bs_roformer.sample_rate"); writer.Write(4u); writer.Write(44100u);
        Text("bs_roformer.default_chunk_size"); writer.Write(4u); writer.Write(588800u);
        Text("stemora.stems"); writer.Write(9u); writer.Write(8u); writer.Write(6UL);
        foreach (var id in new[] { "bass", "drums", "other", "vocals", "guitar", "piano" }) Text(id);
    }
    var descriptor = GgufInspector.Read(gguf);
    Check(descriptor.Stems.Select(t => t.Id).SequenceEqual(new[] { "bass", "drums", "other", "vocals", "guitar", "piano" }) && descriptor.DefaultChunkSize == 588800, "GGUF output count, order and defaults are read from model metadata");
    await File.WriteAllTextAsync(gguf + ".stemora.json", "{\"family\":\"bs_roformer_sw\",\"stems\":[\"bass\",\"drums\",\"other\",\"vocals\",\"guitar\",\"../piano\"]}");
    try { GgufInspector.Read(gguf); throw new Exception("Unsafe stem accepted"); }
    catch (InvalidDataException) { Check(true, "reject unsafe or mismatched stem sidecars"); }
    Console.WriteLine($"\n{passed} checks passed.");
    return 0;
}
finally
{
    Environment.SetEnvironmentVariable("STEMORA_TEST_ENGINE_MODE", null);
    Directory.Delete(root, true);
}

sealed class SynchronousProgress<T>(Action<T> action) : IProgress<T>
{
    public void Report(T value) => action(value);
}

sealed class ArraySamples(float[] samples) : ISampleProvider
{
    private int _position;
    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
    public int Read(float[] buffer, int offset, int count)
    {
        var read = Math.Min(count, samples.Length - _position);
        Array.Copy(samples, _position, buffer, offset, read); _position += read; return read;
    }
}
