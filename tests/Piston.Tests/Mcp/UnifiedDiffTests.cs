// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Mcp.Tools;
using Xunit;

namespace Piston.Tests.Mcp;

public sealed class UnifiedDiffTests : IDisposable
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _root = Directory.CreateTempSubdirectory("piston-unified-diff-").FullName;

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Parse_NumbersContextAddedAndRemovedLinesPerSide()
    {
        const string Diff = """
            diff --git a/src/A.cs b/src/A.cs
            index 1111111..2222222 100644
            --- a/src/A.cs
            +++ b/src/A.cs
            @@ -1,4 +1,5 @@ class A
             one
            -two
            +TWO
            +2.5
             three
             four
            @@ -10,2 +11,0 @@
            -ten
            -eleven

            """;

        var file = Assert.Single(UnifiedDiff.Parse(Diff.ReplaceLineEndings("\n")));

        Assert.Equal("src/A.cs", file.Key);
        Assert.Equal(DiffFileStatus.Modified, file.Value.Status);
        Assert.Equal(2, file.Value.Hunks.Count);
        Assert.Equal(
            [
                new DiffLine(DiffLineKind.Context, 1, 1, "one"),
                new DiffLine(DiffLineKind.Removed, 2, null, "two"),
                new DiffLine(DiffLineKind.Added, null, 2, "TWO"),
                new DiffLine(DiffLineKind.Added, null, 3, "2.5"),
                new DiffLine(DiffLineKind.Context, 3, 4, "three"),
                new DiffLine(DiffLineKind.Context, 4, 5, "four"),
            ],
            file.Value.Hunks[0].Lines);
        var removal = file.Value.Hunks[1];
        Assert.Equal((10, 2, 11, 0), (removal.OldStart, removal.OldCount, removal.NewStart, removal.NewCount));
        Assert.Equal([new DiffLine(DiffLineKind.Removed, 10, null, "ten"), new DiffLine(DiffLineKind.Removed, 11, null, "eleven")], removal.Lines);
        Assert.Equal([2, 3], file.Value.AddedLines);
    }

    [Fact]
    public void Parse_ContentThatLooksLikeHeadersStaysContent()
    {
        const string Diff = """
            diff --git a/A.txt b/A.txt
            --- a/A.txt
            +++ b/A.txt
            @@ -1,2 +1,2 @@
            --- not a header
            +++ also not a header
             diff --git a/x b/x
            \ No newline at end of file

            """;

        var file = Assert.Single(UnifiedDiff.Parse(Diff.ReplaceLineEndings("\n"))).Value;

        Assert.Equal(
            [
                new DiffLine(DiffLineKind.Removed, 1, null, "-- not a header"),
                new DiffLine(DiffLineKind.Added, null, 1, "++ also not a header"),
                new DiffLine(DiffLineKind.Context, 2, 2, "diff --git a/x b/x"),
            ],
            Assert.Single(file.Hunks).Lines);
    }

    [Fact]
    public void Parse_NoNewlineMarkerBetweenRemovedAndAddedLinesIsSkipped()
    {
        const string Diff = "diff --git a/A b/A\n--- a/A\n+++ b/A\n@@ -1 +1 @@\n-old\n\\ No newline at end of file\n+new\n\\ No newline at end of file\n";

        var lines = Assert.Single(Assert.Single(UnifiedDiff.Parse(Diff)).Value.Hunks).Lines;

        Assert.Equal([new DiffLine(DiffLineKind.Removed, 1, null, "old"), new DiffLine(DiffLineKind.Added, null, 1, "new")], lines);
    }

    [Fact]
    public void Parse_CrlfOutputIsTrimmed()
    {
        const string Diff = "diff --git a/A b/A\r\n--- a/A\r\n+++ b/A\r\n@@ -1 +1,2 @@\r\n a\r\n+b\r\n";

        var (path, file) = Assert.Single(UnifiedDiff.Parse(Diff));

        Assert.Equal("A", path);
        Assert.Equal([new DiffLine(DiffLineKind.Context, 1, 1, "a"), new DiffLine(DiffLineKind.Added, null, 2, "b")], Assert.Single(file.Hunks).Lines);
    }

    [Fact]
    public void Parse_NewDeletedAndBinaryFilesKeepTheirStatusAndPath()
    {
        const string Diff = """
            diff --git a/New.cs b/New.cs
            new file mode 100644
            --- /dev/null
            +++ b/New.cs
            @@ -0,0 +1,2 @@
            +a
            +b
            diff --git a/Gone.cs b/Gone.cs
            deleted file mode 100644
            --- a/Gone.cs
            +++ /dev/null
            @@ -1 +0,0 @@
            -x
            diff --git a/img.png b/img.png
            index 1111111..2222222 100644
            Binary files a/img.png and b/img.png differ

            """;

        var files = UnifiedDiff.Parse(Diff.ReplaceLineEndings("\n"));

        Assert.Equal(DiffFileStatus.Added, files["New.cs"].Status);
        Assert.Equal([1, 2], files["New.cs"].AddedLines);
        Assert.Equal(DiffFileStatus.Deleted, files["Gone.cs"].Status);
        Assert.Equal([new DiffLine(DiffLineKind.Removed, 1, null, "x")], Assert.Single(files["Gone.cs"].Hunks).Lines);
        Assert.Equal(DiffFileStatus.Binary, files["img.png"].Status);
        Assert.Empty(files["img.png"].Hunks);
    }

    [Fact]
    public void Parse_QuotedPathsAreUnescapedAsUtf8()
    {
        const string Diff = """
            diff --git "a/caf\303\251 \"x\".cs" "b/caf\303\251 \"x\".cs"
            --- "a/caf\303\251 \"x\".cs"
            +++ "b/caf\303\251 \"x\".cs"
            @@ -0,0 +1 @@
            +a
            diff --git "a/tab\there.png" "b/tab\there.png"
            Binary files "a/tab\there.png" and "b/tab\there.png" differ

            """;

        var files = UnifiedDiff.Parse(Diff.ReplaceLineEndings("\n"));

        Assert.Contains("café \"x\".cs", files.Keys);
        Assert.Contains("tab\there.png", files.Keys);
    }

    [Fact]
    public void Added_IsOneHunkOfAddedLines()
    {
        var file = UnifiedDiff.Added(["a", "b"]);

        Assert.Equal(DiffFileStatus.Added, file.Status);
        var hunk = Assert.Single(file.Hunks);
        Assert.Equal((0, 0, 1, 2), (hunk.OldStart, hunk.OldCount, hunk.NewStart, hunk.NewCount));
        Assert.Equal([1, 2], file.AddedLines);
        Assert.Empty(UnifiedDiff.Added([]).Hunks);
    }

    [Fact]
    public async Task GetAsync_WithContext_ReturnsHunksWhoseAddedLinesMatchTheChangedLines()
    {
        Git("init", "-q", "-b", "main");
        var code = Path.Combine(_root, "src", "A.cs");
        _ = Directory.CreateDirectory(Path.GetDirectoryName(code)!);
        var original = Enumerable.Range(1, 30).Select(i => $"line {i}").ToArray();
        await File.WriteAllLinesAsync(code, original, _ct);
        await File.WriteAllTextAsync(Path.Combine(_root, "Gone.cs"), "x\n", _ct);
        Git("add", ".");
        Git("-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "init");

        var changed = original.ToList();
        changed[2] = "LINE 3";
        changed.Insert(10, "inserted");
        changed.RemoveAt(25);
        await File.WriteAllLinesAsync(code, changed, _ct);
        File.Delete(Path.Combine(_root, "Gone.cs"));
        await File.WriteAllLinesAsync(Path.Combine(_root, "New.cs"), ["n1", "n2"], _ct);

        var plain = await ChangedLines.GetAsync(_root, ChangeBase.Working, null, _ct);
        var withContext = await ChangedLines.GetAsync(_root, ChangeBase.Working, null, contextLines: 3, _ct);

        Assert.Empty(plain.Diff);
        Assert.Equal(plain.Files.Keys.Order(), withContext.Files.Keys.Order());
        foreach (var (path, lines) in plain.Files)
        {
            Assert.Equal(lines, withContext.Files[path]);
            Assert.Equal(lines, withContext.Diff[path].AddedLines.Order());
        }

        var top = Path.GetDirectoryName(withContext.Diff.Keys.Single(k => k.EndsWith("A.cs", StringComparison.Ordinal)))!;
        var file = withContext.Diff[Path.Combine(top, "A.cs")];
        Assert.Equal(DiffFileStatus.Modified, file.Status);
        Assert.Contains(file.Hunks.SelectMany(h => h.Lines), l => l is { Kind: DiffLineKind.Context, Text: "line 1" });
        Assert.Contains(file.Hunks.SelectMany(h => h.Lines), l => l is { Kind: DiffLineKind.Removed, Text: "line 3" });
        var root = Path.GetDirectoryName(top)!;
        Assert.Equal(DiffFileStatus.Deleted, withContext.Diff[Path.Combine(root, "Gone.cs")].Status);
        Assert.Equal(DiffFileStatus.Added, withContext.Diff[Path.Combine(root, "New.cs")].Status);
        Assert.Equal([1, 2], withContext.Diff[Path.Combine(root, "New.cs")].AddedLines);
    }

    [Fact]
    public async Task GetAsync_WithContext_GivesAnUntrackedSymbolicLinkItsTargetPathNotTheTargetsContent()
    {
        Git("init", "-q", "-b", "main");
        await File.WriteAllTextAsync(Path.Combine(_root, "Kept.cs"), "x\n", _ct);
        Git("add", ".");
        Git("-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "init");
        var outside = Directory.CreateTempSubdirectory("piston-unified-diff-outside-").FullName;
        try
        {
            var secret = Path.Combine(outside, "secret.txt");
            await File.WriteAllTextAsync(secret, "do not show\n", _ct);
            try
            {
                _ = File.CreateSymbolicLink(Path.Combine(_root, "Link.cs"), secret);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Assert.Skip($"Symbolic links cannot be created here: {e.Message}");
            }

            var changes = await ChangedLines.GetAsync(_root, ChangeBase.Working, null, contextLines: 3, _ct);

            var link = changes.Diff.Single(kv => kv.Key.EndsWith("Link.cs", StringComparison.Ordinal)).Value;
            var lines = link.Hunks.SelectMany(h => h.Lines).ToList();
            Assert.Equal([secret], lines.Select(l => l.Text));
            Assert.DoesNotContain(lines, l => l.Text.Contains("do not show", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public async Task GetAsync_WithContext_MarksARemovalOnlyRangeFileThatDiffersInTheWorkingTreeAsDiverged()
    {
        Git("init", "-q", "-b", "main");
        var code = Path.Combine(_root, "R.cs");
        await File.WriteAllLinesAsync(code, ["a", "b", "c", "d"], _ct);
        Git("add", ".");
        Git("-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "init");
        await File.WriteAllLinesAsync(code, ["a", "c", "d"], _ct);
        Git("-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-am", "remove");
        await File.WriteAllLinesAsync(code, ["new", "a", "c", "d"], _ct);

        var changes = await ChangedLines.GetAsync(_root, ChangeBase.Range, "HEAD~1..HEAD", contextLines: 3, _ct);

        var path = changes.Diff.Keys.Single(k => k.EndsWith("R.cs", StringComparison.Ordinal));
        Assert.Empty(changes.Files);
        Assert.Contains(path, changes.DivergedFiles);
    }

    private void Git(params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = _root, RedirectStandardError = true };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = System.Diagnostics.Process.Start(psi)!;
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
    }
}
