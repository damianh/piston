// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Text.RegularExpressions;
using Piston.Engine.Models;

namespace Piston.Engine.Execution;

/// <summary>
/// Recognises the errors a test project reports when a port or container name it needs is already taken,
/// typically by the same project running in another worktree, so the failure can point at
/// <c>testResources</c> instead of looking like a product bug.
/// </summary>
internal static partial class ResourceConflictScanner
{
    private const int MaxEvidenceLength = 240;

    /// <summary>A description of the suspected conflict, or null when the project did not fail with one.</summary>
    public static string? Scan(ProjectTestResult result)
    {
        var failed = result.Crashed || result.RunnerError is not null || result.TimedOut is not null
            || result.Suites.Any(s => s.Tests.Any(t => t.Status == TestStatus.Failed));
        if (!failed)
        {
            return null;
        }

        foreach (var text in Texts(result))
        {
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            var match = Signature().Match(text);
            if (match.Success)
            {
                return $"Output suggests a port or container name conflict (\"{Evidence(text, match)}\"). If another " +
                    "run of these tests can hold the same port or container, declare it under 'testResources' in " +
                    ".piston.json (e.g. \"exclusive\": [\"port:5432\"]) so runs wait for each other.";
            }
        }

        return null;
    }

    private static IEnumerable<string?> Texts(ProjectTestResult result)
    {
        yield return result.RunnerError;
        foreach (var test in result.Suites.SelectMany(s => s.Tests).Where(t => t.Status == TestStatus.Failed))
        {
            yield return test.ErrorMessage;
            yield return test.Output;
        }

        foreach (var entry in result.Log?.Entries ?? [])
        {
            yield return entry.Message;
        }
    }

    private static string Evidence(string text, Match match)
    {
        var start = text.LastIndexOf('\n', match.Index) + 1;
        var end = text.IndexOf('\n', match.Index + match.Length);
        var line = text[start..(end < 0 ? text.Length : end)].Trim();
        return line.Length <= MaxEvidenceLength ? line : line[..MaxEvidenceLength] + "…";
    }

    [GeneratedRegex(
        @"address already in use|EADDRINUSE|only one usage of each socket address|port is already allocated|" +
        @"Bind for \S+ failed|container name ""?/?[\w.-]+""? is already in use",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Signature();
}
