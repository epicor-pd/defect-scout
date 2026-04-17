using System.Text.Json.Serialization;

namespace DefectScout.Core.Models;

public enum Verdict { Reproduced, NotReproduced, Error }

public class TestResult
{
    [JsonPropertyName("envName")]
    public string EnvName { get; set; } = string.Empty;

    /// <summary>Alias used by ViewModels.</summary>
    [JsonIgnore]
    public string EnvironmentName
    {
        get => EnvName;
        set => EnvName = value;
    }

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    /// <summary>REPRODUCED | NOT_REPRODUCED | ERROR</summary>
    [JsonPropertyName("result")]
    public string Result { get; set; } = string.Empty;

    [JsonIgnore]
    public Verdict Verdict => Result switch
    {
        "REPRODUCED" => Verdict.Reproduced,
        "NOT_REPRODUCED" => Verdict.NotReproduced,
        _ => Verdict.Error,
    };

    /// <summary>CSS/Avalonia color string for verdict badge.</summary>
    [JsonIgnore]
    public string VerdictColor => Verdict switch
    {
        Verdict.Reproduced => "#D13438",
        Verdict.NotReproduced => "#107C10",
        _ => "#B35900",
    };

    [JsonPropertyName("stepResults")]
    public List<StepResult> StepResults { get; set; } = [];

    [JsonPropertyName("screenshotPaths")]
    public List<string> ScreenshotPaths { get; set; } = [];

    [JsonPropertyName("defectObserved")]
    public bool DefectObserved { get; set; }

    [JsonPropertyName("notes")]
    public string Notes { get; set; } = string.Empty;

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    // ── Computed helpers ──────────────────────────────────────────────────

    [JsonIgnore]
    public bool IsReproduced => Result == "REPRODUCED";

    [JsonIgnore]
    public bool IsNotReproduced => Result == "NOT_REPRODUCED";

    [JsonIgnore]
    public bool IsError => Result == "ERROR";
}

public class StepResult
{
    [JsonPropertyName("stepNumber")]
    public int StepNumber { get; set; }

    [JsonPropertyName("action")]
    public string Action { get; set; } = string.Empty;

    [JsonPropertyName("passed")]
    public bool Passed { get; set; }

    [JsonPropertyName("screenshot")]
    public string? Screenshot { get; set; }

    [JsonPropertyName("notes")]
    public string Notes { get; set; } = string.Empty;
}

/// <summary>Per-environment live progress reported back to the UI during execution.</summary>
public class EnvironmentProgress
{
    public string EnvName { get; set; } = string.Empty;

    /// <summary>Alias used by ViewModels.</summary>
    public string EnvironmentName
    {
        get => EnvName;
        set => EnvName = value;
    }

    public string Version { get; set; } = string.Empty;

    /// <summary>Waiting | Running | Done | Error</summary>
    public string Status { get; set; } = "Waiting...";

    public string StatusColor => Status switch
    {
        "Running" => "#0078D4",
        "Done" => "#107C10",
        "Error" => "#D13438",
        _ => "#888888",
    };

    public int CurrentStep { get; set; }
    public int TotalSteps { get; set; }

    public string CurrentAction { get; set; } = string.Empty;

    /// <summary>Alias used by AXAML.</summary>
    public string StepDescription
    {
        get => CurrentAction;
        set => CurrentAction = value;
    }

    public string? LatestScreenshot { get; set; }

    /// <summary>Alias used by AXAML.</summary>
    public string? LatestScreenshotPath
    {
        get => LatestScreenshot;
        set => LatestScreenshot = value;
    }

    public List<string> Log { get; set; } = [];

    /// <summary>Alias used by AXAML.</summary>
    public List<string> LogLines => Log;

    public TestResult? FinalResult { get; set; }
}
