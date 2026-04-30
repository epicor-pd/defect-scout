using System.Diagnostics;
using System.IO;

namespace DefectScout.Core.Services;

internal static class LocalProcessRunner
{
    public static async Task<CommandResult> RunPowerShellAsync(
        string command,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken ct)
    {
        Directory.CreateDirectory(workingDirectory);
        // Prefer PowerShell Core (pwsh) when available on PATH, fall back to Windows PowerShell.
        static string? FindExecutableOnPath(string exe)
        {
            var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var candidate = Path.Combine(dir, exe);
                    if (File.Exists(candidate)) return candidate;
                }
                catch { }
            }
            return null;
        }

        var exePath = FindExecutableOnPath("pwsh.exe")
                   ?? FindExecutableOnPath("powershell.exe")
                   ?? "powershell.exe";

        var startInfo = new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
        };

        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(command);

        try
        {
            var extraEnv = CliLocator.BuildCliEnvironment();
            if (extraEnv is not null && extraEnv.TryGetValue("PATH", out var prependedPath))
            {
                // Replace the child's PATH with the prepended variant so child process resolves user-local bins first.
                startInfo.Environment["PATH"] = prependedPath;
            }
        }
        catch
        {
            // Best-effort; do not fail process start if environment augmentation is unavailable.
        }

        using var process = new Process { StartInfo = startInfo };

        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            TryKill(process);
            return new CommandResult(-1, await stdoutTask, await stderrTask, TimedOut: true);
        }

        return new CommandResult(process.ExitCode, await stdoutTask, await stderrTask, TimedOut: false);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best effort cleanup after tool timeout.
        }
    }
}

internal sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut)
{
    public bool IsSuccess => ExitCode == 0 && !TimedOut;

    public string ToToolOutput()
    {
        var status = TimedOut ? "TIMED_OUT" : ExitCode == 0 ? "OK" : $"EXIT_{ExitCode}";
        return $"""
            status: {status}
            stdout:
            {StandardOutput}

            stderr:
            {StandardError}
            """;
    }
}
