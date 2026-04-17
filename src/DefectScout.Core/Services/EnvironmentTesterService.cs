using System.Text.Json;
using System.Text.RegularExpressions;
using DefectScout.Core.Models;
using DefectScout.Core.Prompts;
using GitHub.Copilot.SDK;
using Serilog;

namespace DefectScout.Core.Services;

/// <summary>
/// Executes a StructuredTestPlan across environments using the GitHub Copilot SDK.
///
/// Parallel path: Creates a single <see cref="CopilotClient"/> (one bundled CLI process) and
///   runs one independent SDK session per environment concurrently via <c>Task.WhenAll</c>.
///   The defect-scout-env-tester instructions are embedded directly in each session via
///   <see cref="SystemMessageMode.Replace"/> — no file-based agent discovery is required.
///   This is the SDK-native equivalent of the <c>/fleet</c> dispatch used by the repo-based
///   custom copilot agent at the reference project.
///
/// Sequential fallback: Available for ordered execution when explicitly requested.
/// </summary>
public sealed class EnvironmentTesterService : IEnvironmentTesterService
{
    private static readonly ILogger _log = AppLogger.For<EnvironmentTesterService>();
    private static readonly JsonSerializerOptions s_jsonOpts = new() { PropertyNameCaseInsensitive = true };

    // Matches "Step N" anywhere in a log line (case-insensitive)
    private static readonly Regex s_stepRx =
        new(@"[Ss]tep\s+(\d+)", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    // Matches a screenshot filename:  step-03-click.png
    private static readonly Regex s_ssRx =
        new(@"step-\d{2}-[^\s""']+\.png", RegexOptions.IgnoreCase | RegexOptions.Compiled,
            TimeSpan.FromMilliseconds(100));

    // ── Public API ──────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<List<TestResult>> RunAllAsync(
        StructuredTestPlan plan,
        IReadOnlyList<KineticEnvironment> environments,
        string screenshotBaseDir,
        string resultsDir,
        PlaywrightOptions opts,
        IReadOnlyList<IProgress<EnvironmentProgress>> progressList,
        Action<string>? onDispatchMode = null,
        CancellationToken ct = default)
    {
        _log.Information("RunAllAsync: ticket={Ticket}, environments={Count}", plan.Ticket, environments.Count);

        // Pre-create output directories (mirrors Stage 2 of defect-scout.agent.md)
        Directory.CreateDirectory(resultsDir);
        foreach (var env in environments)
        {
            var ssDir = Path.Combine(screenshotBaseDir, env.VersionSlug, plan.Ticket);
            Directory.CreateDirectory(ssDir);
            _log.Debug("Screenshot dir: {Dir}", ssDir);
        }

        // Write plan to disk for traceability — {resultsDir}/{ticket}-steps.json
        // matches the conventional path used by the reference defect-scout.agent.md.
        var planFilePath = Path.Combine(resultsDir, $"{plan.Ticket}-steps.json");
        await File.WriteAllTextAsync(planFilePath,
            JsonSerializer.Serialize(plan, s_jsonOpts), ct);
        _log.Debug("Plan written to {Path}", planFilePath);

        // Dispatch all environments in parallel via SDK sessions on a single CopilotClient.
        // This is the SDK equivalent of the /fleet dispatch in defect-scout.agent.md Step 2b:
        // the defect-scout-env-tester instructions are embedded via SystemMessage.Replace so
        // no .agent.md file discovery or CLI subprocess is required.
        onDispatchMode?.Invoke("parallel");
        return await RunParallelSdkAsync(plan, environments, screenshotBaseDir, resultsDir, opts, progressList, ct);
    }

    /// <inheritdoc/>
    public async Task<TestResult> RunAsync(
        StructuredTestPlan plan,
        KineticEnvironment env,
        string screenshotDir,
        string resultFile,
        PlaywrightOptions opts,
        IProgress<EnvironmentProgress>? progress = null,
        CancellationToken ct = default)
    {
        // For a single-env direct invocation, create a dedicated client.
        await using var client = new CopilotClient(CliLocator.BuildClientOptions());
        await client.StartAsync();
        return await RunSdkSessionAsync(client, plan, env, screenshotDir, resultFile, opts, progress, ct);
    }

