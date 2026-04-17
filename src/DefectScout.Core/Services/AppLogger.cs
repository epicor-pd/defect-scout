using Serilog;
using Serilog.Events;

namespace DefectScout.Core.Services;

/// <summary>
/// Static gateway to the application-wide Serilog logger.
/// Call <see cref="Initialize"/> once at startup (before any service is used).
/// All Core services use <c>AppLogger.For&lt;T&gt;()</c> to obtain a contextual logger.
/// </summary>
public static class AppLogger
{
    private static string? _currentLogDir;

    /// <summary>
    /// Configure Serilog with a console sink and a rolling-file sink.
    /// Safe to call multiple times — re-initializes only when <paramref name="logDir"/> changes.
    /// </summary>
    /// <param name="logDir">Absolute path to the log folder.</param>
    /// <param name="minimumLevel">Minimum log level (default: Debug).</param>
    public static void Initialize(string logDir, LogEventLevel minimumLevel = LogEventLevel.Debug)
    {
        if (string.Equals(_currentLogDir, logDir, StringComparison.OrdinalIgnoreCase)) return;
        _currentLogDir = logDir;

        Directory.CreateDirectory(logDir);

        var logFile = Path.Combine(logDir, "defect-scout-.log");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(minimumLevel)
            .Enrich.WithProperty("App", "DefectScout")
            .WriteTo.Console(
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(
                logFile,
                rollingInterval: RollingInterval.Day,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}",
                retainedFileCountLimit: 14)
            .CreateLogger();

        Log.Information("DefectScout logging initialized. Log directory: {LogDir}", logDir);
    }

    /// <summary>Returns a contextual logger for type <typeparamref name="T"/>.</summary>
    public static ILogger For<T>() => Log.ForContext<T>();

    /// <summary>Flush all sinks on application exit.</summary>
    public static void CloseAndFlush() => Log.CloseAndFlush();
}
