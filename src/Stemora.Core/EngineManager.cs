using System.IO;

namespace Stemora.Core;

public sealed class EngineManager(AppPaths paths)
{
    public const string Version = "0.2.0";
    public const string InfraVersion = "0.2.0";
    public static bool IsInfrastructure(string path) => Path.GetFileName(path).Equals("stemora-infra.exe", StringComparison.OrdinalIgnoreCase);
    public string? FindInfrastructure()
    {
        var parent = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))?.FullName;
        foreach (var root in new[] { Path.Combine(AppContext.BaseDirectory, "native"), parent == null ? "" : Path.Combine(parent, "native"),
                     Path.Combine(paths.Runtime, "stemora-infra"), Path.Combine(paths.Runtime, "native") })
        {
            if (string.IsNullOrEmpty(root)) continue;
            var executable = Path.Combine(root, "stemora-infra.exe");
            if (File.Exists(executable)) return executable;
        }
        return null;
    }
    public string? Find(string? configuredPath = null)
    {
        // Migrate old saved engine selections to the bundled Infra. Legacy engines
        // are no longer discovered or downloaded; only CUDA/CPU Infra ships.
        if (!string.IsNullOrWhiteSpace(configuredPath) && !Path.GetFileName(configuredPath).Equals("bs_roformer-cli.exe", StringComparison.OrdinalIgnoreCase))
            return File.Exists(configuredPath) ? Path.GetFullPath(configuredPath) : null;
        var environmentPath = Environment.GetEnvironmentVariable("STEMORA_ENGINE");
        if (!string.IsNullOrWhiteSpace(environmentPath) && IsInfrastructure(environmentPath) && File.Exists(environmentPath)) return Path.GetFullPath(environmentPath);
        return FindInfrastructure();
    }
    public Task<string> InstallAsync(string flavor = "cuda", IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        if (flavor is not ("cuda" or "cpu")) throw new ArgumentException("引擎支持 cuda 或 cpu。");
        ct.ThrowIfCancellationRequested();
        var engine = FindInfrastructure() ?? throw new FileNotFoundException("未找到 Stemora Infra。请使用完整发布包，或先运行 scripts/Build-Native.ps1 构建后端。");
        progress?.Report(new(1, 1, "Stemora Infra 已就绪（CUDA / CPU）"));
        return Task.FromResult(engine);
    }
}
