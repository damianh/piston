// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Text.RegularExpressions;

namespace Piston.Web.Models;

/// <summary>Expected and actual values extracted from a failed assertion's message.</summary>
/// <param name="Expected">The expected value, without the quotes the framework added.</param>
/// <param name="Actual">The actual value, without the quotes the framework added.</param>
/// <param name="Summary">The rest of the message, or an empty string.</param>
public sealed partial record AssertionDiff(string Expected, string Actual, string Summary)
{
    /// <summary>
    /// The values of <paramref name="message"/>, or <see langword="null"/> when it has no recognised form
    /// or the values are the same:
    /// <list type="bullet">
    /// <item>MSTest: <c>Assert.AreEqual failed. Expected:&lt;10&gt;. Actual:&lt;9&gt;.</c></item>
    /// <item>xUnit, NUnit and MSTest 4: an <c>Expected:</c> line followed by an <c>Actual:</c> or <c>But was:</c> line,
    /// with optional <c>↓</c>/<c>↑</c> marker lines and a <c>---^</c> caret line.</item>
    /// <item>Shouldly: <c>should be</c> and <c>but was</c> lines, each followed by its value.</item>
    /// </list>
    /// </summary>
    public static AssertionDiff? Parse(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        var text = message.ReplaceLineEndings("\n");
        var diff = ParseObjectForm(text) ?? ParseLineForm(text) ?? ParseShouldly(text);

        // Equal values (e.g. a custom message that only labels empty ones) leave nothing to highlight.
        return diff is not null && diff.Expected != diff.Actual ? diff : null;
    }

    /// <summary>
    /// Splits both values into their common prefix, the differing middles and their common suffix,
    /// so the difference can be highlighted.
    /// </summary>
    public (string Prefix, string ExpectedMiddle, string ActualMiddle, string Suffix) Split()
    {
        var prefix = 0;
        var max = Math.Min(Expected.Length, Actual.Length);
        while (prefix < max && Expected[prefix] == Actual[prefix])
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < max - prefix && Expected[^(suffix + 1)] == Actual[^(suffix + 1)])
        {
            suffix++;
        }

        return (
            Expected[..prefix],
            Expected[prefix..^suffix],
            Actual[prefix..^suffix],
            Expected[^suffix..]);
    }

    private static AssertionDiff? ParseObjectForm(string text)
    {
        const string expectedLabel = "Expected:<";
        const string separator = ">. Actual:<";
        var expectedStart = text.IndexOf(expectedLabel, StringComparison.Ordinal);
        if (expectedStart < 0)
        {
            return null;
        }

        var sep = text.IndexOf(separator, expectedStart, StringComparison.Ordinal);
        if (sep < 0)
        {
            return null;
        }

        var actualStart = sep + separator.Length;
        var actualEnd = text.LastIndexOf(">.", StringComparison.Ordinal);
        if (actualEnd < actualStart)
        {
            return null;
        }

        var summary = (text[..expectedStart].Trim() + " " + text[(actualEnd + 2)..].Trim()).Trim();
        return new(text[(expectedStart + expectedLabel.Length)..sep], text[actualStart..actualEnd], summary);
    }

    private static AssertionDiff? ParseLineForm(string text)
    {
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var expected = ExpectedLine().Match(lines[i]);
            if (!expected.Success)
            {
                continue;
            }

            var j = i + 1;
            while (j < lines.Length && MarkerLine().IsMatch(lines[j]))
            {
                j++;
            }

            var actual = j < lines.Length ? ActualLine().Match(lines[j]) : Match.Empty;
            if (!actual.Success)
            {
                continue;
            }

            var end = j + 1;
            while (end < lines.Length && (MarkerLine().IsMatch(lines[end]) || CaretLine().IsMatch(lines[end])))
            {
                end++;
            }

            var summary = string.Join('\n', lines[..i].Concat(lines[end..])
                .Where(line => !MarkerLine().IsMatch(line) && !IsEmptyLabel(line))).Trim();
            return new(Unquote(expected.Groups["value"].Value), Unquote(actual.Groups["value"].Value), summary);
        }

        return null;
    }

    private static AssertionDiff? ParseShouldly(string text)
    {
        var lines = text.Split('\n');
        var shouldBe = Array.FindIndex(lines, line => line.Trim() == "should be");
        if (shouldBe < 0 || shouldBe + 3 >= lines.Length || lines[shouldBe + 2].Trim() != "but was")
        {
            return null;
        }

        var summary = string.Join('\n', lines[..shouldBe].Concat(lines[(shouldBe + 4)..])).Trim();
        return new(Unquote(lines[shouldBe + 1].Trim()), Unquote(lines[shouldBe + 3].Trim()), summary);
    }

    // Some runners repeat the labels with no values after the real ones.
    private static bool IsEmptyLabel(string line) =>
        line.Trim() is "Expected:" or "Actual:" or "But was:";

    private static string Unquote(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"' ? trimmed[1..^1] : trimmed;
    }

    [GeneratedRegex(@"^\s*Expected:\s?(?<value>.*)$")]
    private static partial Regex ExpectedLine();

    [GeneratedRegex(@"^\s*(?:But was|Actual):\s*(?<value>(?:\S.*)?)$")]
    private static partial Regex ActualLine();

    [GeneratedRegex(@"^\s*[↓↑]")]
    private static partial Regex MarkerLine();

    [GeneratedRegex(@"^\s*-+\^\s*$")]
    private static partial Regex CaretLine();
}
