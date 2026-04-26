using DefectScout.Core.Models;
using Microsoft.Extensions.AI;
using OllamaSharp;
using OllamaSharp.Models;

namespace DefectScout.Core.Services;

internal static class LocalOllamaOptionsFactory
{
    public static ChatOptions Create(
        AgentRuntimeOptions runtime,
        string model,
        int requestedMaxOutputTokens,
        IList<AITool>? tools = null)
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

        var think = AgentRuntimeOptions.NormalizeOllamaThink(runtime.OllamaThink);
        if (think != "off" && SupportsThinking(model))
        {
            options.AddOllamaOption(
                OllamaOption.Think,
                RequiresThinkingLevel(model) ? think : true);
        }

        return options;
    }

    public static string Describe(AgentRuntimeOptions runtime, string model, int requestedMaxOutputTokens)
    {
        var contextTokens = AgentRuntimeOptions.NormalizeOllamaContextTokens(runtime.OllamaContextTokens);
        var outputTokens = AgentRuntimeOptions.NormalizeOllamaMaxOutputTokens(requestedMaxOutputTokens);
        var think = AgentRuntimeOptions.NormalizeOllamaThink(runtime.OllamaThink);
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
