using System.ComponentModel;
using System.Runtime.CompilerServices;
using Stemora.Core;

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
    public string Status { get => _status; set { _status = value; Changed(); } }
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
    public string State => Installed ? "已下载" : "";
    public string ButtonLabel => Installed ? "使用" : "下载";
}
