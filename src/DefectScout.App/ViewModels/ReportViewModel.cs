using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DefectScout.Core.Models;
using DefectScout.Core.Services;
using Serilog;

namespace DefectScout.App.ViewModels;

/// <summary>
/// Screen 6 — rendered Markdown report + action buttons.
/// </summary>
public sealed partial class ReportViewModel : ViewModelBase
{
    private static readonly ILogger _log = AppLogger.For<ReportViewModel>();
    private readonly List<TestResult> _results;

    public override string PageTitle => "Report";

    public event Action? RunAgain;
    public event Action? StartOver;

    /// <summary>Raw markdown text — bound to the in-app scrollable text block.</summary>
    [ObservableProperty]
    private string _reportContent = string.Empty;

    /// <summary>Path to the generated .html report file (opened in browser).</summary>
    [ObservableProperty]
    private string _reportPath = string.Empty;

    [ObservableProperty]
    private string _summaryLine = string.Empty;

    public ObservableCollection<TestResult> Results { get; } = [];

    public ReportViewModel(List<TestResult> results, string reportPath)
    {
        _results = results;
        ReportPath = reportPath;

        foreach (var r in results) Results.Add(r);
        SummaryLine = BuildSummaryLine(results);

        _ = LoadReportContentAsync(reportPath);
    }

    private async Task LoadReportContentAsync(string htmlPath)
    {
        if (string.IsNullOrEmpty(htmlPath)) { _log.Warning("LoadReportContent: no report path, using fallback"); ReportContent = BuildFallbackText(); return; }

        var mdPath = Path.ChangeExtension(htmlPath, ".md");
        if (File.Exists(mdPath))
        {
            try
            {
                _log.Debug("LoadReportContent: reading markdown from {Path}", mdPath);
                ReportContent = await File.ReadAllTextAsync(mdPath); return;
            }
            catch (Exception ex) { _log.Warning(ex, "LoadReportContent: failed to read markdown {Path}", mdPath); }
        }
        if (File.Exists(htmlPath))
        {
            try
            {
                _log.Debug("LoadReportContent: HTML fallback from {Path}", htmlPath);
                var html = await File.ReadAllTextAsync(htmlPath);
                ReportContent = System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", string.Empty).Trim();
                return;
            }
            catch (Exception ex) { _log.Warning(ex, "LoadReportContent: failed to read html {Path}", htmlPath); }
        }
        _log.Warning("LoadReportContent: no report files found at {Path}, using fallback", htmlPath);
        ReportContent = BuildFallbackText();
    }

    [RelayCommand]
    private void OpenInBrowser()
    {
        // Prefer .html for rendered view; fall back to .md if html doesn't exist
        var target = File.Exists(ReportPath) ? ReportPath
            : Path.ChangeExtension(ReportPath, ".md");
        if (!File.Exists(target)) return;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = target,
            UseShellExecute = true,
        });
    }

    [RelayCommand]
    private void OpenFolder()
    {
        var dir = Path.GetDirectoryName(ReportPath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = dir,
            UseShellExecute = true,
        });
    }

    [RelayCommand]
    private void GoRunAgain() => RunAgain?.Invoke();

    [RelayCommand]
    private void GoStartOver() => StartOver?.Invoke();

    private static string BuildSummaryLine(List<TestResult> results)
    {
        int repro = results.Count(r => r.Verdict == Verdict.Reproduced);
        int notRepro = results.Count(r => r.Verdict == Verdict.NotReproduced);
        int error = results.Count(r => r.Verdict == Verdict.Error);
        return $"Tested {results.Count} environment(s): {repro} REPRODUCED  |  {notRepro} NOT REPRODUCED  |  {error} ERROR";
    }

    private string BuildFallbackText()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# DefectScout Results");
        sb.AppendLine();
        sb.AppendLine($"| Environment | Version | Verdict |");
        sb.AppendLine($"|-------------|---------|---------|" );
        foreach (var r in _results)
            sb.AppendLine($"| {r.EnvironmentName} | {r.Version} | {r.Verdict} |");
        return sb.ToString();
    }

}
