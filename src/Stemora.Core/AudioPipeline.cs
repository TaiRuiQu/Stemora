using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Stemora.Core;

public static class AudioPipeline
{
    public const int SampleRate = 44100;
    public static readonly string[] Extensions = [".wav", ".mp3", ".flac", ".m4a", ".aac", ".wma", ".aiff", ".aif", ".mp4"];

    public static double Prepare(string source, string destination, IProgress<SeparationProgress>? progress, CancellationToken ct)
    {
        using var reader = new AudioFileReader(source);
        if (reader.TotalTime <= TimeSpan.Zero) throw new InvalidDataException("音频为空或无法读取时长。");
        if (reader.TotalTime.TotalSeconds * SampleRate * 8 > uint.MaxValue - 4096)
            throw new InvalidDataException("音频过长，超过 WAV 的 4 GB 限制，请先拆分音频。");
        ISampleProvider samples = reader;
        if (samples.WaveFormat.Channels == 1) samples = new MonoToStereoSampleProvider(samples);
        else if (samples.WaveFormat.Channels != 2) throw new NotSupportedException("目前支持单声道和双声道音频，请先将环绕声混缩为双声道。");
        if (samples.WaveFormat.SampleRate != SampleRate) samples = new WdlResamplingSampleProvider(samples, SampleRate);
        using var writer = new WaveFileWriter(destination, WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 2));
        var buffer = new float[32768];
        long total = 0;
        int read;
        while ((read = samples.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            for (var i = 0; i < read; i++)
                if (!float.IsFinite(buffer[i])) throw new InvalidDataException("输入音频包含非有限数值。");
            writer.WriteSamples(buffer, 0, read);
            total += read;
        }
        if (total == 0) throw new InvalidDataException("音频没有可解码的采样数据。");
        progress?.Report(new("prepare", 0.08, "音频已转换为 44.1 kHz 双声道"));
        return total / (double)(SampleRate * 2);
    }

    public static void Export(string source, string destination, WavEncoding encoding, CancellationToken ct)
    {
        using var reader = new AudioFileReader(source);
        ValidateOutputFormat(reader.WaveFormat);
        using var writer = CreateWriter(destination, encoding);
        var buffer = new float[32768];
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            Write(writer, buffer, read, encoding);
        }
    }

    public static void ValidateStem(string inputPath, string stemPath)
    {
        using var input = new AudioFileReader(inputPath);
        using var output = new AudioFileReader(stemPath);
        ValidateOutputFormat(output.WaveFormat);
        if (output.Length == 0 || input.Length != output.Length)
            throw new InvalidDataException("模型输出的采样长度与输入不一致，导出已停止。");
    }

    public static void ExportResidual(string mixture, string primary, string destination, WavEncoding encoding, CancellationToken ct)
    {
        using var input = new AudioFileReader(mixture);
        using var target = new AudioFileReader(primary);
        ValidateOutputFormat(input.WaveFormat);
        ValidateOutputFormat(target.WaveFormat);
        if (input.Length != target.Length) throw new InvalidDataException("模型输出的采样长度与输入不一致，无法生成对齐的残差音轨。");
        using var writer = CreateWriter(destination, encoding);
        var sourceBuffer = new float[32768];
        var targetBuffer = new float[32768];
        int read;
        while ((read = input.Read(sourceBuffer, 0, sourceBuffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            var got = 0;
            while (got < read)
            {
                var n = target.Read(targetBuffer, got, read - got);
                if (n == 0) throw new InvalidDataException("模型输出意外截断。");
                got += n;
            }
            for (var i = 0; i < read; i++) sourceBuffer[i] -= targetBuffer[i];
            Write(writer, sourceBuffer, read, encoding);
        }
    }

    public static float[] GetPeaks(string source, int buckets = 220, CancellationToken ct = default)
    {
        using var reader = new AudioFileReader(source);
        var peaks = new float[buckets];
        var sampleCount = Math.Max(1d, reader.TotalTime.TotalSeconds * reader.WaveFormat.SampleRate * reader.WaveFormat.Channels);
        var buffer = new float[32768];
        long index = 0;
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            for (var i = 0; i < read; i++, index++)
            {
                var bucket = Math.Min(buckets - 1, (int)(index * buckets / sampleCount));
                peaks[bucket] = Math.Max(peaks[bucket], float.IsFinite(buffer[i]) ? Math.Abs(buffer[i]) : 0);
            }
        }
        return peaks;
    }

    private static void ValidateOutputFormat(WaveFormat format)
    {
        if (format.SampleRate != SampleRate || format.Channels != 2)
            throw new InvalidDataException("推理输出必须为 44.1 kHz 双声道 WAV。");
    }
    private static WaveFileWriter CreateWriter(string path, WavEncoding encoding) => new(path,
        encoding == WavEncoding.Float32 ? WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 2)
        : new WaveFormat(SampleRate, encoding == WavEncoding.Pcm24 ? 24 : 16, 2));
    private static void Write(WaveFileWriter writer, float[] buffer, int count, WavEncoding encoding)
    {
        for (var i = 0; i < count; i++)
        {
            if (!float.IsFinite(buffer[i])) throw new InvalidDataException("模型输出包含非有限数值，导出已停止。");
            if (encoding != WavEncoding.Float32) buffer[i] = Math.Clamp(buffer[i], -1f, 1f);
        }
        writer.WriteSamples(buffer, 0, count);
    }
}
