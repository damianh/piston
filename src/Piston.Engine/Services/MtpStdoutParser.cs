using System.Text.RegularExpressions;
using Piston.Engine.Models;

namespace Piston.Engine.Services;

/// <summary>
/// Stateful line-by-line parser for Microsoft.Testing.Platform v2 stdout output.
/// </summary>
/// <remarks>
/// MTP stdout format (with <c>--output Detailed</c>):
/// <code>
/// passed Namespace.Class.Method (7ms)
///   from C:\path\to.dll (net10.0|x64)
///
/// failed Namespace.Class.Method (12ms)
///   from C:\path\to.dll (net10.0|x64)
///   Error message here
///   at Namespace.Class.Method() in File.cs:line 42
/// </code>
/// Status is lowercase. Duration is in parentheses. An optional "from" line provides the
/// source DLL. For failed tests, subsequent indented lines contain error info.
/// </remarks>
internal sealed class MtpStdoutParser
{
    // Matches: "passed FQN (7ms)" or "failed FQN (< 1ms)" or "skipped FQN (1.2s)"
    private static readonly Regex StatusLineRegex = new(
        @"^\s*(passed|failed|skipped)\s+(.+?)\s+\((.+?)\)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Matches: "  from C:\path\to.dll (net10.0|x64)"
    private static readonly Regex SourceLineRegex = new(
        @"^\s+from\s+(.+?)\s+\(.+?\)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Matches stack trace line like "  at Ns.Class.Method() in file.cs:line 42"
    // Works both with and without leading whitespace (since extra lines are trimmed)
    private static readonly Regex StackTraceLineRegex = new(
        @"^\s*at\s+",
        RegexOptions.Compiled);

    private string? _pendingFqn;
    private TestStatus _pendingStatus;
    private TimeSpan _pendingDuration;
    private string? _pendingSource;
    private readonly List<string> _pendingExtraLines = [];

    /// <summary>
    /// Processes a single stdout line. Returns a completed <see cref="MtpParsedResult"/>
    /// when the previous test's output is complete (i.e., when the next test header is seen),
    /// or null while accumulating lines for the current test.
    /// </summary>
    public MtpParsedResult? ProcessLine(string line)
    {
        var statusMatch = StatusLineRegex.Match(line);

        if (statusMatch.Success)
        {
            // A new test result header — flush the previous one first
            var completed = FlushPending();

            _pendingFqn      = statusMatch.Groups[2].Value.Trim();
            _pendingStatus   = ParseStatus(statusMatch.Groups[1].Value);
            _pendingDuration = ParseDuration(statusMatch.Groups[3].Value);
            _pendingSource   = null;
            _pendingExtraLines.Clear();

            return completed;
        }

        if (_pendingFqn is null) return null; // Haven't seen a test header yet

        var sourceMatch = SourceLineRegex.Match(line);
        if (sourceMatch.Success)
        {
            _pendingSource = sourceMatch.Groups[1].Value.Trim();
            return null;
        }

        // Any other non-blank indented line after a "failed" header is error/stack info
        if (_pendingStatus == TestStatus.Failed && !string.IsNullOrWhiteSpace(line))
            _pendingExtraLines.Add(line.Trim());

        return null;
    }

    /// <summary>
    /// Returns the last accumulated result (if any). Call after EOF on stdout.
    /// </summary>
    public MtpParsedResult? Flush() => FlushPending();

    // ── Helpers ───────────────────────────────────────────────────────────────

    private MtpParsedResult? FlushPending()
    {
        if (_pendingFqn is null) return null;

        string? errorMessage = null;
        string? stackTrace   = null;

        if (_pendingExtraLines.Count > 0)
        {
            // Split extra lines into stack trace (lines starting with "at ") and error message
            var stackLines  = new List<string>();
            var errorLines  = new List<string>();

            foreach (var extraLine in _pendingExtraLines)
            {
                if (StackTraceLineRegex.IsMatch(extraLine))
                    stackLines.Add(extraLine);
                else
                    errorLines.Add(extraLine);
            }

            errorMessage = errorLines.Count > 0 ? string.Join(Environment.NewLine, errorLines) : null;
            stackTrace   = stackLines.Count > 0 ? string.Join(Environment.NewLine, stackLines) : null;
        }

        var result = new MtpParsedResult(
            FullyQualifiedName: _pendingFqn,
            Status:             _pendingStatus,
            Duration:           _pendingDuration,
            Source:             _pendingSource,
            ErrorMessage:       errorMessage,
            StackTrace:         stackTrace);

        _pendingFqn = null;
        _pendingExtraLines.Clear();

        return result;
    }

    private static TestStatus ParseStatus(string value) =>
        value.ToLowerInvariant() switch
        {
            "passed"  => TestStatus.Passed,
            "failed"  => TestStatus.Failed,
            "skipped" => TestStatus.Skipped,
            _         => TestStatus.NotRun,
        };

    /// <summary>
    /// Parses MTP duration strings such as "7ms", "1.2s", "&lt; 1ms", "150ms".
    /// Returns <see cref="TimeSpan.Zero"/> for unrecognised formats.
    /// </summary>
    internal static TimeSpan ParseDuration(string value)
    {
        var trimmed = value.Trim();

        // "< 1ms" or "< 1 ms" → treat as sub-millisecond
        if (trimmed.StartsWith('<'))
            return TimeSpan.FromMilliseconds(0.5);

        if (trimmed.EndsWith("ms", StringComparison.OrdinalIgnoreCase))
        {
            var num = trimmed[..^2].Trim();
            if (double.TryParse(num, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var ms))
                return TimeSpan.FromMilliseconds(ms);
        }

        if (trimmed.EndsWith('s') && !trimmed.EndsWith("ms", StringComparison.OrdinalIgnoreCase))
        {
            var num = trimmed[..^1].Trim();
            if (double.TryParse(num, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var sec))
                return TimeSpan.FromSeconds(sec);
        }

        return TimeSpan.Zero;
    }
}

/// <summary>
/// A parsed MTP test result produced by <see cref="MtpStdoutParser"/>.
/// </summary>
internal sealed record MtpParsedResult(
    string FullyQualifiedName,
    TestStatus Status,
    TimeSpan Duration,
    string? Source,
    string? ErrorMessage,
    string? StackTrace);
