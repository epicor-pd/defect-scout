using System.Text;
using DefectScout.Core.Models;
using Serilog;

namespace DefectScout.Core.Services;

/// <summary>
/// Generates a Defect Scout report from test results.
/// Writes both <c>.md</c> (for version control / review) and
/// <c>.html</c> (for browser viewing) side by side.
/// Returns the path to the HTML file so the UI can open it in a browser directly.
/// </summary>
public sealed class ReportService : IReportService
{
    private static readonly ILogger _log = AppLogger.For<ReportService>();

    public async Task<string> GenerateAsync(
        StructuredTestPlan plan,
        IReadOnlyList<TestResult> results,
        string reportDir,
        string screenshotBaseDir,
        CancellationToken ct = default)
    {
        _log.Information("GenerateAsync: ticket={Ticket}, results={Count}, outputDir={Dir}",
            plan.Ticket, results.Count, reportDir);
        Directory.CreateDirectory(reportDir);

        var md = BuildMarkdown(plan, results, screenshotBaseDir);

        var mdPath   = Path.Combine(reportDir, $"{plan.Ticket}-report.md");
        var htmlPath = Path.Combine(reportDir, $"{plan.Ticket}-report.html");

        await File.WriteAllTextAsync(mdPath, md, ct);
        _log.Debug("Report markdown written to {Path}", mdPath);

        await File.WriteAllTextAsync(htmlPath, WrapHtml(md), ct);
        _log.Debug("Report HTML written to {Path}", htmlPath);

        _log.Information("GenerateAsync complete: {HtmlPath}", htmlPath);
        return htmlPath;
    }

    // ── Markdown builder ─────────────────────────────────────────────────────

