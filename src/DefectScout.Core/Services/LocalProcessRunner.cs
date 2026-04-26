using System.Diagnostics;

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

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory,
            },
        };

        process.StartInfo.ArgumentList.Add("-NoProfile");
        process.StartInfo.ArgumentList.Add("-ExecutionPolicy");
        process.StartInfo.ArgumentList.Add("Bypass");
        process.StartInfo.ArgumentList.Add("-Command");
        process.StartInfo.ArgumentList.Add(command);

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
