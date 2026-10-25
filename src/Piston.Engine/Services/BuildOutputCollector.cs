// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Text;
using System.Text.RegularExpressions;
using Piston.Engine.Models;

namespace Piston.Engine.Services;

/// <summary>
/// Collects <c>dotnet build</c> output, classifying MSBuild error and warning lines and keeping a
/// bounded tail of raw output so failures without a recognizable error line still carry details.
/// Thread-safe: stdout and stderr callbacks may arrive concurrently.
/// </summary>
internal sealed class BuildOutputCollector
{
    internal const int DefaultMaxTailLines = 30;
    internal const int MaxTailLineLength = 500;

    // MSBuild error/warning formats:
    //   path(line,col): error CSXXXX: message [project]
    //   MSBUILD : error MSB1009: message
    //   path : error : message            (codeless, e.g. <Error Text="..."/> without Code)
    private static readonly Regex ErrorPattern =
        new(@":\s*error(?:\s+[\w.-]+)?\s*:", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex WarningPattern =
        new(@":\s*warning(?:\s+[\w.-]+)?\s*:", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Printed once per built project: "  ProjectName -> /path/to/output.dll". Diagnostics start
    // with a path or "MSBUILD :", so a bare project name (which may contain single spaces) before
    // the arrow identifies these lines; "(Blazor output)" and similar suffixes are excluded.
    private static readonly Regex ProjectOutputPattern =
        new(@"^\s*(?<name>[\w.-]+(?: [\w.-]+)*) -> \S", RegexOptions.Compiled);

    private readonly object _gate = new();
    private readonly List<string> _errors = [];
    private readonly List<string> _warnings = [];
    private readonly Queue<string> _tail = new();
    private readonly int _maxTailLines;
    private int _omittedErrors;
    private int _omittedWarnings;

    public BuildOutputCollector(int maxTailLines = DefaultMaxTailLines) =>
        _maxTailLines = Math.Max(1, maxTailLines);

    /// <summary>Returns the project name when <paramref name="line"/> reports a built project's output.</summary>
    public static bool TryParseBuiltProject(string? line, out string projectName)
    {
        var match = line is null ? Match.Empty : ProjectOutputPattern.Match(line);
        projectName = match.Success ? match.Groups["name"].Value : string.Empty;
        return match.Success;
    }

    public void AddOutputLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        var trimmed = line.Trim();
        lock (_gate)
        {
            AppendTail(trimmed);
            if (ErrorPattern.IsMatch(trimmed))
            {
                AddDiagnostic(_errors, trimmed, ref _omittedErrors);
            }
            else if (WarningPattern.IsMatch(trimmed))
            {
                AddDiagnostic(_warnings, trimmed, ref _omittedWarnings);
            }
        }
    }

    public void AddErrorLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        var trimmed = line.Trim();
        lock (_gate)
        {
            AppendTail(trimmed);
            AddDiagnostic(_errors, trimmed, ref _omittedErrors);
        }
    }

    /// <summary>Result for a build that was cancelled before completing.</summary>
    public BuildResult ToCancelledResult(TimeSpan duration)
    {
        lock (_gate)
        {
            return new BuildResult(BuildStatus.Failed, _errors.ToList(), _warnings.ToList(), duration)
            {
                OmittedErrors = _omittedErrors,
                OmittedWarnings = _omittedWarnings,
            };
        }
    }

    /// <summary>
    /// Result for a completed build. When the build failed but no error line was recognized,
    /// a synthesized error carrying the exit code and the tail of the output is added.
    /// </summary>
    public BuildResult ToResult(int exitCode, TimeSpan duration)
    {
        lock (_gate)
        {
            var errors = _errors.ToList();
            var status = exitCode == 0 && errors.Count == 0 ? BuildStatus.Succeeded : BuildStatus.Failed;
            if (status == BuildStatus.Failed && errors.Count == 0)
            {
                errors.Add(RetentionLimits.TruncateHead(
                    DescribeUnrecognizedFailure(exitCode, _tail.ToList()),
                    RetentionLimits.MaxBuildDiagnosticLength)!);
            }

            return new BuildResult(status, errors, _warnings.ToList(), duration)
            {
                OmittedErrors = _omittedErrors,
                OmittedWarnings = _omittedWarnings,
            };
        }
    }

    internal static string DescribeUnrecognizedFailure(int exitCode, IReadOnlyList<string> tail)
    {
        var sb = new StringBuilder();
        _ = sb.Append($"dotnet build exited with code {exitCode} without reporting a recognizable error.");
        if (tail.Count == 0)
        {
            _ = sb.Append(" No output was captured.");
        }
        else
        {
            _ = sb.Append($" Last {tail.Count} line(s) of output:");
            foreach (var line in tail)
            {
                _ = sb.Append('\n').Append(line);
            }
        }

        return sb.ToString();
    }

    private static void AddDiagnostic(List<string> diagnostics, string line, ref int omitted)
    {
        if (diagnostics.Count >= RetentionLimits.MaxBuildDiagnostics)
        {
            omitted++;
            return;
        }

        diagnostics.Add(RetentionLimits.TruncateHead(line, RetentionLimits.MaxBuildDiagnosticLength)!);
    }

    private void AppendTail(string line)
    {
        _tail.Enqueue(line.Length <= MaxTailLineLength ? line : line[..MaxTailLineLength] + "…");
        while (_tail.Count > _maxTailLines)
        {
            _ = _tail.Dequeue();
        }
    }
}
