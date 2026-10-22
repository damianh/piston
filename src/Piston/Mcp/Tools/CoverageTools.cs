// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Piston.Engine;
using Piston.Engine.Coverage;
using Piston.Engine.Models;

namespace Piston.Mcp.Tools;

[McpServerToolType]
public sealed class CoverageTools(IEngine engine, IMcpCallRecorder recorder, EngineAvailability? availability = null)
{
    public const string GetCoverageTool = "get_coverage";
    public const string GetChangedLineCoverageTool = "get_changed_line_coverage";
    public const string ExportCoverageTool = "export_coverage";
    public const string PlanAffectedTestsTool = "plan_affected_tests";

    /// <summary>Source file types whose changed lines can be measured.</summary>
    private static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase) { ".cs", ".fs", ".vb" };

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private const string Granularity =
        "Coverage is aggregated over the tests that ran; it does not say which test covered a line.";

    [McpServerTool(Name = GetCoverageTool), Description(
        "Get line and branch coverage as JSON (schema piston.coverage/v1). Without a file: solution totals, per-module " +
        "totals and the files with the most uncovered lines. With a file (absolute or solution-relative): its totals plus " +
        "uncovered and partially covered line ranges. Requires the daemon to run with coverage enabled. " +
        "stale means the file changed since it was measured; partial means only a selective run measured it. " +
        "impactBackfill, when present, is the progress of measuring which test classes execute which lines.")]
    public string GetCoverage(
        [Description("Source file to report on. Omit for the solution summary.")] string? file = null,
        [Description("Most files to list in the summary, worst first (default 50).")] int maxFiles = 50)
    {
        availability?.ThrowIfUnavailable();
        var sw = Stopwatch.StartNew();
        var succeeded = false;
        string? summary = null;
        try
        {
            var report = engine.GetCoverageReport()
                ?? throw new McpException("Coverage is disabled. Start Piston with --coverage or set coverageEnabled in .piston.json.");
            var solutionDirectory = SolutionDirectory();
            JsonObject json;
            if (string.IsNullOrWhiteSpace(file))
            {
                json = Summary(report, solutionDirectory, Math.Clamp(maxFiles, 0, 1000));
                if (engine.State.ImpactBackfill is { } backfill)
                {
                    // Until it finishes, tests not yet measured on their own are selected per project.
                    json["impactBackfill"] = new JsonObject
                    {
                        ["granularity"] = backfill.Granularity,
                        ["completed"] = backfill.Completed,
                        ["total"] = backfill.Total,
                        ["current"] = backfill.CurrentUnit,
                    };
                }

                summary = $"{report.Files.Count} file(s), {Percent(report.LinesCovered, report.LinesTotal)}% lines";
            }
            else
            {
                var coverage = engine.GetFileCoverage(file)
                    ?? throw new McpException($"No coverage for '{file}'. It may not be compiled into a tested assembly, or no run has measured it yet.");
                json = FileDetail(coverage, solutionDirectory);
                summary = $"{coverage.File.LinesCovered}/{coverage.File.LinesTotal} lines";
            }

            succeeded = true;
            return json.ToJsonString(WriteOptions);
        }
        finally
        {
            sw.Stop();
            recorder.Record(GetCoverageTool, file, summary, sw.Elapsed.TotalMilliseconds, succeeded);
        }
    }

    [McpServerTool(Name = GetChangedLineCoverageTool), Description(
        "Get coverage of the lines you changed, as JSON (schema piston.changed-line-coverage/v1). Compares the working " +
        "tree (staged, unstaged and untracked files) with HEAD, or with the merge base of baseRef and HEAD. For each " +
        "changed source file: covered, partially covered and uncovered changed lines (non-executable lines are ignored). " +
        "Files changed since the last run that measured them are reported as stale with no line verdicts: run the tests " +
        "first. Requires the daemon to run with coverage enabled.")]
    public async Task<string> GetChangedLineCoverageAsync(
        [Description("Branch or commit to compare against, e.g. main or origin/main. Defaults to HEAD.")] string? baseRef = null,
        CancellationToken ct = default)
    {
        availability?.ThrowIfUnavailable();
        var sw = Stopwatch.StartNew();
        var succeeded = false;
        string? summary = null;
        try
        {
            if (engine.GetCoverageReport() is null)
            {
                throw new McpException("Coverage is disabled. Start Piston with --coverage or set coverageEnabled in .piston.json.");
            }

            if (!string.IsNullOrWhiteSpace(baseRef) && !ChangedLines.IsValidRevision(baseRef))
            {
                throw new McpException($"'{baseRef}' is not a valid git revision.");
            }

            var solutionDirectory = SolutionDirectory()
                ?? throw new McpException("No solution is loaded.");
            ChangedLinesResult changes;
            try
            {
                changes = await ChangedLines.GetAsync(solutionDirectory, baseRef, ct).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                throw new McpException(ex.Message);
            }

            var json = ChangedLineCoverage(changes, solutionDirectory, engine.GetFileCoverage);
            summary = $"{json["totals"]!["uncovered"]} uncovered of {json["totals"]!["executable"]} changed executable line(s)";
            succeeded = true;
            return json.ToJsonString(WriteOptions);
        }
        finally
        {
            sw.Stop();
            recorder.Record(GetChangedLineCoverageTool, baseRef, summary, sw.Elapsed.TotalMilliseconds, succeeded);
        }
    }

    internal static JsonObject ChangedLineCoverage(
        ChangedLinesResult changes, string solutionDirectory, Func<string, FileCoverageSnapshot?> getCoverage)
    {
        int executable = 0, covered = 0, partial = 0, uncovered = 0;
        var files = new JsonArray();
        var notMeasured = new JsonArray();
        var stale = new JsonArray();
        foreach (var (path, lines) in changes.Files.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            if (!CodeExtensions.Contains(Path.GetExtension(path)))
            {
                continue;
            }

            var relative = Relative(path, solutionDirectory);
            if (getCoverage(path) is not { } coverage)
            {
                notMeasured.Add(relative);
                continue;
            }

            if (coverage.File.IsStale)
            {
                stale.Add(relative);
                continue;
            }

            var byLine = coverage.Lines.ToDictionary(l => l.LineNumber);
            var changed = lines.Where(byLine.ContainsKey).Select(n => byLine[n]).ToList();
            if (changed.Count == 0)
            {
                continue;
            }

            var fileUncovered = changed.Where(l => l.HitCount == 0).Select(l => l.LineNumber).ToList();
            var filePartial = changed.Where(IsPartial).ToList();
            var fileCovered = changed.Count - fileUncovered.Count - filePartial.Count;
            executable += changed.Count;
            covered += fileCovered;
            partial += filePartial.Count;
            uncovered += fileUncovered.Count;
            files.Add(new JsonObject
            {
                ["path"] = relative,
                ["changedExecutableLines"] = changed.Count,
                ["covered"] = fileCovered,
                ["uncoveredLines"] = ChangedLines.FormatRanges(fileUncovered),
                ["partialLines"] = PartialLines(filePartial),
                ["isPartial"] = coverage.File.IsPartial,
            });
        }

        return new JsonObject
        {
            ["schema"] = "piston.changed-line-coverage/v1",
            ["base"] = changes.Basis,
            ["granularity"] = Granularity,
            ["totals"] = new JsonObject
            {
                ["executable"] = executable,
                ["covered"] = covered,
                ["partial"] = partial,
                ["uncovered"] = uncovered,
            },
            ["files"] = files,
            ["staleFiles"] = stale,
            ["notMeasuredFiles"] = notMeasured,
        };
    }

    [McpServerTool(Name = PlanAffectedTestsTool), Description(
        "Preview which projects and tests Piston would build and run for a set of changed files, as JSON (schema " +
        "piston.affected-plan/v1). Runs nothing and changes no state. Without files, uses the working tree's changes " +
        "against HEAD (or the merge base with baseRef). scope is full (every test), projects (every test in the listed " +
        "test projects) or tests (only the listed tests); explanation says why. With coverage, each selected test lists " +
        "the changed lines that selected it and the granularity of the evidence: run- or class-level evidence " +
        "over-selects and does not mean that test executed those lines.")]
    public async Task<string> PlanAffectedTestsAsync(
        [Description("Changed files, absolute or relative to the solution directory. Omit to use git changes.")] string[]? files = null,
        [Description("When files is omitted: branch or commit to compare against. Defaults to HEAD.")] string? baseRef = null,
        CancellationToken ct = default)
    {
        availability?.ThrowIfUnavailable();
        var sw = Stopwatch.StartNew();
        var succeeded = false;
        string? summary = null;
        try
        {
            var solutionDirectory = SolutionDirectory()
                ?? throw new McpException("No solution is loaded.");
            string basis;
            IReadOnlyList<string> changed;
            if (files is { Length: > 0 })
            {
                basis = "files";
                changed = [.. files.Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => Path.GetFullPath(f, solutionDirectory))];
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(baseRef) && !ChangedLines.IsValidRevision(baseRef))
                {
                    throw new McpException($"'{baseRef}' is not a valid git revision.");
                }

                ChangedLinesResult changes;
                try
                {
                    changes = await ChangedLines.GetAsync(solutionDirectory, baseRef, ct).ConfigureAwait(false);
                }
                catch (InvalidOperationException ex)
                {
                    throw new McpException(ex.Message);
                }

                basis = changes.Basis;
                changed = [.. changes.Files.Keys.Order(StringComparer.Ordinal)];
            }

            JsonObject json;
            if (changed.Count == 0)
            {
                json = AffectedPlan(basis, changed,
                    new ImpactAnalysisResult([], [], RequiresGraphRebuild: false, IsFullRun: false)
                    {
                        Explanation = "No file changed, so nothing would run.",
                    },
                    solutionDirectory);
            }
            else
            {
                var plan = engine.PlanAffected(changed)
                    ?? throw new McpException("This engine cannot plan affected tests.");
                json = AffectedPlan(basis, changed, plan, solutionDirectory);
            }

            summary = $"{json["scope"]}: {json["explanation"]}";
            succeeded = true;
            return json.ToJsonString(WriteOptions);
        }
        finally
        {
            sw.Stop();
            recorder.Record(PlanAffectedTestsTool, files is { Length: > 0 } ? string.Join(", ", files) : baseRef, summary,
                sw.Elapsed.TotalMilliseconds, succeeded);
        }
    }

    internal static JsonObject AffectedPlan(
        string basis, IReadOnlyList<string> changedFiles, ImpactAnalysisResult plan, string solutionDirectory)
    {
        var explanation = plan.RunExplanation ?? string.Empty;
        var fqns = plan.AffectedTestFqns ?? [];
        var narrowed = plan.NarrowsToTests;
        var scope = plan.IsFullRun ? "full"
            : narrowed ? "tests"
            : plan.AffectedTestProjectPaths.Count > 0 ? "projects"
            : "none";
        var tests = new JsonArray();
        if (narrowed)
        {
            foreach (var fqn in fqns.Order(StringComparer.Ordinal))
            {
                var test = new JsonObject { ["name"] = fqn };
                if (plan.SelectionReasons?.GetValueOrDefault(fqn) is { } reasons)
                {
                    test["reasons"] = new JsonArray([.. reasons.Select(r => (JsonNode)new JsonObject
                    {
                        ["file"] = Relative(r.FilePath, solutionDirectory),
                        ["changedLines"] = r.ChangedLines,
                        ["executedLines"] = r.ExecutedLines,
                        ["granularity"] = r.Granularity,
                        ["project"] = Relative(r.ProjectPath, solutionDirectory),
                    })]);
                }

                tests.Add(test);
            }
        }

        return new JsonObject
        {
            ["schema"] = "piston.affected-plan/v1",
            ["base"] = basis,
            ["changedFiles"] = new JsonArray([.. changedFiles.Select(f => (JsonNode)Relative(f, solutionDirectory)!)]),
            ["scope"] = scope,
            ["explanation"] = explanation,
            ["buildProjects"] = new JsonArray([.. plan.AffectedProjectPaths
                .Select(p => Relative(p, solutionDirectory)).Order(StringComparer.Ordinal).Select(p => (JsonNode)p!)]),
            ["testProjects"] = new JsonArray([.. plan.AffectedTestProjectPaths
                .Select(p => Relative(p, solutionDirectory)).Order(StringComparer.Ordinal).Select(p => (JsonNode)p!)]),
            ["tests"] = tests,
        };
    }

    internal static JsonObject Summary(CoverageReportSnapshot report, string? solutionDirectory, int maxFiles)
    {
        var modules = new JsonArray();
        foreach (var module in report.Files.GroupBy(f => f.Module ?? "(unknown)").OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var totals = Totals(module.Sum(f => f.LinesTotal), module.Sum(f => f.LinesCovered),
                module.Sum(f => f.BranchesTotal), module.Sum(f => f.BranchesCovered));
            totals["module"] = module.Key;
            totals["files"] = module.Count();
            modules.Add(totals);
        }

        var worst = new JsonArray();
        foreach (var file in report.Files
            .Where(f => f.LinesCovered < f.LinesTotal)
            .OrderByDescending(f => f.LinesTotal - f.LinesCovered)
            .ThenBy(f => f.FilePath, StringComparer.Ordinal)
            .Take(maxFiles))
        {
            worst.Add(FileTotals(file, solutionDirectory));
        }

        return new JsonObject
        {
            ["schema"] = "piston.coverage/v1",
            ["generatedAt"] = report.GeneratedAt?.ToString("O", CultureInfo.InvariantCulture),
            ["granularity"] = Granularity,
            ["totals"] = Totals(report.LinesTotal, report.LinesCovered, report.BranchesTotal, report.BranchesCovered),
            ["fileCount"] = report.Files.Count,
            ["staleFiles"] = report.Files.Count(f => f.IsStale),
            ["partialFiles"] = report.Files.Count(f => f.IsPartial),
            ["modules"] = modules,
            ["filesWithMostUncoveredLines"] = worst,
        };
    }

    internal static JsonObject FileDetail(FileCoverageSnapshot coverage, string? solutionDirectory)
    {
        var json = FileTotals(coverage.File, solutionDirectory);
        json["schema"] = "piston.coverage/v1";
        json["granularity"] = Granularity;
        json["uncoveredLines"] = ChangedLines.FormatRanges(coverage.Lines.Where(l => l.HitCount == 0).Select(l => l.LineNumber));
        json["partialLines"] = PartialLines(coverage.Lines.Where(IsPartial));
        return json;
    }

    private static bool IsPartial(LineCoverage line) =>
        line.HitCount > 0 && line.BranchesTotal > 0 && line.BranchesCovered < line.BranchesTotal;

    private static JsonArray PartialLines(IEnumerable<LineCoverage> lines) =>
        [.. lines.Select(l => (JsonNode)new JsonObject
        {
            ["line"] = l.LineNumber,
            ["branches"] = $"{l.BranchesCovered}/{l.BranchesTotal}",
        })];

    private static JsonObject FileTotals(CoverageFileSummary file, string? solutionDirectory)
    {
        var json = Totals(file.LinesTotal, file.LinesCovered, file.BranchesTotal, file.BranchesCovered);
        json["path"] = Relative(file.FilePath, solutionDirectory);
        json["module"] = file.Module;
        json["isStale"] = file.IsStale;
        json["isPartial"] = file.IsPartial;
        return json;
    }

    private static JsonObject Totals(int lines, int linesCovered, int branches, int branchesCovered) => new()
    {
        ["lines"] = lines,
        ["linesCovered"] = linesCovered,
        ["linePercent"] = Percent(linesCovered, lines),
        ["branches"] = branches,
        ["branchesCovered"] = branchesCovered,
        ["branchPercent"] = Percent(branchesCovered, branches),
    };

    private static double Percent(int covered, int total) =>
        total == 0 ? 100 : Math.Round(100.0 * covered / total, 1);

    [McpServerTool(Name = ExportCoverageTool), Description(
        "Write the coverage report to a file as LCOV (lcov.info) or Cobertura XML, for editors, CI or ReportGenerator. " +
        "Files changed since they were measured are left out. Returns the path written and the file counts. " +
        "Requires the daemon to run with coverage enabled.")]
    public string ExportCoverage(
        [Description("lcov or cobertura (default lcov).")] string format = "lcov",
        [Description("Where to write, absolute or solution-relative, inside the solution directory. " +
            "Defaults to .piston/coverage/lcov.info or .piston/coverage/cobertura.xml.")] string? outputPath = null)
    {
        availability?.ThrowIfUnavailable();
        var sw = Stopwatch.StartNew();
        var succeeded = false;
        string? summary = null;
        try
        {
            if (!CoverageExporter.TryParseFormat(format, out var exportFormat))
            {
                throw new McpException("format must be 'lcov' or 'cobertura'.");
            }

            var solutionDirectory = SolutionDirectory()
                ?? throw new McpException("The daemon has no solution loaded.");
            var target = ResolveOutputPath(outputPath, solutionDirectory, CoverageExporter.FileName(exportFormat));
            var export = CoverageExporter.Export(engine, exportFormat)
                ?? throw new McpException("Coverage is disabled. Start Piston with --coverage or set coverageEnabled in .piston.json.");

            _ = Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, export.Content);

            var json = new JsonObject
            {
                ["schema"] = "piston.coverage-export/v1",
                ["format"] = exportFormat.ToString().ToLowerInvariant(),
                ["path"] = target,
                ["files"] = export.FileCount,
                ["staleFilesSkipped"] = export.StaleFilesSkipped,
            };
            summary = $"{export.FileCount} file(s) -> {Relative(target, solutionDirectory)}";
            succeeded = true;
            return json.ToJsonString(WriteOptions);
        }
        finally
        {
            sw.Stop();
            recorder.Record(ExportCoverageTool, $"{format} {outputPath}".Trim(), summary, sw.Elapsed.TotalMilliseconds, succeeded);
        }
    }

    /// <summary>Resolves an export path, refusing paths outside <paramref name="solutionDirectory"/>.</summary>
    internal static string ResolveOutputPath(string? outputPath, string solutionDirectory, string defaultFileName)
    {
        var root = Path.GetFullPath(solutionDirectory);
        var target = string.IsNullOrWhiteSpace(outputPath)
            ? Path.Combine(root, ".piston", "coverage", defaultFileName)
            : Path.GetFullPath(outputPath, root);
        var relative = Path.GetRelativePath(root, target);
        if (relative is "." or ".." || Path.IsPathRooted(relative)
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relative.StartsWith("../", StringComparison.Ordinal))
        {
            throw new McpException($"outputPath must be a file inside the solution directory ({root}).");
        }

        return target;
    }

    private static string Relative(string path, string? solutionDirectory) =>
        solutionDirectory is null ? path : Path.GetRelativePath(solutionDirectory, path).Replace('\\', '/');

    private string? SolutionDirectory() =>
        engine.State.SolutionPath is { } solutionPath ? Path.GetDirectoryName(Path.GetFullPath(solutionPath)) : null;
}
