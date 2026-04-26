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

    /// <summary>Controls whether Defect Scout uses GitHub Copilot SDK or local Agent Framework/Ollama agents.</summary>
    [JsonPropertyName("agentRuntime")]
    public AgentRuntimeOptions AgentRuntime { get; set; } = new();
}

public class AgentRuntimeOptions
{
    public const string CopilotSdkMode = "copilotSdk";
    public const string LocalOllamaMode = "localOllama";
    public const int DefaultOllamaContextTokens = 8192;
    public const int MinOllamaContextTokens = 2048;
    public const int MaxOllamaContextTokens = 32768;
    public const int DefaultOllamaMaxOutputTokens = 4096;
    public const int MinOllamaMaxOutputTokens = 512;
    public const int MaxOllamaMaxOutputTokens = 8192;
    public const string DefaultOllamaThink = "low";

    [JsonPropertyName("mode")]
    public string Mode { get; set; } = CopilotSdkMode;

    [JsonPropertyName("ollamaEndpoint")]
    public string OllamaEndpoint { get; set; } = "http://localhost:11434";

    [JsonPropertyName("stepExtractorModel")]
    public string StepExtractorModel { get; set; } = "qwen3.5:4b";

    [JsonPropertyName("envTesterModel")]
    public string EnvTesterModel { get; set; } = "qwen3.5:4b";

    [JsonPropertyName("maxConcurrentEnvTesters")]
    public int MaxConcurrentEnvTesters { get; set; } = 3;

    [JsonPropertyName("toolTimeoutSeconds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int? LegacyToolTimeoutSeconds { get; set; }

    [JsonPropertyName("maxToolIterations")]
    public int MaxToolIterations { get; set; } = 80;

    [JsonPropertyName("ollamaContextTokens")]
    public int OllamaContextTokens { get; set; } = DefaultOllamaContextTokens;

    [JsonPropertyName("ollamaMaxOutputTokens")]
    public int OllamaMaxOutputTokens { get; set; } = DefaultOllamaMaxOutputTokens;

    [JsonPropertyName("ollamaThink")]
    public string OllamaThink { get; set; } = DefaultOllamaThink;

    [JsonIgnore]
    public bool IsLocalOllama =>
        string.Equals(Mode, LocalOllamaMode, StringComparison.OrdinalIgnoreCase);

    public static int NormalizeOllamaContextTokens(int value) =>
        Math.Clamp(
            value > 0 ? value : DefaultOllamaContextTokens,
            MinOllamaContextTokens,
            MaxOllamaContextTokens);

    public static int NormalizeOllamaMaxOutputTokens(int value) =>
        Math.Clamp(
            value > 0 ? value : DefaultOllamaMaxOutputTokens,
            MinOllamaMaxOutputTokens,
            MaxOllamaMaxOutputTokens);

    public static string NormalizeOllamaThink(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return DefaultOllamaThink;

        var normalized = value.Trim().ToLowerInvariant();
        return normalized is "off" or "false" or "none" or "disabled"
            ? "off"
            : normalized is "medium" or "high"
                ? normalized
                : DefaultOllamaThink;
    }
}
