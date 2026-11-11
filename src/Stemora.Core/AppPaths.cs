using static Stemora.Core.Strings;
using System.Text.Json;

namespace Stemora.Core;

public sealed class AppPaths
{
    public string Root { get; }
    public string Models => Path.Combine(Root, "models");
    public string Runtime => Path.Combine(Root, "runtime");
    public string Jobs => Path.Combine(Root, "jobs");
    public string SettingsFile => Path.Combine(Root, "settings.json");
    public AppPaths(string? root = null)
    {
        Root = Path.GetFullPath(root ?? Environment.GetEnvironmentVariable("STEMORA_HOME") ?? PortableRoot()
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Stemora"));
    }
    private static string? PortableRoot()
    {
        var directory = AppContext.BaseDirectory;
        // The CLI lives in a child folder in portable distributions.
        foreach (var candidate in new[] { directory, Path.GetDirectoryName(directory.TrimEnd(Path.DirectorySeparatorChar)) })
            if (candidate != null && File.Exists(Path.Combine(candidate, "portable.flag"))) return Path.Combine(candidate, "data");
        return null;
    }
    public void EnsureCreated()
    {
        foreach (var path in new[] { Root, Models, Runtime, Jobs }) Directory.CreateDirectory(path);
    }
    public AppSettings LoadSettings()
    {
        if (!File.Exists(SettingsFile)) return new();
        // Invalid settings are reported, never silently overwritten.
        var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsFile), JsonDefaults.Options)
            ?? throw new InvalidDataException(Tr("设置文件无效：{0}", SettingsFile));
        return settings with
        {
            ModelPath = ResolvePath(settings.ModelPath),
            EnginePath = ResolvePath(settings.EnginePath),
            OutputDirectory = ResolvePath(settings.OutputDirectory)!
        };
    }
    public async Task SaveSettingsAsync(AppSettings settings)
    {
        EnsureCreated();
        var temp = SettingsFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var stored = settings with
            {
                ModelPath = StorePath(settings.ModelPath),
                EnginePath = StorePath(settings.EnginePath),
                OutputDirectory = StorePath(settings.OutputDirectory)!
            };
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(stored, JsonDefaults.Options));
            File.Move(temp, SettingsFile, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private string? ResolvePath(string? value) => string.IsNullOrWhiteSpace(value) ? null
        : Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(Root, value));
    private string? StorePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var absolute = Path.GetFullPath(value);
        var relative = Path.GetRelativePath(Root, absolute);
        return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? relative : absolute;
    }
}