    // ── Parallel SDK dispatch (SDK equivalent of fleet) ────────────────────

    /// <summary>
    /// Creates a single <see cref="CopilotClient"/> (one bundled CLI process) and runs one
    /// independent SDK session per environment in parallel.  Mirrors the parallel-track
    /// behaviour of <c>/fleet</c> in defect-scout.agent.md Step 2b without requiring any
    /// external CLI subprocess or .agent.md file discovery.
    /// </summary>
    private async Task<List<TestResult>> RunParallelSdkAsync(
        StructuredTestPlan plan,
        IReadOnlyList<KineticEnvironment> environments,
        string screenshotBaseDir,
        string resultsDir,
        PlaywrightOptions opts,
        IReadOnlyList<IProgress<EnvironmentProgress>> progressList,
        CancellationToken ct)
    {
        _log.Information("Parallel SDK dispatch: {Count} environments for ticket {Ticket}",
            environments.Count, plan.Ticket);

        // Initialise all progress cards to Queued before any session starts.
        for (int i = 0; i < environments.Count; i++)
        {
            var queuedProg = new EnvironmentProgress
            {
                EnvName    = environments[i].Name,
                Version    = environments[i].Version,
                Status     = "Queued",
                TotalSteps = plan.Steps.Count,
            };
            queuedProg.Log.Add("⏳ Waiting for SDK session...");
            progressList[i].Report(queuedProg);
        }

        // A single CopilotClient manages one bundled Copilot CLI process.
        // Multiple sessions on the same client run independently and concurrently.
        await using var client = new CopilotClient(CliLocator.BuildClientOptions());
        await client.StartAsync();
        _log.Information("CopilotClient started; dispatching {Count} parallel sessions", environments.Count);

        var tasks = environments.Select((env, i) =>
        {
            var screenshotDir = Path.Combine(screenshotBaseDir, env.VersionSlug, plan.Ticket);
            var resultFile    = Path.Combine(resultsDir, $"{env.EnvSlug}-result.json");
            return RunSdkSessionAsync(client, plan, env, screenshotDir, resultFile, opts, progressList[i], ct);
        }).ToList();

        var results = await Task.WhenAll(tasks);
        _log.Information("All {Count} parallel SDK sessions completed", results.Length);
        return [..results];
    }

    // ── Core SDK session (shared by parallel + sequential + direct RunAsync) ─

