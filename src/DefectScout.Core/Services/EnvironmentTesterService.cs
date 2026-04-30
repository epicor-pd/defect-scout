using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using DefectScout.Core.Models;
using DefectScout.Core.Prompts;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.AI;
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
        AgentRuntimeOptions? runtime = null,
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

        runtime ??= new AgentRuntimeOptions();
        if (runtime.IsLocalOllama)
        {
            onDispatchMode?.Invoke("local-parallel");
            return await RunParallelLocalAsync(
                plan, environments, screenshotBaseDir, resultsDir, opts, progressList, runtime, ct);
        }

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
        AgentRuntimeOptions? runtime = null,
        CancellationToken ct = default)
    {
        runtime ??= new AgentRuntimeOptions();
        if (runtime.IsLocalOllama)
            return await RunLocalAgentAsync(plan, env, screenshotDir, resultFile, opts, runtime, progress, ct);

        // For a single-env direct invocation, create a dedicated client.
        var clientOptions = await Task.Run(() => CliLocator.BuildClientOptions());
        await using var client = new CopilotClient(clientOptions);
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
        var clientOptions = await Task.Run(() => CliLocator.BuildClientOptions());
        await using var client = new CopilotClient(clientOptions);
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

    // ── Parallel local Agent Framework dispatch ───────────────────────────

    private async Task<List<TestResult>> RunParallelLocalAsync(
        StructuredTestPlan plan,
        IReadOnlyList<KineticEnvironment> environments,
        string screenshotBaseDir,
        string resultsDir,
        PlaywrightOptions opts,
        IReadOnlyList<IProgress<EnvironmentProgress>> progressList,
        AgentRuntimeOptions runtime,
        CancellationToken ct)
    {
        var maxConcurrent = Math.Clamp(runtime.MaxConcurrentEnvTesters, 1, Math.Max(1, environments.Count));
        _log.Information("Local Ollama dispatch: {Count} environments, maxConcurrent={Max}, model={Model}",
            environments.Count, maxConcurrent, runtime.EnvTesterModel);

        for (int i = 0; i < environments.Count; i++)
        {
            var queuedProg = new EnvironmentProgress
            {
                EnvName    = environments[i].Name,
                Version    = environments[i].Version,
                Status     = "Queued",
                TotalSteps = plan.Steps.Count,
            };
            queuedProg.Log.Add("Waiting for local Ollama agent...");
            progressList[i].Report(queuedProg);
        }

        using var gate = new SemaphoreSlim(maxConcurrent);
        var tasks = environments.Select(async (env, i) =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var screenshotDir = Path.Combine(screenshotBaseDir, env.VersionSlug, plan.Ticket);
                var resultFile    = Path.Combine(resultsDir, $"{env.EnvSlug}-result.json");
                return await RunLocalAgentAsync(
                    plan, env, screenshotDir, resultFile, opts, runtime, progressList[i], ct);
            }
            finally
            {
                gate.Release();
            }
        }).ToList();

        var results = await Task.WhenAll(tasks);
        _log.Information("All {Count} local Ollama sessions completed", results.Length);
        return [..results];
    }

    private async Task<TestResult> RunLocalAgentAsync(
        StructuredTestPlan plan,
        KineticEnvironment env,
        string screenshotDir,
        string resultFile,
        PlaywrightOptions opts,
        AgentRuntimeOptions runtime,
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
        var logLock = prog.Log;

        void Report(string msg)
        {
            if (string.IsNullOrWhiteSpace(msg))
                return;

            _log.Debug("[{Env}] Local progress: {Message}", env.Name, Limit(SanitizeForLog(msg), 1000));
            lock (logLock) { prog.Log.Add(msg); }

            var sm = s_stepRx.Match(msg);
            if (sm.Success && int.TryParse(sm.Groups[1].Value, out int sn) && sn > prog.CurrentStep)
            {
                prog.CurrentStep = sn;
                prog.CurrentAction = msg;
            }

            var ssm = s_ssRx.Match(msg);
            if (ssm.Success)
            {
                var ssPath = Path.Combine(screenshotDir, ssm.Value);
                if (File.Exists(ssPath))
                    prog.LatestScreenshot = ssPath;
            }

            progress?.Report(prog);
        }

        string SanitizeForLog(string value)
        {
            var sanitized = value.ReplaceLineEndings(" ");
            if (!string.IsNullOrWhiteSpace(env.Password))
                sanitized = sanitized.Replace(env.Password, "<redacted>", StringComparison.Ordinal);
            if (!string.IsNullOrWhiteSpace(env.ApiKey))
                sanitized = sanitized.Replace(env.ApiKey, "<redacted>", StringComparison.Ordinal);
            return sanitized;
        }

        if (File.Exists(resultFile)) File.Delete(resultFile);

        _log.Information("Starting local Ollama env tester for {Env} (v{Version})",
            env.Name, env.Version);
        Report($"Connecting local Ollama tester to {runtime.OllamaEndpoint} using {runtime.EnvTesterModel}...");

        try
        {
            var operationTimeout = opts.TimeoutDuration;
            Report($"Operation timeout: {(int)operationTimeout.TotalMilliseconds:N0} ms");
            using var toolsHost = new LocalEnvTesterTools(
                env, screenshotDir, resultFile, opts, operationTimeout, Report, ct);

            using var ollama = LocalOllamaClientFactory.Create(
                runtime.OllamaEndpoint,
                runtime.EnvTesterModel,
                operationTimeout);
            var functionClient = new FunctionInvokingChatClient(
                (IChatClient)ollama.Client,
                loggerFactory: null,
                functionInvocationServices: null)
            {
                IncludeDetailedErrors = true,
                MaximumIterationsPerRequest = Math.Clamp(runtime.MaxToolIterations, 1, 200),
                TerminateOnUnknownCalls = false,
            };

            var tools = toolsHost.CreateTools();
            var localMaxOutput = Math.Min(
                AgentRuntimeOptions.NormalizeOllamaMaxOutputTokens(runtime.OllamaMaxOutputTokens),
                2048);
            var options = LocalOllamaOptionsFactory.Create(
                runtime,
                runtime.EnvTesterModel,
                localMaxOutput,
                tools);
            var prompt = AgentPrompts.BuildLocalEnvTesterPrompt(plan, env, screenshotDir, resultFile, opts);
            var messages = new[]
            {
                new ChatMessage(ChatRole.System, AgentPrompts.LocalEnvTester),
                new ChatMessage(ChatRole.User, prompt),
            };

            Report($"Ollama options: {LocalOllamaOptionsFactory.Describe(runtime, runtime.EnvTesterModel, localMaxOutput)}");
            _log.Information(
                "[{Env}] Local Ollama request starting: model={Model}, promptChars={PromptChars}, tools={ToolCount}, {Options}",
                env.Name,
                runtime.EnvTesterModel,
                prompt.Length,
                tools.Count,
                LocalOllamaOptionsFactory.Describe(runtime, runtime.EnvTesterModel, localMaxOutput));

            var stopwatch = Stopwatch.StartNew();
            var response = await AwaitWithHeartbeatAsync(
                functionClient.GetResponseAsync(messages, options, ct),
                TimeSpan.FromSeconds(30),
                elapsed =>
                {
                    var msg = $"Waiting for local model/tool loop after {elapsed:mm\\:ss}...";
                    _log.Information("[{Env}] Local Ollama heartbeat: model={Model}, elapsedMs={ElapsedMs}",
                        env.Name,
                        runtime.EnvTesterModel,
                        (long)elapsed.TotalMilliseconds);
                    Report(msg);
                },
                ct);
            _log.Information(
                "[{Env}] Local Ollama request completed: model={Model}, elapsedMs={ElapsedMs}, responseChars={ResponseChars}",
                env.Name,
                runtime.EnvTesterModel,
                stopwatch.ElapsedMilliseconds,
                response.Text.Length);
            Report(Limit(response.Text, 12000));

            if (!File.Exists(resultFile))
                await TryWriteResultFileFromResponseAsync(response.Text, resultFile, env, ct);

            _log.Information("[{Env}] Local Ollama session complete. Result file present: {Present}",
                env.Name, File.Exists(resultFile));
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            var message =
                $"Local agent exceeded the configured operation timeout of {(int)opts.TimeoutDuration.TotalMilliseconds:N0} ms.";
            _log.Error(ex, "[{Env}] Local Ollama session timed out", env.Name);
            Report(message);
            return BuildErrorResult(env, message);
        }
        catch (OperationCanceledException) { Report("Cancelled."); throw; }
        catch (Exception ex)
        {
            _log.Error(ex, "[{Env}] Local Agent Framework session failed", env.Name);
            Report($"Local agent invocation error: {ex.Message}");
            return BuildErrorResult(env, ex.Message);
        }
        finally
        {
            prog.Status = File.Exists(resultFile) ? "Done" : "Error";
            progress?.Report(prog);
        }

        return await ReadResultFileAsync(resultFile, env, ct);
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
        var clientOptions = await Task.Run(() => CliLocator.BuildClientOptions());
        await using var client = new CopilotClient(clientOptions);
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
            return BuildErrorResult(env, "Env-tester produced no result file.");
        try
        {
            var json = await File.ReadAllTextAsync(resultFile, ct);
            json = JsonResponseParser.ExtractFirstObject(json);
            return JsonSerializer.Deserialize<TestResult>(json, s_jsonOpts)
                   ?? BuildErrorResult(env, "Null result from env-tester.");
        }
        catch (Exception ex)
        {
            return BuildErrorResult(env, $"Could not parse TestResult: {ex.Message}");
        }
    }

    private static async Task TryWriteResultFileFromResponseAsync(
        string rawResponse,
        string resultFile,
        KineticEnvironment env,
        CancellationToken ct)
    {
        try
        {
            var json = JsonResponseParser.ExtractFirstObject(rawResponse);
            var result = JsonSerializer.Deserialize<TestResult>(json, s_jsonOpts);
            if (result is null)
                return;

            if (string.IsNullOrWhiteSpace(result.EnvName))
                result.EnvName = env.Name;
            if (string.IsNullOrWhiteSpace(result.Version))
                result.Version = env.Version;

            Directory.CreateDirectory(Path.GetDirectoryName(resultFile) ?? ".");
            await File.WriteAllTextAsync(resultFile, JsonSerializer.Serialize(result, s_jsonOpts), ct);
        }
        catch
        {
            // The caller will surface the missing result file as an ERROR TestResult.
        }
    }

    private static string Limit(string value, int maxChars) =>
        value.Length <= maxChars ? value : value[..maxChars] + "\n...<truncated>";

    private static TestResult BuildErrorResult(KineticEnvironment env, string error) => new()
    {
        EnvName = env.Name,
        Version = env.Version,
        Result  = "ERROR",
        Error   = error,
        Notes   = $"Test aborted: {error}",
    };
}