    private static string BuildMarkdown(
        StructuredTestPlan plan,
        IReadOnlyList<TestResult> results,
        string screenshotBaseDir)
    {
        var sb = new StringBuilder();
        var today = DateTime.Today.ToString("yyyy-MM-dd");

        var reproduced = results.Where(r => r.IsReproduced).ToList();
        var notReproduced = results.Where(r => r.IsNotReproduced).ToList();
        var errors = results.Where(r => r.IsError).ToList();

        // ── Header ───────────────────────────────────────────────────────────
        sb.AppendLine($"# Defect Scout Report: {plan.Ticket}");
        sb.AppendLine();
        sb.AppendLine($"**Date**: {today}");
        sb.AppendLine($"**Ticket**: [{plan.Ticket}](https://epicor.atlassian.net/browse/{plan.Ticket})");
        sb.AppendLine($"**Summary**: {plan.Summary}");
        sb.AppendLine($"**Affected Module**: {plan.AffectedModule}");
        sb.AppendLine($"**Affected BO**: {plan.AffectedBO ?? "Not identified"}");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();

        // ── Version Impact Matrix ─────────────────────────────────────────────
        sb.AppendLine("## Version Impact Matrix");
        sb.AppendLine();
        sb.AppendLine("| Environment | Version | Result | Screenshots |");
        sb.AppendLine("|-------------|---------|--------|-------------|");

        foreach (var r in results)
        {
            var icon = r.IsReproduced ? "✅ REPRODUCED"
                     : r.IsNotReproduced ? "❌ NOT REPRODUCED"
                     : $"⚠ ERROR";

            var ssLink = r.IsError
                ? $"Error: {r.Error}"
                : $"[View]({screenshotBaseDir}/{r.Version.Replace('.', '-')}/{plan.Ticket}/)";

            sb.AppendLine($"| {r.EnvName} | {r.Version} | {icon} | {ssLink} |");
        }

        sb.AppendLine();
        sb.AppendLine($"> **{reproduced.Count} of {results.Count} environments reproduced the defect.**");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();

        // ── Defect Present In ─────────────────────────────────────────────────
        sb.AppendLine("## Defect Present In");
        sb.AppendLine();
        if (reproduced.Count == 0)
        {
            sb.AppendLine("*None — defect was not reproduced in any tested environment.*");
        }
        else
        {
            foreach (var r in reproduced)
            {
                var failStep = r.StepResults.FirstOrDefault(s => !s.Passed);
                sb.AppendLine($"- **{r.Version}** — {r.EnvName}: {failStep?.Notes ?? "Defect observed"}");
            }
        }
        sb.AppendLine();

        // ── Defect NOT Present In ─────────────────────────────────────────────
        sb.AppendLine("## Defect NOT Present In");
        sb.AppendLine();
        if (notReproduced.Count == 0)
        {
            sb.AppendLine("*None — defect was reproduced (or errored) in all tested environments.*");
        }
        else
        {
            foreach (var r in notReproduced)
                sb.AppendLine($"- **{r.Version}** — {r.EnvName}: All steps passed; expected behaviour observed.");
        }
        sb.AppendLine();

        if (errors.Count > 0)
        {
            sb.AppendLine("## Environments That Could Not Be Tested");
            sb.AppendLine();
            foreach (var r in errors)
                sb.AppendLine($"- **{r.Version}** — {r.EnvName}: {r.Error}");
            sb.AppendLine();
        }

        sb.AppendLine("---");
        sb.AppendLine();

        // ── Reproduction Steps ────────────────────────────────────────────────
        sb.AppendLine("## Reproduction Steps");
        sb.AppendLine();
        if (plan.Preconditions.Count > 0)
        {
            sb.AppendLine("**Preconditions**:");
            foreach (var pre in plan.Preconditions)
                sb.AppendLine($"- {pre}");
            sb.AppendLine();
        }

        sb.AppendLine("| # | Action | Target | Expected After Step |");
        sb.AppendLine("|---|--------|--------|---------------------|");
        foreach (var step in plan.Steps)
        {
            var discriminating = step.IsDiscriminatingStep ? " ⚠ *(discriminating)*" : string.Empty;
            sb.AppendLine($"| {step.StepNumber} | {step.Action} | {Escape(step.Target)} | {Escape(step.Expected)}{discriminating} |");
        }
        sb.AppendLine();
        sb.AppendLine($"**Expected Result**: {plan.ExpectedResult}");
        sb.AppendLine($"**Actual Result (defect)**: {plan.ActualResult}");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();

        // ── Per-environment step results ──────────────────────────────────────
        sb.AppendLine("## Per-Environment Step Results");
        sb.AppendLine();

        foreach (var r in results)
        {
            var statusBadge = r.IsReproduced ? "✅ REPRODUCED"
                            : r.IsNotReproduced ? "❌ NOT REPRODUCED"
                            : "⚠ ERROR";

            var versionSlug = r.Version.Replace('.', '-');
            sb.AppendLine($"### {r.EnvName} — {statusBadge} {{#steps-{versionSlug}}}");
            sb.AppendLine();

            if (r.StepResults.Count > 0)
            {
                sb.AppendLine("| Step | Action | Target | Result | Notes |");
                sb.AppendLine("|------|--------|--------|--------|-------|");

                foreach (var sr in r.StepResults)
                {
                    var planStep = plan.Steps.FirstOrDefault(s => s.StepNumber == sr.StepNumber);
                    var resultIcon = sr.Passed ? "✅ Pass" : "❌ **FAIL**";
                    if (!sr.Passed && (planStep?.IsDiscriminatingStep == true))
                        resultIcon += " ⚠";
                    sb.AppendLine($"| {sr.StepNumber} | {sr.Action} | {Escape(planStep?.Target ?? "?")} | {resultIcon} | {Escape(sr.Notes)} |");
                }
            }
            else if (r.IsError)
            {
                sb.AppendLine($"**Error**: {r.Error}");
            }

            sb.AppendLine();

            if (r.IsReproduced)
            {
                var failStep = r.StepResults.FirstOrDefault(s => !s.Passed);
                sb.AppendLine($"**Defect observed**: {failStep?.Notes ?? plan.ActualResult}");
                sb.AppendLine();
            }

            sb.AppendLine("---");
            sb.AppendLine();
        }

        // ── Screenshots ───────────────────────────────────────────────────────
        sb.AppendLine("## Screenshots");
        sb.AppendLine();
        sb.AppendLine($"> Screenshots are organised by version and ticket under: `{screenshotBaseDir}`");
        sb.AppendLine($"> Path pattern: `{{screenshotBaseDir}}/{{version-slug}}/{plan.Ticket}/step-NN-action.png`");
        sb.AppendLine();

        foreach (var r in results.Where(r => r.ScreenshotPaths.Count > 0))
        {
            var versionSlug = r.Version.Replace('.', '-');
            sb.AppendLine($"### {r.EnvName} (v{r.Version}) {{#screenshots-{versionSlug}}}");
            sb.AppendLine();
            sb.AppendLine("| Step | Screenshot | Outcome |");
            sb.AppendLine("|------|-----------|---------|");

            foreach (var ss in r.ScreenshotPaths)
            {
                var fileName = Path.GetFileName(ss);
                var stepNum = ExtractStepNumber(fileName);
                var stepResult = r.StepResults.FirstOrDefault(s => s.StepNumber == stepNum);
                var outcome = stepResult is null ? "–" : (stepResult.Passed ? "✅" : "❌");
                sb.AppendLine($"| {stepNum} | {fileName} | {outcome} |");
            }
            sb.AppendLine();
        }

        // ── Conclusion ────────────────────────────────────────────────────────
        sb.AppendLine("## Conclusion");
        sb.AppendLine();
        sb.AppendLine(BuildConclusion(plan, results, reproduced, notReproduced, errors));
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine($"*Generated by Defect Scout on {today}*");

        return sb.ToString();
    }

