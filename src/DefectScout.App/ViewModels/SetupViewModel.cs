using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefectScout.Core.Models;
using DefectScout.Core.Services;
using Serilog;

namespace DefectScout.App.ViewModels;

/// <summary>
/// Screen 2 — edit environments and global settings; save always goes to the app-local config.
/// </summary>
public sealed partial class SetupViewModel : ViewModelBase
{
    private static readonly ILogger _log = AppLogger.For<SetupViewModel>();
    private readonly IConfigService _configService;

    public override string PageTitle => "Configuration";

    public event Action? Back;
    public event Action<DefectScoutConfig>? Saved;

    private string _logDir = string.Empty;

    [ObservableProperty]
    private string _screenshotBaseDir = string.Empty;

    [ObservableProperty]
    private string _reportDir = string.Empty;

    [ObservableProperty]
    private bool _playwrightHeadless = true;

    [ObservableProperty]
    private bool _screenshotOnStep = true;

    [ObservableProperty]
    private bool _screenshotOnFailure = true;

    [ObservableProperty]
    private bool _ignoreHttpsErrors = true;

    [ObservableProperty]
    private int _timeout = PlaywrightOptions.DefaultTimeoutMilliseconds;

    [ObservableProperty]
    private string _agentRuntimeMode = AgentRuntimeOptions.CopilotSdkMode;

    [ObservableProperty]
    private string _ollamaEndpoint = "http://localhost:11434";

    [ObservableProperty]
    private string _stepExtractorModel = "qwen3.5:4b";

    [ObservableProperty]
    private string _envTesterModel = "qwen3.5:4b";

    [ObservableProperty]
    private int _maxConcurrentEnvTesters = 3;

    [ObservableProperty]
    private int _maxToolIterations = 80;

    [ObservableProperty]
    private int _ollamaContextTokens = AgentRuntimeOptions.DefaultOllamaContextTokens;

    [ObservableProperty]
    private int _ollamaMaxOutputTokens = AgentRuntimeOptions.DefaultOllamaMaxOutputTokens;

    [ObservableProperty]
    private string _ollamaThink = AgentRuntimeOptions.DefaultOllamaThink;

    [ObservableProperty]
    private bool _isSaving;

    [ObservableProperty]
    private string? _saveResultMessage;

    [ObservableProperty]
    private bool _isSaveError;

    /// <summary>Green on success, red on error — bound to the save result TextBlock foreground.</summary>
    public string SaveResultColor => IsSaveError ? "#D13438" : "#107C10";

    [ObservableProperty]
    private KineticEnvironment? _selectedEnvironment;

    public ObservableCollection<KineticEnvironment> Environments { get; } = [];

    public IReadOnlyList<string> AgentRuntimeModes { get; } =
    [
        AgentRuntimeOptions.CopilotSdkMode,
        AgentRuntimeOptions.LocalOllamaMode,
    ];

    public bool IsLocalOllamaRuntime =>
        string.Equals(AgentRuntimeMode, AgentRuntimeOptions.LocalOllamaMode, StringComparison.OrdinalIgnoreCase);

    public SetupViewModel(IConfigService configService, DefectScoutConfig? existing)
    {
        _configService = configService;

        if (existing is not null)
            ApplyFromConfig(existing);
        else
            ApplyFromConfig(configService.CreateDefault());
    }

    private void ApplyFromConfig(DefectScoutConfig cfg)
    {
        ScreenshotBaseDir = cfg.ScreenshotBaseDir;
        ReportDir = cfg.ReportDir;
        _logDir = string.IsNullOrEmpty(cfg.LogDir)
            ? _configService.CreateDefault().LogDir
            : cfg.LogDir;
        PlaywrightHeadless = cfg.Playwright.Headless;
        ScreenshotOnStep = cfg.Playwright.ScreenshotOnStep;
        ScreenshotOnFailure = cfg.Playwright.ScreenshotOnFailure;
        IgnoreHttpsErrors = cfg.Playwright.IgnoreHttpsErrors;
        Timeout = PlaywrightOptions.NormalizeTimeout(cfg.Playwright.Timeout);

        AgentRuntimeMode = cfg.AgentRuntime.Mode;
        OllamaEndpoint = cfg.AgentRuntime.OllamaEndpoint;
        StepExtractorModel = cfg.AgentRuntime.StepExtractorModel;
        EnvTesterModel = cfg.AgentRuntime.EnvTesterModel;
        MaxConcurrentEnvTesters = cfg.AgentRuntime.MaxConcurrentEnvTesters;
        MaxToolIterations = cfg.AgentRuntime.MaxToolIterations;
        OllamaContextTokens = AgentRuntimeOptions.NormalizeOllamaContextTokens(cfg.AgentRuntime.OllamaContextTokens);
        OllamaMaxOutputTokens = AgentRuntimeOptions.NormalizeOllamaMaxOutputTokens(cfg.AgentRuntime.OllamaMaxOutputTokens);
        OllamaThink = AgentRuntimeOptions.NormalizeOllamaThink(cfg.AgentRuntime.OllamaThink);

        Environments.Clear();
        foreach (var e in cfg.Environments)
            Environments.Add(e);
    }

