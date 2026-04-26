using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DefectScout.Core.Models;
using Microsoft.Extensions.AI;

namespace DefectScout.Core.Services;

internal sealed class LocalEnvTesterTools : IDisposable
{
    private static readonly JsonSerializerOptions s_jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly KineticEnvironment _env;
    private readonly string _screenshotDir;
    private readonly string _resultFile;
    private readonly TimeSpan _operationTimeout;
    private readonly CancellationToken _ct;
    private readonly Action<string> _report;
    private readonly HttpClient _httpClient;

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
        _screenshotDir = screenshotDir;
        _resultFile = resultFile;
        _operationTimeout = operationTimeout;
        _ct = ct;
        _report = report;

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

    public IList<AITool> CreateTools() =>
    [
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
    ];

    public string GetEnvironmentLogin() =>
        JsonSerializer.Serialize(new
        {
            _env.Username,
            _env.Password,
            _env.Company,
        }, s_jsonOpts);

    public async Task<string> RunPlaywrightAsync(string arguments)
    {
        var args = NormalizePlaywrightArguments(arguments);
        if (!IsSafePlaywrightArguments(args, out var error))
            return $"Rejected playwright command: {error}";

        var command = $"$ErrorActionPreference = 'Continue'; playwright-cli {args}";
        _report($"playwright-cli {args}");

        var result = await LocalProcessRunner.RunPowerShellAsync(command, _screenshotDir, _operationTimeout, _ct);
        if (LooksLikeCommandMissing(result))
        {
            var fallback = $"$ErrorActionPreference = 'Continue'; npx playwright-cli {args}";
            _report($"npx playwright-cli {args}");
            result = await LocalProcessRunner.RunPowerShellAsync(fallback, _screenshotDir, _operationTimeout, _ct);
        }

        var output = Limit(result.ToToolOutput(), 12000);
        _report(output);
        return output;
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
            _report(output);
            return output;
        }
        catch (Exception ex)
        {
            var output = $"Could not write TestResult: {ex.Message}";
            _report(output);
            return output;
        }
    }

    public string ListEvidenceFiles()
    {
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

        return JsonSerializer.Serialize(files, s_jsonOpts);
    }

    public void Dispose() => _httpClient.Dispose();

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
}
