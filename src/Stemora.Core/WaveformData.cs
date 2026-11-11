using static Stemora.Core.Strings;
using NAudio.Wave;

namespace Stemora.Core;

public readonly record struct WaveformBin(float Minimum, float Maximum, float Rms, int Frames);
public sealed record WaveformLevel(int FramesPerBin, WaveformBin[][] Channels);
public sealed record WaveformData(int SampleRate, int Channels, long Frames, IReadOnlyList<WaveformLevel> Levels,
    float Peak, float Rms)
{
    public double Duration => Frames / (double)SampleRate;
    public static async Task<WaveformData> LoadAsync(string path, CancellationToken ct = default) =>
        await Task.Run(() => Build(path, ct), ct);
    private static WaveformData Build(string path, CancellationToken ct)
    {
        using var reader = new AudioFileReader(path);
        var channels = reader.WaveFormat.Channels;
        if (channels is < 1 or > 2) throw new NotSupportedException(Tr("波形预览支持单声道和双声道。"));
        var rate = reader.WaveFormat.SampleRate;
        var expectedFrames = Math.Max(1, (long)(reader.TotalTime.TotalSeconds * rate));
        var block = 32;
        while (expectedFrames / block > 500_000) block *= 2;
        var bins = Enumerable.Range(0, channels).Select(_ => new List<WaveformBin>()).ToArray();
        var min = Enumerable.Repeat(float.PositiveInfinity, channels).ToArray();
        var max = Enumerable.Repeat(float.NegativeInfinity, channels).ToArray();
        var square = new double[channels];
        double totalSquare = 0; float peak = 0; long frames = 0; var inBin = 0;
        var buffer = new float[32768];
        void Flush()
        {
            if (inBin == 0) return;
            for (var ch = 0; ch < channels; ch++)
            {
                bins[ch].Add(new(min[ch], max[ch], (float)Math.Sqrt(square[ch] / inBin), inBin));
                min[ch] = float.PositiveInfinity; max[ch] = float.NegativeInfinity; square[ch] = 0;
            }
            inBin = 0;
        }
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            for (var i = 0; i < read; i += channels)
            {
                for (var ch = 0; ch < channels; ch++)
                {
                    var value = float.IsFinite(buffer[i + ch]) ? buffer[i + ch] : 0;
                    min[ch] = Math.Min(min[ch], value); max[ch] = Math.Max(max[ch], value);
                    square[ch] += value * value; totalSquare += value * value;
                    peak = Math.Max(peak, Math.Abs(value));
                }
                frames++; inBin++;
                if (inBin == block) Flush();
            }
        }
        Flush();
        if (frames == 0) throw new InvalidDataException(Tr("音频没有采样数据。"));
        var levels = new List<WaveformLevel> { new(block, bins.Select(b => b.ToArray()).ToArray()) };
        while (levels[^1].Channels[0].Length > 1)
        {
            ct.ThrowIfCancellationRequested();
            var previous = levels[^1];
            var reduced = new WaveformBin[channels][];
            for (var ch = 0; ch < channels; ch++)
            {
                reduced[ch] = new WaveformBin[(previous.Channels[ch].Length + 1) / 2];
                for (var i = 0; i < reduced[ch].Length; i++)
                {
                    var a = previous.Channels[ch][i * 2];
                    if (i * 2 + 1 >= previous.Channels[ch].Length) { reduced[ch][i] = a; continue; }
                    var b = previous.Channels[ch][i * 2 + 1]; var count = a.Frames + b.Frames;
                    reduced[ch][i] = new(Math.Min(a.Minimum, b.Minimum), Math.Max(a.Maximum, b.Maximum),
                        (float)Math.Sqrt((a.Rms * a.Rms * a.Frames + b.Rms * b.Rms * b.Frames) / count), count);
                }
            }
            levels.Add(new(previous.FramesPerBin * 2, reduced));
        }
        return new(rate, channels, frames, levels, peak, (float)Math.Sqrt(totalSquare / (frames * channels)));
    }
    public WaveformBin[] View(int channel, double start, double duration, int pixels)
    {
        pixels = Math.Max(1, pixels); channel = Math.Clamp(channel, 0, Channels - 1);
        var samplesPerPixel = duration * SampleRate / pixels;
        var level = Levels.LastOrDefault(l => l.FramesPerBin <= samplesPerPixel) ?? Levels[0];
        var result = new WaveformBin[pixels];
        var source = level.Channels[channel];
        for (var pixel = 0; pixel < pixels; pixel++)
        {
            var first = Math.Max(0, (int)Math.Floor((start + duration * pixel / pixels) * SampleRate / level.FramesPerBin));
            var end = Math.Min(source.Length, Math.Max(first + 1, (int)Math.Ceiling((start + duration * (pixel + 1) / pixels) * SampleRate / level.FramesPerBin)));
            if (first >= source.Length) continue;
            float minimum = float.PositiveInfinity, maximum = float.NegativeInfinity; double energy = 0; var count = 0;
            for (var i = first; i < end; i++) { var bin = source[i]; minimum = Math.Min(minimum, bin.Minimum); maximum = Math.Max(maximum, bin.Maximum); energy += bin.Rms * bin.Rms * bin.Frames; count += bin.Frames; }
            result[pixel] = new(minimum, maximum, count > 0 ? (float)Math.Sqrt(energy / count) : 0, count);
        }
        return result;
    }
    public static string Decibels(float value) => value > 0.000001f ? $"{20 * Math.Log10(value):F1} dB" : "−∞ dB";
}
