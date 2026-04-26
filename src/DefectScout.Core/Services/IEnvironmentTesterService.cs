using DefectScout.Core.Models;

namespace DefectScout.Core.Services;

public interface IEnvironmentTesterService
{
    /// <summary>
    /// Run a StructuredTestPlan against ALL enabled environments.
    /// Probes for Copilot CLI fleet support first:
    /// - If available: dispatches a single /fleet invocation so all tracks run in parallel
    ///   inside Copilot's own orchestrator, then polls result files to track per-env progress.
    /// - If unavailable: falls back to sequential per-env runs.
    /// Reports live progress per environment via <paramref name="progressList"/>[i].
    /// </summary>
    /// <param name="progressList">One <see cref="IProgress{T}"/> per environment (same order as <paramref name="environments"/>).</param>
    /// <param name="onDispatchMode">Callback invoked once with "fleet" or "sequential" so the UI can display the mode.</param>
    Task<List<TestResult>> RunAllAsync(
        StructuredTestPlan plan,
        IReadOnlyList<KineticEnvironment> environments,
        string screenshotBaseDir,
        string resultsDir,
        PlaywrightOptions opts,
        IReadOnlyList<IProgress<EnvironmentProgress>> progressList,
        Action<string>? onDispatchMode = null,
        AgentRuntimeOptions? runtime = null,
        CancellationToken ct = default);

    /// <summary>
    /// Execute a StructuredTestPlan against ONE Kinetic environment (used by sequential fallback).
    /// </summary>
    Task<TestResult> RunAsync(
        StructuredTestPlan plan,
        KineticEnvironment environment,
        string screenshotDir,
        string resultFile,
        PlaywrightOptions playwrightOptions,
        IProgress<EnvironmentProgress>? progress = null,
        AgentRuntimeOptions? runtime = null,
        CancellationToken ct = default);
}
