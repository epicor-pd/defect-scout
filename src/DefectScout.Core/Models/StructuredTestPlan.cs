using System.Text.Json.Serialization;

namespace DefectScout.Core.Models;

/// <summary>
/// Environment-agnostic structured test plan produced by the step extractor.
/// Matches the JSON schema used by the original DefectScout agent.
/// </summary>
public class StructuredTestPlan
{
    [JsonPropertyName("ticket")]
    public string Ticket { get; set; } = string.Empty;

    [JsonPropertyName("summary")]
    public string Summary { get; set; } = string.Empty;

    [JsonPropertyName("affectedModule")]
    public string AffectedModule { get; set; } = string.Empty;

    [JsonPropertyName("affectedBO")]
    public string? AffectedBO { get; set; }

    [JsonPropertyName("preconditions")]
    public List<string> Preconditions { get; set; } = [];

    [JsonPropertyName("steps")]
    public List<TestStep> Steps { get; set; } = [];

    [JsonPropertyName("expectedResult")]
    public string ExpectedResult { get; set; } = string.Empty;

    [JsonPropertyName("actualResult")]
    public string ActualResult { get; set; } = string.Empty;

    [JsonPropertyName("generatedAt")]
    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class TestStep
{
    [JsonPropertyName("stepNumber")]
    public int StepNumber { get; set; }

    /// <summary>navigate | click | fill | select | verify | wait | screenshot | api-call</summary>
    [JsonPropertyName("action")]
    public string Action { get; set; } = string.Empty;

    [JsonPropertyName("target")]
    public string Target { get; set; } = string.Empty;

    [JsonPropertyName("value")]
    public string? Value { get; set; }

    [JsonPropertyName("expected")]
    public string Expected { get; set; } = string.Empty;

    [JsonPropertyName("selectorHints")]
    public List<string> SelectorHints { get; set; } = [];

    [JsonPropertyName("isDiscriminatingStep")]
    public bool IsDiscriminatingStep { get; set; }

    [JsonPropertyName("ambiguous")]
    public bool Ambiguous { get; set; }

    [JsonPropertyName("clarificationNeeded")]
    public string? ClarificationNeeded { get; set; }
}
