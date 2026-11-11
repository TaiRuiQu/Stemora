using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace Stemora.Core;

// Chinese source messages are the resource keys. Keep placeholders identical in translations.
public static class Strings
{
    private static readonly IReadOnlyDictionary<string, string> English = LoadEnglish();
    public static string Language { get; private set; } = ResolveLanguage(null);
    public static CultureInfo Culture => CultureInfo.GetCultureInfo(Language);
    public static event Action? LanguageChanged;
    public static string ResolveLanguage(string? language, CultureInfo? systemCulture = null) =>
        (string.IsNullOrWhiteSpace(language) ? (systemCulture ?? CultureInfo.CurrentUICulture).Name : language)
        .StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh-CN" : "en-US";
    public static void SetLanguage(string? language)
    {
        var resolved = ResolveLanguage(language);
        if (Language == resolved) return;
        Language = resolved;
        LanguageChanged?.Invoke();
    }
    public static string Tr(string message, params object?[] arguments)
    {
        var translated = Language == "en-US" ? English.GetValueOrDefault(message, message) : message;
        return arguments.Length == 0 ? translated : string.Format(Culture, translated, arguments);
    }
    public static string StemLabel(string id, string? fallback = null) => id switch
    {
        "bass" => Tr("贝斯"),
        "drums" => Tr("鼓"),
        "other" => Tr("其他"),
        "vocals" => Tr("人声"),
        "guitar" => Tr("吉他"),
        "piano" => Tr("钢琴"),
        "primary" => Tr("主音轨"),
        "residual" => Tr("残差伴奏"),
        "instrumental" => Tr("伴奏"),
        "original" => Tr("原始混音"),
        _ => fallback ?? id
    };
    private static IReadOnlyDictionary<string, string> LoadEnglish()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Stemora.Core.Data.en-US.json")!;
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
    }
}