    /// <summary>
    /// Runs a single defect-scout-env-tester session on an existing <see cref="CopilotClient"/>.
    /// The env-tester instructions from <see cref="AgentPrompts.EnvTester"/> are embedded
    /// directly via <see cref="SystemMessageMode.Replace"/> — the SDK equivalent of the
    /// <c>defect-scout-env-tester.agent.md</c> system prompt used by the repo-based agent.
    /// </summary>
    private async Task<TestResult> RunSdkSessionAsync(
        CopilotClient client,
        StructuredTestPlan plan,
        KineticEnvironment env,
        string screenshotDir,
        string resultFile,
        PlaywrightOptions opts,
        IProgress<EnvironmentProgress>? progress,
        CancellationToken ct)
    {
        Directory.CreateDirectory(screenshotDir);

        var prog = new EnvironmentProgress
        {
            EnvName    = env.Name,
            Version    = env.Version,
            Status     = "Running",
            TotalSteps = plan.Steps.Count,
        };

        // prog.Log is the lock root — must be the same object locked in RunningViewModel
        var logLock = prog.Log;

        void Report(string msg)
        {
            lock (logLock) { prog.Log.Add(msg); }
            progress?.Report(prog);
        }

        if (File.Exists(resultFile)) File.Delete(resultFile);

        _log.Information("Starting SDK session for {Env} (v{Version})", env.Name, env.Version);
        Report($"Connecting Copilot SDK for {env.Name}...");

        try
        {
            await using var session = await client.CreateSessionAsync(new SessionConfig
            {
                Model = "claude-sonnet-4.6",
                Streaming = true,
                OnPermissionRequest = PermissionHandler.ApproveAll,
                // Exclude ask_user so the agent never blocks waiting for human input.
                // This is the SDK equivalent of running the CLI with --no-interactive.
                ExcludedTools = ["ask_user"],
                WorkingDirectory = screenshotDir,
                SystemMessage = new SystemMessageConfig
                {
                    // Replace replaces the entire system prompt — equivalent to the
                    // defect-scout-env-tester.agent.md system message in the repo-based agent.
                    Mode    = SystemMessageMode.Replace,
                    Content = AgentPrompts.EnvTester,
                },
            });

            // Gate SessionIdleEvent on `sent`: a freshly-created session fires
            // SessionIdleEvent immediately (idle before any message). Only
            // mark TCS complete once we've actually dispatched the prompt.
            bool sent = false;
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

            using var sub = session.On(evt =>
            {
                switch (evt)
                {
                    case AssistantMessageDeltaEvent delta:
                    {
                        var chunk = delta.Data.DeltaContent;
                        if (!string.IsNullOrEmpty(chunk))
                        {
                            Report(chunk);
                            var sm = s_stepRx.Match(chunk);
                            if (sm.Success && int.TryParse(sm.Groups[1].Value, out int sn) && sn > prog.CurrentStep)
                            {
                                prog.CurrentStep   = sn;
                                prog.CurrentAction = chunk;
                                progress?.Report(prog);
                            }
                            var ssm = s_ssRx.Match(chunk);
                            if (ssm.Success)
                            {
                                var ssPath = Path.Combine(screenshotDir, ssm.Value);
                                if (File.Exists(ssPath)) { prog.LatestScreenshot = ssPath; progress?.Report(prog); }
                            }
                        }
                        break;
                    }
                    case AssistantMessageEvent msg:
                        // The complete message fires alongside (or instead of) delta events.
                        // Surface it to the UI as a single log entry so the card always shows
                        // something meaningful even when streaming deltas are suppressed.
                        _log.Debug("[{Env}] AssistantMessage turn ({Len} chars): {Preview}",
                            env.Name,
                            (msg.Data.Content ?? string.Empty).Length,
                            (msg.Data.Content ?? string.Empty).Length > 200
                                ? (msg.Data.Content!)[..200] + "…"
                                : msg.Data.Content ?? "(empty)");
                        if (!string.IsNullOrEmpty(msg.Data.Content))
                            Report(msg.Data.Content);
                        break;
                    case ToolExecutionStartEvent toolStart:
                        // Surface which tool/command is about to run (e.g. run_in_terminal, read_file)
                        Report($"▶ {toolStart.Data.ToolName}");
                        break;
                    case ToolExecutionPartialResultEvent partial:
                        // Streaming output of the running tool — this is the actual
                        // playwright-cli / Invoke-RestMethod terminal output.
                        if (!string.IsNullOrEmpty(partial.Data.PartialOutput))
                        {
                            Report(partial.Data.PartialOutput);
                            // Check partial output for screenshot filenames
                            var ssm2 = s_ssRx.Match(partial.Data.PartialOutput);
                            if (ssm2.Success)
                            {
                                var ssPath = Path.Combine(screenshotDir, ssm2.Value);
                                if (File.Exists(ssPath)) { prog.LatestScreenshot = ssPath; progress?.Report(prog); }
                            }
                            // Detect step numbers in partial output
                            var sm2 = s_stepRx.Match(partial.Data.PartialOutput);
                            if (sm2.Success && int.TryParse(sm2.Groups[1].Value, out int sn2) && sn2 > prog.CurrentStep)
                            {
                                prog.CurrentStep   = sn2;
                                prog.CurrentAction = partial.Data.PartialOutput;
                                progress?.Report(prog);
                            }
                        }
                        break;
                    case ToolExecutionCompleteEvent toolDone:
                        Report(toolDone.Data.Success ? "✓ done" : "✗ tool error");
                        break;
                    case SessionIdleEvent:
                        // SessionIdleEvent fires after the entire agentic loop is done
                        // (model + all tool calls). Guard on `sent` to skip the initial
                        // idle that fires before we dispatch the prompt.
                        _log.Debug("[{Env}] SessionIdle (sent={Sent}) — resolving TCS", env.Name, sent);
                        if (sent) tcs.TrySetResult(string.Empty);
                        break;
                    case SessionErrorEvent err:
                        _log.Error("[{Env}] Session error: {Msg}", env.Name, err.Data.Message);
                        tcs.TrySetException(new InvalidOperationException(
                            $"Copilot session error: {err.Data.Message}"));
                        break;
                }
            });

            using var reg = ct.Register(() => tcs.TrySetCanceled(ct));

            var prompt = AgentPrompts.BuildEnvTesterPrompt(plan, env, screenshotDir, resultFile, opts);
            _log.Debug("[{Env}] Sending prompt ({Len} chars)", env.Name, prompt.Length);
            sent = true;  // set BEFORE SendAsync so any immediate idle after send is captured
            await session.SendAsync(new MessageOptions { Prompt = prompt });
            await tcs.Task;

            _log.Information("[{Env}] SDK session complete. Result file present: {Present}",
                env.Name, File.Exists(resultFile));
        }
        catch (OperationCanceledException) { Report("Cancelled."); throw; }
        catch (Exception ex)
        {
            _log.Error(ex, "[{Env}] SDK session failed", env.Name);
            Report($"Copilot invocation error: {ex.Message}");
            return BuildErrorResult(env, ex.Message);
        }
        finally
        {
            prog.Status = File.Exists(resultFile) ? "Done" : "Error";
            progress?.Report(prog);
        }

        return await ReadResultFileAsync(resultFile, env, ct);
    }

