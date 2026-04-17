using DefectScout.Core.Models;

namespace DefectScout.Core.Services;

public interface IConfigService
{
    /// <summary>
    /// Absolute path to the app-local config file (next to the executable).
    /// All reads and writes go here.
    /// </summary>
    string AppConfigPath { get; }

    /// <summary>
    /// Absolute path to the app data directory (next to the executable).
    /// Used for screenshots, reports, and temp files so everything stays app-relative.
    /// </summary>
    string AppDataDir { get; }

    /// <summary>Load the app-local config. If the file is missing/corrupt, returns a default config.</summary>
    Task<DefectScoutConfig> LoadAsync(CancellationToken ct = default);

    /// <summary>
    /// Load config from an external path, copy its contents into the app-local config file, and return it.
    /// </summary>
    Task<DefectScoutConfig> ImportFromExternalAsync(string externalPath, CancellationToken ct = default);

    /// <summary>Save config to the app-local config file.</summary>
    Task SaveAsync(DefectScoutConfig config, CancellationToken ct = default);

    /// <summary>Create a new config pre-populated with one example environment.</summary>
    DefectScoutConfig CreateDefault();
}
