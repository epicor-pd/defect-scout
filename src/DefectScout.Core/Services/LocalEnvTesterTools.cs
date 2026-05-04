using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Globalization;
using System.Threading;
using System.Collections.Generic;
using System.Linq;
using DefectScout.Core.Models;
using Microsoft.Extensions.AI;
using Serilog;

namespace DefectScout.Core.Services;

internal sealed class LocalEnvTesterTools : IDisposable
{
    private static readonly JsonSerializerOptions s_jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private static readonly ILogger _log = AppLogger.For<LocalEnvTesterTools>();

    private record ToolState
    {
        public string? Command { get; init; }
        public bool Success { get; init; }
        public int ExitCode { get; init; }
        public string? Stdout { get; init; }
        public string? Stderr { get; init; }
        public string? SnapshotPath { get; init; }
        public string? ConsolePath { get; init; }
        public List<string>? ScreenshotPaths { get; init; }
        public List<string>? DetectedIssues { get; init; }
        public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    }

    private ToolState? _lastToolState;
    private readonly object _stateLock = new();

    private readonly KineticEnvironment _env;
    private readonly string _screenshotDir;
    private readonly string _resultFile;
    private readonly PlaywrightOptions _opts;
    private readonly TimeSpan _operationTimeout;
    private readonly CancellationToken _ct;
    private readonly Action<string> _report;
    private readonly HttpClient _httpClient;
    private int _actionCounter = 0;
    private readonly object _actionLock = new();
    private bool _sessionOpened = false;
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private string? _playwrightHelpRaw;
    private readonly HashSet<string> _availablePlaywrightCommands = new(StringComparer.OrdinalIgnoreCase);
    private bool _supportsIgnoreHttps = false;
    private bool _supportsProfile = false;
    private readonly object _helpLock = new();
    private readonly string _defaultSessionOption;

    public LocalEnvTesterTools(
        KineticEnvironment env,
        string screenshotDir,
        string resultFile,
        PlaywrightOptions opts,
        TimeSpan operationTimeout,
        Action<string> report,
        CancellationToken ct)
    {
        _env = env;
        _opts = opts;
        _screenshotDir = screenshotDir;
        _resultFile = resultFile;
        _operationTimeout = operationTimeout;
        _ct = ct;
        _report = report;

        // Generate a default session option for Playwright invocations when caller doesn't supply one.
        var sessionIdBase = SanitizeSessionId((env?.Name ?? "env") + "-" + (env?.Version ?? "v") + "-" + Guid.NewGuid().ToString("n").Substring(0, 8));
        _defaultSessionOption = $"-s={sessionIdBase}";

        var handler = new HttpClientHandler();
        if (opts.IgnoreHttpsErrors)
        {
            handler.ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        _httpClient = new HttpClient(handler)
        {
            Timeout = operationTimeout,
        };
    }

    public IList<AITool> CreateTools()
    {
        _log.Debug("[{Env}] Creating LocalEnvTester tools", _env?.Name);

        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(
                (Func<string, Task<string>>)RunPlaywrightAsync,
                new AIFunctionFactoryOptions
                {
                    Name = "run_playwright",
                    Description = "Runs one playwright-cli command. Pass only the arguments after playwright-cli. Do not use npx playwright test.",
                }),
            AIFunctionFactory.Create(
                (Func<string>)GetEnvironmentLogin,
                new AIFunctionFactoryOptions
                {
                    Name = "get_environment_login",
                    Description = "Returns the current environment login fields when the Kinetic login page requires explicit username, password, or company values.",
                }),
            AIFunctionFactory.Create(
                (Func<string, string, string?, string?, Task<string>>)InvokeKineticRestAsync,
                new AIFunctionFactoryOptions
                {
                    Name = "invoke_kinetic_rest",
                    Description = "Calls the configured Kinetic REST API using the current environment credentials and saves the response as evidence.",
                }),
            AIFunctionFactory.Create(
                (Func<string, Task<string>>)WriteResultFileAsync,
                new AIFunctionFactoryOptions
                {
                    Name = "write_result_file",
                    Description = "Validates and writes the final TestResult JSON to the exact resultFile path for this environment.",
                }),
            AIFunctionFactory.Create(
                (Func<string>)ListEvidenceFiles,
                new AIFunctionFactoryOptions
                {
                    Name = "list_evidence_files",
                    Description = "Lists screenshot and API-response evidence files already created in screenshotDir.",
                }),
            AIFunctionFactory.Create(
                (Func<Task<string>>)ReadLatestSnapshotAsync,
                new AIFunctionFactoryOptions
                {
                    Name = "read_latest_snapshot",
                    Description = "Reads the most recent Playwright snapshot YAML from the session (.playwright-cli/page-*.yml).",
                }),
            AIFunctionFactory.Create(
                (Func<Task<string>>)ReadLatestConsoleAsync,
                new AIFunctionFactoryOptions
                {
                    Name = "read_latest_console",
                    Description = "Reads the most recent Playwright console log (.playwright-cli/console-*.log).",
                }),
            AIFunctionFactory.Create(
                (Func<string>)GetLatestScreenshot,
                new AIFunctionFactoryOptions
                {
                    Name = "get_latest_screenshot",
                    Description = "Returns the latest screenshot file path from the screenshot directory, if any.",
                }),
            AIFunctionFactory.Create(
                (Func<string>)GetLastToolState,
                new AIFunctionFactoryOptions
                {
                    Name = "get_last_tool_state",
                    Description = "Returns the structured last tool invocation state (command, stdout, stderr, snapshots, screenshots, detected issues).",
                }),
        };

        _log.Debug("[{Env}] Created {Count} tools", _env?.Name, tools.Count);
        return tools;
    }

    public string GetEnvironmentLogin()
    {
        _log.Debug("[{Env}] get_environment_login called (UserConfigured={HasUser}, ApiKeyConfigured={HasKey})",
            _env?.Name, !string.IsNullOrWhiteSpace(_env?.Username), !string.IsNullOrWhiteSpace(_env?.ApiKey));

        var json = JsonSerializer.Serialize(new
        {
            _env.Username,
            _env.Password,
            _env.Company,
        }, s_jsonOpts);

        _log.Debug("[{Env}] get_environment_login produced JSON length {Len}", _env?.Name, json?.Length ?? 0);
        return json;
    }

