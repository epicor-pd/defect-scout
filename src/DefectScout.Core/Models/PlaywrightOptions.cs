using System.Text.Json.Serialization;

namespace DefectScout.Core.Models;

public class PlaywrightOptions
{
    public const int DefaultTimeoutMilliseconds = 120000;
    public const int MinTimeoutMilliseconds = 5000;
    public const int MaxTimeoutMilliseconds = 7200000;

    [JsonPropertyName("timeout")]
    public int Timeout { get; set; } = DefaultTimeoutMilliseconds;

    [JsonPropertyName("screenshotOnStep")]
    public bool ScreenshotOnStep { get; set; } = true;

    [JsonPropertyName("screenshotOnFailure")]
    public bool ScreenshotOnFailure { get; set; } = true;

    [JsonPropertyName("headless")]
    public bool Headless { get; set; } = true;

    [JsonPropertyName("ignoreHttpsErrors")]
    public bool IgnoreHttpsErrors { get; set; } = true;

    [JsonPropertyName("maxAutoHealAttempts")]
    public int MaxAutoHealAttempts { get; set; } = 3;

    [JsonIgnore]
    public TimeSpan TimeoutDuration => TimeSpan.FromMilliseconds(NormalizeTimeout(Timeout));

    public static int NormalizeTimeout(int timeoutMilliseconds) =>
        Math.Clamp(
            timeoutMilliseconds > 0 ? timeoutMilliseconds : DefaultTimeoutMilliseconds,
            MinTimeoutMilliseconds,
            MaxTimeoutMilliseconds);
}
