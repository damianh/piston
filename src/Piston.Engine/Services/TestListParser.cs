// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Models;

namespace Piston.Engine.Services;

/// <summary>
/// Incremental parser for <c>--list-tests</c> output: test names are the indented lines following a
/// header line, up to the first empty or unindented line (output for several assemblies repeats the header).
/// </summary>
internal sealed class TestListParser(Func<string, bool> isHeader)
{
    private readonly List<TestResult> _results = [];
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private bool _inList;

    /// <summary>The distinct tests parsed so far, in output order.</summary>
    public IReadOnlyList<TestResult> Results => _results;

    /// <summary>Processes one output line; returns <see langword="true"/> when it added a test.</summary>
    public bool ProcessLine(string line)
    {
        if (isHeader(line))
        {
            _inList = true;
            return false;
        }

        if (!_inList)
        {
            return false;
        }

        if (line.Length == 0 || !char.IsWhiteSpace(line[0]))
        {
            _inList = false;
            return false;
        }

        var name = line.Trim();
        if (name.Length == 0 || !_seen.Add(name))
        {
            return false;
        }

        _results.Add(new TestResult(name, TestProcessRunner.DisplayNameOf(name), TestStatus.NotRun,
            TimeSpan.Zero, null, null, null, null));
        return true;
    }

    /// <summary>Parses all <paramref name="lines"/> and returns the tests found.</summary>
    public static IReadOnlyList<TestResult> ParseAll(TestListParser parser, IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            _ = parser.ProcessLine(line);
        }

        return parser.Results;
    }
}