    public async Task<string?> ProbeBrowserLoginFailureAsync()
    {
        var webUrlValidationError = ValidateConfiguredWebUrl();
        if (!string.IsNullOrWhiteSpace(webUrlValidationError))
            return webUrlValidationError;

        if (string.IsNullOrWhiteSpace(_env?.Username) || string.IsNullOrWhiteSpace(_env?.Password))
            return null;

        var tokenUri = TryBuildTokenResourceUri();
        if (tokenUri is null)
            return null;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, tokenUri);
            var bytes = Encoding.ASCII.GetBytes($"{_env.Username}:{_env.Password}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(bytes));
            request.Headers.Accept.ParseAdd("application/json");

            using var response = await _httpClient.SendAsync(request, _ct);
            var body = await response.Content.ReadAsStringAsync(_ct);

            if (response.StatusCode == HttpStatusCode.Unauthorized &&
                body.Contains("Invalid username or password", StringComparison.OrdinalIgnoreCase))
            {
                return $"Configured browser login was rejected by {tokenUri} (HTTP 401 Invalid username or password). The Kinetic browser login flow uses username/password, not apiKey.";
            }
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "[{Env}] Browser login probe failed for {Uri}", _env?.Name, tokenUri);
        }

        return null;
    }

    public async Task<string> RunPlaywrightAsync(string arguments)
    {
        _log.Debug("[{Env}] run_playwright invoked; rawArgs={Args}", _env?.Name, Limit(arguments ?? string.Empty, 400));

        var originalArgs = NormalizePlaywrightArguments(arguments);
        var args = originalArgs;

        // Ensure we have playwright help info so we choose supported commands/flags.
        try { await QueryPlaywrightHelpAsync(); } catch (Exception ex) { _report($"Could not query playwright help: {ex.Message}"); }

        // Determine command name and lists of commands we should treat specially.
        var commandsSupportingIgnore = new[] { "goto", "click", "fill", "screenshot", "snapshot", "type", "select", "upload", "check", "uncheck", "hover", "dblclick" };
        var commandsRequiringSession = new[] { "goto", "click", "fill", "screenshot", "snapshot", "type", "select", "upload", "check", "uncheck", "hover", "dblclick" };

        var maxAttempts = _opts?.MaxAutoHealAttempts > 0 ? _opts.MaxAutoHealAttempts : 3;
        var attempt = 0;
        var triedNpxFallback = false;
        var removedIgnoreFlag = false;

        CommandResult? lastResult = null;
        string lastOutput = string.Empty;

        while (attempt < maxAttempts)
        {
            attempt++;

            _log.Debug("[{Env}] run_playwright attempt {Attempt} of {Max}", _env?.Name, attempt, maxAttempts);

            var cmdMatch = Regex.Match(args ?? string.Empty, "^\\s*(\\S+)", RegexOptions.IgnoreCase);
            var cmdName = cmdMatch.Success ? cmdMatch.Groups[1].Value.ToLowerInvariant() : string.Empty;

            // Normalize common patterns where callers embed the command name inside
            // an option value (e.g. `--grep "open"` or `-g open`) so we don't
            // mis-detect option values as the intended `open` command.
            args = Regex.Replace(args ?? string.Empty,
                "(?:--grep|-g)(?:\\s*=\\s*|\\s+)(?:\"|')?open(?:\"|')?",
                "open",
                RegexOptions.IgnoreCase);

            // Tokenize arguments and determine primary command token robustly.
            var tokens = SplitArgsToTokens(args ?? string.Empty);

            // Normalize accidental patterns like `--grep "open"` -> `open` so we
            // don't mis-detect option values as command tokens.
            for (int i = 0; i < tokens.Count; i++)
            {
                var t = tokens[i];
                if (t.StartsWith("--grep", StringComparison.OrdinalIgnoreCase) || string.Equals(t, "-g", StringComparison.OrdinalIgnoreCase))
                {
                    if (t.Contains("="))
                    {
                        var parts = t.Split('=', 2);
                        var val = parts.Length > 1 ? parts[1].Trim('"', '\'') : string.Empty;
                        if (string.Equals(val, "open", StringComparison.OrdinalIgnoreCase))
                            tokens[i] = "open";
                    }
                    else if (i + 1 < tokens.Count)
                    {
                        var val = tokens[i + 1].Trim('"', '\'');
                        if (string.Equals(val, "open", StringComparison.OrdinalIgnoreCase))
                        {
                            tokens[i] = "open";
                            tokens.RemoveAt(i + 1);
                        }
                    }
                }
            }

            // Reconstruct args from normalized tokens for downstream processing.
            // Tokens starting with '#' must be double-quoted: PowerShell treats '#' as a
            // comment character in mid-line command arguments, so `click #details-button`
            // silently drops the target. Similarly quote '$' (variable expansion risk).
            args = string.Join(" ", tokens.Select(t =>
                (t.Contains(' ') || t.StartsWith("#") || t.StartsWith("$"))
                ? "\"" + t + "\""
                : t));

            var knownCommands = new[] { "open", "goto", "click", "fill", "screenshot", "snapshot", "type", "select", "upload", "check", "uncheck", "hover", "dblclick" };
            var primaryCmd = FindPrimaryCommandFromTokens(tokens, knownCommands) ?? cmdName;

            // If the caller requested `open` (possibly with a leading -s= session option),
            // route it through our helper so we can ensure the appropriate flags (e.g. --ignore-https-errors)
            // and supply the session token in the correct position.
            if (string.Equals(primaryCmd, "open", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    // Normalize session option and the remainder to construct a canonical open command.
                    var sessionOpt = SessionOptionOrDefault(args);
                    var remainder = args ?? string.Empty;
                    var existingSession = GetSessionOption(remainder);
                    if (!string.IsNullOrWhiteSpace(existingSession))
                        remainder = Regex.Replace(remainder, Regex.Escape(existingSession), "", RegexOptions.IgnoreCase).Trim();

                    // Tokenize and remove the `open` token if present.
                    var remTokens = SplitArgsToTokens(remainder)
                        .Where(t => !string.Equals(t, "open", StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    // Sanitize tokens: only permit the flags that playwright-cli open actually
                    // accepts (whitelist). Anything else — including --ignore-https-errors,
                    // --ignore-certificate-errors, --no-sandbox, --headless, etc. — is stripped
                    // to prevent "Unknown option" failures. SSL bypass is handled via the
                    // .playwright/cli.config.json file written by EnsurePlaywrightConfigAsync().
                    string positionalUrl = string.Empty;
                    var sanitizedTokens = new List<string>();
                    var knownOpenFlags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                        { "browser", "config", "headed", "persistent", "profile" };

                    for (int i = 0; i < remTokens.Count; i++)
                    {
                        var t = remTokens[i];
                        if (string.IsNullOrWhiteSpace(t)) continue;
                        if (t.StartsWith("-", StringComparison.Ordinal))
                        {
                            var stripped = t.TrimStart('-').ToLowerInvariant();
                            // Extract --url=<value> (or --url <value>) as the positional URL.
                            if (stripped.StartsWith("url"))
                            {
                                var eq = t.IndexOf('=');
                                if (eq >= 0)
                                {
                                    var val = t[(eq + 1)..].Trim('"', '\'');
                                    if (!string.IsNullOrWhiteSpace(val)) positionalUrl = val;
                                }
                                else if (i + 1 < remTokens.Count && !remTokens[i + 1].StartsWith("-"))
                                {
                                    positionalUrl = remTokens[i + 1].Trim('"', '\'');
                                    i++;
                                }
                                continue;
                            }
                            // Only known-good flags are forwarded to playwright-cli open.
                            var flagName = stripped.Contains('=') ? stripped[..stripped.IndexOf('=')] : stripped;
                            if (!knownOpenFlags.Contains(flagName))
                            {
                                // Skip the associated value token if flag is in --key value form.
                                if (!t.Contains('=') && i + 1 < remTokens.Count && !remTokens[i + 1].StartsWith("-"))
                                    i++;
                                continue;
                            }
                        }

                        sanitizedTokens.Add(t);
                    }

                    var remainderSanitized = string.Join(" ", sanitizedTokens).Trim();
                    if (!string.IsNullOrWhiteSpace(positionalUrl))
                    {
                        if (positionalUrl.Contains(' ')) positionalUrl = '"' + positionalUrl + '"';
                        remainderSanitized = string.IsNullOrWhiteSpace(remainderSanitized)
                            ? positionalUrl
                            : remainderSanitized + " " + positionalUrl;
                    }

                    // Optionally include --ignore-https-errors on open when configured and supported.
                    var ignoreFlag = (_opts?.IgnoreHttpsErrors == true && _supportsIgnoreHttps) ? "--ignore-https-errors " : string.Empty;

                    var openArgs = (string.IsNullOrWhiteSpace(sessionOpt) ? string.Empty : sessionOpt + " ") +
                                   "open " + ignoreFlag + (string.IsNullOrWhiteSpace(remainderSanitized) ? string.Empty : remainderSanitized);

                    // Pre-write playwright config for SSL bypass (ignoreHTTPSErrors).
                    await EnsurePlaywrightConfigAsync();

                    var openCmd = $"$ErrorActionPreference = 'Continue'; playwright-cli {openArgs}";
                    _report($"(open-via-helper) playwright-cli {openArgs}");
                    string executedOpenCmd = openCmd;
                    var openRes = await LocalProcessRunner.RunPowerShellAsync(executedOpenCmd, _screenshotDir, _operationTimeout, _ct);
                    if (LooksLikeCommandMissing(openRes))
                    {
                        executedOpenCmd = $"$ErrorActionPreference = 'Continue'; npx playwright-cli {openArgs}";
                        _report($"npx playwright-cli {openArgs}");
                        openRes = await LocalProcessRunner.RunPowerShellAsync(executedOpenCmd, _screenshotDir, _operationTimeout, _ct);
                    }

                    UpdateLastToolState(executedOpenCmd, openRes);

                    if (openRes.IsSuccess)
                    {
                        _sessionOpened = true;
                        var outText = openRes.ToToolOutput();

                        // playwright-cli returns a file-reference snapshot "[Snapshot](...yml)"
                        // after open <url> instead of inline ARIA. The model cannot read page
                        // structure from a file-ref, so immediately fetch the real inline snapshot.
                        if (!string.IsNullOrWhiteSpace(positionalUrl) &&
                            outText.Contains("[Snapshot]", StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                var snapArgs = (string.IsNullOrWhiteSpace(sessionOpt) ? string.Empty : sessionOpt + " ") + "snapshot";
                                var snapCmd = $"$ErrorActionPreference = 'Continue'; playwright-cli {snapArgs}";
                                _report($"(auto-snapshot after open) playwright-cli {snapArgs}");
                                var snapRes = await LocalProcessRunner.RunPowerShellAsync(snapCmd, _screenshotDir, _operationTimeout, _ct);
                                if (snapRes.IsSuccess && !ContainsPlaywrightOutputError(snapRes))
                                {
                                    var snapText = snapRes.ToToolOutput();
                                    if (!string.IsNullOrWhiteSpace(snapText))
                                    {
                                        // If the snapshot reveals the Chrome SSL interstitial, auto-bypass it
                                        // so the model never sees the interstitial page.
                                        if (snapText.Contains("chrome-error://", StringComparison.OrdinalIgnoreCase) ||
                                            snapText.Contains("ERR_CERT", StringComparison.OrdinalIgnoreCase) ||
                                            snapText.Contains("Your connection is not private", StringComparison.OrdinalIgnoreCase))
                                        {
                                            _report("(auto-ssl-bypass) Chrome SSL interstitial detected after open — clicking #details-button then #proceed-link");
                                            foreach (var bypassStep in new[] { $"click \"#details-button\"", "snapshot", $"click \"#proceed-link\"", "snapshot" })
                                            {
                                                var bArgs = (string.IsNullOrWhiteSpace(sessionOpt) ? string.Empty : sessionOpt + " ") + bypassStep;
                                                var bCmd = $"$ErrorActionPreference = 'Continue'; playwright-cli {bArgs}";
                                                _report($"(auto-ssl-bypass) playwright-cli {bArgs}");
                                                try
                                                {
                                                    var bRes = await LocalProcessRunner.RunPowerShellAsync(bCmd, _screenshotDir, _operationTimeout, _ct);
                                                    UpdateLastToolState(bCmd, bRes);
                                                    if (bypassStep.StartsWith("snapshot") && bRes.IsSuccess && !ContainsPlaywrightOutputError(bRes))
                                                        snapText = bRes.ToToolOutput();
                                                }
                                                catch (Exception bex) { _report($"(auto-ssl-bypass) step failed: {bex.Message}"); }
                                            }
                                        }

                                        outText = snapText;
                                        _log.Debug("[{Env}] auto-snapshot after open replaced file-ref; snapshotLen={Len}", _env?.Name, snapText.Length);
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                _report($"(auto-snapshot after open) failed: {ex.Message}");
                            }
                        }

                        outText = Limit(outText, 12000);
                        _log.Debug("[{Env}] run_playwright(open) succeeded; session opened, outputLen={Len}", _env?.Name, outText.Length);
                        _report(outText);
                        return outText;
                    }

                    // Capture as lastResult and continue retry loop
                    lastResult = openRes;
                    lastOutput = openRes.ToToolOutput();
                    _report($"playwright open attempt failed: {Limit(lastOutput, 2000)}");
                    // brief backoff then continue retry attempts
                    try { await Task.Delay(300, _ct); } catch { }
                    continue;
                }
                catch (Exception ex)
                {
                    _report($"(open-via-helper) failed: {ex.Message}");
                }
            }

            // Append --ignore-https-errors for commands where it is sensible and supported.
            // Determine primary command token (handles leading -s= session tokens) and use it.
            if (_opts?.IgnoreHttpsErrors == true && !args.Contains("--ignore-https-errors", StringComparison.OrdinalIgnoreCase) && _supportsIgnoreHttps)
            {
                if (commandsSupportingIgnore.Contains(primaryCmd))
                    args = args + " --ignore-https-errors";
            }

            // Ensure SSL-bypass config is written before any goto navigation (same as open).
            if (primaryCmd == "goto")
            {
                await EnsurePlaywrightConfigAsync();
            }

            if (!IsSafePlaywrightArguments(args, out var error))
                return $"Rejected playwright command: {error}";

            // If this command needs an open browser session, ensure one is open before issuing it.
            if (commandsRequiringSession.Contains(primaryCmd) && primaryCmd != "open")
            {
                if (!_sessionOpened)
                {
                    try
                    {
                        await _sessionLock.WaitAsync(_ct).ConfigureAwait(false);
                        if (!_sessionOpened)
                        {
                            try
                            {
                                var sessionOpt = SessionOptionOrDefault(args);
                                await EnsureBrowserSessionOpenAsync(sessionOpt).ConfigureAwait(false);
                                _sessionOpened = true;
                            }
                            catch (Exception ex)
                            {
                                _report($"Could not open Playwright session before running '{cmdName}': {ex.Message}");
                            }
                        }
                    }
                    finally
                    {
                        try { _sessionLock.Release(); } catch { }
                    }
                }

                // Ensure the args are prefixed with the session option so Playwright's allowed-roots align
                args = PrependSessionIfMissing(args);

                // Normalize common selector shorthand and malformed selector expressions
                // (e.g. `click button=Login` -> `click getByRole('button', { name: 'Login' })`)
                args = NormalizeClickShorthand(args);
                args = NormalizeSelectorExpressions(args);
            }

            var command = $"$ErrorActionPreference = 'Continue'; playwright-cli {args}";
            _report($"playwright-cli {args}");

            // Normalize compact `fill` invocations (key=value pairs) into multiple
            // Playwright `fill` commands with selector heuristics. This prevents errors
            // like "too many arguments: expected 2" when the model emits `fill a=1 b=2`.
            // IMPORTANT: check tokens AFTER the fill command rather than the full args string.
            // The full string contains the prepended -s=SESSION option whose '=' would
            // incorrectly trigger multi-fill for simple `fill refId value` commands.
            var fillCheckIdx = tokens.FindIndex(t => string.Equals(t.Trim('"', '\''), "fill", StringComparison.OrdinalIgnoreCase));
            bool hasKeyValuePairs = fillCheckIdx >= 0 &&
                tokens.Skip(fillCheckIdx + 1)
                      .Any(t => t.Contains('=') && !Regex.IsMatch(t, @"^-s=", RegexOptions.IgnoreCase));
            if (string.Equals(primaryCmd, "fill", StringComparison.OrdinalIgnoreCase) && hasKeyValuePairs)
            {
                // Parse key=value tokens from the tokenized args rather than a complex regex.
                var pairs = new List<(string key, string value)>();
                var fillIndex = tokens.FindIndex(t => string.Equals(t.Trim('"', '\''), "fill", StringComparison.OrdinalIgnoreCase));
                if (fillIndex >= 0)
                {
                    for (int i = fillIndex + 1; i < tokens.Count; i++)
                    {
                        var tok = tokens[i].Trim();
                        if (string.Equals(tok, "submit", StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (tok.Contains("="))
                        {
                            // CSS attribute selector=value: "input[name=username]=manager"
                            // The selector ends at ']' and the value follows ']='.
                            // Detect: the key contains '[' which means it's a CSS attribute selector.
                            var cssValMatch = Regex.Match(tok, @"^(.+\])=(.*)$");
                            if (cssValMatch.Success && cssValMatch.Groups[1].Value.Contains('['))
                            {
                                var selector = cssValMatch.Groups[1].Value; // e.g. input[name=username]
                                var val = cssValMatch.Groups[2].Value.Trim('"', '\'');
                                pairs.Add((selector, val));
                                continue;
                            }

                            var parts = tok.Split(new[] { '=' }, 2);
                            var key = parts[0];
                            var valStr = parts.Length > 1 ? parts[1].Trim('"', '\'') : string.Empty;
                            pairs.Add((key, valStr));
                        }
                    }
                }

                var wantsSubmit = Regex.IsMatch(args ?? string.Empty, "\\bsubmit\\b", RegexOptions.IgnoreCase);
                CommandResult? aggregateResult = null;
                bool allOk = true;
                var sessionOpt = SessionOptionOrDefault(args);

                // Take a LIVE snapshot of the current page — not a stale cached file — so that
                // selector heuristics are grounded in what is actually visible right now.
                string snapshotText = string.Empty;
                try
                {
                    var liveSnapArgs = (string.IsNullOrWhiteSpace(sessionOpt) ? string.Empty : sessionOpt + " ") + "snapshot";
                    var liveSnapCmd = $"$ErrorActionPreference = 'Continue'; playwright-cli {liveSnapArgs}";
                    var liveSnapRes = await LocalProcessRunner.RunPowerShellAsync(liveSnapCmd, _screenshotDir, _operationTimeout, _ct);
                    if (liveSnapRes.IsSuccess && !ContainsPlaywrightOutputError(liveSnapRes))
                        snapshotText = liveSnapRes.ToToolOutput() ?? string.Empty;
                    else
                        snapshotText = await ReadLatestSnapshotAsync() ?? string.Empty;
                }
                catch
                {
                    snapshotText = await ReadLatestSnapshotAsync() ?? string.Empty;
                }

                // If the page is blank / about:blank / too short to be a real page, navigate to
                // the environment URL now before attempting any fills.
                bool isBlankPage = string.IsNullOrWhiteSpace(snapshotText) ||
                                   snapshotText.Contains("about:blank", StringComparison.OrdinalIgnoreCase) ||
                                   snapshotText.Length < 120;
                if (isBlankPage && !string.IsNullOrWhiteSpace(_env?.WebUrl))
                {
                    _report($"(multi-fill: blank page detected) navigating to environment URL before fills: {_env.WebUrl}");
                    await EnsurePlaywrightConfigAsync();
                    var navArgs = (string.IsNullOrWhiteSpace(sessionOpt) ? string.Empty : sessionOpt + " ") + $"goto {_env.WebUrl}";
                    var navCmd = $"$ErrorActionPreference = 'Continue'; playwright-cli {navArgs}";
                    var navRes = await LocalProcessRunner.RunPowerShellAsync(navCmd, _screenshotDir, _operationTimeout, _ct);
                    UpdateLastToolState(navCmd, navRes);
                    if (navRes.IsSuccess)
                    {
                        // Re-snapshot after navigation
                        var snapAfterNav = (string.IsNullOrWhiteSpace(sessionOpt) ? string.Empty : sessionOpt + " ") + "snapshot";
                        var snapNavCmd = $"$ErrorActionPreference = 'Continue'; playwright-cli {snapAfterNav}";
                        var snapNavRes = await LocalProcessRunner.RunPowerShellAsync(snapNavCmd, _screenshotDir, _operationTimeout, _ct);
                        if (snapNavRes.IsSuccess && !ContainsPlaywrightOutputError(snapNavRes))
                        {
                            var newSnap = snapNavRes.ToToolOutput() ?? string.Empty;
                            // Auto-bypass SSL if needed
                            if (newSnap.Contains("chrome-error://", StringComparison.OrdinalIgnoreCase) ||
                                newSnap.Contains("ERR_CERT", StringComparison.OrdinalIgnoreCase))
                            {
                                _report("(multi-fill ssl-bypass) SSL interstitial after navigation");
                                foreach (var step in new[] { "click \"#details-button\"", "snapshot", "click \"#proceed-link\"", "snapshot" })
                                {
                                    var bArgs = (string.IsNullOrWhiteSpace(sessionOpt) ? string.Empty : sessionOpt + " ") + step;
                                    var bCmd = $"$ErrorActionPreference = 'Continue'; playwright-cli {bArgs}";
                                    try
                                    {
                                        var bRes = await LocalProcessRunner.RunPowerShellAsync(bCmd, _screenshotDir, _operationTimeout, _ct);
                                        if (step.StartsWith("snapshot") && bRes.IsSuccess) newSnap = bRes.ToToolOutput() ?? newSnap;
                                    }
                                    catch { }
                                }
                            }
                            snapshotText = newSnap;
                        }
                    }
                }

                foreach (var (key, val) in pairs)
                {
                    var label = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(key.Replace("-", " ").Replace("_", " "));
                    var selectorsList = new List<string>();

                    // When the key is already a CSS selector (contains '[', '.', '#', or is a
                    // tag[attr=val] form), use it directly and also try a quoted attribute variant.
                    // Skip the human-label heuristics entirely for CSS selectors.
                    bool isCssSelector = key.Contains('[') || key.StartsWith('.') || key.StartsWith('#') ||
                                         key.StartsWith("getBy") || key.StartsWith(">>") ||
                                         Regex.IsMatch(key, @"^[a-z]+\[", RegexOptions.IgnoreCase);
                    if (isCssSelector)
                    {
                        selectorsList.Add(key);
                        // Also try quoting unquoted attribute values: input[name=username] → input[name='username']
                        var quotedSelector = Regex.Replace(key, @"\[(\w+)=([^'\"">\]]+)\]", "[$1='$2']");
                        if (quotedSelector != key) selectorsList.Add(quotedSelector);
                    }
                    else
                    {
                    // If we have a snapshot, prefer selectors that reference accessible names present in it.
                    if (!string.IsNullOrWhiteSpace(snapshotText) &&
                        !snapshotText.StartsWith("No ", StringComparison.OrdinalIgnoreCase) &&
                        !snapshotText.StartsWith("Could not", StringComparison.OrdinalIgnoreCase))
                    {
                        if (snapshotText.IndexOf(label, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            selectorsList.Add($"getByLabel('{label}')");
                            selectorsList.Add($"getByRole('textbox', {{ name: '{label}' }})");
                            selectorsList.Add($"getByPlaceholder('{label}')");
                        }

                        if (snapshotText.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            selectorsList.Add($"input[name=\"{key}\"]");
                            selectorsList.Add($"input[id=\"{key}\"]");
                        }
                    }

                    // Always include sensible fallbacks (preserve order and avoid duplicates).
                    var fallbacks = new[]
                    {
                        $"getByLabel('{label}')",
                        $"getByRole('textbox', {{ name: '{label}' }})",
                        $"getByPlaceholder('{label}')",
                        $"input[name=\"{key}\"]",
                        $"input[id=\"{key}\"]",
                        $"[aria-label=\"{label}\"]",
                    };

                    foreach (var f in fallbacks)
                    {
                        if (!selectorsList.Contains(f)) selectorsList.Add(f);
                    }
                    } // end else (not a CSS selector)

                    bool filled = false;
                    foreach (var sel in selectorsList)
                    {
                        var escapedVal = val.Replace("\"", "\\\"");
                        var fillArgs = (string.IsNullOrWhiteSpace(sessionOpt) ? string.Empty : sessionOpt + " ") + $"fill \"{sel}\" \"{escapedVal}\"";
                        var fillCmd = $"$ErrorActionPreference = 'Continue'; playwright-cli {fillArgs}";
                        var fillRes = await LocalProcessRunner.RunPowerShellAsync(fillCmd, _screenshotDir, _operationTimeout, _ct);
                        UpdateLastToolState(fillCmd, fillRes);
                        if (fillRes.IsSuccess && !ContainsPlaywrightOutputError(fillRes))
                        {
                            aggregateResult = fillRes;
                            filled = true;
                            break;
                        }

                        if (LooksLikeCommandMissing(fillRes))
                        {
                            var fb = $"$ErrorActionPreference = 'Continue'; npx playwright-cli {fillArgs}";
                            fillRes = await LocalProcessRunner.RunPowerShellAsync(fb, _screenshotDir, _operationTimeout, _ct);
                            UpdateLastToolState(fb, fillRes);
                            if (fillRes.IsSuccess && !ContainsPlaywrightOutputError(fillRes))
                            {
                                aggregateResult = fillRes;
                                filled = true;
                                break;
                            }
                        }
                    }

                    if (!filled)
                    {
                        try
                        {
                            // Attempt DOM-eval fallback to set the field value directly when
                            // Playwright refs/selectors did not match. This evaluates a small
                            // JS snippet in the page that searches for input/textarea by
                            // name/id/placeholder/aria-label and sets the `value`.
                            var sanitizedSelectors = selectorsList.Select(s => s.Replace("'", "\"")).ToList();
                            var selectorsArray = string.Join(",", sanitizedSelectors.Select(s => JsonSerializer.Serialize(s)));
                            var js = "(async () => { const v = " + JsonSerializer.Serialize(val) + "; const selectors = [" + selectorsArray + "]; for (let i = 0; i < selectors.length; i++) { const q = selectors[i]; const el = document.querySelector(q); if (el) { if (el.focus) el.focus(); el.value = v; el.dispatchEvent(new Event(\"input\", {bubbles:true})); return \"FILLED:\" + q; } } return \"NOT_FOUND\"; })()";

                            // Escape single quotes for PowerShell single-quoted literal
                            var jsForPowerShell = js.Replace("'", "''");
                            var evalArgs = (string.IsNullOrWhiteSpace(sessionOpt) ? string.Empty : sessionOpt + " ") + $"eval '{jsForPowerShell}'";
                            var evalCmd = $"$ErrorActionPreference = 'Continue'; playwright-cli {evalArgs}";
                            var evalRes = await LocalProcessRunner.RunPowerShellAsync(evalCmd, _screenshotDir, _operationTimeout, _ct);
                            UpdateLastToolState(evalCmd, evalRes);
                            if (evalRes.IsSuccess && (evalRes.StandardOutput?.Contains("FILLED:") == true))
                            {
                                aggregateResult = evalRes;
                                filled = true;
                            }
                        }
                        catch { }

                        if (!filled) allOk = false;
                    }
                }

                if (wantsSubmit)
                {
                    // playwright-cli exits 0 even when an element is not found; always check
                    // output content so we don't falsely break on "exit 0 + ### Error" output.
                    var submitSelectors = new[]
                    {
                        "getByRole('button', { name: 'Login' })",
                        "getByRole('button', { name: 'Sign In' })",
                        "getByRole('button', { name: 'Sign in' })",
                        "getByRole('button', { name: 'Submit' })",
                        "button[type='submit']",
                        "input[type='submit']",
                        "getByText('Login')",
                        "getByText('Sign in')",
                        "getByText('Submit')",
                    };

                    foreach (var s in submitSelectors)
                    {
                        var clickArgs = (string.IsNullOrWhiteSpace(sessionOpt) ? string.Empty : sessionOpt + " ") + $"click \"{s}\"";
                        var clickCmd = $"$ErrorActionPreference = 'Continue'; playwright-cli {clickArgs}";
                        var clickRes = await LocalProcessRunner.RunPowerShellAsync(clickCmd, _screenshotDir, _operationTimeout, _ct);
                        UpdateLastToolState(clickCmd, clickRes);
                        if (clickRes.IsSuccess && !ContainsPlaywrightOutputError(clickRes))
                        {
                            aggregateResult = clickRes;
                            break;
                        }

                        if (LooksLikeCommandMissing(clickRes))
                        {
                            var fb = $"$ErrorActionPreference = 'Continue'; npx playwright-cli {clickArgs}";
                            clickRes = await LocalProcessRunner.RunPowerShellAsync(fb, _screenshotDir, _operationTimeout, _ct);
                            UpdateLastToolState(fb, clickRes);
                            if (clickRes.IsSuccess && !ContainsPlaywrightOutputError(clickRes))
                            {
                                aggregateResult = clickRes;
                                break;
                            }
                        }
                    }
                }

                if (aggregateResult is not null && aggregateResult.IsSuccess && allOk)
                {
                    if (_opts?.ScreenshotOnStep == true)
                    {
                        var fileName = MakeAutoScreenshotFileName();
                        var shotArgs = (string.IsNullOrWhiteSpace(sessionOpt) ? string.Empty : sessionOpt + " ") + $"screenshot --filename=\"{Path.Combine(_screenshotDir, fileName)}\"";
                        var shotCmd = $"$ErrorActionPreference = 'Continue'; playwright-cli {shotArgs}";
                        var shotRes = await LocalProcessRunner.RunPowerShellAsync(shotCmd, _screenshotDir, _operationTimeout, _ct);
                        UpdateLastToolState(shotCmd, shotRes);
                    }

                    var outText = Limit(aggregateResult.ToToolOutput(), 12000);
                    _report(outText);
                    return outText;
                }

                lastResult = aggregateResult;
                lastOutput = aggregateResult?.ToToolOutput() ?? string.Empty;
                _report($"Playwright multi-fill attempts produced: {Limit(lastOutput, 2000)}");
                continue;
            }

            string executedCmd = command;
            var result = await LocalProcessRunner.RunPowerShellAsync(executedCmd, _screenshotDir, _operationTimeout, _ct);
            if (LooksLikeCommandMissing(result) && !triedNpxFallback)
            {
                triedNpxFallback = true;
                executedCmd = $"$ErrorActionPreference = 'Continue'; npx playwright-cli {args}";
                _report($"npx playwright-cli {args}");
                result = await LocalProcessRunner.RunPowerShellAsync(executedCmd, _screenshotDir, _operationTimeout, _ct);
            }

            UpdateLastToolState(executedCmd, result);

            var combinedOut = result.StandardOutput + "\n" + result.StandardError;
            lastResult = result;
            lastOutput = result.ToToolOutput();

            if (result.IsSuccess)
            {
                // Always attempt an automatic screenshot after actions when configured.
                string autoShotOutput = string.Empty;
                string inlineSnapshotOutput = string.Empty;
                string delayedLoginSnapshotOutput = string.Empty;
                try
                {
                    if (_opts?.ScreenshotOnStep == true &&
                        !Regex.IsMatch(args ?? string.Empty, "(^|\\s)(snapshot|screenshot)(\\s|$)", RegexOptions.IgnoreCase))
                    {
                        var sessionOpt = SessionOptionOrDefault(args);

                        // Ensure a browser session is open before attempting an auto-screenshot
                        if (string.IsNullOrWhiteSpace(GetSessionOption(args)) && !_sessionOpened)
                        {
                            try
                            {
                                await EnsureBrowserSessionOpenAsync(sessionOpt);
                                _sessionOpened = true;
                            }
                            catch (Exception ex)
                            {
                                _report($"Auto-screenshot ensure-session failed: {ex.Message}");
                            }
                        }

                        var fileName = MakeAutoScreenshotFileName();
                        var shotArgs = (string.IsNullOrWhiteSpace(sessionOpt) ? string.Empty : sessionOpt + " ") +
                                       $"screenshot --filename=\"{Path.Combine(_screenshotDir, fileName)}\"";
                        var shotCmd = $"$ErrorActionPreference = 'Continue'; playwright-cli {shotArgs}";
                        _report($"(auto-screenshot) playwright-cli {shotArgs}");
                        var shotRes = await LocalProcessRunner.RunPowerShellAsync(shotCmd, _screenshotDir, _operationTimeout, _ct);
                        autoShotOutput = Limit(shotRes.ToToolOutput(), 8000);
                        UpdateLastToolState(shotCmd, shotRes);
                        _report(autoShotOutput);
                    }

                    if (!Regex.IsMatch(args ?? string.Empty, "(^|\\s)(snapshot|screenshot)(\\s|$)", RegexOptions.IgnoreCase) &&
                        ContainsSnapshotFileReference(result.ToToolOutput()))
                    {
                        var sessionOpt = SessionOptionOrDefault(args);
                        var liveSnapshot = await TryCaptureInlineSnapshotAsync(sessionOpt, "auto-inline-snapshot");
                        if (!string.IsNullOrWhiteSpace(liveSnapshot))
                        {
                            inlineSnapshotOutput = Limit(liveSnapshot, 8000);
                            _report(inlineSnapshotOutput);
                        }
                    }

                    if (string.Equals(primaryCmd, "click", StringComparison.OrdinalIgnoreCase) &&
                        LooksLikeLoginSubmit(result.ToToolOutput()))
                    {
                        await Task.Delay(TimeSpan.FromSeconds(2), _ct);
                        var sessionOpt = SessionOptionOrDefault(args);
                        var delayedSnapshot = await TryCaptureInlineSnapshotAsync(sessionOpt, "post-login-check");
                        if (!string.IsNullOrWhiteSpace(delayedSnapshot) &&
                            ContainsPostLoginSignal(delayedSnapshot))
                        {
                            delayedLoginSnapshotOutput = Limit(delayedSnapshot, 8000);
                            _report(delayedLoginSnapshotOutput);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _report($"Auto-screenshot failed: {ex.Message}");
                }

                var composed = result.ToToolOutput();
                if (!string.IsNullOrEmpty(autoShotOutput)) composed += "\n\n" + autoShotOutput;
                if (!string.IsNullOrEmpty(inlineSnapshotOutput)) composed += "\n\n" + inlineSnapshotOutput;
                if (!string.IsNullOrEmpty(delayedLoginSnapshotOutput)) composed += "\n\n" + delayedLoginSnapshotOutput;
                var output = Limit(composed, 12000);
                _log.Debug("[{Env}] run_playwright command succeeded; args={Args}, outputLen={Len}", _env?.Name, Limit(args ?? string.Empty, 200), output.Length);
                _report(output);
                return output;
            }

            // Inspect tool output for a closed browser session and try to open one and retry.
            if (combinedOut.Contains("Browser 'default' is not open", StringComparison.OrdinalIgnoreCase) ||
                Regex.IsMatch(combinedOut, "Browser '\\w+' is not open", RegexOptions.IgnoreCase))
            {
                _report("Detected closed Playwright browser session — attempting to open a session and retrying command.");
                try
                {
                    var sessionOpt = GetSessionOption(args);
                    await EnsureBrowserSessionOpenAsync(sessionOpt);
                    _sessionOpened = true;
                    continue; // retry
                }
                catch (Exception ex)
                {
                    _report($"Session open attempt failed: {ex.Message}");
                }
            }

            // If the tooling output indicates a certificate/interstitial problem, try an automated SSL bypass
            // sequence (Advanced → Proceed) using the same session, then retry the original command once.
            if (combinedOut.Contains("ERR_CERT", StringComparison.OrdinalIgnoreCase) ||
                combinedOut.Contains("Your connection is not private", StringComparison.OrdinalIgnoreCase) ||
                combinedOut.Contains("NET::ERR_CERT_AUTHORITY_INVALID", StringComparison.OrdinalIgnoreCase))
            {
                _report("Detected certificate interstitial in Playwright output — attempting SSL bypass and retrying command.");
                try
                {
                    var retry = await TrySslBypassAndRetryAsync(args);
                    if (retry is not null)
                    {
                        if (retry.IsSuccess)
                        {
                            var composed = retry.ToToolOutput();
                            var output = Limit(composed, 12000);
                            _report(output);
                            return output;
                        }

                        // update lastResult and go to next attempt
                        lastResult = retry;
                        lastOutput = retry.ToToolOutput();
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    _report($"SSL bypass attempt failed: {ex.Message}");
                }
            }

            // Unknown option error — extract the unsupported flag from the error message and
            // strip it so we can retry. This handles any flag generically
            // (--ignore-https-errors, --ignore-certificate-errors, --no-sandbox, etc.).
            var unknownOptMatch = Regex.Match(combinedOut,
                @"Unknown option:?\s*(--[\w-]+)", RegexOptions.IgnoreCase);
            if (!unknownOptMatch.Success)
                unknownOptMatch = Regex.Match(combinedOut,
                    @"unknown option\s+(--[\w-]+)", RegexOptions.IgnoreCase);
            if (unknownOptMatch.Success && !removedIgnoreFlag)
            {
                removedIgnoreFlag = true;
                var badFlag = unknownOptMatch.Groups[1].Value;
                args = Regex.Replace(args, Regex.Escape(badFlag) + @"(?:=[^\s]+)?", "", RegexOptions.IgnoreCase).Trim();
                // Strip the --no-xxx / --xxx counterpart when applicable.
                var noCounterpart = badFlag.StartsWith("--no-", StringComparison.OrdinalIgnoreCase)
                    ? "--" + badFlag[5..]
                    : "--no-" + badFlag[2..];
                args = Regex.Replace(args, Regex.Escape(noCounterpart) + @"(?:=[^\s]+)?", "", RegexOptions.IgnoreCase).Trim();
                _report($"Removed unsupported flag '{badFlag}' and will retry.");
                continue;
            }

            // Not resolved: log and optionally take failure screenshot, then retry until attempts exhausted.
            _report($"Playwright command failed (attempt {attempt} of {maxAttempts}). Output:\n{Limit(lastOutput, 8000)}");

            try
            {
                    if (_opts?.ScreenshotOnFailure == true)
                {
                    var fileName = $"failure-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}.png";
                    var sessionOpt = SessionOptionOrDefault(args);
                    var shotArgs = (($"{sessionOpt} ") + $"screenshot --filename=\"{Path.Combine(_screenshotDir, fileName)}\"").Trim();
                    var shotCmd = $"$ErrorActionPreference = 'Continue'; playwright-cli {shotArgs}";
                    _report($"(failure-screenshot) playwright-cli {shotArgs}");
                        var shotRes = await LocalProcessRunner.RunPowerShellAsync(shotCmd, _screenshotDir, _operationTimeout, _ct);
                        UpdateLastToolState(shotCmd, shotRes);
                        _report(Limit(shotRes.ToToolOutput(), 8000));
                }
            }
            catch (Exception ex)
            {
                _report($"Failure auto-screenshot failed: {ex.Message}");
            }

            // Brief backoff before retrying
            try { await Task.Delay(500, _ct); } catch { }
        }

        // Exhausted attempts: write TestResult error file and close session to terminate gracefully.
        var details = lastResult?.ToToolOutput() ?? lastOutput;
        var reason = $"Playwright command '{arguments}' failed after {maxAttempts} attempts.";
        _log.Warning("[{Env}] run_playwright exhausted attempts: args={Args}, lastExit={Exit}", _env?.Name, Limit(arguments ?? string.Empty, 300), lastResult?.ExitCode);
        await WriteErrorResultAndCloseSessionAsync(reason, Limit(details, 20000));
        return reason + "\n\n" + Limit(details, 12000);
    }

    public async Task<string> InvokeKineticRestAsync(
        string method,
        string relativePath,
        string? bodyJson,
        string? evidenceFileName)
    {
        if (string.IsNullOrWhiteSpace(_env.RestApiBaseUrl))
            return "Kinetic REST API base URL is not configured for this environment.";

        method = method.Trim().ToUpperInvariant();
        if (!IsAllowedHttpMethod(method))
            return $"Rejected REST method '{method}'. Allowed methods: GET, POST, PATCH, PUT, DELETE.";

        if (method == "DELETE" &&
            (string.IsNullOrWhiteSpace(bodyJson) ||
             !bodyJson.Contains("method=DELETE", StringComparison.OrdinalIgnoreCase)))
        {
            return "Rejected DELETE. The step value must explicitly contain method=DELETE.";
        }

        var baseUri = new Uri(EnsureTrailingSlash(_env.RestApiBaseUrl), UriKind.Absolute);
        var requestUri = Uri.TryCreate(relativePath, UriKind.Absolute, out var absoluteUri)
            ? absoluteUri
            : new Uri(baseUri, relativePath.TrimStart('/'));

        if (!string.Equals(requestUri.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase))
            return $"Rejected REST URL outside configured environment host: {requestUri}";

        using var request = new HttpRequestMessage(new HttpMethod(method), requestUri);
        ApplyAuthHeaders(request);

        var bodyForRequest = bodyJson;
        if (method == "DELETE" && bodyForRequest?.Contains("method=DELETE", StringComparison.OrdinalIgnoreCase) == true)
            bodyForRequest = null;

        if (!string.IsNullOrWhiteSpace(bodyForRequest) && method is "POST" or "PATCH" or "PUT" or "DELETE")
            request.Content = new StringContent(bodyForRequest, Encoding.UTF8, "application/json");

        _log.Debug("[{Env}] invoke_kinetic_rest: {Method} {Uri}", _env?.Name, method, requestUri);
        _report($"{method} {requestUri}");

        try
        {
            using var response = await _httpClient.SendAsync(request, _ct);
            var text = await response.Content.ReadAsStringAsync(_ct);
            var savedPath = await SaveApiEvidenceAsync(evidenceFileName, method, requestUri, response, text);

            var output = $"""
                status: {(int)response.StatusCode} {response.ReasonPhrase}
                evidencePath: {savedPath}
                body:
                {Limit(text, 10000)}
                """;
            _log.Debug("[{Env}] invoke_kinetic_rest response: status={Status} evidence={Evidence}", _env?.Name, (int)response.StatusCode, savedPath);
            _report(output);
            try
            {
                var pseudo = new CommandResult(response.IsSuccessStatusCode ? 0 : (int)response.StatusCode, text ?? string.Empty, string.Empty, TimedOut: false);
                UpdateLastToolState($"REST {method} {requestUri}", pseudo);
            }
            catch { }
            return output;
        }
        catch (Exception ex)
        {
            var output = $"REST call failed: {ex.Message}";
            _report(output);
            return output;
        }
    }

    public async Task<string> WriteResultFileAsync(string testResultJson)
    {
        try
        {
            _log.Debug("[{Env}] write_result_file called; inputLen={Len}", _env?.Name, Limit(testResultJson ?? string.Empty, 200));

            var cleaned = JsonResponseParser.ExtractFirstObject(testResultJson);
            var result = JsonSerializer.Deserialize<TestResult>(cleaned, s_jsonOpts)
                         ?? throw new InvalidOperationException("TestResult JSON deserialized to null.");

            if (string.IsNullOrWhiteSpace(result.EnvName))
                result.EnvName = _env.Name;
            if (string.IsNullOrWhiteSpace(result.Version))
                result.Version = _env.Version;
            if (string.IsNullOrWhiteSpace(result.Result))
                result.Result = result.DefectObserved ? "REPRODUCED" : "NOT_REPRODUCED";
            if (result.ScreenshotPaths.Count == 0)
                result.ScreenshotPaths.AddRange(ListEvidencePaths("*.png"));

            Directory.CreateDirectory(Path.GetDirectoryName(_resultFile) ?? ".");
            await File.WriteAllTextAsync(_resultFile, JsonSerializer.Serialize(result, s_jsonOpts), _ct);

            var output = $"Wrote TestResult to {_resultFile}";
            _log.Debug("[{Env}] write_result_file wrote file {Path}", _env?.Name, _resultFile);
            _report(output);
            return output;
        }
        catch (Exception ex)
        {
            var output = $"Could not write TestResult: {ex.Message}";
            _log.Warning(ex, "[{Env}] write_result_file failed", _env?.Name);
            _report(output);
            return output;
        }
    }

    public string ListEvidenceFiles()
    {
        _log.Debug("[{Env}] list_evidence_files called for dir {Dir}", _env?.Name, _screenshotDir);
        var files = Directory.Exists(_screenshotDir)
            ? Directory.EnumerateFiles(_screenshotDir)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .Select(path => new FileInfo(path))
                .Select(info => new
                {
                    info.Name,
                    FullPath = info.FullName,
                    info.Length,
                })
                .ToList()
            : [];

        _log.Debug("[{Env}] list_evidence_files found {Count} files", _env?.Name, files.Count);
        return JsonSerializer.Serialize(files, s_jsonOpts);
    }

    public string GetLastToolState()
    {
        lock (_stateLock)
        {
            if (_lastToolState is null)
            {
                return JsonSerializer.Serialize(new { message = "no tool state available" }, s_jsonOpts);
            }

            try
            {
                return JsonSerializer.Serialize(_lastToolState, s_jsonOpts);
            }
            catch (Exception ex)
            {
                _report($"Could not serialize last tool state: {ex.Message}");
                return JsonSerializer.Serialize(new { message = "could not serialize tool state" }, s_jsonOpts);
            }
        }
    }

    private void UpdateLastToolState(string command, CommandResult? result)
    {
        try
        {
            var screenshots = ListEvidencePaths("*.png").ToList();
            string? snapshot = null;
            string? console = null;
            var dir = Path.Combine(_screenshotDir, ".playwright-cli");
            if (Directory.Exists(dir))
            {
                snapshot = Directory.EnumerateFiles(dir, "page-*.yml").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
                console = Directory.EnumerateFiles(dir, "console-*.log").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            }

            var combined = result is not null ? (result.StandardOutput + "\n" + result.StandardError) : string.Empty;
            var detected = new List<string>();
            if (!string.IsNullOrWhiteSpace(combined))
            {
                if (combined.Contains("ERR_CERT", StringComparison.OrdinalIgnoreCase) ||
                    combined.Contains("Your connection is not private", StringComparison.OrdinalIgnoreCase) ||
                    combined.Contains("NET::ERR_CERT_AUTHORITY_INVALID", StringComparison.OrdinalIgnoreCase))
                    detected.Add("CERT_ERROR");

                if (combined.Contains("Browser '", StringComparison.OrdinalIgnoreCase) && combined.Contains("is not open", StringComparison.OrdinalIgnoreCase))
                    detected.Add("BROWSER_CLOSED");

                if (combined.Contains("not recognized", StringComparison.OrdinalIgnoreCase) ||
                    combined.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
                    combined.Contains("CommandNotFoundException", StringComparison.OrdinalIgnoreCase))
                    detected.Add("COMMAND_MISSING");

                if (result?.TimedOut == true)
                    detected.Add("TIMED_OUT");
            }

            var state = new ToolState
            {
                Command = Limit(command ?? string.Empty, 1000),
                Success = result?.IsSuccess ?? false,
                ExitCode = result?.ExitCode ?? -1,
                Stdout = Limit(result?.StandardOutput ?? string.Empty, 20000),
                Stderr = Limit(result?.StandardError ?? string.Empty, 20000),
                SnapshotPath = snapshot,
                ConsolePath = console,
                ScreenshotPaths = screenshots,
                DetectedIssues = detected,
                Timestamp = DateTimeOffset.UtcNow
            };

            _log.Debug("[{Env}] UpdateLastToolState: cmd={Cmd}, success={Success}, issues={Issues}, snaps={Snap}, shots={Shots}",
                _env?.Name,
                Limit(state.Command ?? string.Empty, 200),
                state.Success,
                state.DetectedIssues is null ? 0 : state.DetectedIssues.Count,
                state.SnapshotPath ?? "(none)",
                state.ScreenshotPaths?.Count ?? 0);

            lock (_stateLock)
            {
                _lastToolState = state;
            }
        }
        catch (Exception ex)
        {
            _report($"UpdateLastToolState failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        // Close the Playwright session asynchronously so Dispose does not block the UI thread.
        _ = Task.Run(async () =>
        {
            try
            {
                await EnsureBrowserSessionCloseAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _report($"Dispose: session close failed: {ex.Message}");
            }
            finally
            {
                try { _httpClient.Dispose(); } catch { }
            }
        });
    }

    private static string NormalizePlaywrightArguments(string arguments)
    {
        var args = (arguments ?? string.Empty).Trim();
        args = Regex.Replace(args, @"^(npx\s+)?playwright-cli\s+", "", RegexOptions.IgnoreCase);
        return args;
    }

    private static string NormalizeClickShorthand(string args)
    {
        if (string.IsNullOrWhiteSpace(args)) return args;
        try
        {
            // click button=Login  -> click getByRole('button', { name: 'Login' })
            args = Regex.Replace(args,
                "\\bclick\\s+button\\s*=\\s*(?:\\\"([^\\\"]+)\\\"|'([^']+)'|([^\\s]+))",
                m =>
                {
                    var name = m.Groups[1].Success ? m.Groups[1].Value : (m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value);
                    return $"click getByRole('button', {{ name: '{name}' }})";
                },
                RegexOptions.IgnoreCase);

            // click button:Login -> click getByRole('button', { name: 'Login' })
            args = Regex.Replace(args,
                "\\bclick\\s+button\\s*:\\s*(?:\\\"([^\\\"]+)\\\"|'([^']+)'|([^\\s]+))",
                m =>
                {
                    var name = m.Groups[1].Success ? m.Groups[1].Value : (m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value);
                    return $"click getByRole('button', {{ name: '{name}' }})";
                },
                RegexOptions.IgnoreCase);
        }
        catch { }
        return args;
    }

    private static string NormalizeSelectorExpressions(string args)
    {
        if (string.IsNullOrWhiteSpace(args)) return args;
        try
        {
            // Normalize getByRole(...) content to ensure the role and name are quoted
            args = Regex.Replace(args, @"getByRole\(([^)]*)\)", m =>
            {
                var inner = m.Groups[1].Value;
                // Split by first comma to separate role and options
                var idx = inner.IndexOf(',');
                string rolePart = inner, optsPart = null;
                if (idx >= 0)
                {
                    rolePart = inner.Substring(0, idx).Trim();
                    optsPart = inner.Substring(idx + 1).Trim();
                }

                if (!(rolePart.StartsWith("\"") || rolePart.StartsWith("'")))
                    rolePart = $"'{rolePart.Trim()}'";

                if (!string.IsNullOrWhiteSpace(optsPart))
                {
                    // Ensure name:value becomes name: 'value'
                    optsPart = Regex.Replace(optsPart, "name\\s*:\\s*(?:'([^']*)'|\\\"([^\\\"]*)\\\"|([^,}\\s]+))",
                        "name: '$1$2$3'", RegexOptions.IgnoreCase);
                    return $"getByRole({rolePart}, {optsPart})";
                }

                return $"getByRole({rolePart})";
            }, RegexOptions.IgnoreCase | RegexOptions.Singleline);

            // Ensure getByLabel/getByText/getByPlaceholder args are quoted when missing
            args = Regex.Replace(args, @"getByLabel\(([^)]+)\)", m =>
            {
                var v = m.Groups[1].Value.Trim();
                if (!(v.StartsWith("\"") || v.StartsWith("'"))) v = $"'{v}'";
                return $"getByLabel({v})";
            }, RegexOptions.IgnoreCase);

            args = Regex.Replace(args, @"getByText\(([^)]+)\)", m =>
            {
                var v = m.Groups[1].Value.Trim();
                if (!(v.StartsWith("\"") || v.StartsWith("'"))) v = $"'{v}'";
                return $"getByText({v})";
            }, RegexOptions.IgnoreCase);
        }
        catch { }

        return args;
    }

    private static bool IsSafePlaywrightArguments(string args, out string error)
    {
        if (string.IsNullOrWhiteSpace(args))
        {
            error = "empty arguments";
            return false;
        }

        if (Regex.IsMatch(args, @"[`;\r\n]|&&|\|\|?"))
        {
            error = "command separators and pipelines are not allowed";
            return false;
        }

        if (Regex.IsMatch(args, @"(^|\s)test(\s|$)", RegexOptions.IgnoreCase))
        {
            error = "playwright test runner is not allowed";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool LooksLikeCommandMissing(CommandResult result)
    {
        var combined = $"{result.StandardOutput}\n{result.StandardError}";
        return result.ExitCode != 0 &&
               (combined.Contains("not recognized", StringComparison.OrdinalIgnoreCase) ||
                combined.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
                combined.Contains("CommandNotFoundException", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// playwright-cli exits with code 0 even when a selector doesn't match or a page action
    /// fails; the error is embedded in stdout as "### Error …".  Use this instead of
    /// (or in addition to) <see cref="CommandResult.IsSuccess"/> when you need to distinguish
    /// a genuine success from a playwright error returned with exit 0.
    /// </summary>
    private static bool ContainsPlaywrightOutputError(CommandResult result)
    {
        var stdout = result.StandardOutput ?? string.Empty;
        return stdout.Contains("### Error", StringComparison.OrdinalIgnoreCase) ||
               stdout.Contains("does not match any elements", StringComparison.OrdinalIgnoreCase) ||
               stdout.Contains("locator.click: Timeout", StringComparison.OrdinalIgnoreCase) ||
               stdout.Contains("locator.fill: Timeout", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAllowedHttpMethod(string method) =>
        method is "GET" or "POST" or "PATCH" or "PUT" or "DELETE";

    private void ApplyAuthHeaders(HttpRequestMessage request)
    {
        if (!string.IsNullOrWhiteSpace(_env.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _env.ApiKey);
            request.Headers.TryAddWithoutValidation("X-API-Key", _env.ApiKey);
        }
        else if (!string.IsNullOrWhiteSpace(_env.Username) || !string.IsNullOrWhiteSpace(_env.Password))
        {
            var bytes = Encoding.ASCII.GetBytes($"{_env.Username}:{_env.Password}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(bytes));
        }

        if (!string.IsNullOrWhiteSpace(_env.Company))
            request.Headers.TryAddWithoutValidation("CallContext", JsonSerializer.Serialize(new { _env.Company }));
    }

    private string? ValidateConfiguredWebUrl()
    {
        if (string.IsNullOrWhiteSpace(_env?.WebUrl))
            return $"Environment '{_env?.Name}' is missing webUrl. Configure an absolute http(s) webUrl before running the env tester.";

        var raw = _env.WebUrl.Trim();
        if (raw.Contains("goes here", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("your-url", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("placeholder", StringComparison.OrdinalIgnoreCase))
        {
            return $"Environment '{_env?.Name}' has a placeholder webUrl ('{raw}'). Configure the real Kinetic URL before running the env tester.";
        }

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) ||
            !(uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
              uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            return $"Environment '{_env?.Name}' has an invalid webUrl ('{raw}'). Configure an absolute http(s) URL before running the env tester.";
        }

        return null;
    }

    private Uri? TryBuildTokenResourceUri()
    {
        if (Uri.TryCreate(_env?.RestApiBaseUrl, UriKind.Absolute, out var restUri))
        {
            var apiIndex = restUri.AbsolutePath.IndexOf("/api/", StringComparison.OrdinalIgnoreCase);
            if (apiIndex >= 0)
            {
                var rootPath = restUri.AbsolutePath[..apiIndex].TrimEnd('/');
                return new Uri($"{restUri.Scheme}://{restUri.Authority}{rootPath}/api/TokenResource/");
            }
        }

        if (Uri.TryCreate(_env?.WebUrl, UriKind.Absolute, out var webUri))
        {
            var path = webUri.AbsolutePath;
            var appsIndex = path.IndexOf("/Apps/", StringComparison.OrdinalIgnoreCase);
            if (appsIndex >= 0)
                path = path[..appsIndex];
            path = path.TrimEnd('/');
            return new Uri($"{webUri.Scheme}://{webUri.Authority}{path}/api/TokenResource/");
        }

        return null;
    }

    private static bool ContainsSnapshotFileReference(string? output) =>
        !string.IsNullOrWhiteSpace(output) &&
        Regex.IsMatch(output, @"\[\s*Snapshot\s*\]\([^)]+\)", RegexOptions.IgnoreCase);

    private static bool LooksLikeLoginSubmit(string? output) =>
        !string.IsNullOrWhiteSpace(output) &&
        (output.Contains("name: 'Log in'", StringComparison.OrdinalIgnoreCase) ||
         output.Contains("button \"Log in\"", StringComparison.OrdinalIgnoreCase));

    private static bool ContainsPostLoginSignal(string snapshotText) =>
        snapshotText.Contains("Invalid username or password", StringComparison.OrdinalIgnoreCase) ||
        snapshotText.Contains("Conversions Pending", StringComparison.OrdinalIgnoreCase) ||
        snapshotText.Contains("Data Conversion processing", StringComparison.OrdinalIgnoreCase) ||
        snapshotText.Contains("#/login?", StringComparison.OrdinalIgnoreCase) ||
        snapshotText.Contains("heading \"Log in\"", StringComparison.OrdinalIgnoreCase);

    private async Task<string> TryCaptureInlineSnapshotAsync(string sessionOpt, string reason)
    {
        var snapArgs = (string.IsNullOrWhiteSpace(sessionOpt) ? string.Empty : sessionOpt + " ") + "snapshot";
        var snapCmd = $"$ErrorActionPreference = 'Continue'; playwright-cli {snapArgs}";
        _report($"({reason}) playwright-cli {snapArgs}");
        var snapRes = await LocalProcessRunner.RunPowerShellAsync(snapCmd, _screenshotDir, _operationTimeout, _ct);
        UpdateLastToolState(snapCmd, snapRes);
        if (snapRes.IsSuccess && !ContainsPlaywrightOutputError(snapRes))
            return snapRes.ToToolOutput() ?? string.Empty;
        return string.Empty;
    }

    private async Task<string> SaveApiEvidenceAsync(
        string? evidenceFileName,
        string method,
        Uri requestUri,
        HttpResponseMessage response,
        string body)
    {
        var fileName = string.IsNullOrWhiteSpace(evidenceFileName)
            ? $"api-response-{DateTimeOffset.UtcNow:HHmmssfff}.json"
            : SanitizeFileName(evidenceFileName);

        if (!fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            fileName += ".json";

        var path = Path.Combine(_screenshotDir, fileName);
        var evidence = new
        {
            Method = method,
            Uri = requestUri.ToString(),
            StatusCode = (int)response.StatusCode,
            response.ReasonPhrase,
            Body = body,
            SavedAt = DateTimeOffset.UtcNow,
        };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(evidence, s_jsonOpts), _ct);
        return path;
    }

    private IEnumerable<string> ListEvidencePaths(string pattern) =>
        Directory.Exists(_screenshotDir)
            ? Directory.EnumerateFiles(_screenshotDir, pattern).OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            : [];

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? $"api-response-{DateTimeOffset.UtcNow:HHmmssfff}.json" : sanitized;
    }

    private static string EnsureTrailingSlash(string value) =>
        value.EndsWith("/", StringComparison.Ordinal) ? value : value + "/";

    private static string Limit(string value, int maxChars) =>
        value.Length <= maxChars ? value : value[..maxChars] + "\n...<truncated>";

    private async Task<CommandResult?> TrySslBypassAndRetryAsync(string originalArgs)
    {
        // Use the session option if present in the original args, otherwise use the default session
        var sessionOpt = SessionOptionOrDefault(originalArgs);

        // Ensure a browser session is open before attempting bypass clicks.
        try
        {
            await EnsureBrowserSessionOpenAsync(sessionOpt);
        }
        catch (Exception ex)
        {
            _report($"(SSL-bypass) ensure session failed: {ex.Message}");
        }

        var bypassArgs = new[]
        {
            // snapshot to capture current page state
            (sessionOpt + " snapshot").Trim(),
            // Click Chrome's "Advanced" button — use the stable DOM ID (#details-button) first;
            // fall back to getByRole in case a non-Chrome browser renders the page differently.
            (sessionOpt + " click \"#details-button\"").Trim(),
            (sessionOpt + " snapshot").Trim(),
            // Click "Proceed to <host> (unsafe)" — stable ID is #proceed-link.
            (sessionOpt + " click \"#proceed-link\"").Trim(),
            (sessionOpt + " snapshot").Trim(),
        };

        foreach (var args in bypassArgs)
        {
            try
            {
                var cmd = $"$ErrorActionPreference = 'Continue'; playwright-cli {args}";
                _report($"(SSL-bypass) playwright-cli {args}");
                var res = await LocalProcessRunner.RunPowerShellAsync(cmd, _screenshotDir, _operationTimeout, _ct);
                UpdateLastToolState(cmd, res);
                _report(Limit(res.ToToolOutput(), 8000));
            }
            catch (Exception ex)
            {
                _report($"(SSL-bypass) command failed: {ex.Message}");
            }
        }

        // Retry original command one more time after bypass attempts. Sanitize the
        // original args (especially `open` invocations) so we don't pass unsupported
        // flags directly to the playwright-cli binary.
        try
        {
            var rem = originalArgs ?? string.Empty;
            var existingSession = GetSessionOption(rem);
            if (!string.IsNullOrWhiteSpace(existingSession))
                rem = Regex.Replace(rem, Regex.Escape(existingSession), "", RegexOptions.IgnoreCase).Trim();
            rem = Regex.Replace(rem, "\\bopen\\b", "", RegexOptions.IgnoreCase).Trim();

            string positionalUrl = string.Empty;
            var urlMatch = Regex.Match(rem, "--url=(?:\"([^\"]+)\"|([^\\s]+))", RegexOptions.IgnoreCase);
            if (urlMatch.Success)
            {
                positionalUrl = !string.IsNullOrWhiteSpace(urlMatch.Groups[1].Value)
                    ? urlMatch.Groups[1].Value
                    : urlMatch.Groups[2].Value;
                rem = Regex.Replace(rem, Regex.Escape(urlMatch.Value), "", RegexOptions.IgnoreCase).Trim();
            }

            rem = Regex.Replace(rem, "--browser-type(?:=[^\\s]+)?", "", RegexOptions.IgnoreCase).Trim();
            rem = Regex.Replace(rem, "--headless(?:=[^\\s]+)?", "", RegexOptions.IgnoreCase).Trim();
            rem = Regex.Replace(rem, "--timeout(?:=[^\\s]+)?", "", RegexOptions.IgnoreCase).Trim();
            rem = Regex.Replace(rem, "--url(?:=[^\\s]+)?", "", RegexOptions.IgnoreCase).Trim();

            var remainderParts = new List<string>();
            if (!string.IsNullOrWhiteSpace(rem)) remainderParts.Add(rem);
            if (!string.IsNullOrWhiteSpace(positionalUrl)) remainderParts.Add(positionalUrl);
            var remainderSanitized = string.Join(" ", remainderParts).Trim();

            var retryArgs = (string.IsNullOrWhiteSpace(sessionOpt) ? string.Empty : sessionOpt + " ") + "open" + (string.IsNullOrWhiteSpace(remainderSanitized) ? string.Empty : " " + remainderSanitized);
            var retryCmd = $"$ErrorActionPreference = 'Continue'; playwright-cli {retryArgs}";
            _report($"(SSL-bypass) retrying: playwright-cli {retryArgs}");
            var retryRes = await LocalProcessRunner.RunPowerShellAsync(retryCmd, _screenshotDir, _operationTimeout, _ct);
            UpdateLastToolState(retryCmd, retryRes);
            _report(Limit(retryRes.ToToolOutput(), 12000));
            return retryRes;
        }
        catch (Exception ex)
        {
            _report($"(SSL-bypass) retry failed: {ex.Message}");
            return null;
        }
    }

    private static string GetSessionOption(string args)
    {
        if (string.IsNullOrWhiteSpace(args)) return string.Empty;
        var m = Regex.Match(args, "-s=([^\\s]+)|-s\\s+([^\\s]+)", RegexOptions.IgnoreCase);
        if (!m.Success) return string.Empty;
        var val = !string.IsNullOrWhiteSpace(m.Groups[1].Value) ? m.Groups[1].Value : m.Groups[2].Value;
        return $"-s={val}";
    }

    private string SessionOptionOrDefault(string args)
    {
        var s = GetSessionOption(args);
        return string.IsNullOrWhiteSpace(s) ? _defaultSessionOption : s;
    }

    private string PrependSessionIfMissing(string args)
    {
        if (string.IsNullOrWhiteSpace(args)) return _defaultSessionOption;
        if (Regex.IsMatch(args, "(^|\\s)-s=([^\\s]+)", RegexOptions.IgnoreCase))
            return args;
        return _defaultSessionOption + " " + args;
    }

    private static string SanitizeSessionId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Guid.NewGuid().ToString("n").Substring(0, 12);
        var sanitized = Regex.Replace(value, "[^a-zA-Z0-9_-]", "-");
        if (sanitized.Length > 48)
            sanitized = sanitized.Substring(0, 48);
        return sanitized;
    }

    private static List<string> SplitArgsToTokens(string args)
    {
        var tokens = new List<string>();
        if (string.IsNullOrWhiteSpace(args)) return tokens;
        var sb = new StringBuilder();
        bool inQuotes = false;
        char quote = '\0';
        for (int i = 0; i < args.Length; i++)
        {
            var c = args[i];
            if ((c == '"' || c == '\'') )
            {
                if (!inQuotes)
                {
                    inQuotes = true;
                    quote = c;
                    continue;
                }
                else if (quote == c)
                {
                    inQuotes = false;
                    // Don't flush the token here — continue building (POSIX shell concatenation:
                    // 'foo'bar is a single token "foobar"). This preserves CSS attribute selectors
                    // like input[name='username'] as a single token instead of splitting at the quote.
                    continue;
                }
            }

            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (sb.Length > 0)
                {
                    tokens.Add(sb.ToString());
                    sb.Clear();
                }
                continue;
            }

            sb.Append(c);
        }

        if (sb.Length > 0) tokens.Add(sb.ToString());
        return tokens;
    }

    private static string? FindPrimaryCommandFromTokens(List<string> tokens, string[] knownCommands)
    {
        if (tokens == null || tokens.Count == 0) return null;
        for (int i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (string.IsNullOrWhiteSpace(t)) continue;
            var lower = t.Trim().Trim('"', '\'').ToLowerInvariant();
            if (!knownCommands.Contains(lower, StringComparer.OrdinalIgnoreCase)) continue;

            if (i == 0) return lower;
            var prev = tokens[i - 1];
            if (!prev.StartsWith("-", StringComparison.Ordinal)) return lower;
            if (prev.Contains("=")) return lower;
            // previous token is likely an option name with a separate value -> skip
        }
        return null;
    }

    private string MakeAutoScreenshotFileName()
    {
        lock (_actionLock)
        {
            _actionCounter++;
            return $"step-{_actionCounter:00}-autoshot.png";
        }
    }

    private async Task<string> ReadLatestSnapshotAsync()
    {
        try
        {
            var dir = Path.Combine(_screenshotDir, ".playwright-cli");
            _log.Debug("[{Env}] read_latest_snapshot scanning dir {Dir}", _env?.Name, dir);
            if (!Directory.Exists(dir)) return "No .playwright-cli snapshots found.";
            var file = Directory.EnumerateFiles(dir, "page-*.yml").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            if (file is null) return "No snapshot files found.";
            var txt = await File.ReadAllTextAsync(file, _ct);
            _log.Debug("[{Env}] read_latest_snapshot found {File} (len={Len})", _env?.Name, Path.GetFileName(file), Limit(txt, 200).Length);
            _report($"Read snapshot: {Path.GetFileName(file)}");
            return Limit(txt, 20000);
        }
        catch (Exception ex)
        {
            return $"Could not read snapshot: {ex.Message}";
        }
    }

    private async Task<string> ReadLatestConsoleAsync()
    {
        try
        {
            var dir = Path.Combine(_screenshotDir, ".playwright-cli");
            _log.Debug("[{Env}] read_latest_console scanning dir {Dir}", _env?.Name, dir);
            if (!Directory.Exists(dir)) return "No .playwright-cli console logs found.";
            var file = Directory.EnumerateFiles(dir, "console-*.log").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            if (file is null) return "No console log files found.";
            var txt = await File.ReadAllTextAsync(file, _ct);
            _log.Debug("[{Env}] read_latest_console found {File} (len={Len})", _env?.Name, Path.GetFileName(file), Limit(txt, 200).Length);
            _report($"Read console log: {Path.GetFileName(file)}");
            return Limit(txt, 20000);
        }
        catch (Exception ex)
        {
            return $"Could not read console log: {ex.Message}";
        }
    }

    private string GetLatestScreenshot()
    {
        try
        {
            _log.Debug("[{Env}] get_latest_screenshot scanning dir {Dir}", _env?.Name, _screenshotDir);
            if (!Directory.Exists(_screenshotDir)) return "No screenshots directory found.";
            var file = Directory.EnumerateFiles(_screenshotDir, "*.png").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            _log.Debug("[{Env}] get_latest_screenshot result: {File}", _env?.Name, file ?? "(none)");
            return file ?? "No screenshot files found.";
        }
        catch (Exception ex)
        {
            return $"Error listing screenshots: {ex.Message}";
        }
    }

    /// <summary>
    /// Pre-writes <c>.playwright/cli.config.json</c> in the working directory when
    /// <see cref="PlaywrightOptions.IgnoreHttpsErrors"/> is enabled.  playwright-cli loads
    /// this config automatically (the --config default) so SSL errors are bypassed without
    /// any unsupported command-line flags on <c>open</c>.
    /// </summary>
    private async Task EnsurePlaywrightConfigAsync()
    {
        if (_opts?.IgnoreHttpsErrors != true) return;
        try
        {
            var configDir = Path.Combine(_screenshotDir, ".playwright");
            Directory.CreateDirectory(configDir);
            var configPath = Path.Combine(configDir, "cli.config.json");
            if (!File.Exists(configPath))
            {
                await File.WriteAllTextAsync(configPath,
                    """{"use":{"ignoreHTTPSErrors":true}}""", _ct);
                _log.Debug("[{Env}] Wrote playwright config with ignoreHTTPSErrors=true to {Path}",
                    _env?.Name, configPath);
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[{Env}] Could not write playwright config file", _env?.Name);
        }
    }

    private async Task EnsureBrowserSessionOpenAsync(string sessionOption)
    {
        try
        {
            // Pre-write playwright config for SSL bypass before each open.
            await EnsurePlaywrightConfigAsync();

            var sessionOpt = SessionOptionOrDefault(sessionOption);

            // Navigate directly to the environment URL (not about:blank) so that any
            // immediate fill/click after auto-open lands on the actual page, not a blank tab.
            var targetUrl = _env?.WebUrl ?? string.Empty;
            var openArgs = (string.IsNullOrWhiteSpace(sessionOpt) ? string.Empty : sessionOpt + " ") +
                            "open" +
                            (string.IsNullOrWhiteSpace(targetUrl) ? string.Empty : " " + targetUrl);

            var cmd = $"$ErrorActionPreference = 'Continue'; playwright-cli {openArgs}";
            _report($"(session-open) playwright-cli {openArgs}");
            var res = await LocalProcessRunner.RunPowerShellAsync(cmd, _screenshotDir, _operationTimeout, _ct);
            UpdateLastToolState(cmd, res);
            _report(Limit(res.ToToolOutput(), 10000));

            // When a URL was opened, take a live snapshot and auto-bypass any SSL interstitial.
            if (res.IsSuccess && !string.IsNullOrWhiteSpace(targetUrl))
            {
                try
                {
                    var snapArgs = (string.IsNullOrWhiteSpace(sessionOpt) ? string.Empty : sessionOpt + " ") + "snapshot";
                    var snapCmd = $"$ErrorActionPreference = 'Continue'; playwright-cli {snapArgs}";
                    var snapRes = await LocalProcessRunner.RunPowerShellAsync(snapCmd, _screenshotDir, _operationTimeout, _ct);
                    var snapText = snapRes.IsSuccess ? (snapRes.ToToolOutput() ?? string.Empty) : string.Empty;

                    if (snapText.Contains("chrome-error://", StringComparison.OrdinalIgnoreCase) ||
                        snapText.Contains("ERR_CERT", StringComparison.OrdinalIgnoreCase) ||
                        snapText.Contains("Your connection is not private", StringComparison.OrdinalIgnoreCase))
                    {
                        _report("(session-open ssl-bypass) SSL interstitial detected — clicking #details-button then #proceed-link");
                        foreach (var step in new[] { "click \"#details-button\"", "snapshot", "click \"#proceed-link\"", "snapshot" })
                        {
                            var bArgs = (string.IsNullOrWhiteSpace(sessionOpt) ? string.Empty : sessionOpt + " ") + step;
                            var bCmd = $"$ErrorActionPreference = 'Continue'; playwright-cli {bArgs}";
                            _report($"(session-open ssl-bypass) playwright-cli {bArgs}");
                            try
                            {
                                await LocalProcessRunner.RunPowerShellAsync(bCmd, _screenshotDir, _operationTimeout, _ct);
                            }
                            catch { }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _report($"(session-open post-snapshot) failed: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            _report($"(session-open) failed: {ex.Message}");
            throw;
        }
    }

    private async Task QueryPlaywrightHelpAsync()
    {
        lock (_helpLock)
        {
            if (_playwrightHelpRaw is not null)
                return;
        }

        try
        {
            var helpCmd = "$ErrorActionPreference = 'Continue'; playwright-cli --help";
            _report("(help) playwright-cli --help");
            string executedHelpCmd = helpCmd;
            var res = await LocalProcessRunner.RunPowerShellAsync(executedHelpCmd, _screenshotDir, _operationTimeout, _ct);
            if (LooksLikeCommandMissing(res))
            {
                executedHelpCmd = "$ErrorActionPreference = 'Continue'; npx playwright-cli --help";
                _report("(help) npx playwright-cli --help");
                res = await LocalProcessRunner.RunPowerShellAsync(executedHelpCmd, _screenshotDir, _operationTimeout, _ct);
            }

            UpdateLastToolState(executedHelpCmd, res);

            var combined = res.StandardOutput + "\n" + res.StandardError;

            lock (_helpLock)
            {
                _playwrightHelpRaw = combined;
                _availablePlaywrightCommands.Clear();
                var matches = Regex.Matches(combined, "^\\s{0,4}([a-zA-Z0-9_-]+)(?:\\s|:|\\[)", RegexOptions.Multiline);
                foreach (Match m in matches)
                {
                    var name = m.Groups[1].Value.Trim();
                    if (!string.IsNullOrWhiteSpace(name))
                        _availablePlaywrightCommands.Add(name);
                }

                _supportsIgnoreHttps = combined.Contains("--ignore-https-errors", StringComparison.OrdinalIgnoreCase);
                _supportsProfile = combined.Contains("--profile", StringComparison.OrdinalIgnoreCase) || combined.Contains("--user-data-dir", StringComparison.OrdinalIgnoreCase);
            }

            _report(Limit(combined, 4000));
        }
        catch (Exception ex)
        {
            _report($"Could not run playwright help: {ex.Message}");
        }
    }

    private async Task EnsureBrowserSessionCloseAsync()
    {
        try
        {
            // Quick check: if session isn't open, nothing to do.
            var needClose = false;
            await _sessionLock.WaitAsync(_ct).ConfigureAwait(false);
            try
            {
                needClose = _sessionOpened;
            }
            finally
            {
                try { _sessionLock.Release(); } catch { }
            }

            if (!needClose)
                return;

            var cmd = "$ErrorActionPreference = 'Continue'; playwright-cli close";
            _report("(session-close) playwright-cli close");
            var res = await LocalProcessRunner.RunPowerShellAsync(cmd, _screenshotDir, _operationTimeout, _ct);
            UpdateLastToolState(cmd, res);
            _report(Limit(res.ToToolOutput(), 8000));

            await _sessionLock.WaitAsync(_ct).ConfigureAwait(false);
            try
            {
                _sessionOpened = false;
            }
            finally
            {
                try { _sessionLock.Release(); } catch { }
            }
        }
        catch (Exception ex)
        {
            _report($"(session-close) failed: {ex.Message}");
        }
    }

    private async Task WriteErrorResultAndCloseSessionAsync(string reason, string details)
    {
        try
        {
            var result = new TestResult
            {
                EnvName = _env.Name,
                Version = _env.Version,
                Result = "ERROR",
                Error = reason + "\n\n" + details,
                ScreenshotPaths = ListEvidencePaths("*.png").ToList(),
            };

            Directory.CreateDirectory(Path.GetDirectoryName(_resultFile) ?? ".");
            await File.WriteAllTextAsync(_resultFile, JsonSerializer.Serialize(result, s_jsonOpts), _ct);
            _report($"Wrote error TestResult to {_resultFile}");
        }
        catch (Exception ex)
        {
            _report($"Could not write error TestResult: {ex.Message}");
        }

        try
        {
            await EnsureBrowserSessionCloseAsync();
        }
        catch (Exception ex)
        {
            _report($"Error closing session after failure: {ex.Message}");
        }
    }
}
