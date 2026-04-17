using System.Runtime.InteropServices;
using Serilog;

namespace DefectScout.Core.Services;

/// <summary>
/// Resolves paths to external CLI tools (Copilot agent CLI, Playwright, etc.)
/// by checking the application's bundled location first, then falling back to
/// locally installed tools found on the system PATH or common install directories.
/// </summary>
internal static class CliLocator
{
    private static readonly ILogger _log = Log.ForContext(typeof(CliLocator));
    private static readonly string _exeSuffix =
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".exe" : string.Empty;

    /// <summary>
    /// Resolves the Copilot CLI executable path used by the GitHub Copilot SDK.
    /// Each candidate is validated by executing it with <c>--version</c> before being
    /// accepted, so broken installs (e.g. WinGet packages with CSPRNG crash bugs) are
    /// automatically skipped.
    /// Search order:
    /// 1. Bundled runtime path in the application directory (as shipped by the NuGet package).
    /// 2. All matching executables on the system PATH, skipping any that fail validation.
    /// Returns <c>null</c> when no working candidate is found (the SDK's own error will surface).
    /// </summary>
    public static string? ResolveCopilotCli()
    {
        // 1. Bundled path — the NuGet package places copilot[.exe] here at publish time.
        var rid     = GetRid();
        var bundled = Path.Combine(
            AppContext.BaseDirectory, "runtimes", rid, "native", "copilot" + _exeSuffix);

        if (File.Exists(bundled))
        {
            if (ValidateCli(bundled))
            {
                _log.Debug("Copilot CLI found and validated at bundled path: {Path}", bundled);
                return bundled;
            }
            _log.Warning("Bundled Copilot CLI at {Path} failed validation; falling back to PATH.", bundled);
        }
        else
        {
            _log.Debug("Copilot CLI not found at bundled path ({Path}); searching system PATH…", bundled);
        }

        // 2. System PATH — try every match so that a broken install is skipped in favour of
        //    a working one (e.g. WinGet packages can ship a variant that crashes on startup).
        foreach (var candidate in FindAllOnPath("copilot" + _exeSuffix))
        {
            if (ValidateCli(candidate))
            {
                _log.Information("Copilot CLI resolved and validated from system PATH: {Path}", candidate);
                return candidate;
            }
            _log.Warning("Copilot CLI candidate at {Path} failed validation (process crash or non-zero exit); skipping.", candidate);
        }

        // 3. Well-known user-local directories — searched directly so that tools installed by
        //    the current user (e.g. via npm without admin) are found even when their directory
        //    has not been added to PATH yet.  All directories are under the user profile or
        //    roaming AppData; no elevated access is required to read them.
        foreach (var dir in GetUserLocalBinDirs())
        {
            var candidate = Path.Combine(dir, "copilot" + _exeSuffix);
            if (!File.Exists(candidate)) continue;
            if (ValidateCli(candidate))
            {
                _log.Information("Copilot CLI resolved and validated from user-local directory: {Path}", candidate);
                return candidate;
            }
            _log.Warning("Copilot CLI candidate at {Path} failed validation; skipping.", candidate);
        }

        _log.Warning(
            "No working Copilot CLI found at bundled path or on system PATH. " +
            "Ensure the GitHub.Copilot.SDK NuGet artefacts were restored, or install the " +
            "Copilot CLI and add it to your PATH.");
        return null;
    }

