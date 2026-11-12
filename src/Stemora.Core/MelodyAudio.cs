using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using static Stemora.Core.Strings;

namespace Stemora.Core;

public static class MelodyAudio
{
    public static void PrepareRange(string source, string destination, double start, double end, CancellationToken ct, int sampleRate = 16000)
    {
        using var reader = new AudioFileReader(source);
        if (!double.IsFinite(start) || !double.IsFinite(end) || start < 0 || end <= start || end > reader.TotalTime.TotalSeconds + 0.02)
            throw new ArgumentException(Tr("选段起止时间无效。"));
        reader.CurrentTime = TimeSpan.FromSeconds(start);
        ISampleProvider samples = reader;
        if (samples.WaveFormat.Channels == 2) samples = new StereoToMonoSampleProvider(samples);
        if (samples.WaveFormat.Channels != 1) throw new NotSupportedException(Tr("试听支持单声道或双声道音频。"));
        if (samples.WaveFormat.SampleRate != sampleRate) samples = new WdlResamplingSampleProvider(samples, sampleRate);
        using var writer = new WaveFileWriter(destination, WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1));
        var left = (long)Math.Round((end - start) * sampleRate);
        var buffer = new float[16384];
        while (left > 0)
        {
            ct.ThrowIfCancellationRequested();
            var n = samples.Read(buffer, 0, (int)Math.Min(left, buffer.Length));
            if (n == 0) break;
            if (buffer.Take(n).Any(v => !float.IsFinite(v))) throw new InvalidDataException(Tr("输入音频包含非有限数值。"));
            writer.WriteSamples(buffer, 0, n); left -= n;
        }
    }
}

// Immutable note arrays are exchanged at audio-buffer boundaries; the clock keeps running.
public sealed class MelodySynth : ISampleProvider
{
    private MelodyNote[] _notes;
    private MelodyNote[]? _previous;
    private int _fade;
    private long _frame;
    private readonly long _end;
    private const int FadeFrames = 441;
    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(44100, 1);
    public MelodySynth(IEnumerable<MelodyNote> notes, double start, double end)
    {
        _notes = notes.OrderBy(n => n.Start).ToArray();
        PianoVoice.Prepare(_notes);
        _frame = (long)(start * 44100); _end = (long)(end * 44100);
    }
    public void UpdateNotes(IEnumerable<MelodyNote> notes)
    {
        var snapshot = notes.OrderBy(n => n.Start).ToArray();
        PianoVoice.Prepare(snapshot); Volatile.Write(ref _notes, snapshot);
    }
    private static void Render(MelodyNote[] notes, long frame, float[] buffer, int offset, int count, double gainStart, double gainStep)
    {
        var start = frame / 44100d; var end = (frame + count) / 44100d;
        foreach (var note in notes)
        {
            if (note.Start >= end) break;
            if (note.End + PianoVoice.ReleaseSeconds <= start) continue;
            var from = Math.Max(0, (int)Math.Ceiling((note.Start - start) * 44100));
            var to = Math.Min(count, (int)Math.Ceiling((note.End + PianoVoice.ReleaseSeconds - start) * 44100));
            var voice = new PianoVoice(note.Pitch);
            for (var i = from; i < to; i++)
            {
                var t = (frame + i) / 44100d - note.Start;
                buffer[offset + i] += (float)(voice.Sample(t, note.End - note.Start) * Math.Clamp(gainStart + i * gainStep, 0, 1));
            }
        }
    }
    public int Read(float[] buffer, int offset, int count)
    {
        var notes = Volatile.Read(ref _notes);
        var read = (int)Math.Min(count, Math.Max(0, _end - _frame));
        Array.Clear(buffer, offset, read);
        if (_previous != null && !ReferenceEquals(_previous, notes))
        {
            var fading = Math.Min(read, FadeFrames - _fade);
            Render(_previous, _frame, buffer, offset, fading, 1 - _fade / (double)FadeFrames, -1d / FadeFrames);
            Render(notes, _frame, buffer, offset, read, _fade / (double)FadeFrames, 1d / FadeFrames);
            _fade += fading;
            if (_fade >= FadeFrames) { _previous = notes; _fade = 0; }
        }
        else { Render(notes, _frame, buffer, offset, read, 1, 0); _previous = notes; }
        for (var i = 0; i < read; i++) buffer[offset + i] = Math.Clamp(buffer[offset + i], -0.9f, 0.9f);
        _frame += read; return read;
    }
}