    partial void OnIsSaveErrorChanged(bool value) => OnPropertyChanged(nameof(SaveResultColor));
    partial void OnAgentRuntimeModeChanged(string value) => OnPropertyChanged(nameof(IsLocalOllamaRuntime));

    [RelayCommand]
    private void AddEnvironment()
    {
        var env = new KineticEnvironment
        {
            Name = "New Environment",
            Version = "2026.1",
            WebUrl = "https://localhost/ERPCurrent/",
            Username = "manager",
            Company = "EPIC06",
        };
        Environments.Add(env);
        SelectedEnvironment = env;
        _log.Debug("Environment added: {Name}", env.Name);
    }

    [RelayCommand]
    private void RemoveEnvironment(KineticEnvironment env)
    {
        Environments.Remove(env);
        if (SelectedEnvironment == env) SelectedEnvironment = Environments.FirstOrDefault();
        _log.Debug("Environment removed: {Name}", env.Name);
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (Environments.Count == 0)
        {
            IsSaveError = true;
            SaveResultMessage = "Add at least one environment.";
            _log.Warning("Save attempted with no environments configured");
            return;
        }

        IsSaving = true;
        IsSaveError = false;
        SaveResultMessage = null;
        _log.Information("Saving config: {EnvCount} environments, screenshotDir={Dir}", Environments.Count, ScreenshotBaseDir);
        try
        {
            var config = BuildConfig();
            await _configService.SaveAsync(config);
            SaveResultMessage = $"Saved to {_configService.AppConfigPath}";
            _log.Information("Config saved to {Path}", _configService.AppConfigPath);
            Saved?.Invoke(config);
        }
        catch (Exception ex)
        {
            IsSaveError = true;
            SaveResultMessage = $"Save failed: {ex.Message}";
            _log.Error(ex, "Config save failed");
        }
        finally { IsSaving = false; }
    }

    [RelayCommand]
    private void GoBack()
    {
        _log.Debug("SetupViewModel: GoBack");
        Back?.Invoke();
    }

    private DefectScoutConfig BuildConfig() => new()
    {
        Environments = [.. Environments],
        ScreenshotBaseDir = ScreenshotBaseDir,
        ReportDir = ReportDir,
        LogDir = string.IsNullOrEmpty(_logDir) ? _configService.CreateDefault().LogDir : _logDir,
        Playwright = new PlaywrightOptions
        {
            Headless = PlaywrightHeadless,
            ScreenshotOnStep = ScreenshotOnStep,
            ScreenshotOnFailure = ScreenshotOnFailure,
            IgnoreHttpsErrors = IgnoreHttpsErrors,
            Timeout = PlaywrightOptions.NormalizeTimeout(Timeout),
        },
        AgentRuntime = new AgentRuntimeOptions
        {
            Mode = AgentRuntimeMode,
            OllamaEndpoint = OllamaEndpoint,
            StepExtractorModel = StepExtractorModel,
            EnvTesterModel = EnvTesterModel,
            MaxConcurrentEnvTesters = Math.Max(1, MaxConcurrentEnvTesters),
            MaxToolIterations = Math.Max(1, MaxToolIterations),
            OllamaContextTokens = AgentRuntimeOptions.NormalizeOllamaContextTokens(OllamaContextTokens),
            OllamaMaxOutputTokens = AgentRuntimeOptions.NormalizeOllamaMaxOutputTokens(OllamaMaxOutputTokens),
            OllamaThink = AgentRuntimeOptions.NormalizeOllamaThink(OllamaThink),
        },
    };
}
