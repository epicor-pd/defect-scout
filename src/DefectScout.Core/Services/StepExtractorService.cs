using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DefectScout.Core.Models;
using DefectScout.Core.Prompts;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.AI;
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
        DefectScoutConfig? config = null,
        CancellationToken ct = default)
    {
        var prompt = BuildPrompt(ticketIdOrText, filePath, out var ticketContext);
        _log.Information(
            "ExtractAsync: ticket={Ticket}, hasFile={HasFile}, sourceBytes={SourceBytes}, contextChars={ContextChars}, compacted={Compacted}, promptChars={PromptChars}",
            ticketIdOrText,
            filePath is not null,
            ticketContext?.SourceBytes,
            ticketContext?.Text.Length,
            ticketContext?.WasCompacted,
            prompt.Length);

        if (config?.AgentRuntime.IsLocalOllama == true)
            return await ExtractWithLocalOllamaAsync(
                prompt, ticketIdOrText, config.AgentRuntime, config.Playwright, progress, ct);

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

    private static string BuildPrompt(string customSteps, string? filePath, out TicketContext? ticketContext)
    {
        var sb = new StringBuilder();
        ticketContext = null;

        if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
        {
            ticketContext = TicketContextExtractor.Extract(filePath);
            sb.AppendLine($"Ticket file context ({Path.GetFileName(filePath)}, compact extracted view):");
            sb.AppendLine(ticketContext.Text);

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
        var cleaned = JsonResponseParser.ExtractFirstObject(raw);

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

    private static async Task<StructuredTestPlan> ExtractWithLocalOllamaAsync(
        string prompt,
        string ticketFallback,
        AgentRuntimeOptions runtime,
        PlaywrightOptions opts,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        var operationTimeout = opts.TimeoutDuration;
        progress?.Report($"Connecting to Ollama at {runtime.OllamaEndpoint}...\n");
        progress?.Report($"Using local extraction model: {runtime.StepExtractorModel}\n");
        progress?.Report($"Operation timeout: {(int)operationTimeout.TotalMilliseconds:N0} ms\n");
        progress?.Report($"Ollama options: {LocalOllamaOptionsFactory.Describe(runtime, runtime.StepExtractorModel, runtime.OllamaMaxOutputTokens)}\n");

        using var ollama = LocalOllamaClientFactory.Create(
            runtime.OllamaEndpoint,
            runtime.StepExtractorModel,
            operationTimeout);
        var chatClient = (IChatClient)ollama.Client;
        var messages = new[]
        {
            new ChatMessage(ChatRole.System, AgentPrompts.StepExtractor),
            new ChatMessage(ChatRole.User, prompt),
        };
        var options = LocalOllamaOptionsFactory.Create(
            runtime,
            runtime.StepExtractorModel,
            runtime.OllamaMaxOutputTokens);

        progress?.Report("Analysing ticket locally...\n");
        string responseText;
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var response = await AwaitWithHeartbeatAsync(
                chatClient.GetResponseAsync(messages, options, ct),
                TimeSpan.FromSeconds(30),
                elapsed =>
                {
                    var msg = $"Still waiting for local extraction model after {elapsed:mm\\:ss}...";
                    _log.Information("Step extraction heartbeat: model={Model}, elapsedMs={ElapsedMs}",
                        runtime.StepExtractorModel,
                        (long)elapsed.TotalMilliseconds);
                    progress?.Report(msg + "\n");
                },
                ct);
            _log.Information("Local step extraction completed: model={Model}, elapsedMs={ElapsedMs}, responseChars={ResponseChars}",
                runtime.StepExtractorModel,
                stopwatch.ElapsedMilliseconds,
                response.Text.Length);
            responseText = response.Text;
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Local Ollama step extraction exceeded the configured operation timeout of {(int)operationTimeout.TotalMilliseconds:N0} ms. " +
                "Increase the Configuration timeout if the local model needs more time.",
                ex);
        }

        progress?.Report(responseText);
        progress?.Report("\nParsing extracted steps...\n");

        return ParseJson(responseText, ticketFallback);
    }

    private static async Task<T> AwaitWithHeartbeatAsync<T>(
        Task<T> task,
        TimeSpan interval,
        Action<TimeSpan> heartbeat,
        CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            var completed = await Task.WhenAny(task, Task.Delay(interval, ct));
            if (ReferenceEquals(completed, task))
                return await task;

            heartbeat(stopwatch.Elapsed);
        }
    }
}
