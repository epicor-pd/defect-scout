using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DefectScout.Core.Models;
using DefectScout.Core.Prompts;
using GitHub.Copilot.SDK;
using Serilog;

namespace DefectScout.Core.Services;

/// <summary>
/// Uses the GitHub Copilot SDK to parse a Kinetic ERPS ticket into a StructuredTestPlan.
/// Mirrors the behaviour of defect-scout-step-extractor.agent.md.
/// </summary>
public sealed class StepExtractorService : IStepExtractorService
{
    private static readonly ILogger _log = AppLogger.For<StepExtractorService>();
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public async Task<StructuredTestPlan> ExtractAsync(
        string ticketIdOrText,
        string? filePath,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var prompt = BuildPrompt(ticketIdOrText, filePath);
        _log.Information("ExtractAsync: ticket={Ticket}, hasFile={HasFile}",
            ticketIdOrText, filePath is not null);
        progress?.Report("Connecting to GitHub Copilot...");

        await using var client = new CopilotClient(CliLocator.BuildClientOptions());
        await client.StartAsync();

        await using var session = await client.CreateSessionAsync(new SessionConfig
        {
            Model = "gpt-5.4",
            Streaming = true,
            OnPermissionRequest = PermissionHandler.ApproveAll,
            SystemMessage = new SystemMessageConfig
            {
                Mode = SystemMessageMode.Replace,
                Content = AgentPrompts.StepExtractor,
            },
            // Limit to read-only tools to prevent any accidental file writes
            AvailableTools = [],
            ExcludedTools = ["edit_file", "create_file", "delete_file",
                             "run_command", "shell", "write"],
        });

        var sb = new StringBuilder();
        var tcs = new TaskCompletionSource<string>();

        using var sub = session.On(evt =>
        {
            switch (evt)
            {
                case AssistantMessageDeltaEvent delta:
                    sb.Append(delta.Data.DeltaContent);
                    progress?.Report(delta.Data.DeltaContent ?? string.Empty);
                    break;

                case AssistantMessageEvent msg:
                    tcs.TrySetResult(msg.Data.Content ?? sb.ToString());
                    break;

                case SessionIdleEvent:
                    tcs.TrySetResult(sb.ToString());
                    break;

                case SessionErrorEvent err:
                    tcs.TrySetException(new InvalidOperationException(
                        $"Copilot session error: {err.Data.Message}"));
                    break;
            }
        });

        using var reg = ct.Register(() =>
            tcs.TrySetCanceled(ct));

        progress?.Report("Analysing ticket...");
        await session.SendAsync(new MessageOptions { Prompt = prompt });

        var rawJson = await tcs.Task;
        _log.Debug("Step extractor raw response length: {Length}", rawJson.Length);
        progress?.Report("Parsing extracted steps...");

        return ParseJson(rawJson, ticketIdOrText);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string BuildPrompt(string customSteps, string? filePath)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
        {
            var content = File.ReadAllText(filePath);
            sb.AppendLine($"Ticket file contents ({Path.GetFileName(filePath)}):");
            sb.AppendLine(content);

            if (!string.IsNullOrWhiteSpace(customSteps))
            {
                sb.AppendLine();
                sb.AppendLine("Additional custom reproduction steps provided by the user (supplement the ticket above):");
                sb.AppendLine(customSteps);
            }
        }
        else
        {
            sb.AppendLine("Ticket:");
            sb.AppendLine(customSteps);
        }

        sb.AppendLine();
        sb.AppendLine("Return ONLY the StructuredTestPlan JSON. Do not include explanations or markdown code fences.");
        return sb.ToString();
    }

    private static StructuredTestPlan ParseJson(string raw, string ticketFallback)
    {
        // Strip any accidental markdown code fences
        var cleaned = Regex.Replace(raw.Trim(), @"^```[a-z]*\n?|```$", "", RegexOptions.Multiline).Trim();

        try
        {
            var plan = JsonSerializer.Deserialize<StructuredTestPlan>(cleaned, s_jsonOptions);
            if (plan is not null)
            {
                plan.GeneratedAt = DateTimeOffset.UtcNow;
                _log.Information("Extracted plan: ticket={Ticket}, steps={Steps}",
                    plan.Ticket, plan.Steps?.Count ?? 0);
                return plan;
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"The AI returned a response that could not be parsed as a StructuredTestPlan. " +
                $"Raw JSON:\n{cleaned}\n\nParse error: {ex.Message}", ex);
        }

        throw new InvalidOperationException("AI returned null plan.");
    }
}
