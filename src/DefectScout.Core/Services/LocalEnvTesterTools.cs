using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
            args = string.Join(" ", tokens.Select(t => t.Contains(' ') ? "\"" + t + "\"" : t));

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

                    // Sanitize tokens: remove unsupported flags and extract any --url value
                    string positionalUrl = string.Empty;
                    var sanitizedTokens = new List<string>();
                    for (int i = 0; i < remTokens.Count; i++)
                    {
                        var t = remTokens[i];
                        if (string.IsNullOrWhiteSpace(t)) continue;
                        if ((t.StartsWith("--", StringComparison.Ordinal) || t.StartsWith("-", StringComparison.Ordinal)))
                        {
                            var lower = t.ToLowerInvariant();
                            if (lower.StartsWith("--url"))
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

                            if (lower.StartsWith("--browser-type") || string.Equals(lower, "--browser-type", StringComparison.OrdinalIgnoreCase))
                            {
                                if (!t.Contains('=') && i + 1 < remTokens.Count && !remTokens[i + 1].StartsWith("-")) i++;
                                continue;
                            }

                            if (lower.StartsWith("--headless") || string.Equals(lower, "--headless", StringComparison.OrdinalIgnoreCase))
                            {
                                if (!t.Contains('=') && i + 1 < remTokens.Count && !remTokens[i + 1].StartsWith("-")) i++;
                                continue;
                            }

                            if (lower.StartsWith("--timeout") || string.Equals(lower, "--timeout", StringComparison.OrdinalIgnoreCase))
                            {
                                if (!t.Contains('=') && i + 1 < remTokens.Count && !remTokens[i + 1].StartsWith("-")) i++;
                                continue;
                            }

                            if (lower.StartsWith("--ignore-https-errors") || string.Equals(lower, "--ignore-https-errors", StringComparison.OrdinalIgnoreCase))
                            {
                                // Drop original ignore flags; we'll add canonical ignore flag if supported.
                                if (!t.Contains('=') && i + 1 < remTokens.Count && !remTokens[i + 1].StartsWith("-")) i++;
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

                    var openCmd = $"$ErrorActionPreference = 'Continue'; playwright-cli {openArgs}";
                    _report($"(open-via-helper) playwright-cli {openArgs}");
                    var openRes = await LocalProcessRunner.RunPowerShellAsync(openCmd, _screenshotDir, _operationTimeout, _ct);
                    if (LooksLikeCommandMissing(openRes))
                    {
                        var fallback = $"$ErrorActionPreference = 'Continue'; npx playwright-cli {openArgs}";
                        _report($"npx playwright-cli {openArgs}");
                        openRes = await LocalProcessRunner.RunPowerShellAsync(fallback, _screenshotDir, _operationTimeout, _ct);
                    }

                    if (openRes.IsSuccess)
                    {
                        _sessionOpened = true;
                        var outText = Limit(openRes.ToToolOutput(), 12000);
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
            }

            var command = $"$ErrorActionPreference = 'Continue'; playwright-cli {args}";
            _report($"playwright-cli {args}");

            var result = await LocalProcessRunner.RunPowerShellAsync(command, _screenshotDir, _operationTimeout, _ct);
            if (LooksLikeCommandMissing(result) && !triedNpxFallback)
            {
                triedNpxFallback = true;
                var fallback = $"$ErrorActionPreference = 'Continue'; npx playwright-cli {args}";
                _report($"npx playwright-cli {args}");
                result = await LocalProcessRunner.RunPowerShellAsync(fallback, _screenshotDir, _operationTimeout, _ct);
            }

            var combinedOut = result.StandardOutput + "\n" + result.StandardError;
            lastResult = result;
            lastOutput = result.ToToolOutput();

            if (result.IsSuccess)
            {
                // Always attempt an automatic screenshot after actions when configured.
                string autoShotOutput = string.Empty;
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
                        _report(autoShotOutput);
                    }
                }
                catch (Exception ex)
                {
                    _report($"Auto-screenshot failed: {ex.Message}");
                }

                var composed = result.ToToolOutput();
                if (!string.IsNullOrEmpty(autoShotOutput)) composed += "\n\n" + autoShotOutput;
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

            // Unknown option errors for unsupported flags (e.g., --ignore-https-errors on `open`)
            if (combinedOut.Contains("Unknown option: --ignore-https-errors", StringComparison.OrdinalIgnoreCase) ||
                combinedOut.Contains("unknown option --ignore-https-errors", StringComparison.OrdinalIgnoreCase))
            {
                if (!removedIgnoreFlag)
                {
                    removedIgnoreFlag = true;
                    args = Regex.Replace(args, "--ignore-https-errors(?:=[^\\s]+)?", "", RegexOptions.IgnoreCase).Trim();
                    _report("Removed unsupported --ignore-https-errors flag and will retry.");
                    continue;
                }
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
            // click Advanced
            (sessionOpt + " click \"getByRole('button', { name: 'Advanced' })\"").Trim(),
            (sessionOpt + " snapshot").Trim(),
            // click Proceed (partial text match)
            (sessionOpt + " click \"getByText('Proceed to')\"").Trim(),
            (sessionOpt + " snapshot").Trim(),
        };

        foreach (var args in bypassArgs)
        {
            try
            {
                var cmd = $"$ErrorActionPreference = 'Continue'; playwright-cli {args}";
                _report($"(SSL-bypass) playwright-cli {args}");
                var res = await LocalProcessRunner.RunPowerShellAsync(cmd, _screenshotDir, _operationTimeout, _ct);
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
                    tokens.Add(sb.ToString());
                    sb.Clear();
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

    private async Task EnsureBrowserSessionOpenAsync(string sessionOption)
    {
        try
        {
            // Open a browser session without supplying a custom profile path.
            // Prefix the session option before the command so allowed-roots are scoped correctly.
            var sessionOpt = SessionOptionOrDefault(sessionOption);
            var ignoreFlag = (_opts?.IgnoreHttpsErrors == true && _supportsIgnoreHttps) ? " --ignore-https-errors" : string.Empty;
            var openArgs = (string.IsNullOrWhiteSpace(sessionOpt) ? string.Empty : sessionOpt + " ") + "open" + ignoreFlag;

            var cmd = $"$ErrorActionPreference = 'Continue'; playwright-cli {openArgs}";
            _report($"(session-open) playwright-cli {openArgs}");
            var res = await LocalProcessRunner.RunPowerShellAsync(cmd, _screenshotDir, _operationTimeout, _ct);
            _report(Limit(res.ToToolOutput(), 10000));
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
            var res = await LocalProcessRunner.RunPowerShellAsync(helpCmd, _screenshotDir, _operationTimeout, _ct);
            if (LooksLikeCommandMissing(res))
            {
                var fallback = "$ErrorActionPreference = 'Continue'; npx playwright-cli --help";
                _report("(help) npx playwright-cli --help");
                res = await LocalProcessRunner.RunPowerShellAsync(fallback, _screenshotDir, _operationTimeout, _ct);
            }

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
