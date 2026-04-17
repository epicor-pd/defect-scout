using DefectScout.Core.Models;

namespace DefectScout.Core.Services;

public interface IReportService
{
    /// <summary>
    /// Generate a Defect Scout markdown report and save it to disk.
    /// Returns the path of the saved report file.
    /// </summary>
    Task<string> GenerateAsync(
        StructuredTestPlan plan,
        IReadOnlyList<TestResult> results,
        string reportDir,
        string screenshotBaseDir,
        CancellationToken ct = default);
}
