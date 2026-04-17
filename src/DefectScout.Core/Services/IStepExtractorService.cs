using DefectScout.Core.Models;

namespace DefectScout.Core.Services;

public interface IStepExtractorService
{
    /// <summary>
    /// Parse a ticket (by ID string, raw text, or XML file path) into a StructuredTestPlan.
    /// Reports streaming progress via <paramref name="progress"/>.
    /// </summary>
    Task<StructuredTestPlan> ExtractAsync(
        string ticketIdOrText,
        string? filePath,
        IProgress<string>? progress = null,
        CancellationToken ct = default);
}