    /// <summary>
    /// Probes <paramref name="cliPath"/> by running it with <c>--version</c>.
    /// Returns <c>true</c> only when the process exits cleanly (exit code 0).
    /// Binaries that crash (e.g. WinGet copilot with the ncrypto CSPRNG assertion) produce
    /// a non-zero exit code and are rejected.
    /// </summary>
    private static bool ValidateCli(string cliPath)
    {
        try
        {
            using var proc = new System.Diagnostics.Process();
            proc.StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName               = cliPath,
                Arguments              = "--version",
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true,
            };
            proc.Start();

            // Give the process up to 5 s to respond.  A healthy CLI prints its version
            // almost immediately; a crashing one exits within milliseconds.
            bool exited = proc.WaitForExit(5_000);
            if (!exited)
            {
                try { proc.Kill(); } catch { /* best-effort */ }
                _log.Debug("Copilot CLI validation timed out for {Path}", cliPath);
                return false;
            }

            _log.Debug("Copilot CLI validation exit code {Code} for {Path}", proc.ExitCode, cliPath);
            return proc.ExitCode == 0;
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Copilot CLI validation threw for {Path}", cliPath);
            return false;
        }
    }

    /// <summary>
    /// Builds an environment-variable dictionary to forward to the Copilot CLI process via
    /// <see cref="GitHub.Copilot.SDK.CopilotClientOptions.Environment"/> so that locally
    /// installed tools such as Playwright are accessible inside agent sessions.
    /// Returns <c>null</c> when no extra entries are required.
    /// </summary>
    public static IReadOnlyDictionary<string, string>? BuildCliEnvironment()
    {
        var extraDirs = new List<string>();

        // Prepend all user-local bin directories so that tools installed without admin
        // (npm global, pipx, user-scope installers) are reachable inside the CLI process
        // even when those directories are not on the current process PATH.
        foreach (var dir in GetUserLocalBinDirs())
        {
            if (Directory.Exists(dir))
                extraDirs.Add(dir);
        }

        if (extraDirs.Count == 0)
            return null;

        var existing  = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var prepended = string.Join(Path.PathSeparator.ToString(),
                            extraDirs.Distinct(StringComparer.OrdinalIgnoreCase))
                        + Path.PathSeparator + existing;

        _log.Debug("Extended CLI PATH with: {Dirs}", string.Join(";", extraDirs));
        return new Dictionary<string, string>(StringComparer.Ordinal) { ["PATH"] = prepended };
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns well-known user-owned bin directories where CLI tools (npm global, pipx,
    /// user-scope installers, etc.) are typically installed without administrator rights.
    /// All paths are under the current user's profile — no registry reads, no WMI, no
    /// elevated access required.  <see cref="File.Exists"/> and
    /// <see cref="Directory.Exists"/> are used for all checks; both return <c>false</c>
    /// on access-denied rather than throwing.
    /// </summary>
    private static IEnumerable<string> GetUserLocalBinDirs()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // All paths below are under the current user's own profile directories —
            // no elevation is required to read from them.
            var appData  = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var home     = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            yield return Path.Combine(appData,  "npm");                    // npm global (default Windows prefix)
            yield return Path.Combine(localApp, "npm");                    // some npm configs use LocalAppData
            yield return Path.Combine(home,     ".npm-global", "bin");     // custom npm prefix
            yield return Path.Combine(home,     "AppData", "Roaming", "npm"); // explicit roaming fallback
            yield return Path.Combine(localApp, "Programs", "nodejs");     // nvm-windows per-user node
        }
        else
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            yield return Path.Combine(home, ".local",      "bin");  // pipx / user installs
            yield return Path.Combine(home, ".npm-global", "bin");  // custom npm prefix
            yield return Path.Combine(home, ".npm",        "bin");  // older npm layouts
            yield return "/usr/local/bin";                           // Homebrew / system npm (world-readable)
        }
    }

    /// <summary>
    /// Builds a <see cref="GitHub.Copilot.SDK.CopilotClientOptions"/> with the resolved
    /// Copilot CLI path and an extended PATH that includes Playwright and npm globals.
    /// Throws <see cref="InvalidOperationException"/> with an actionable message when no
    /// working Copilot CLI can be found, so callers get a clear error instead of a cryptic
    /// SDK-internal "not found" or process-crash exception.
    /// </summary>
    public static GitHub.Copilot.SDK.CopilotClientOptions BuildClientOptions()
    {
        var cliPath = ResolveCopilotCli()
            ?? throw new InvalidOperationException(
                "No working Copilot CLI was found. " +
                "The application tried the bundled runtime path and all 'copilot[.exe]' entries on your PATH. " +
                "One or more PATH entries were skipped because the binary crashed on startup (e.g. the " +
                "WinGet GitHub.Copilot package is known to fail with an ncrypto CSPRNG assertion on some machines). " +
                "To fix this, install the GitHub Copilot CLI via 'npm install -g @github-copilot/cli' and ensure " +
                "the npm global bin directory is on your PATH, then restart the application.");

        var opts = new GitHub.Copilot.SDK.CopilotClientOptions { CliPath = cliPath };
        var env  = BuildCliEnvironment();
        if (env is not null) opts.Environment = env;
        return opts;
    }

    /// <summary>Returns every directory-entry on PATH matching <paramref name="exeName"/>, in order.</summary>
    private static IEnumerable<string> FindAllOnPath(string exeName)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, exeName);
            if (File.Exists(candidate))
                yield return candidate;
        }
    }

    /// <summary>Searches every directory on the current PATH for <paramref name="exeName"/>.</summary>
    private static string? FindOnPath(string exeName) => FindAllOnPath(exeName).FirstOrDefault();

    private static string GetRid()
    {
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64   => "x64",
            Architecture.X86   => "x86",
            Architecture.Arm64 => "arm64",
            Architecture.Arm   => "arm",
            _                  => "x64",
        };

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return $"win-{arch}";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))   return $"linux-{arch}";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))     return $"osx-{arch}";
        return $"unknown-{arch}";
    }
}