    private static string BuildConclusion(
        StructuredTestPlan plan,
        IReadOnlyList<TestResult> all,
        List<TestResult> reproduced,
        List<TestResult> notReproduced,
        List<TestResult> errors)
    {
        if (errors.Count > 0 && reproduced.Count == 0 && notReproduced.Count == 0)
            return $"**Inconclusive**: All {all.Count} environments produced errors. " +
                   "Review the error details and retry before concluding.";

        if (reproduced.Count > 0 && notReproduced.Count == 0)
            return $"**Long-standing defect (all versions)**: The defect was reproduced in all " +
                   $"{reproduced.Count} tested environments. This is not a recent regression. " +
                   "Recommended action: create a targeted fix.";

        if (reproduced.Count > 0 && notReproduced.Count > 0)
        {
            var lastClean = notReproduced.OrderByDescending(r => r.Version).First().Version;
            var firstAffected = reproduced.OrderBy(r => r.Version).First().Version;
            return $"**Regression (partial coverage)**: The defect is present in " +
                   $"{string.Join(", ", reproduced.Select(r => r.Version))} " +
                   $"but NOT in {string.Join(", ", notReproduced.Select(r => r.Version))}. " +
                   $"This suggests a regression introduced between v{lastClean} and v{firstAffected}. " +
                   "Recommended action: git-bisect or compare changelists between these versions.";
        }

        if (notReproduced.Count == all.Count)
            return $"**Could not reproduce**: The defect was NOT reproduced in any of the " +
                   $"{all.Count} tested environments. The ticket steps may need clarification or " +
                   "the defect may require specific data conditions not captured in the test plan.";

        return $"**Inconclusive**: Only {reproduced.Count + notReproduced.Count} of {all.Count} " +
               "environments produced usable results. Review screenshot folders and retry failed " +
               "environments before concluding.";
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string Escape(string? text) =>
        (text ?? string.Empty).Replace("|", "\\|").Replace("\n", " ");

    private static int ExtractStepNumber(string fileName)
    {
        var parts = Path.GetFileNameWithoutExtension(fileName).Split('-');
        if (parts.Length >= 2 && int.TryParse(parts[1], out var n)) return n;
        return 0;
    }

    /// <summary>Wraps rendered HTML body in a minimal full-page HTML document.</summary>
    private static string WrapHtml(string markdownSource)
    {
        // Convert markdown to HTML via a simple inline conversion
        // (avoids a Markdig dependency in the Core project)
        var lines = markdownSource.Split('\n');
        var body = new StringBuilder();
        bool inCodeBlock = false;
        bool inTable = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');

            if (line.StartsWith("```"))
            {
                inCodeBlock = !inCodeBlock;
                body.AppendLine(inCodeBlock ? "<pre><code>" : "</code></pre>");
                continue;
            }
            if (inCodeBlock) { body.AppendLine(System.Net.WebUtility.HtmlEncode(line)); continue; }

            if (line.StartsWith("|"))
            {
                if (!inTable) { inTable = true; body.AppendLine("<table>"); }
                if (line.Replace("|", "").Replace("-", "").Replace(" ", "").Length == 0)
                    continue; // separator row
                var cells = line.Split('|').Skip(1).SkipLast(1).ToArray();
                body.Append("<tr>");
                foreach (var cell in cells) body.Append($"<td>{cell.Trim()}</td>");
                body.AppendLine("</tr>");
                continue;
            }
            else if (inTable)
            {
                inTable = false;
                body.AppendLine("</table>");
            }

            if (line.StartsWith("### ")) { body.AppendLine($"<h3>{line[4..]}</h3>"); continue; }
            if (line.StartsWith("## "))  { body.AppendLine($"<h2>{line[3..]}</h2>"); continue; }
            if (line.StartsWith("# "))   { body.AppendLine($"<h1>{line[2..]}</h1>"); continue; }
            if (line.StartsWith("---"))  { body.AppendLine("<hr/>"); continue; }
            if (string.IsNullOrWhiteSpace(line)) { body.AppendLine("<br/>"); continue; }

            // Inline bold/italic/code
            var html = System.Net.WebUtility.HtmlEncode(line);
            html = System.Text.RegularExpressions.Regex.Replace(html, @"\*\*(.+?)\*\*", "<strong>$1</strong>");
            html = System.Text.RegularExpressions.Regex.Replace(html, @"`(.+?)`", "<code>$1</code>");
            body.AppendLine($"<p>{html}</p>");
        }
        if (inTable) body.AppendLine("</table>");

        var css = """
            body { font-family: Segoe UI, Arial, sans-serif; margin: 24px; color: #222; }
            table { border-collapse: collapse; width: 100%; margin: 12px 0; }
            th { background: #0078D4; color: white; padding: 8px; text-align: left; }
            td { padding: 8px; border: 1px solid #ddd; }
            tr:nth-child(even) { background: #f5f5f5; }
            h1,h2,h3 { color: #0078D4; }
            code { background: #f4f4f4; padding: 2px 4px; border-radius: 3px; font-family: Consolas, monospace; }
            pre  { background: #f4f4f4; padding: 12px; border-radius: 6px; overflow-x: auto; }
            hr   { border: none; border-top: 1px solid #ddd; margin: 16px 0; }
            strong { color: #111; }
            """;

        var output = new StringBuilder();
        output.AppendLine("<!DOCTYPE html><html><head><meta charset='utf-8'/>");
        output.AppendLine("<title>Defect Scout Report</title>");
        output.AppendLine($"<style>{css}</style></head><body>");
        output.Append(body);
        output.AppendLine("</body></html>");
        return output.ToString();
    }
}
