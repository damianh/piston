// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Text;
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
///   Standard output
///     captured stdout line
///   Error output
///     captured stderr line
/// </code>
/// Status is lowercase. Duration is in parentheses. An optional "from" line provides the
/// source DLL. For failed tests, subsequent indented lines contain error info. Captured
/// output follows any error info under "Standard output" / "Error output" headings, with
/// every line (blank lines included) indented by four spaces.
/// </remarks>
internal sealed class MtpStdoutParser
{
    // Matches: "passed FQN (7ms)" or "failed FQN (< 1ms)" or "skipped FQN (1.2s)"
    private static readonly Regex StatusLineRegex = new(
        @"^\s*(passed|failed|skipped)\s+(.+?)\s+\((.+?)\)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Matches: "  from C:\path\to.dll (net10.0|x64)"; the parenthesised part starts with the target framework.
    private static readonly Regex SourceLineRegex = new(
        @"^\s+from\s+(.+?)\s+\(([^|)]+).*?\)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Matches stack trace line like "  at Ns.Class.Method() in file.cs:line 42"
    // Works both with and without leading whitespace (since extra lines are trimmed)
    private static readonly Regex StackTraceLineRegex = new(
        @"^\s*at\s+",
        RegexOptions.Compiled);

    private static readonly Regex ArtifactLineRegex = new(
        @"^\s*-\s+For test '(.+)':\s*(.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private const string StandardOutputHeading = "  Standard output";
    private const string ErrorOutputHeading = "  Error output";
    private const string OutputIndentation = "    ";

    private string? _pendingFqn;
    private TestStatus _pendingStatus;
    private TimeSpan _pendingDuration;
    private string? _pendingSource;
    private string? _pendingTargetFramework;
    private readonly BoundedTextBuffer _pendingErrorLines = new(RetentionLimits.MaxTestTextLength);
    private readonly BoundedTextBuffer _pendingStackTrace = new(RetentionLimits.MaxTestTextLength);
    private readonly BoundedTextBuffer _pendingStdOut = new(RetentionLimits.MaxTestTextLength);
    private readonly BoundedTextBuffer _pendingStdErr = new(RetentionLimits.MaxTestTextLength);
    private BoundedTextBuffer? _currentOutputSection;
    private readonly List<MtpParsedAttachment> _attachments = [];

    public IReadOnlyList<MtpParsedAttachment> Attachments => _attachments;

    /// <summary>
    /// Processes a single stdout line. Returns a completed <see cref="MtpParsedResult"/>
    /// when the previous test's output is complete (i.e., when the next test header is seen),
    /// or null while accumulating lines for the current test.
    /// </summary>
    public MtpParsedResult? ProcessLine(string line)
    {
        // Captured output is indented under its heading; check it first so output text that
        // happens to look like a status or stack-trace line is not misread.
        if (_currentOutputSection is not null)
        {
            if (line.StartsWith(OutputIndentation, StringComparison.Ordinal))
            {
                _currentOutputSection.AppendLine(line[OutputIndentation.Length..]);
                return null;
            }

            _currentOutputSection = null;
        }

        var artifactMatch = ArtifactLineRegex.Match(line);
        if (artifactMatch.Success)
        {
            _attachments.Add(new(artifactMatch.Groups[1].Value, artifactMatch.Groups[2].Value.Trim()));
            return null;
        }

        if (line.Trim().EndsWith("file artifacts produced:", StringComparison.OrdinalIgnoreCase))
        {
            return FlushPending();
        }

        var statusMatch = StatusLineRegex.Match(line);

        if (statusMatch.Success)
        {
            // A new test result header — flush the previous one first
            var completed = FlushPending();

            _pendingFqn = statusMatch.Groups[2].Value.Trim();
            _pendingStatus = ParseStatus(statusMatch.Groups[1].Value);
            _pendingDuration = ParseDuration(statusMatch.Groups[3].Value);
            _pendingSource = null;
            _pendingTargetFramework = null;

            return completed;
        }

        if (_pendingFqn is null)
        {
            return null; // Haven't seen a test header yet
        }

        switch (line.TrimEnd())
        {
            case StandardOutputHeading:
                _currentOutputSection = _pendingStdOut;
                return null;
            case ErrorOutputHeading:
                _currentOutputSection = _pendingStdErr;
                return null;
        }

        var sourceMatch = SourceLineRegex.Match(line);
        if (sourceMatch.Success)
        {
            _pendingSource = sourceMatch.Groups[1].Value.Trim();
            _pendingTargetFramework = sourceMatch.Groups[2].Value.Trim();
            return null;
        }

        // Any other non-blank indented line after a "failed" header is error/stack info
        if (_pendingStatus == TestStatus.Failed && !string.IsNullOrWhiteSpace(line))
        {
            var extraLine = line.Trim();
            if (StackTraceLineRegex.IsMatch(extraLine))
            {
                _pendingStackTrace.AppendLine(extraLine);
            }
            else
            {
                _pendingErrorLines.AppendLine(extraLine);
            }
        }

        return null;
    }

    /// <summary>
    /// Returns the last accumulated result (if any). Call after EOF on stdout.
    /// </summary>
    public MtpParsedResult? Flush() => FlushPending();

    // ── Helpers ───────────────────────────────────────────────────────────────

    private MtpParsedResult? FlushPending()
    {
        if (_pendingFqn is null)
        {
            return null;
        }

        var errorMessage = _pendingErrorLines.GetHeadValue();
        var stackTrace = _pendingStackTrace.GetHeadValue();

        var result = new MtpParsedResult(
            FullyQualifiedName: _pendingFqn,
            Status: _pendingStatus,
            Duration: _pendingDuration,
            Source: _pendingSource,
            ErrorMessage: errorMessage,
            StackTrace: stackTrace,
            Output: _pendingStdOut.GetHeadTailValue(),
            ErrorOutput: _pendingStdErr.GetHeadTailValue(),
            TargetFramework: _pendingTargetFramework);

        _pendingFqn = null;
        _pendingErrorLines.Reset();
        _pendingStackTrace.Reset();
        _pendingStdOut.Reset();
        _pendingStdErr.Reset();
        _currentOutputSection = null;

        return result;
    }

    private static TestStatus ParseStatus(string value) =>
        value.ToLowerInvariant() switch
        {
            "passed" => TestStatus.Passed,
            "failed" => TestStatus.Failed,
            "skipped" => TestStatus.Skipped,
            _ => TestStatus.NotRun,
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
        {
            return TimeSpan.FromMilliseconds(0.5);
        }

        if (trimmed.EndsWith("ms", StringComparison.OrdinalIgnoreCase))
        {
            var num = trimmed[..^2].Trim();
            if (double.TryParse(num, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var ms))
            {
                return TimeSpan.FromMilliseconds(ms);
            }
        }

        if (trimmed.EndsWith('s') && !trimmed.EndsWith("ms", StringComparison.OrdinalIgnoreCase))
        {
            var num = trimmed[..^1].Trim();
            if (double.TryParse(num, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var sec))
            {
                return TimeSpan.FromSeconds(sec);
            }
        }

        return TimeSpan.Zero;
    }
}

internal sealed record MtpParsedAttachment(string TestName, string Path);

internal sealed class BoundedTextBuffer(int maxLength)
{
    private readonly StringBuilder _head = new();
    private readonly Queue<char> _tail = new();
    private readonly StringBuilder _pendingWhitespace = new();
    private bool _hasLine;
    private long _totalLength;
    private long _pendingWhitespaceLength;

    public long TrimmedLength => _totalLength - _pendingWhitespaceLength;

    public void AppendLine(string line)
    {
        if (_hasLine)
        {
            Append(Environment.NewLine);
        }

        _hasLine = true;
        Append(line);
    }

    public string? GetHeadValue()
    {
        var trimmedLength = TrimmedLength;
        if (trimmedLength == 0)
        {
            return null;
        }

        var headLength = (int)Math.Min(trimmedLength, _head.Length);
        return RetentionLimits.TruncateHead(_head.ToString(0, headLength), trimmedLength, maxLength);
    }

    public string? GetHeadTailValue()
    {
        var length = TrimmedLength;
        if (length == 0)
        {
            return null;
        }

        var headLength = (int)Math.Min(length, _head.Length);
        var head = _head.ToString(0, headLength);
        return length <= maxLength
            ? head
            : RetentionLimits.TruncateHeadTail(head, GetSuffix(maxLength), length, maxLength);
    }

    public string GetPrefix(int length) => length == 0 ? string.Empty : _head.ToString(0, Math.Min(length, _head.Length));

    public string GetSuffix(int length)
    {
        if (length == 0)
        {
            return string.Empty;
        }

        if (TrimmedLength <= _head.Length)
        {
            return _head.ToString(0, (int)TrimmedLength)[^length..];
        }

        var characters = _tail.ToArray();
        var available = (int)Math.Min(TrimmedLength, characters.Length);
        var count = Math.Min(length, available);
        return new string(characters, available - count, count);
    }

    public void Reset()
    {
        _ = _head.Clear();
        _tail.Clear();
        _ = _pendingWhitespace.Clear();
        _hasLine = false;
        _totalLength = 0;
        _pendingWhitespaceLength = 0;
    }

    private void Append(string text)
    {
        foreach (var character in text)
        {
            _totalLength++;
            if (_head.Length < maxLength)
            {
                _ = _head.Append(character);
            }

            if (char.IsWhiteSpace(character))
            {
                _pendingWhitespaceLength++;
                if (_pendingWhitespace.Length == maxLength)
                {
                    _ = _pendingWhitespace.Remove(0, 1);
                }

                _ = _pendingWhitespace.Append(character);
                continue;
            }

            if (_pendingWhitespace.Length > 0)
            {
                foreach (var whitespace in _pendingWhitespace.ToString())
                {
                    AppendTail(whitespace);
                }

                _ = _pendingWhitespace.Clear();
            }
            _pendingWhitespaceLength = 0;
            AppendTail(character);
        }
    }

    private void AppendTail(char character)
    {
        _tail.Enqueue(character);
        if (_tail.Count > maxLength)
        {
            _ = _tail.Dequeue();
        }
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
    string? StackTrace,
    string? Output = null,
    string? ErrorOutput = null,
    string? TargetFramework = null)
{
    /// <summary>Identifies the result within one run: a multi-targeted project reports each test once per framework.</summary>
    public string Key => TargetFramework is null ? FullyQualifiedName : $"{FullyQualifiedName} [{TargetFramework}]";
}
