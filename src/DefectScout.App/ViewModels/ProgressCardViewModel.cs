using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DefectScout.App.ViewModels;

/// <summary>
/// Per-environment card ViewModel displayed during the running phase.
/// Must be ObservableObject so the UI reacts to live updates.
/// </summary>
public sealed partial class ProgressCardViewModel : ObservableObject
{
    [ObservableProperty] private string _environmentName = string.Empty;
    [ObservableProperty] private string _status = "Waiting...";
    [ObservableProperty] private int _currentStep;
    [ObservableProperty] private int _totalSteps = 1;
    [ObservableProperty] private string _stepDescription = string.Empty;
    [ObservableProperty] private string? _latestScreenshotPath;

    /// <summary>True when running in fleet mode — cards are driven by result-file polling, not live progress.</summary>
    [ObservableProperty] private bool _isFleetMode;

    public ObservableCollection<string> LogLines { get; } = [];

    public string StatusColor => Status switch
    {
        "Running"  => "#0078D4",
        "Done"     => "#107C10",
        "Error"    => "#D13438",
        "Queued"   => "#B35900",
        "Waiting"  => "#888888",
        _ => "#888888",
    };

    /// <summary>Verdict icon shown after the run completes.</summary>
    public string VerdictIcon => Status switch
    {
        "Done"  => "✅",
        "Error" => "⚠",
        _       => string.Empty,
    };

    public void AppendLog(string line)
    {
        LogLines.Add(line);
        // Keep capped at last 50 lines to avoid memory growth
        while (LogLines.Count > 50)
            LogLines.RemoveAt(0);
    }

    partial void OnStatusChanged(string value)
    {
        OnPropertyChanged(nameof(StatusColor));
        OnPropertyChanged(nameof(VerdictIcon));
    }
}