    // ── Step 2b: Fleet Dispatch ─────────────────────────────────────────────

    /// <summary>
    /// Builds a single /fleet prompt and dispatches it via one copilot CLI call.
    /// Polls result files during execution to update per-environment progress cards.
    /// Mirrors Step 2b + 2c of defect-scout.agent.md.
    // ── Sequential (for explicit ordered execution) ────────────────────────

    private async Task<List<TestResult>> RunSequentialAsync(
        StructuredTestPlan plan,
        IReadOnlyList<KineticEnvironment> environments,
        string screenshotBaseDir,
        string resultsDir,
        PlaywrightOptions opts,
        IReadOnlyList<IProgress<EnvironmentProgress>> progressList,
        CancellationToken ct)
    {
        _log.Information("Sequential dispatch: {Count} environments for ticket {Ticket}",
            environments.Count, plan.Ticket);

        // Share one client across all sequential sessions to avoid spawning multiple CLI processes.
        await using var client = new CopilotClient(CliLocator.BuildClientOptions());
        await client.StartAsync();

        var results = new List<TestResult>();
        for (int i = 0; i < environments.Count; i++)
        {
            var env           = environments[i];
            var screenshotDir = Path.Combine(screenshotBaseDir, env.VersionSlug, plan.Ticket);
            var resultFile    = Path.Combine(resultsDir, $"{env.EnvSlug}-result.json");

            var initProg = new EnvironmentProgress
            {
                EnvName    = env.Name,
                Version    = env.Version,
                Status     = "Waiting",
                TotalSteps = plan.Steps.Count,
            };
            initProg.Log.Add($"⏳ Queued ({i + 1}/{environments.Count})...");
            progressList[i].Report(initProg);

            var result = await RunSdkSessionAsync(
                client, plan, env, screenshotDir, resultFile, opts, progressList[i], ct);
            results.Add(result);
        }
        return results;
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static async Task<TestResult> ReadResultFileAsync(
        string resultFile, KineticEnvironment env, CancellationToken ct)
    {
        if (!File.Exists(resultFile))
            return BuildErrorResult(env, "Copilot env-tester produced no result file.");
        try
        {
            var json = await File.ReadAllTextAsync(resultFile, ct);
            var start = json.IndexOf('{');
            if (start > 0) json = json[start..];
            return JsonSerializer.Deserialize<TestResult>(json, s_jsonOpts)
                   ?? BuildErrorResult(env, "Null result from env-tester.");
        }
        catch (Exception ex)
        {
            return BuildErrorResult(env, $"Could not parse TestResult: {ex.Message}");
        }
    }

    private static TestResult BuildErrorResult(KineticEnvironment env, string error) => new()
    {
        EnvName = env.Name,
        Version = env.Version,
        Result  = "ERROR",
        Error   = error,
        Notes   = $"Test aborted: {error}",
    };
}
