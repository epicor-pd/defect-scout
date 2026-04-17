using System.Text.Json.Serialization;

namespace DefectScout.Core.Models;

public class PlaywrightOptions
{
    [JsonPropertyName("timeout")]
    public int Timeout { get; set; } = 30000;

    [JsonPropertyName("screenshotOnStep")]
    public bool ScreenshotOnStep { get; set; } = true;

    [JsonPropertyName("screenshotOnFailure")]
    public bool ScreenshotOnFailure { get; set; } = true;

    [JsonPropertyName("headless")]
    public bool Headless { get; set; } = true;

    [JsonPropertyName("ignoreHttpsErrors")]
    public bool IgnoreHttpsErrors { get; set; } = true;
}
