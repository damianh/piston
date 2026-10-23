// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Protocol.Dtos;

namespace Piston.Web.Models;

/// <summary>What the watch strip says about a run: what triggered it, its build, its scope and what it changed.</summary>
/// <param name="Trigger">What started the run, e.g. <c>Calc.cs saved</c>.</param>
/// <param name="TriggerDetail">Every changed file, one per line, for the trigger's tooltip; null when not a file change.</param>
/// <param name="Build">The build step, e.g. <c>build 4.2s</c>; null when the run had no build result (yet).</param>
/// <param name="BuildFailed">True when the build failed.</param>
/// <param name="Scope">Which tests ran, e.g. <c>6 tests by changed lines</c>; null when unknown.</param>
/// <param name="Duration">The run's elapsed time; null until it completes.</param>
public sealed record WatchStripSummary(
    string Trigger,
    string? TriggerDetail,
    string? Build,
    bool BuildFailed,
    string? Scope,
    string? Duration);

public static class WatchStrip
{
    /// <summary>Summarizes <paramref name="run"/>; changed files are shown relative to <paramref name="solutionDirectory"/>.</summary>
    public static WatchStripSummary Summarize(TestRunDto run, string? solutionDirectory)
    {
        // Atomic saves leave extensionless temp files (e.g. sed3xYoqV) in the batch; name a real source file first.
        var files = (run.ChangedFiles ?? []).OrderBy(f => Path.HasExtension(f) ? 0 : 1).ToList();
        var trigger = (run.Trigger, files.Count) switch
        {
            ("FileChange", 0) => "file change",
            ("FileChange", 1) => $"{FileName(files[0])} saved",
            ("FileChange", var n) => $"{FileName(files[0])} +{n - 1} saved",
            ("Startup", _) => "startup",
            ("Forced", _) => "run all",
            ("Targeted", _) => "run requested",
            (var other, _) => other,
        };
        var detail = files.Count == 0
            ? null
            : string.Join(Environment.NewLine, files.Select(f => Relative(f, solutionDirectory)));

        var build = run.Build switch
        {
            null => null,
            { Status: BuildStatusDto.Skipped } => "build skipped",
            { Status: BuildStatusDto.Failed } b => $"build failed after {TestNode.FormatDuration(b.DurationMs)}",
            { Status: BuildStatusDto.Building } => "building",
            var b => $"build {TestNode.FormatDuration(b.DurationMs)}",
        };

        var duration = run is { StartedAt: { } started, CompletedAt: { } completed }
            ? TestNode.FormatDuration(Math.Max(0, (completed - started).TotalMilliseconds))
            : null;

        return new WatchStripSummary(
            trigger, detail, build, run.Build?.Status == BuildStatusDto.Failed, ScopeLabel(run), duration);
    }

    /// <summary>Which tests <paramref name="run"/> ran, in a few words; null when it can't say.</summary>
    public static string? ScopeLabel(TestRunDto run) => run switch
    {
        { SelectionReasons.Count: > 0 } => $"{Count(run.SelectionReasons.Count, "test")} by changed lines",
        { IsFullScope: true } => "every test",
        { Scope: "Selective", TestProjects: { } projects } => $"{Count(projects.Count, "project")}{(run.IsFiltered ? ", filtered" : "")}",
        { IsFiltered: true } => "filtered",
        _ => null,
    };

    /// <summary>The method name (and arguments) of a test's result key.</summary>
    public static string ShortTestName(string name)
    {
        var args = name.IndexOf('(');
        var dot = name.LastIndexOf('.', args < 0 ? name.Length - 1 : args);
        return dot < 0 ? name : name[(dot + 1)..];
    }

    public static string Count(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    public static string PassedSkippedLabel(TestRunDto run) => run.Passed == run.TotalTests
        ? run.TotalTests == 1 ? "passed" : $"all {run.Passed} passed"
        : $"{run.Passed} passed, {run.Skipped} skipped";

    public static bool ShowOutcomeSummary(TestRunDto run) =>
        run.Status is "Cancelled" or "Faulted" or "Incomplete"
        || (run.FixedTests is not { Count: > 0 } && run.NewlyFailingTests is not { Count: > 0 });

    private static string FileName(string path) => path[(path.LastIndexOfAny(['/', '\\']) + 1)..];

    private static string Relative(string path, string? directory)
    {
        if (string.IsNullOrEmpty(directory))
        {
            return path;
        }

        var prefix = directory.TrimEnd('/', '\\');
        return path.Length > prefix.Length + 1
            && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && path[prefix.Length] is '/' or '\\'
            ? path[(prefix.Length + 1)..]
            : path;
    }
}
