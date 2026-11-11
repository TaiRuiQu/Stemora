using static Stemora.Core.Strings;
using System.Reflection;
using System.Text.Json;
using System.Collections.Concurrent;

namespace Stemora.Core;

public sealed class ModelRepository(AppPaths paths)
{
    public const string Repository = "chenmozhijin/BSRoformer-GGUF";
    public static IReadOnlyList<ModelInfo> Catalog { get; } = LoadCatalog();
    private static readonly ConcurrentDictionary<string, ModelDescriptor> Descriptors = new(StringComparer.OrdinalIgnoreCase);
    private static IReadOnlyList<ModelInfo> LoadCatalog()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Stemora.Core.Data.models.json")!;
        return JsonSerializer.Deserialize<List<ModelInfo>>(stream, JsonDefaults.Options)!;
    }
    public string GetPath(ModelInfo model) => Path.Combine(paths.Models, Path.GetFileName(model.RepositoryPath));
    public bool IsInstalled(ModelInfo model) => File.Exists(GetPath(model)) && new FileInfo(GetPath(model)).Length == model.Size;
    public async Task<string> DownloadAsync(ModelInfo model, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        paths.EnsureCreated();
        var escapedPath = string.Join('/', model.RepositoryPath.Split('/').Select(Uri.EscapeDataString));
        var uri = new Uri($"https://huggingface.co/{model.Repository}/resolve/{model.Revision}/{escapedPath}?download=true");
        var destination = GetPath(model);
        await Downloads.FetchAsync(uri, destination, model.Sha256, model.Size, progress, ct);
        return destination;
    }
    public static async Task<ModelDescriptor> InspectAsync(string path, CancellationToken ct = default)
    {
        path = Path.GetFullPath(path);
        var file = new FileInfo(path);
        var sidecar = path + ".stemora.json";
        var key = $"{path}|{file.Length}|{file.LastWriteTimeUtc.Ticks}|{(File.Exists(sidecar) ? File.GetLastWriteTimeUtc(sidecar).Ticks : 0)}";
        if (Descriptors.TryGetValue(key, out var cached)) return cached;
        var model = await Task.Run(() => GgufInspector.Read(path), ct);
        foreach (var known in Catalog.Where(m => m.Size == file.Length && (m.Profile != null || m.Stems != null)))
        {
            if (!await Downloads.IsValidAsync(path, known.Sha256, known.Size, ct)) continue;
            var stems = known.Stems ?? model.Stems.Select(s => s.Id).ToArray();
            GgufInspector.ValidateStems(stems, model.Stems.Count);
            model = model with { Family = known.Profile ?? model.Family, Name = known.Name, Stems = stems.Select(id => new StemDefinition(id, GgufInspector.StemLabel(id))).ToArray() };
            break;
        }
        Descriptors[key] = model;
        return model;
    }
    public static void ValidateGguf(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        if (reader.BaseStream.Length < 24 || reader.ReadUInt32() != 0x46554747)
            throw new InvalidDataException(Tr("请选择有效的 GGUF 文件（Git LFS 指针不是模型文件）。"));
        var version = reader.ReadUInt32();
        if (version is < 2 or > 3) throw new InvalidDataException(Tr("不支持 GGUF v{0}，需要 v2 或 v3。", version));
    }
}
