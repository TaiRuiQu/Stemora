using System.ComponentModel;
using System.Runtime.CompilerServices;
using Stemora.Core;
using static Stemora.Core.Strings;

namespace Stemora.App;

public abstract class Bindable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
public sealed class AudioJob(string path) : Bindable
{
    public string Path { get; } = path;
    public string Name => System.IO.Path.GetFileName(Path);
    private string _status = "等待分离";
    private object?[] _statusArguments = [];
    public string Status => Tr(_status, _statusArguments);
    public void SetStatus(string message, params object?[] arguments)
    { _status = message; _statusArguments = arguments; RefreshLanguage(); }
    public void RefreshLanguage() => Changed(nameof(Status));
    private bool _isRunning;
    public bool IsRunning { get => _isRunning; set { _isRunning = value; Changed(); } }
    public SeparationResult? Result { get; set; }
}
public sealed record ModelChoice(string Label, string Path);
public sealed record PreviewChoice(string Label, string Path);
public sealed class ModelRow(ModelInfo model, bool installed) : Bindable
{
    public ModelInfo Model { get; } = model;
    public string Details => $"{Model.Author}   /   {Model.Architecture}   /   {Model.SizeLabel}";
    private bool _installed = installed;
    public bool Installed { get => _installed; set { _installed = value; Changed(nameof(State)); Changed(nameof(ButtonLabel)); } }
    public string State => Installed ? Tr("已下载") : "";
    public string ButtonLabel => Installed ? Tr("使用") : Tr("下载");
    public string TrackType => Model.Stems is { Length: > 1 } stems ? Tr("多轨 ({0} 轨)", stems.Length) : Tr("单轨");
    public string OutputSummary => Tr("输出：{0}", string.Join(" / ", (Model.Stems ?? []).Select(id => Strings.StemLabel(id))));
    public string ExportSummary => string.Join(" / ", Model.ExportFormats ?? []);
    public string PlatformSummary => Model.Hardware is { } hardware
        ? string.Join(" / ", new[] { hardware.CpuSupported ? Tr("CPU 可用") : null,
            hardware.CudaSupported ? Tr("CUDA 推荐") : null }.OfType<string>())
        : "";
    public string MemorySummary => Model.Hardware is { RecommendedRamGb: { } ram, RecommendedVramGb: { } vram }
        ? Tr("建议内存 {0} GB / 显存 {1} GB", ram, vram) : "";
    public void RefreshLanguage()
    {
        foreach (var property in new[] { nameof(State), nameof(ButtonLabel), nameof(TrackType), nameof(OutputSummary),
            nameof(ExportSummary), nameof(PlatformSummary), nameof(MemorySummary) }) Changed(property);
    }
}