public enum MelodyListenMode { Vocal, Piano, Together, Mix, MixTogether }

// All streams advance on the same device clock, including while individually muted.
public sealed class MelodyMonitor : ISampleProvider
{
    private readonly ISampleProvider? _vocal, _mix;
    private readonly MelodySynth _current;
    private float[] _voiceBuffer = [], _mixBuffer = [], _currentBuffer = [];
    private int _mode;
    private float _pianoVolume = 0.8f, _vocalVolume = 0.8f;
    private double _voiceGain, _mixGain, _pianoGain;
    public WaveFormat WaveFormat => _current.WaveFormat;
    public MelodyMonitor(ISampleProvider? vocal, IEnumerable<MelodyNote> current, double start, double end, MelodyListenMode mode, ISampleProvider? mix = null)
    {
        _vocal = vocal; _mix = mix; _current = new(current, start, end);
        SetMode(mode);
        _voiceGain = mode is MelodyListenMode.Vocal or MelodyListenMode.Together ? _vocalVolume : 0;
        _mixGain = mode is MelodyListenMode.Mix or MelodyListenMode.MixTogether ? _vocalVolume : 0;
        _pianoGain = mode is MelodyListenMode.Piano or MelodyListenMode.Together or MelodyListenMode.MixTogether ? _pianoVolume : 0;
    }
    public void SetMode(MelodyListenMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (mode is MelodyListenMode.Vocal or MelodyListenMode.Together && _vocal == null) throw new FileNotFoundException(Tr("原人声不可用，请重新定位音频。"));
        if (mode is MelodyListenMode.Mix or MelodyListenMode.MixTogether && _mix == null) throw new FileNotFoundException(Tr("原曲不可用，请选择原曲音频。"));
        Volatile.Write(ref _mode, (int)mode);
    }
    public void SetVolumes(float piano, float vocal)
    {
        if (!float.IsFinite(piano) || !float.IsFinite(vocal)) throw new ArgumentOutOfRangeException(nameof(piano));
        Volatile.Write(ref _pianoVolume, Math.Clamp(piano, 0, 1));
        Volatile.Write(ref _vocalVolume, Math.Clamp(vocal, 0, 1));
    }
    public void UpdateNotes(IEnumerable<MelodyNote> notes) => _current.UpdateNotes(notes);
    public int Read(float[] buffer, int offset, int count)
    {
        if (_currentBuffer.Length < count) { _voiceBuffer = new float[count]; _mixBuffer = new float[count]; _currentBuffer = new float[count]; }
        var read = _current.Read(_currentBuffer, 0, count);
        ReadSource(_vocal, _voiceBuffer, read); ReadSource(_mix, _mixBuffer, read);
        var mode = (MelodyListenMode)Volatile.Read(ref _mode);
        var voiceTarget = mode is MelodyListenMode.Vocal or MelodyListenMode.Together ? Volatile.Read(ref _vocalVolume) : 0;
        var mixTarget = mode is MelodyListenMode.Mix or MelodyListenMode.MixTogether ? Volatile.Read(ref _vocalVolume) : 0;
        var pianoTarget = mode is MelodyListenMode.Piano or MelodyListenMode.Together or MelodyListenMode.MixTogether ? Volatile.Read(ref _pianoVolume) : 0;
        for (var i = 0; i < read; i++)
        {
            _voiceGain += Math.Clamp(voiceTarget - _voiceGain, -1d / 441, 1d / 441);
            _mixGain += Math.Clamp(mixTarget - _mixGain, -1d / 441, 1d / 441);
            _pianoGain += Math.Clamp(pianoTarget - _pianoGain, -1d / 441, 1d / 441);
            // Fixed headroom keeps the solo/mixed loudness consistent; the limiter catches hot source peaks.
            buffer[offset + i] = (float)Math.Clamp((_voiceBuffer[i] * _voiceGain + _mixBuffer[i] * _mixGain + _currentBuffer[i] * _pianoGain) * 0.65, -0.98, 0.98);
        }
        return read;
    }
    private static void ReadSource(ISampleProvider? source, float[] buffer, int count)
    {
        var read = 0;
        while (source != null && read < count)
        {
            var next = source.Read(buffer, read, count - read);
            if (next == 0) break;
            read += next;
        }
        Array.Clear(buffer, read, count - read);
    }
}

