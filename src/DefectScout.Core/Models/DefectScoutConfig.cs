using System.Text.Json.Serialization;

namespace DefectScout.Core.Models;

/// <summary>Root object matching defect-scout-config.json schema.</summary>
public class DefectScoutConfig
{
    [JsonPropertyName("environments")]
    public List<KineticEnvironment> Environments { get; set; } = [];

    [JsonPropertyName("screenshotBaseDir")]
    public string ScreenshotBaseDir { get; set; } = string.Empty;

    [JsonPropertyName("reportDir")]
    public string ReportDir { get; set; } = string.Empty;

    [JsonPropertyName("playwright")]
    public PlaywrightOptions Playwright { get; set; } = new();

    /// <summary>Directory where log files are written. Normalised to app-relative by ConfigService.</summary>
    [JsonPropertyName("logDir")]
    public string LogDir { get; set; } = string.Empty;
}
