using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Stemora.Core;

namespace Stemora.App;

// Attached resource bindings also update controls in collapsed pages, templates and flyouts.
public static class Loc
{
    private sealed class Entry(string key) : INotifyPropertyChanged
    {
        public string Value => Strings.Tr(key);
        public event PropertyChangedEventHandler? PropertyChanged;
        public void Refresh() => PropertyChanged?.Invoke(this, new(nameof(Value)));
    }
    private static readonly Dictionary<string, Entry> Entries = [];
    static Loc() => Strings.LanguageChanged += () => { foreach (var entry in Entries.Values) entry.Refresh(); };
    private static DependencyProperty Register(string name) => DependencyProperty.RegisterAttached(name,
        typeof(string), typeof(Loc), new PropertyMetadata(null, (target, args) => Bind(target, name, (string)args.NewValue)));
    public static readonly DependencyProperty TextProperty = Register("Text");
    public static readonly DependencyProperty ContentProperty = Register("Content");
    public static readonly DependencyProperty HeaderProperty = Register("Header");
    public static readonly DependencyProperty PlaceholderProperty = Register("Placeholder");
    public static readonly DependencyProperty ToolTipProperty = Register("ToolTip");
    public static readonly DependencyProperty NameProperty = Register("Name");
    public static string GetText(DependencyObject target) => (string)target.GetValue(TextProperty);
    public static void SetText(DependencyObject target, string value) => target.SetValue(TextProperty, value);
    public static string GetContent(DependencyObject target) => (string)target.GetValue(ContentProperty);
    public static void SetContent(DependencyObject target, string value) => target.SetValue(ContentProperty, value);
    public static string GetHeader(DependencyObject target) => (string)target.GetValue(HeaderProperty);
    public static void SetHeader(DependencyObject target, string value) => target.SetValue(HeaderProperty, value);
    public static string GetPlaceholder(DependencyObject target) => (string)target.GetValue(PlaceholderProperty);
    public static void SetPlaceholder(DependencyObject target, string value) => target.SetValue(PlaceholderProperty, value);
    public static string GetToolTip(DependencyObject target) => (string)target.GetValue(ToolTipProperty);
    public static void SetToolTip(DependencyObject target, string value) => target.SetValue(ToolTipProperty, value);
    public static string GetName(DependencyObject target) => (string)target.GetValue(NameProperty);
    public static void SetName(DependencyObject target, string value) => target.SetValue(NameProperty, value);
    private static void Bind(DependencyObject target, string property, string key)
    {
        if (!Entries.TryGetValue(key, out var entry)) Entries[key] = entry = new(key);
        var destination = property switch
        {
            "Text" => target is TextBlock ? TextBlock.TextProperty : TextBox.TextProperty,
            "Content" => ContentControl.ContentProperty,
            "Header" => target switch
            {
                ComboBox => ComboBox.HeaderProperty,
                NumberBox => NumberBox.HeaderProperty,
                TextBox => TextBox.HeaderProperty,
                Expander => Expander.HeaderProperty,
                _ => throw new ArgumentException("Unsupported localized header.")
            },
            "Placeholder" => target switch
            {
                ComboBox => ComboBox.PlaceholderTextProperty,
                NumberBox => NumberBox.PlaceholderTextProperty,
                _ => TextBox.PlaceholderTextProperty
            },
            "ToolTip" => ToolTipService.ToolTipProperty,
            "Name" => AutomationProperties.NameProperty,
            _ => throw new ArgumentException("Unsupported localized property.")
        };
        BindingOperations.SetBinding(target, destination, new Binding
        {
            Source = entry,
            Path = new PropertyPath(nameof(Entry.Value)),
            Mode = BindingMode.OneWay
        });
    }
}
