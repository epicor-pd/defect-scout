using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefectScout.Core.Models;
using DefectScout.Core.Services;
using Serilog;

namespace DefectScout.App.ViewModels;

/// <summary>
/// Root view-model. Owns the current page and wires all navigation transitions.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private static readonly ILogger _log = AppLogger.For<MainWindowViewModel>();
    private readonly IConfigService _configService;
    private readonly IStepExtractorService _stepExtractor;
    private readonly IEnvironmentTesterService _envTester;
    private readonly IReportService _reportService;

    // ── Current page ─────────────────────────────────────────────────────────

    [ObservableProperty]
    private ViewModelBase _currentPage = null!;

    [ObservableProperty]
    private string _currentPageTitle = string.Empty;

    // ── Session state ────────────────────────────────────────────────────────

    private DefectScoutConfig? _config;
    private StructuredTestPlan? _currentPlan;
    private List<TestResult>? _lastResults;
    private string? _lastReportPath;

    /// <summary>Raised when a usable config is first loaded so the App can re-initialise logging.</summary>
    public event Action<DefectScoutConfig>? ConfigReady;

    public MainWindowViewModel(
        IConfigService configService,
        IStepExtractorService stepExtractor,
        IEnvironmentTesterService envTester,
        IReportService reportService)
    {
        _configService = configService;
        _stepExtractor = stepExtractor;
        _envTester = envTester;
        _reportService = reportService;

        // Try to auto-load the app-local config on startup.
        // If it has environments configured, jump straight to ticket input.
        _ = AutoLoadAsync();
    }

    private async Task AutoLoadAsync()
    {
        _log.Debug("AutoLoadAsync: loading app-local config");
        var config = await _configService.LoadAsync();
        _config = config.Environments.Count > 0 ? config : null;
        _log.Information("AutoLoadAsync: config loaded, environments={Count}, hasConfig={Has}",
            config.Environments.Count, _config is not null);
        if (_config is not null) ConfigReady?.Invoke(_config);
        NavigateToWelcome();
    }

    // ── Navigation methods (called by child ViewModels) ───────────────────────

    public void NavigateToWelcome()
    {
        _log.Debug("Navigating to Welcome");
        var vm = new WelcomeViewModel(_configService, _config);
        vm.OpenWithConfig += config => { _config = config; NavigateToSetup(); };
        vm.StartTesting   += NavigateToTicketInput;
        vm.NavigateToSetup += NavigateToSetup;
        SetPage(vm);
    }

    public void NavigateToSetup()
    {
        _log.Debug("Navigating to Setup");
        var vm = new SetupViewModel(_configService, _config);
        vm.Saved += OnConfigSaved;
        vm.Back += NavigateToWelcome;
        SetPage(vm);
    }

    public void NavigateToTicketInput()
    {
        if (_config is null) { _log.Warning("NavigateToTicketInput: no config, redirecting to Setup"); NavigateToSetup(); return; }
        _log.Debug("Navigating to TicketInput: {EnvCount} enabled environments",
            _config.Environments.Count(e => e.Enabled));
        var vm = new TicketInputViewModel(_stepExtractor, _config);
        vm.Back += NavigateToSetup;
        vm.StepsExtracted += OnStepsExtracted;
        SetPage(vm);
    }

    public void NavigateToStepReview()
    {
        if (_currentPlan is null) { _log.Warning("NavigateToStepReview: no plan, redirecting to TicketInput"); NavigateToTicketInput(); return; }
        _log.Debug("Navigating to StepReview: ticket={Ticket}, steps={Steps}",
            _currentPlan.Ticket, _currentPlan.Steps.Count);
        var vm = new StepReviewViewModel(_currentPlan);
        vm.Back += NavigateToTicketInput;
        vm.Confirmed += plan => { _currentPlan = plan; NavigateToRunning(); };
        SetPage(vm);
    }

    public void NavigateToRunning()
    {
        if (_config is null || _currentPlan is null)
        {
            _log.Warning("NavigateToRunning: missing config or plan, aborting");
            return;
        }
        _log.Information("Navigating to Running: ticket={Ticket}", _currentPlan.Ticket);
        var vm = new RunningViewModel(_envTester, _reportService, _config, _currentPlan);
        vm.Finished += OnRunFinished;
        SetPage(vm);
        _ = vm.StartAsync(); // fire and forget — progress drives the UI
    }

    public void NavigateToReport()
    {
        if (_lastResults is null) { _log.Warning("NavigateToReport: no results available"); return; }
        _log.Debug("Navigating to Report: {Count} results, reportPath={Path}",
            _lastResults.Count, _lastReportPath ?? "(none)");
        var vm = new ReportViewModel(_lastResults, _lastReportPath ?? string.Empty);
        vm.RunAgain += NavigateToTicketInput;
        vm.StartOver += NavigateToWelcome;
        SetPage(vm);
    }

    // ── Event handlers ────────────────────────────────────────────────────────

    private void OnConfigSaved(DefectScoutConfig config)
    {
        _log.Information("Config saved: {EnvCount} environments", config.Environments.Count);
        _config = config;
        ConfigReady?.Invoke(config);
        NavigateToTicketInput();
    }

    private void OnStepsExtracted(StructuredTestPlan plan)
    {
        _log.Information("Steps extracted: ticket={Ticket}, steps={Count}", plan.Ticket, plan.Steps.Count);
        _currentPlan = plan;
        NavigateToStepReview();
    }

    private void OnRunFinished(List<TestResult> results, string reportPath)
    {
        _log.Information("Run finished: {Count} results, reportPath={Path}", results.Count, reportPath);
        _lastResults = results;
        _lastReportPath = reportPath;
        NavigateToReport();
    }

    // ── Helper ────────────────────────────────────────────────────────────────

    private void SetPage(ViewModelBase vm)
    {
        CurrentPage = vm;
        CurrentPageTitle = vm.PageTitle;
    }
}
