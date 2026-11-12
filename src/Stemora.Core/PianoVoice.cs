using System.Buffers.Binary;
using System.IO.Compression;

namespace Stemora.Core;

// Salamander Grand Piano recordings by Alexander Holm (CC BY 3.0).
// Anchors are three semitones apart; decoding is cached and completed before playback.
public readonly struct PianoVoice
{
    public const double ReleaseSeconds = 0.12;
    private static readonly Lazy<float[]>[] Bank = Enumerable.Range(0, 30)
        .Select(index => new Lazy<float[]>(() => Load(21 + index * 3))).ToArray();
    private readonly float[] _samples;
    private readonly double _rate;
    public PianoVoice(int pitch)
    {
        pitch = Math.Clamp(pitch, 0, 127);
        var index = Math.Clamp((int)Math.Round((pitch - 21) / 3d), 0, Bank.Length - 1);
        _samples = Bank[index].Value;
        _rate = 44100 * Math.Pow(2, (pitch - (21 + index * 3)) / 12d);
    }
    public float Sample(double seconds, double heldSeconds)
    {
        if (seconds < 0 || seconds >= heldSeconds + ReleaseSeconds) return 0;
        var position = seconds * _rate;
        if (position >= _samples.Length - 1) return 0;
        var index = (int)position;
        var value = _samples[index] + (_samples[index + 1] - _samples[index]) * (position - index);
        var attack = Math.Min(1, seconds / 0.002);
        var release = Math.Clamp(1 - (seconds - heldSeconds) / ReleaseSeconds, 0, 1);
        return (float)(value * attack * release * release);
    }
    public static void Prepare(IEnumerable<MelodyNote> notes)
    {
        foreach (var pitch in notes.Select(n => n.Pitch).Distinct()) _ = new PianoVoice(pitch);
    }
    private static float[] Load(int pitch)
    {
        using var resource = typeof(PianoVoice).Assembly.GetManifestResourceStream($"Stemora.Core.Data.Piano.{pitch}.pcm.gz")
            ?? throw new InvalidDataException($"Missing bundled piano sample: {pitch}");
        using var gzip = new GZipStream(resource, CompressionMode.Decompress);
        using var decoded = new MemoryStream(); gzip.CopyTo(decoded);
        var bytes = decoded.ToArray();
        var samples = new float[bytes.Length / 2];
        var peak = 0f;
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(i * 2, 2)) / 32768f;
            peak = Math.Max(peak, Math.Abs(samples[i]));
        }
        var gain = peak > 0 ? 0.55f / peak : 1;
        for (var i = 0; i < samples.Length; i++) samples[i] *= gain;
        return samples;
    }
}
