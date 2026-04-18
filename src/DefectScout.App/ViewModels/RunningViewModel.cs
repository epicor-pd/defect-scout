using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DefectScout.Core.Models;
using DefectScout.Core.Services;
using Serilog;

namespace DefectScout.App.ViewModels;

/// <summary>
/// Screen 5 — dispatches Copilot CLI fleet (parallel) or sequential fallback per environment,
/// and shows live tracking cards for each environment during the run.
/// </summary>
public sealed partial class RunningViewModel : ViewModelBase
{
    private static readonly ILogger _log = AppLogger.For<RunningViewModel>();
    private readonly IEnvironmentTesterService _tester;
    private readonly IReportService _reportService;
    private readonly DefectScoutConfig _config;
    private readonly StructuredTestPlan _plan;

    public override string PageTitle => "Running Tests";

    public event Action<List<TestResult>, string>? Finished;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private bool _isComplete;

    [ObservableProperty]
    private string _overallStatus = "Preparing...";

    /// <summary>"fleet" or "sequential" — set once dispatch mode is determined.</summary>
    [ObservableProperty]
    private string _dispatchMode = string.Empty;

    /// <summary>Displayed in the header badge. Empty until dispatch mode is known.</summary>
    public string DispatchBadge => DispatchMode switch
    {
        "fleet" or "parallel" => "⚡ Fleet (parallel)",
        "sequential"          => "▶ Sequential",
        _                     => "Probing...",
    };

    public ObservableCollection<ProgressCardViewModel> EnvProgresses { get; } = [];

    public RunningViewModel(
        IEnvironmentTesterService tester,
        IReportService reportService,
        DefectScoutConfig config,
        StructuredTestPlan plan)
    {
        _tester = tester;
        _reportService = reportService;
        _config = config;
        _plan = plan;
    }

    public async Task StartAsync()
    {
        IsRunning = true;
        IsComplete = false;
        OverallStatus = "Probing Copilot CLI...";
        EnvProgresses.Clear();

        var enabledEnvs = _config.Environments.Where(e => e.Enabled).ToList();
        _log.Information("StartAsync: ticket={Ticket}, enabledEnvironments={Count}",
            _plan.Ticket, enabledEnvs.Count);
        if (enabledEnvs.Count == 0)
        {
            _log.Warning("No enabled environments found — aborting run");
            OverallStatus = "No enabled environments.";
            IsRunning = false;
            return;
        }

        // Pre-populate cards
        foreach (var env in enabledEnvs)
            EnvProgresses.Add(new ProgressCardViewModel { EnvironmentName = env.Name });

        // Build a progress reporter for every environment
        var logOffsets = new int[enabledEnvs.Count];
        var progressList = enabledEnvs.Select((_, i) =>
        {
            var card = EnvProgresses[i];
            return (IProgress<EnvironmentProgress>) new Progress<EnvironmentProgress>(update =>
            {
                card.Status = update.Status;
                card.CurrentStep = update.CurrentStep;
                card.TotalSteps = Math.Max(1, update.TotalSteps);
                card.StepDescription = update.CurrentAction ?? string.Empty;
                card.LatestScreenshotPath = update.LatestScreenshot;

                // Lock on the same list object used as the lock root in EnvironmentTesterService
                // to prevent concurrent List<string> access between the SDK callback thread and
                // the UI thread.
                lock (update.Log)
                {
                    for (int li = logOffsets[i]; li < update.Log.Count; li++)
                        card.AppendLog(update.Log[li]);
                    logOffsets[i] = update.Log.Count;
                }
            });
        }).ToList();

        // Directories — app-relative
        var resultsDir = Path.Combine(AppContext.BaseDirectory, "data", "results",
            SanitizePath(_plan.Ticket));

        List<TestResult> results;
        try
        {
            results = await _tester.RunAllAsync(
                _plan,
                enabledEnvs,
                _config.ScreenshotBaseDir,
                resultsDir,
                _config.Playwright,
                progressList,
                mode =>
                {
                    _log.Information("Dispatch mode: {Mode}", mode);
                    DispatchMode = mode;
                    OnPropertyChanged(nameof(DispatchBadge));
                    OverallStatus = mode is "fleet" or "parallel"
                        ? $"⚡ Fleet dispatched — {enabledEnvs.Count} environments running in parallel..."
                        : $"▶ Sequential — testing {enabledEnvs.Count} environments one at a time...";

                    if (mode is "fleet" or "parallel")
                        foreach (var card in EnvProgresses)
                            card.IsFleetMode = true;
                });
        }
        catch (Exception ex)
        {
            _log.Error(ex, "RunAllAsync failed for ticket={Ticket}", _plan.Ticket);
            OverallStatus = $"Run error: {ex.Message}";
            IsRunning = false;
            return;
        }

        // Generate report
        string reportPath;
        try
        {
            reportPath = await _reportService.GenerateAsync(
                _plan, results, _config.ReportDir, _config.ScreenshotBaseDir);
            _log.Information("Report generated: {Path}", reportPath);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Report generation failed for ticket={Ticket}", _plan.Ticket);
            reportPath = string.Empty;
        }

        IsRunning = false;
        IsComplete = true;
        OverallStatus = SummarizeResults(results);
        _log.Information("Run complete: {Summary}", OverallStatus);
        Finished?.Invoke(results, reportPath);
    }

    private static string SummarizeResults(List<TestResult> results)
    {
        int repro    = results.Count(r => r.Verdict == Verdict.Reproduced);
        int notRepro = results.Count(r => r.Verdict == Verdict.NotReproduced);
        int error    = results.Count(r => r.Verdict == Verdict.Error);
        return $"Complete — {repro} reproduced, {notRepro} not reproduced, {error} errors.";
    }

    partial void OnDispatchModeChanged(string value) => OnPropertyChanged(nameof(DispatchBadge));

    private static string SanitizePath(string s) =>
        string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}

