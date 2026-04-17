using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using DefectScout.App.ViewModels;
using DefectScout.App.Views;
using DefectScout.Core.Services;
using Serilog.Events;

namespace DefectScout.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // Ensure a working Node.js is on PATH before any services start.
        // NVM for Windows uses a junction at %ProgramFiles%\nodejs that may point to an
        // incomplete version; find the first NVM-managed version that actually has node.exe.
        EnsureNodeOnPath();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Compose services
            var configService = new ConfigService();
            var reportService = new ReportService();
            var stepExtractor = new StepExtractorService();
            var envTester     = new EnvironmentTesterService();

            // Bootstrap logging as early as possible using config defaults.
            // A full async load is deferred to AutoLoadAsync; use defaults for now
            // so any startup errors are captured.
            var defaultLogDir = Path.Combine(AppContext.BaseDirectory, "data", "logs");
            AppLogger.Initialize(defaultLogDir, LogEventLevel.Debug);

            var mainVm = new MainWindowViewModel(
                configService, stepExtractor, envTester, reportService);

            desktop.MainWindow = new MainWindow { DataContext = mainVm };

            // Re-initialize logging once the real config is loaded so the log
            // directory from defect-scout-config.json is honoured.
            mainVm.ConfigReady += cfg =>
                AppLogger.Initialize(cfg.LogDir, LogEventLevel.Debug);

            desktop.ShutdownRequested += (_, _) => AppLogger.CloseAndFlush();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Checks whether <c>node.exe</c> is reachable on the current PATH.  If not (common with
    /// NVM for Windows when the active version's folder is missing binaries), searches the NVM
    /// versions directory for the highest-numbered version that has <c>node.exe</c> and prepends
    /// that directory—plus its sibling <c>npm</c> global prefix—to the process PATH so that npm
    /// and playwright-cli commands work inside the Copilot SDK sessions.
    /// </summary>
    private static void EnsureNodeOnPath()
    {
        // Fast path: node is already reachable.
        if (IsOnPath("node.exe")) return;

        var nvmHome = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "nvm");
        if (!Directory.Exists(nvmHome)) return;

        // Pick the highest version directory that actually contains node.exe.
        var nodeDir = Directory.EnumerateDirectories(nvmHome, "v*")
            .Where(d => File.Exists(Path.Combine(d, "node.exe")))
            .OrderByDescending(d => d)   // lexicographic desc is fine for vX.Y.Z
            .FirstOrDefault();

        if (nodeDir is null) return;

        // Also add the npm global bin (sibling of AppData\Roaming\npm).
        var npmGlobalBin = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm");

        var current = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var extra   = string.Join(Path.PathSeparator.ToString(),
            nodeDir,
            npmGlobalBin);

        Environment.SetEnvironmentVariable("PATH", extra + Path.PathSeparator + current);
    }

    private static bool IsOnPath(string exe)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        return path.Split(Path.PathSeparator)
                   .Any(dir => File.Exists(Path.Combine(dir, exe)));
    }
}
