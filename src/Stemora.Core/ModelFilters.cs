namespace Stemora.Core;

public sealed record ModelFilters(string Query = "", string? Quantization = null, string? Stem = null,
    int? StemCount = null, string? Platform = null, string? Architecture = null, string? ExportFormat = null)
{
    public bool Matches(ModelInfo model)
    {
        var stems = model.Stems ?? [];
        if (Quantization != null && model.Quantization != Quantization) return false;
        if (Stem != null && !stems.Contains(Stem, StringComparer.OrdinalIgnoreCase)) return false;
        if (StemCount == 1 && stems.Length != 1 || StemCount == -1 && stems.Length < 2) return false;
        if (Platform == "cpu" && model.Hardware?.CpuSupported != true) return false;
        if (Platform == "cuda" && model.Hardware?.CudaSupported != true) return false;
        if (Platform == "gpu-only" && model.Hardware?.CpuSupported != false) return false;
        if (Architecture != null && model.Architecture != Architecture) return false;
        if (ExportFormat != null && model.ExportFormats?.Contains(ExportFormat, StringComparer.OrdinalIgnoreCase) != true) return false;
        var search = $"{model.DisplayName} {model.Author} {model.Architecture} {string.Join(' ', stems)} "
            + string.Join(' ', stems.Select(id => Strings.StemLabel(id))) + " " + string.Join(' ', model.ExportFormats ?? []);
        return Query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .All(word => search.Contains(word, StringComparison.OrdinalIgnoreCase));
    }
}