// A reusable, short preview voice: changing pitch does not restart an audio device.
public sealed class MelodyAudition : ISampleProvider, IDisposable
{
    private WaveOutEvent? _output;
    private sealed record Request(PianoVoice Voice);
    private Request? _request, _seen;
    private PianoVoice _voice, _previousVoice;
    private int _frame = 44100, _previousFrame = 44100, _fade = 441;
    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(44100, 1);
    public void Play(int pitch)
    {
        Volatile.Write(ref _request, new Request(new PianoVoice(pitch)));
        if (_output != null) return;
        _output = new WaveOutEvent { DesiredLatency = 40, NumberOfBuffers = 2 };
        try { _output.Init(new SampleToWaveProvider16(this)); _output.Play(); }
        catch { Dispose(); throw; }
    }
    public int Read(float[] buffer, int offset, int count)
    {
        var request = Volatile.Read(ref _request);
        if (request != null && !ReferenceEquals(request, _seen))
        {
            _seen = request; _previousVoice = _voice; _previousFrame = _frame;
            _voice = request.Voice; _frame = 0; _fade = 0;
        }
        for (var i = 0; i < count; i++)
        {
            var blend = Math.Min(1, _fade++ / 441d);
            var current = _frame < 26460 ? _voice.Sample(_frame / 44100d, 0.48) : 0;
            var previous = _previousFrame < 26460 ? _previousVoice.Sample(_previousFrame / 44100d, 0.48) : 0;
            buffer[offset + i] = (float)((current * blend + previous * (1 - blend)) * 0.65);
            _frame = Math.Min(44100, _frame + 1); _previousFrame = Math.Min(44100, _previousFrame + 1); _fade = Math.Min(441, _fade);
        }
        return count;
    }
    public void Dispose() { _output?.Stop(); _output?.Dispose(); _output = null; _frame = _previousFrame = 44100; _seen = null; _request = null; }
}

public sealed class MelodyPlayback : IDisposable
{
    private WaveOutEvent? _output;
    private AudioFileReader? _reader, _mixReader;
    private MelodyMonitor? _monitor;
    private double _start, _end;
    public bool IsPlaying => _output?.PlaybackState == PlaybackState.Playing;
    public double Position => Math.Clamp(_start + (_output?.GetPosition() ?? 0) / (44100d * 2), _start, _end);
    public void UpdateNotes(IEnumerable<MelodyNote> notes) => _monitor?.UpdateNotes(notes);
    public void SetMode(MelodyListenMode mode) => _monitor?.SetMode(mode);
    public void SetVolumes(float piano, float vocal) => _monitor?.SetVolumes(piano, vocal);
    public static string? GetMixPath(MelodyDocument document) => document.MixPath ?? (document.IsVocal ? null : document.SourcePath);
    public void Play(MelodyDocument document, MelodyListenMode mode, double start, double end, float pianoVolume = 0.8f, float vocalVolume = 0.8f)
    {
        Dispose(); _start = start; _end = end;
        try
        {
            var path = document.VocalPath ?? (document.IsVocal ? document.SourcePath : null);
            ISampleProvider? Open(string? audioPath, out AudioFileReader? reader)
            {
                reader = null;
                if (audioPath == null || !File.Exists(audioPath)) return null;
                reader = new AudioFileReader(audioPath); reader.CurrentTime = TimeSpan.FromSeconds(start);
                ISampleProvider audio = reader;
                if (audio.WaveFormat.Channels == 2) audio = new StereoToMonoSampleProvider(audio);
                if (audio.WaveFormat.Channels != 1) throw new NotSupportedException(Tr("试听支持单声道或双声道音频。"));
                if (audio.WaveFormat.SampleRate != 44100) audio = new WdlResamplingSampleProvider(audio, 44100);
                return audio;
            }
            var vocal = Open(path, out _reader);
            var mix = Open(GetMixPath(document), out _mixReader);
            _monitor = new(vocal, document.Notes, start, end, mode, mix);
            _monitor.SetVolumes(pianoVolume, vocalVolume);
            _output = new WaveOutEvent { DesiredLatency = 60, NumberOfBuffers = 2 };
            _output.Init(new SampleToWaveProvider16(_monitor)); _output.Play();
        }
        catch { Dispose(); throw; }
    }
    public void Dispose() { _output?.Stop(); _output?.Dispose(); _output = null; _reader?.Dispose(); _reader = null; _mixReader?.Dispose(); _mixReader = null; _monitor = null; }
}
