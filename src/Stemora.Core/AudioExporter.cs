using static Stemora.Core.Strings;
using NAudio.Wave;

namespace Stemora.Core;

public enum AudioExportFormat { Wav, Mp3 }

public static class AudioExporter
{
    public static Task ExportAsync(IReadOnlyList<StemResult> tracks, string outputDirectory,
        AudioExportFormat format = AudioExportFormat.Wav, WavEncoding encoding = WavEncoding.Float32,
        IProgress<SeparationProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        if (tracks.Count == 0) throw new ArgumentException(Tr("请至少选择一条音轨。"), nameof(tracks));
        if (!Enum.IsDefined(format) || !Enum.IsDefined(encoding)) throw new ArgumentException(Tr("导出格式无效。"));
        var snapshot = tracks.ToArray();
        GgufInspector.ValidateStems(snapshot.Select(track => track.Id).ToArray(), snapshot.Length);
        var destination = Path.GetFullPath(outputDirectory);
        return Task.Run(() => Export(snapshot, destination, format, encoding, progress, ct), ct);
    }

    private static void Export(StemResult[] tracks, string destination, AudioExportFormat format,
        WavEncoding encoding, IProgress<SeparationProgress>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (Directory.Exists(destination) || File.Exists(destination))
            throw new IOException(Tr("导出文件夹已存在，请选择新的文件夹以保留原有文件。"));
        foreach (var track in tracks)
            if (!File.Exists(track.Path)) throw new FileNotFoundException(Tr("找不到音轨：{0}", track.Label), track.Path);
        var parent = Path.GetDirectoryName(destination) ?? throw new ArgumentException(Tr("请选择有效的导出位置。"));
        Directory.CreateDirectory(parent);
        var stage = Path.Combine(parent, ".stemora-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            for (var i = 0; i < tracks.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                var track = tracks[i];
                progress?.Report(new("export", (double)i / tracks.Length, Tr("正在导出 {0} ({1}/{2})", track.Label, i + 1, tracks.Length)));
                var output = Path.Combine(stage, track.Id + (format == AudioExportFormat.Mp3 ? ".mp3" : ".wav"));
                if (format == AudioExportFormat.Wav) AudioPipeline.Export(track.Path, output, encoding, ct);
                else ExportMp3(track.Path, output, ct);
            }
            ct.ThrowIfCancellationRequested();
            Directory.Move(stage, destination);
        }
        finally
        {
            if (Directory.Exists(stage)) Directory.Delete(stage, true);
        }
    }

    private static void ExportMp3(string source, string destination, CancellationToken ct)
    {
        using var reader = new AudioFileReader(source);
        var samples = new ExportSamples(reader, ct);
        MediaFoundationEncoder.EncodeToMp3(samples.ToWaveProvider16(), destination, 320000);
        ct.ThrowIfCancellationRequested();
    }

    private sealed class ExportSamples(ISampleProvider source, CancellationToken ct) : ISampleProvider
    {
        public WaveFormat WaveFormat => source.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            ct.ThrowIfCancellationRequested();
            var read = source.Read(buffer, offset, count);
            for (var i = offset; i < offset + read; i++)
            {
                if (!float.IsFinite(buffer[i])) throw new InvalidDataException(Tr("音轨包含非有限数值，导出已停止。"));
                buffer[i] = Math.Clamp(buffer[i], -1f, 1f);
            }
            return read;
        }
    }
}
