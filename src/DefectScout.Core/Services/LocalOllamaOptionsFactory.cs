using DefectScout.Core.Models;
using Microsoft.Extensions.AI;
using OllamaSharp;
using OllamaSharp.Models;

namespace DefectScout.Core.Services;

internal static class LocalOllamaOptionsFactory
{
    // Purpose constants for explicit think-setting selection.
    public const string PurposeStepExtractor = "stepExtractor";
    public const string PurposeEnvTester     = "envTester";

    public static ChatOptions Create(
        AgentRuntimeOptions runtime,
        string model,
        int requestedMaxOutputTokens,
        IList<AITool>? tools = null,
        string? purpose = null)
    {
        var contextTokens = AgentRuntimeOptions.NormalizeOllamaContextTokens(runtime.OllamaContextTokens);
        var outputTokens = AgentRuntimeOptions.NormalizeOllamaMaxOutputTokens(requestedMaxOutputTokens);

        var options = new ChatOptions
        {
            Temperature = 0.1f,
            TopP = 0.8f,
            MaxOutputTokens = outputTokens,
            AllowMultipleToolCalls = true,
        };

        if (tools is not null)
            options.Tools = tools;

        options.AddOllamaOption(OllamaOption.NumCtx, contextTokens);
        options.AddOllamaOption(OllamaOption.NumPredict, outputTokens);
        options.AddOllamaOption(OllamaOption.Temperature, 0.1f);
        options.AddOllamaOption(OllamaOption.TopP, 0.8f);

        // Determine the effective "think" setting for this model. When an explicit
        // purpose is supplied it takes precedence — this avoids the ambiguity where
        // StepExtractorModel and EnvTesterModel are the same model name, which would
        // cause the step-extractor check to always win.
        var modelName = NormalizeModelName(model);
        var stepModelName = NormalizeModelName(runtime.StepExtractorModel);
        var envModelName = NormalizeModelName(runtime.EnvTesterModel);

        var thinkSource = purpose == PurposeStepExtractor ? runtime.OllamaThinkStepExtractor
            : purpose == PurposeEnvTester               ? runtime.OllamaThinkEnvTester
            : string.Equals(modelName, stepModelName, StringComparison.OrdinalIgnoreCase)
                ? runtime.OllamaThinkStepExtractor
                : string.Equals(modelName, envModelName, StringComparison.OrdinalIgnoreCase)
                    ? runtime.OllamaThinkEnvTester
                    : runtime.OllamaThink;

        var think = AgentRuntimeOptions.NormalizeOllamaThink(thinkSource);
        if (think != "off" && SupportsThinking(model))
        {
            options.AddOllamaOption(
                OllamaOption.Think,
                RequiresThinkingLevel(model) ? think : true);
        }

        return options;
    }

    public static string Describe(AgentRuntimeOptions runtime, string model, int requestedMaxOutputTokens, string? purpose = null)
    {
        var contextTokens = AgentRuntimeOptions.NormalizeOllamaContextTokens(runtime.OllamaContextTokens);
        var outputTokens = AgentRuntimeOptions.NormalizeOllamaMaxOutputTokens(requestedMaxOutputTokens);
        var modelName = NormalizeModelName(model);
        var stepModelName = NormalizeModelName(runtime.StepExtractorModel);
        var envModelName = NormalizeModelName(runtime.EnvTesterModel);

        var thinkSource = purpose == PurposeStepExtractor ? runtime.OllamaThinkStepExtractor
            : purpose == PurposeEnvTester               ? runtime.OllamaThinkEnvTester
            : string.Equals(modelName, stepModelName, StringComparison.OrdinalIgnoreCase)
                ? runtime.OllamaThinkStepExtractor
                : string.Equals(modelName, envModelName, StringComparison.OrdinalIgnoreCase)
                    ? runtime.OllamaThinkEnvTester
                    : runtime.OllamaThink;

        var think = AgentRuntimeOptions.NormalizeOllamaThink(thinkSource);
        var effectiveThink = think == "off" || !SupportsThinking(model)
            ? "off"
            : RequiresThinkingLevel(model) ? think : "on";

        return $"context={contextTokens:N0}, maxOutput={outputTokens:N0}, think={effectiveThink}";
    }

    private static bool SupportsThinking(string model)
    {
        var name = NormalizeModelName(model);
        return name.StartsWith("gpt-oss", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("qwen3", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("deepseek-r1", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("deepseek-v3.1", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("openthinker", StringComparison.OrdinalIgnoreCase);
    }

    private static bool RequiresThinkingLevel(string model) =>
        NormalizeModelName(model).StartsWith("gpt-oss", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeModelName(string model)
    {
        var trimmed = (model ?? string.Empty).Trim();
        var slash = trimmed.LastIndexOf('/');
        return slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
    }
}
