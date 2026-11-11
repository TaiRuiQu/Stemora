using System.Text.Json;
using System.Text.Json.Serialization;

namespace Stemora.Core;

public enum ComputeDevice { Auto, Cpu, Cuda }
public enum WavEncoding { Float32, Pcm24, Pcm16 }
public sealed record SeparationOptions(string InputPath, string ModelPath, string OutputDirectory,
    ComputeDevice Device = ComputeDevice.Auto, int? ChunkSize = null, int? Overlap = null,
    WavEncoding Encoding = WavEncoding.Float32, bool ExportResidual = true, string? EnginePath = null,
    string? Profile = null, IReadOnlyList<string>? StemNames = null);
public sealed record SeparationProgress(string Stage, double? Fraction, string Message);
public sealed record SeparationResult(string InputPath, string ModelPath, string OutputDirectory,
    string PrimaryPath, string? ResidualPath, IReadOnlyList<string> StemPaths,
    double DurationSeconds, double ElapsedSeconds, DateTimeOffset CompletedAt,
    IReadOnlyList<StemResult>? Tracks = null, string? Family = null, int SampleRate = 44100, string? Backend = null);
public sealed record StemResult(string Id, string Label, string Path);
public sealed record StemDefinition(string Id, string Label);
public sealed record ModelDescriptor(string Architecture, string Family, string Name, int SampleRate,
    IReadOnlyList<StemDefinition> Stems, int DefaultChunkSize = 352800, int DefaultOverlap = 2)
{
    public bool IsMultiStem => Stems.Count > 1;
}
public sealed record DownloadProgress(long Received, long? Total, string Message)
{
    public double? Fraction => Total is > 0 ? Math.Clamp((double)Received / Total.Value, 0, 1) : null;
}
public sealed record ModelInfo(string Id, string Name, string Author, string Architecture,
    string Quantization, string RepositoryPath, long Size, string Sha256, string Revision,
    string Repository = ModelRepository.Repository, string? Profile = null, string[]? Stems = null,
    ModelHardware? Hardware = null, string[]? ExportFormats = null)
{
    public string DisplayName => $"{Name} ({Quantization})";
    public string SizeLabel => $"{Size / 1048576d:F1} MB";
}
public sealed record ModelHardware(bool CpuSupported, bool CudaSupported, string RecommendedDevice,
    double? RecommendedVramGb = null, string? Source = null, double? RecommendedRamGb = null,
    ModelMemoryProfile? Memory = null);
public sealed record ModelMemoryProfile(string Basis, string ReferenceModelId,
    double CpuRamMiB, double CudaRamMiB, double VramMiB,
    int ChunkFrames, int Overlap, double AudioSeconds, string Benchmark);
public sealed record AppSettings
{
    public string? ModelPath { get; init; }
    public string? EnginePath { get; init; }
    public string OutputDirectory { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Stemora");
    public ComputeDevice Device { get; init; }
    public WavEncoding Encoding { get; init; }
    public int? ChunkSize { get; init; }
    public int? Overlap { get; init; }
    public bool ExportResidual { get; init; } = true;
    public string Theme { get; init; } = "Default";
    // null follows the Windows display language; explicit choices survive portable moves.
    public string? Language { get; init; }
}
public static class JsonDefaults
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
}
