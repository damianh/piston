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
    public const string RunAffectedTestsTool = "run_affected_tests";

    private const string BaseDescription =
        "What to compare: working (staged, unstaged and untracked changes against HEAD), branch (everything since the " +
        "branch left baseRef, uncommitted changes included; baseRef defaults to origin/HEAD, else origin/main, " +
        "origin/master, main or master) or range (baseRef is A..B, A...B or one commit). Defaults to working, or " +
        "branch when baseRef is set.";

    private const string BaseRefDescription =
        "For branch: the branch or commit to compare against, e.g. main or origin/main. For range: the range or commit.";

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private const string Granularity =
        "Coverage is aggregated over the tests that ran; it does not say which test covered a line.";

    [McpServerTool(Name = GetCoverageTool), Description(
        "Get line and branch coverage as JSON (schema piston.coverage/v1). Without a file: solution totals, per-module " +
        "totals and the files with the most uncovered lines. With a file (absolute or solution-relative): its totals plus " +
        "uncovered and partially covered line ranges. Requires the daemon to run with coverage enabled. " +
        "stale means the file changed since it was measured; partial means only a selective run measured it. " +
        "impactBackfill, when present, is the progress of measuring which tests execute which lines.")]
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
        "Get a verdict for each line you changed, as JSON (schema piston.changed-line-coverage/v2). Compares the " +
        "working tree (staged, unstaged and untracked files) with HEAD, or with the merge base of baseRef and HEAD, or " +
        "a commit range (files whose range version differs from the working tree are unknown: stale). Each changed " +
        "line of a source file is coveredByPassing (it ran and every current test credited with executing it passed), " +
        "coveredOnlyByFailing (every such test failed; failingTests lists them), uncovered, nonExecutable, or unknown " +
        "with a reason: stale (the line's code changed after it was measured: run the tests; lines in unchanged C# " +
        "members keep their remapped coverage), notMeasured, noTestEvidence, " +
        "mixedEvidence, testsNotRun, outdatedEvidence or resultsUpdating (a run was publishing results: ask again). " +
        "Only coveredByPassing shows a line is exercised by passing tests; it uses each credited test's latest " +
        "result, so hits kept from an earlier run of unchanged code are judged by later results. Requires the daemon " +
        "to run with coverage enabled.")]
    public async Task<string> GetChangedLineCoverageAsync(
        [Description(BaseRefDescription)] string? baseRef = null,
        [Description(BaseDescription)] string? @base = null,
        CancellationToken ct = default)
    {
        availability?.ThrowIfUnavailable();
        var sw = Stopwatch.StartNew();
        var succeeded = false;
        string? summary = null;
        try
        {
            // The flag, not the report: reading the report waits on the lock a run holds while storing coverage.
            if (!engine.State.CoverageEnabled)
            {
                throw new McpException("Coverage is disabled. Start Piston with --coverage or set coverageEnabled in .piston.json.");
            }

            var solutionDirectory = SolutionDirectory()
                ?? throw new McpException("No solution is loaded.");
            ChangedLineCoverageResult result;
            try
            {
                result = await ChangedLineVerdicts.GetAsync(engine, solutionDirectory, @base, baseRef, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                throw new McpException(ex.Message);
            }

            var totals = result.Totals;
            summary = $"{totals.CoveredByPassing} covered by passing, {totals.Uncovered} uncovered, " +
                $"{totals.Unknown} unknown of {totals.ChangedLines} changed line(s)";
            succeeded = true;
            return ChangedLineCoverage(result, solutionDirectory).ToJsonString(WriteOptions);
        }
        finally
        {
            sw.Stop();
            recorder.Record(GetChangedLineCoverageTool, baseRef, summary, sw.Elapsed.TotalMilliseconds, succeeded);
        }
    }

    /// <summary>Most failing tests listed per file.</summary>
    internal const int MaxFailingTestsPerFile = 20;

    internal static JsonObject ChangedLineCoverage(ChangedLineCoverageResult result, string solutionDirectory)
    {
        var files = new JsonArray();
        foreach (var file in result.Files)
        {
            var failing = file.FailingTests();
            files.Add(new JsonObject
            {
                ["path"] = Relative(file.FilePath, solutionDirectory),
                ["measured"] = file.IsMeasured,
                ["isStale"] = file.IsStale,
                ["isPartial"] = file.IsPartial,
                ["totals"] = VerdictTotals(file.Totals),
                ["coveredByPassing"] = Ranges(file, ChangedLineVerdict.CoveredByPassing),
                ["coveredOnlyByFailing"] = Ranges(file, ChangedLineVerdict.CoveredOnlyByFailing),
                ["uncovered"] = Ranges(file, ChangedLineVerdict.Uncovered),
                ["nonExecutable"] = Ranges(file, ChangedLineVerdict.NonExecutable),
                ["unknown"] = new JsonArray([.. file.Lines
                    .Where(l => l.Verdict == ChangedLineVerdict.Unknown)
                    .GroupBy(l => l.Reason)
                    .OrderBy(g => g.Min(l => l.Line))
                    .Select(g => (JsonNode)new JsonObject
                    {
                        ["lines"] = ChangedLines.FormatRanges(g.Select(l => l.Line)),
                        ["reason"] = ChangedLineVerdicts.Name(g.Key),
                    })]),
                ["partialBranches"] = PartialLines(file.Lines.Where(l => l.HasPartialBranches).Select(l => l.Coverage!)),
                ["failingTests"] = Strings(failing.Take(MaxFailingTestsPerFile)),
                ["failingTestsOmitted"] = Math.Max(0, failing.Count - MaxFailingTestsPerFile),
            });
        }

        return new JsonObject
        {
            ["schema"] = "piston.changed-line-coverage/v2",
            ["base"] = result.Basis,
            ["evidence"] = ChangedLineEvidence,
            ["totals"] = VerdictTotals(result.Totals),
            ["unknownReasons"] = new JsonObject(result.UnknownReasons
                .Select(kv => KeyValuePair.Create(ChangedLineVerdicts.Name(kv.Key)!, (JsonNode?)kv.Value))),
            ["files"] = files,
            ["warnings"] = Strings(result.Warnings),
        };

        static string Ranges(ChangedFileVerdicts file, ChangedLineVerdict verdict) =>
            ChangedLines.FormatRanges(file.Lines.Where(l => l.Verdict == verdict).Select(l => l.Line));
    }

    private const string ChangedLineEvidence =
        "Hit counts come from the coverage report; which tests executed a line comes from the impact map, which " +
        "records executed methods per test, class or project run. Coarse or method-level evidence can credit tests " +
        "that did not execute the line itself, so mixed outcomes give unknown rather than a pass.";

    private static JsonObject VerdictTotals(ChangedLineTotals totals) => new()
    {
        ["changedLines"] = totals.ChangedLines,
        ["coveredByPassing"] = totals.CoveredByPassing,
        ["coveredOnlyByFailing"] = totals.CoveredOnlyByFailing,
        ["uncovered"] = totals.Uncovered,
        ["nonExecutable"] = totals.NonExecutable,
        ["unknown"] = totals.Unknown,
    };

    private static JsonArray Strings(IEnumerable<string> values) => [.. values.Select(v => (JsonNode)v!)];

    /// <summary>Reads git changes for the tools, turning bad input and git failures into tool errors.</summary>
    private static async Task<ChangedLinesResult> GitChangesAsync(
        string solutionDirectory, string? @base, string? baseRef, CancellationToken ct)
    {
        try
        {
            return await ChangedLines.GetCheckedAsync(solutionDirectory, @base, baseRef, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            throw new McpException(ex.Message);
        }
    }

    /// <summary>The changed files and the plan for them; an empty change set plans nothing.</summary>
    private async Task<(JsonObject Json, ImpactAnalysisResult Plan)> PlanAsync(
        string solutionDirectory, string[]? files, string? @base, string? baseRef, CancellationToken ct)
    {
        string basis;
        IReadOnlyList<string> changed;
        IReadOnlyList<string> warnings = [];
        if (files is { Length: > 0 })
        {
            basis = "files";
            changed = [.. files.Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => Path.GetFullPath(f, solutionDirectory))];
        }
        else
        {
            var changes = await GitChangesAsync(solutionDirectory, @base, baseRef, ct).ConfigureAwait(false);
            basis = changes.Basis;
            warnings = changes.Warnings;
            changed = [.. changes.Files.Keys.Order(StringComparer.Ordinal)];
        }

        var plan = changed.Count == 0
            ? new ImpactAnalysisResult([], [], RequiresGraphRebuild: false, IsFullRun: false)
            {
                Explanation = "No file changed, so nothing would run.",
            }
            : engine.PlanAffected(changed) ?? throw new McpException("This engine cannot plan affected tests.");
        var json = AffectedPlan(basis, changed, plan, solutionDirectory);
        json["warnings"] = Strings(warnings);
        return (json, plan);
    }

    [McpServerTool(Name = PlanAffectedTestsTool), Description(
        "Preview which projects and tests Piston would build and run for a set of changed files, as JSON (schema " +
        "piston.affected-plan/v1). Runs nothing and changes no state. Without files, uses git changes: the working " +
        "tree against HEAD by default, a branch since it left its base, or a commit range (see base). scope is full " +
        "(every test), projects (every test in the listed test projects), tests (only the listed tests) or none; " +
        "explanation says why. With coverage, each selected test lists the changed lines that selected it and the " +
        "granularity of the evidence: run- or class-level evidence over-selects and does not mean that test executed " +
        "those lines. Use run_affected_tests to run the plan.")]
    public async Task<string> PlanAffectedTestsAsync(
        [Description("Changed files, absolute or relative to the solution directory. Omit to use git changes.")] string[]? files = null,
        [Description("When files is omitted: " + BaseRefDescription)] string? baseRef = null,
        [Description("When files is omitted: " + BaseDescription)] string? @base = null,
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
            var (json, _) = await PlanAsync(solutionDirectory, files, @base, baseRef, ct).ConfigureAwait(false);
            summary = $"{json["scope"]}: {json["explanation"]}";
            succeeded = true;
            return json.ToJsonString(WriteOptions);
        }
        finally
        {
            sw.Stop();
            recorder.Record(PlanAffectedTestsTool, Describe(files, @base, baseRef), summary,
                sw.Elapsed.TotalMilliseconds, succeeded);
        }
    }

    [McpServerTool(Name = RunAffectedTestsTool), Description(
        "Run the tests affected by a set of changed files and wait for the run, as JSON: the plan of " +
        "plan_affected_tests (schema piston.affected-plan/v1) plus run (its ID, status and counts, or null when nothing " +
        "is affected). Without files, uses git changes (see base): e.g. base=branch runs what changed since the branch " +
        "left main. A full-scope plan runs every test. Selection is a heuristic: use verify for full verification.")]
    public async Task<string> RunAffectedTestsAsync(
        [Description("Changed files, absolute or relative to the solution directory. Omit to use git changes.")] string[]? files = null,
        [Description("When files is omitted: " + BaseRefDescription)] string? baseRef = null,
        [Description("When files is omitted: " + BaseDescription)] string? @base = null,
        [Description("How long to wait for the run in seconds (default 600). On timeout the run continues.")] int timeoutSeconds = 600,
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
            var (json, plan) = await PlanAsync(solutionDirectory, files, @base, baseRef, ct).ConfigureAwait(false);
            var scope = json["scope"]!.GetValue<string>();
            Task<TestRunRecord?> run;
            try
            {
                run = scope switch
                {
                    "none" => Task.FromResult<TestRunRecord?>(null),
                    "full" => engine.RequestRunAsync(),
                    _ => AsNullable(engine.RunSelectedAsync(Selection(plan, SolutionDirectory()))),
                };
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
            {
                throw new McpException($"Could not start the run: {ex.Message}");
            }

            TestRunRecord? record;
            try
            {
                record = await run.WaitAsync(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 3600)), ct)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new McpException(
                    $"The run did not finish within {timeoutSeconds}s; it continues. Use get_test_results to follow it.");
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                throw new McpException($"Could not start the run: {ex.Message}");
            }

            json["run"] = record is null ? null : RunSummary(record);
            summary = record is null
                ? $"{scope}: nothing to run"
                : $"{scope}: run {record.RunId} {record.Status}";
            succeeded = true;
            return json.ToJsonString(WriteOptions);
        }
        finally
        {
            sw.Stop();
            recorder.Record(RunAffectedTestsTool, Describe(files, @base, baseRef), summary,
                sw.Elapsed.TotalMilliseconds, succeeded);
        }

        static async Task<TestRunRecord?> AsNullable(Task<TestRunRecord> task) => await task.ConfigureAwait(false);
    }

    /// <summary>The test projects of a narrowed or project-scope plan, by solution-relative ID, with tests when narrowed.</summary>
    internal static TestSelection Selection(ImpactAnalysisResult plan, string? solutionDirectory = null) => new(
        [.. plan.AffectedTestProjectPaths
            .Select(p => solutionDirectory is null ? Path.GetFullPath(p) : Relative(p, solutionDirectory))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
        plan.NarrowsToTests ? [.. (plan.AffectedTestFqns ?? []).Order(StringComparer.Ordinal)] : null);

    private static JsonObject RunSummary(TestRunRecord run) => new()
    {
        ["runId"] = run.RunId,
        ["status"] = run.Status.ToString(),
        ["passed"] = run.Tests.Count(t => t.Status == TestStatus.Passed),
        ["failed"] = run.Tests.Count(t => t.Status == TestStatus.Failed),
        ["skipped"] = run.Tests.Count(t => t.Status == TestStatus.Skipped),
    };

    private static string? Describe(string[]? files, string? @base, string? baseRef) =>
        files is { Length: > 0 } ? string.Join(", ", files)
        : @base is null && baseRef is null ? null
        : $"{@base ?? "branch"} {baseRef}".Trim();

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
        json["staleLines"] = ChangedLines.FormatRanges(coverage.StaleLines.SelectMany(range => Enumerable.Range(range.Start, range.End - range.Start + 1)));
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
