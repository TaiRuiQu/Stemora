using Microsoft.UI.Xaml;

namespace Stemora.App;

public partial class App : Application
{
    public static MainWindow? MainWindow { get; private set; }
    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Stemora");
            Directory.CreateDirectory(folder);
            File.AppendAllText(Path.Combine(folder, "crash.log"), $"{DateTimeOffset.Now:O}\n{e.Exception}\n");
        };
    }
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWindow = new MainWindow();
        MainWindow.Activate();
        var commandLine = Environment.GetCommandLineArgs();
        var captureIndex = Array.IndexOf(commandLine, "--capture-ui");
        if (captureIndex >= 0 && captureIndex + 1 < commandLine.Length)
        {
            var captureExitCode = 0;
            try
            {
                var inputIndex = Array.IndexOf(commandLine, "--capture-input");
                var modelIndex = Array.IndexOf(commandLine, "--capture-model");
                var resultIndex = Array.IndexOf(commandLine, "--capture-result");
                var melodyDraftIndex = Array.IndexOf(commandLine, "--capture-melody-draft");
                await MainWindow.CaptureUiAsync(commandLine[captureIndex + 1],
                    inputIndex >= 0 && inputIndex + 1 < commandLine.Length ? commandLine[inputIndex + 1] : null,
                    commandLine.Contains("--capture-run"),
                    modelIndex >= 0 && modelIndex + 1 < commandLine.Length ? commandLine[modelIndex + 1] : null,
                    resultIndex >= 0 && resultIndex + 1 < commandLine.Length ? commandLine[resultIndex + 1] : null,
                    melodyDraftIndex >= 0 && melodyDraftIndex + 1 < commandLine.Length ? commandLine[melodyDraftIndex + 1] : null, commandLine.Contains("--capture-melody"));
            }
            catch (Exception e)
            {
                captureExitCode = 1;
                Directory.CreateDirectory(commandLine[captureIndex + 1]);
                await File.WriteAllTextAsync(Path.Combine(commandLine[captureIndex + 1], "capture-error.txt"), e.ToString());
            }
            // Diagnostic invocations are finite commands, even if Media Foundation retains a native thread.
            finally { Environment.Exit(captureExitCode); }
        }
        var draftIndex = Array.IndexOf(commandLine, "--open-melody-draft");
        if (draftIndex >= 0 && draftIndex + 1 < commandLine.Length)
            await MainWindow.OpenMelodyDraftAsync(commandLine[draftIndex + 1]);
    }
}
