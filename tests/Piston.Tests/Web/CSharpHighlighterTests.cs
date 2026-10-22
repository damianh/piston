// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Web.Models;
using Xunit;

namespace Piston.Tests.Web;

public sealed class CSharpHighlighterTests
{
    private static string Render(params string[] lines) =>
        string.Join("\n", CSharpHighlighter.Highlight(lines).Select(tokens => string.Concat(tokens.Select(t => t.Kind switch
        {
            SourceTokenKind.Keyword => $"[k:{t.Text}]",
            SourceTokenKind.String => $"[s:{t.Text}]",
            SourceTokenKind.Comment => $"[c:{t.Text}]",
            SourceTokenKind.Number => $"[n:{t.Text}]",
            _ => t.Text,
        }))));

    [Fact]
    public void MarksKeywordsStringsNumbersAndComments() =>
        Assert.Equal(
            """[k:var] total = [n:42] + Count([s:"a\"b"]); [c:// sum]""",
            Render("""var total = 42 + Count("a\"b"); // sum"""));

    [Fact]
    public void LeavesKeywordsInsideIdentifiersAndEscapedNamesPlain() =>
        Assert.Equal("[k:int] @class = intValue + x2;", Render("int @class = intValue + x2;"));

    [Fact]
    public void HandlesVerbatimInterpolatedAndRawStrings() =>
        Assert.Equal(
            "[s:@\"C:\\x\"\"y\"] + [s:$\"{a}\"] + [s:\"\"\"raw \" text\"\"\"] + [s:'\\'']",
            Render("@\"C:\\x\"\"y\" + $\"{a}\" + \"\"\"raw \" text\"\"\" + '\\''"));

    [Fact]
    public void CarriesABlockCommentAcrossLines() =>
        Assert.Equal(
            "[k:return] [c:/* one]\n[c:two */] [k:null];",
            Render("return /* one", "two */ null;"));

    [Fact]
    public void CarriesARawStringAcrossLines() =>
        Assert.Equal(
            "Write([s:\"\"\"]\n[s:  <a b=\"c\" /> // not a comment]\n[s:  \"\"\"]); [k:int] x;",
            Render("Write(\"\"\"", "  <a b=\"c\" /> // not a comment", "  \"\"\"); int x;"));

    [Fact]
    public void KeepsEveryCharacter()
    {
        const string line = "  if (x is not null && y > 0.5f) { s = $@\"{x}\"; } // done";

        Assert.Equal(line, string.Concat(CSharpHighlighter.Highlight([line])[0].Select(t => t.Text)));
    }
}
