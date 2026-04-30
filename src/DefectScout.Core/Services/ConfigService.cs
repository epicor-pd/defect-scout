using System.Text.Json;
using DefectScout.Core.Models;
using Serilog;

namespace DefectScout.Core.Services;

public sealed class ConfigService : IConfigService
{
    private static readonly ILogger _log = AppLogger.For<ConfigService>();
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Directory where the executable lives.  During development this is the bin/Debug/net9.0
    /// folder; in a published build it is the installation folder.
    /// </summary>
    private static string AppBaseDir =>
        AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar,
                                         Path.AltDirectorySeparatorChar);

    /// <inheritdoc/>
    public string AppConfigPath { get; } =
        Path.Combine(AppContext.BaseDirectory, "defect-scout-config.json");

    /// <inheritdoc/>
    public string AppDataDir { get; } =
        Path.Combine(AppContext.BaseDirectory, "data");

    /// <inheritdoc/>
    public async Task<DefectScoutConfig> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(AppConfigPath))
        {
            _log.Debug("LoadAsync: config file not found at {Path}, returning default", AppConfigPath);
            return CreateDefault();
        }

        try
        {
            await using var stream = File.OpenRead(AppConfigPath);
            var cfg = await JsonSerializer.DeserializeAsync<DefectScoutConfig>(stream, s_jsonOptions, ct);
            var result = NormalizePaths(cfg ?? CreateDefault());
            _log.Information("LoadAsync: loaded config from {Path}, environments={Count}",
                AppConfigPath, result.Environments.Count);
            return result;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "LoadAsync: failed to deserialize config from {Path}, using default", AppConfigPath);
            return CreateDefault();
        }
    }

    /// <inheritdoc/>
    public async Task<DefectScoutConfig> ImportFromExternalAsync(
        string externalPath, CancellationToken ct = default)
    {
        _log.Information("ImportFromExternalAsync: {Path}", externalPath);
        DefectScoutConfig imported;
        await using (var stream = File.OpenRead(externalPath))
        {
            imported = await JsonSerializer.DeserializeAsync<DefectScoutConfig>(stream, s_jsonOptions, ct)
                       ?? CreateDefault();
        }

        imported = NormalizePaths(imported);
        _log.Information("ImportFromExternalAsync: imported {Count} environments, saving to {Dest}",
            imported.Environments.Count, AppConfigPath);
        await SaveAsync(imported, ct);
        return imported;
    }

    /// <inheritdoc/>
    public async Task SaveAsync(DefectScoutConfig config, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(AppConfigPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        NormalizeTimeouts(config);
        _log.Information("SaveAsync: writing config to {Path}", AppConfigPath);
        await using var stream = File.Create(AppConfigPath);
        await JsonSerializer.SerializeAsync(stream, config, s_jsonOptions, ct);
        _log.Debug("SaveAsync: config written successfully");
    }

    /// <inheritdoc/>
    public DefectScoutConfig CreateDefault()
    {
        // Screenshots and reports go inside the app data dir so everything is app-relative
        Directory.CreateDirectory(AppDataDir);

        return new DefectScoutConfig
        {
            ScreenshotBaseDir = Path.Combine(AppDataDir, "screenshots"),
            ReportDir = Path.Combine(AppDataDir, "reports"),
            LogDir = Path.Combine(AppDataDir, "logs"),
            Playwright = new PlaywrightOptions
            {
                Timeout = PlaywrightOptions.DefaultTimeoutMilliseconds,
                ScreenshotOnStep = true,
                ScreenshotOnFailure = true,
                Headless = true,
                IgnoreHttpsErrors = true,
            },
            AgentRuntime = new AgentRuntimeOptions
            {
                Mode = AgentRuntimeOptions.CopilotSdkMode,
                OllamaEndpoint = "http://localhost:11434",
                StepExtractorModel = "qwen3.5:4b",
                EnvTesterModel = "qwen3.5:4b",
                MaxConcurrentEnvTesters = 3,
                MaxToolIterations = 80,
                OllamaContextTokens = AgentRuntimeOptions.DefaultOllamaContextTokens,
                OllamaMaxOutputTokens = AgentRuntimeOptions.DefaultOllamaMaxOutputTokens,
                OllamaThink = AgentRuntimeOptions.DefaultOllamaThink,
                OllamaThinkStepExtractor = AgentRuntimeOptions.DefaultOllamaThink,
                OllamaThinkEnvTester = AgentRuntimeOptions.DefaultOllamaThink,
            },
            Environments =
            [
                new KineticEnvironment
                {
                    Name = "Kinetic 2026.1 Dev",
                    Version = "2026.1",
                    Enabled = true,
                    WebUrl = "https://localhost/ERPCurrent/Apps/ERP/Home/",
                    RestApiBaseUrl = "https://localhost/ERPCurrent/api/v2/odata/EPIC06/",
                    ApiKey = "",
                    Username = "manager",
                    Password = "",
                    Company = "EPIC06",
                    Notes = "Local dev instance. Fill in your credentials above.",
                },
                new KineticEnvironment
                {
                    Name = "Kinetic 2025.2 QA",
                    Version = "2025.2",
                    Enabled = false,
                    WebUrl = "https://qa-server/ERP252/Apps/ERP/Home/",
                    RestApiBaseUrl = "https://qa-server/ERP252/api/v2/odata/EPIC06/",
                    ApiKey = "",
                    Username = "manager",
                    Password = "",
                    Company = "EPIC06",
                    Notes = "Fill in the QA URL and credentials, then enable for cross-version runs.",
                },
                new KineticEnvironment
                {
                    Name = "Kinetic 2025.1 Staging",
                    Version = "2025.1",
                    Enabled = false,
                    WebUrl = "https://staging-server/ERP251/Apps/ERP/Home/",
                    RestApiBaseUrl = "https://staging-server/ERP251/api/v2/odata/EPIC06/",
                    ApiKey = "",
                    Username = "manager",
                    Password = "",
                    Company = "EPIC06",
                    Notes = "Fill in the staging URL and credentials, then enable for cross-version runs.",
                }
            ]
        };
    }

    /// <summary>
    /// Ensures <see cref="DefectScoutConfig.ScreenshotBaseDir"/> and
    /// <see cref="DefectScoutConfig.ReportDir"/> refer to paths under the app data
    /// directory.  External / hardcoded absolute paths from imported configs are
    /// replaced with app-relative defaults so all output files stay self-contained.
    /// </summary>
    private DefectScoutConfig NormalizePaths(DefectScoutConfig cfg)
    {
        cfg.ScreenshotBaseDir = NormalizePath(cfg.ScreenshotBaseDir, "screenshots");
        cfg.ReportDir         = NormalizePath(cfg.ReportDir, "reports");
        cfg.LogDir            = NormalizePath(cfg.LogDir, "logs");
        cfg.Playwright ??= new PlaywrightOptions();
        cfg.AgentRuntime ??= new AgentRuntimeOptions();
        NormalizeTimeouts(cfg);
        return cfg;
    }

    private static void NormalizeTimeouts(DefectScoutConfig cfg)
    {
        cfg.Playwright ??= new PlaywrightOptions();
        cfg.AgentRuntime ??= new AgentRuntimeOptions();

        if (cfg.AgentRuntime.LegacyToolTimeoutSeconds is int legacySeconds &&
            legacySeconds > 0 &&
            cfg.Playwright.Timeout <= 30000)
        {
            var maxLegacySeconds = PlaywrightOptions.MaxTimeoutMilliseconds / 1000;
            var migratedMs = Math.Min(legacySeconds, maxLegacySeconds) * 1000;
            cfg.Playwright.Timeout = PlaywrightOptions.NormalizeTimeout(migratedMs);
        }
        else
        {
            cfg.Playwright.Timeout = PlaywrightOptions.NormalizeTimeout(cfg.Playwright.Timeout);
        }

        cfg.AgentRuntime.LegacyToolTimeoutSeconds = null;
        cfg.AgentRuntime.MaxConcurrentEnvTesters = Math.Max(1, cfg.AgentRuntime.MaxConcurrentEnvTesters);
        cfg.AgentRuntime.MaxToolIterations = Math.Clamp(cfg.AgentRuntime.MaxToolIterations, 1, 200);
        cfg.AgentRuntime.OllamaContextTokens =
            AgentRuntimeOptions.NormalizeOllamaContextTokens(cfg.AgentRuntime.OllamaContextTokens);
        cfg.AgentRuntime.OllamaMaxOutputTokens =
            AgentRuntimeOptions.NormalizeOllamaMaxOutputTokens(cfg.AgentRuntime.OllamaMaxOutputTokens);
        cfg.AgentRuntime.OllamaThink =
            AgentRuntimeOptions.NormalizeOllamaThink(cfg.AgentRuntime.OllamaThink);

        // Normalize per-purpose thinking levels; fall back to legacy OllamaThink when not set.
        cfg.AgentRuntime.OllamaThinkStepExtractor = AgentRuntimeOptions.NormalizeOllamaThink(
            string.IsNullOrWhiteSpace(cfg.AgentRuntime.OllamaThinkStepExtractor)
                ? cfg.AgentRuntime.OllamaThink
                : cfg.AgentRuntime.OllamaThinkStepExtractor);

        cfg.AgentRuntime.OllamaThinkEnvTester = AgentRuntimeOptions.NormalizeOllamaThink(
            string.IsNullOrWhiteSpace(cfg.AgentRuntime.OllamaThinkEnvTester)
                ? cfg.AgentRuntime.OllamaThink
                : cfg.AgentRuntime.OllamaThinkEnvTester);
    }

    private string NormalizePath(string path, string fallbackSubDir)
    {
        if (!string.IsNullOrWhiteSpace(path) &&
            path.StartsWith(AppDataDir, StringComparison.OrdinalIgnoreCase))
            return path; // already app-relative, keep it

        // Blank, or an absolute path pointing outside the app directory
        // (e.g. a path imported from a developer's own ERP repo).
        return Path.Combine(AppDataDir, fallbackSubDir);
    }
}
