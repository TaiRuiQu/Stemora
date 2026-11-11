using static Stemora.Core.Strings;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Stemora.Core;

public sealed record MixerTrack(string Id, string Label, string Path, bool Muted = false);

public sealed class MixerChannel : ISampleProvider
{
    private readonly ISampleProvider _source;
    private float _gain = 1, _peak, _rms;
    public MixerTrack Track { get; }
    public bool Muted { get; internal set; }
    public bool Solo { get; internal set; }
    public float Volume { get; internal set; } = 1;
    public float Peak => Volatile.Read(ref _peak);
    public float Rms => Volatile.Read(ref _rms);
    public WaveFormat WaveFormat => _source.WaveFormat;
    public MixerChannel(MixerTrack track, ISampleProvider source) { Track = track; _source = source; Muted = track.Muted; }
    internal void ApplyGain(bool hasSolo) => Volatile.Write(ref _gain, Muted || hasSolo && !Solo ? 0 : Volume);
    public int Read(float[] buffer, int offset, int count)
    {
        var read = _source.Read(buffer, offset, count);
        var gain = Volatile.Read(ref _gain);
        float peak = 0; double square = 0;
        for (var i = offset; i < offset + read; i++)
        {
            var value = buffer[i] * gain;
            buffer[i] = value;
            peak = Math.Max(peak, Math.Abs(value)); square += value * value;
        }
        Volatile.Write(ref _peak, peak);
        Volatile.Write(ref _rms, read == 0 ? 0 : (float)Math.Sqrt(square / read));
        return read;
    }
}

// All channels are pulled by one mixer and one audio device clock. Muted and
// non-solo channels still consume samples, so changing M/S never changes alignment.
public sealed class StemMixerGraph : ISampleProvider
{
    private readonly MixingSampleProvider _mixer;
    public IReadOnlyList<MixerChannel> Channels { get; }
    public WaveFormat WaveFormat => _mixer.WaveFormat;
    public StemMixerGraph(IEnumerable<MixerChannel> channels)
    {
        Channels = channels.ToArray();
        _mixer = new MixingSampleProvider(Channels) { ReadFully = false };
        UpdateGains();
    }
    public void SetMute(int index, bool value) { Channels[index].Muted = value; UpdateGains(); }
    public void SetSolo(int index, bool value) { Channels[index].Solo = value; UpdateGains(); }
    public void SetVolume(int index, float value) { Channels[index].Volume = Math.Clamp(value, 0, 1); UpdateGains(); }
    private void UpdateGains()
    {
        var hasSolo = Channels.Any(c => c.Solo);
        foreach (var channel in Channels) channel.ApplyGain(hasSolo);
    }
    public int Read(float[] buffer, int offset, int count)
    {
        var read = _mixer.Read(buffer, offset, count);
        for (var i = offset; i < offset + read; i++) buffer[i] = Math.Clamp(buffer[i], -1, 1);
        return read;
    }
}

public sealed class AudioMixerSession : IDisposable
{
    private readonly List<AudioFileReader> _readers = [];
    private WaveOutEvent? _output;
    private StemMixerGraph? _graph;
    private double _positionBase;
    private bool _disposed;
    private volatile bool _playing;
    private readonly MixerTrack[] _tracks;
    private readonly (bool Muted, bool Solo, float Volume)[] _states;
    public TimeSpan Duration { get; private set; }
    public bool IsPlaying => _playing;
    public IReadOnlyList<MixerChannel> Channels => _graph?.Channels ?? [];
    public TimeSpan Position
    {
        get
        {
            if (_disposed) return TimeSpan.Zero;
            try { return TimeSpan.FromSeconds(Math.Clamp(_positionBase + (_output?.GetPosition() ?? 0) / (44100d * 2 * 2), 0, Duration.TotalSeconds)); }
            catch (Exception e) when (e is InvalidOperationException or NAudio.MmException) { return TimeSpan.FromSeconds(_positionBase); }
        }
    }
    public AudioMixerSession(IEnumerable<MixerTrack> tracks)
    {
        _tracks = tracks.ToArray();
        if (_tracks.Length == 0) throw new ArgumentException(Tr("需要至少一条音轨。"));
        _states = _tracks.Select(t => (t.Muted, false, 1f)).ToArray();
        try { Rebuild(0); } catch { Dispose(); throw; }
    }
    private void Rebuild(double seconds)
    {
        _playing = false;
        var old = _output; _output = null;
        if (old != null) { old.Stop(); old.Dispose(); }
        foreach (var reader in _readers) reader.Dispose();
        _readers.Clear();
        var channels = new List<MixerChannel>();
        foreach (var track in _tracks)
        {
            var reader = new AudioFileReader(track.Path);
            _readers.Add(reader);
            Duration = reader.TotalTime > Duration ? reader.TotalTime : Duration;
            reader.CurrentTime = TimeSpan.FromSeconds(Math.Min(seconds, reader.TotalTime.TotalSeconds));
            ISampleProvider source = reader;
            if (source.WaveFormat.Channels == 1) source = new MonoToStereoSampleProvider(source);
            if (source.WaveFormat.Channels != 2) throw new NotSupportedException(Tr("试听支持单声道或双声道音频。"));
            if (source.WaveFormat.SampleRate != 44100) source = new WdlResamplingSampleProvider(source, 44100);
            channels.Add(new(track, source));
        }
        _graph = new(channels);
        for (var i = 0; i < channels.Count; i++)
        {
            _graph.SetMute(i, _states[i].Muted); _graph.SetSolo(i, _states[i].Solo); _graph.SetVolume(i, _states[i].Volume);
        }
        _positionBase = seconds;
        var output = new WaveOutEvent { DesiredLatency = 100, NumberOfBuffers = 2 };
        try { output.Init(new SampleToWaveProvider16(_graph)); }
        catch { output.Dispose(); throw; }
        _output = output;
        output.PlaybackStopped += (_, _) => { if (ReferenceEquals(_output, output)) _playing = false; };
    }
    public void Play()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Position >= Duration - TimeSpan.FromMilliseconds(5)) Rebuild(0);
        _output!.Play(); _playing = true;
    }
    public void Pause() { if (_disposed) return; _output?.Pause(); _playing = false; }
    public void Seek(TimeSpan position, bool resume)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Rebuild(Math.Clamp(position.TotalSeconds, 0, Duration.TotalSeconds));
        if (resume) Play();
    }
    public void SetMute(int index, bool value) { _states[index].Muted = value; _graph?.SetMute(index, value); }
    public void SetSolo(int index, bool value) { _states[index].Solo = value; _graph?.SetSolo(index, value); }
    public void SetVolume(int index, float value) { _states[index].Volume = value; _graph?.SetVolume(index, value); }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _playing = false;
        var output = _output; _output = null;
        if (output != null) { output.Stop(); output.Dispose(); }
        foreach (var reader in _readers) reader.Dispose();
        _readers.Clear();
    }
}
