// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Diagnostics;
using Piston.Engine.Services;
using Xunit;

namespace Piston.Engine.Tests.Services;

public sealed class SourceFingerprinterTests : IDisposable
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _parent = Path.Combine(Path.GetTempPath(), $"piston-fp-{Guid.NewGuid():N}");
    private readonly string _root;

    public SourceFingerprinterTests()
    {
        _root = Path.Combine(_parent, "repo");
        _ = Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "App.slnx"), "<Solution />");
        File.WriteAllText(Path.Combine(_root, "src", "Code.cs"), "class Code { }");
    }

    public void Dispose()
    {
        try { Directory.Delete(_parent, recursive: true); } catch { /* best effort */ }
    }

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public void Compute_IsDeterministic()
    {
        var a = SourceFingerprinter.Compute(_root);
        var b = SourceFingerprinter.Compute(_root);

        Assert.Equal(a, b);
        Assert.Equal(SourceFingerprinter.Algorithm, a.Algorithm);
        Assert.Equal(2, a.FileCount);
        Assert.Equal(64, a.Value.Length);
    }

    [Fact]
    public void Compute_KnownVector()
    {
        // Pins the algorithm: fingerprints are reported to clients and persisted, so changes need a new version.
        var dir = Path.Combine(_parent, "vector");
        _ = Directory.CreateDirectory(Path.Combine(dir, "src"));
        File.WriteAllText(Path.Combine(dir, "a.txt"), "A");
        File.WriteAllText(Path.Combine(dir, "src", "b.cs"), "B");

        var fp = SourceFingerprinter.Compute(dir);

        const string expected = "47a023661287f6ce3402c175f1b8adf87ecde9245e70d0dda884a87b0e3ee16a";
        Assert.Equal(expected, fp.Value);
    }

    [Fact]
    public void Compute_EmbeddedNulsCannotImpersonateFileBoundaries()
    {
        var dir = Path.Combine(_parent, "nul-vector");
        _ = Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "a"), "X\0b\0Y");
        var single = SourceFingerprinter.Compute(dir);
        File.WriteAllText(Path.Combine(dir, "a"), "X");
        File.WriteAllText(Path.Combine(dir, "b"), "Y");

        Assert.NotEqual(single.Value, SourceFingerprinter.Compute(dir).Value);
    }

    [Fact]
    public void Compute_ChangesWhenContentChanges()
    {
        var before = SourceFingerprinter.Compute(_root);
        Write("src/Code.cs", "class Code { int x; }");

        Assert.NotEqual(before.Value, SourceFingerprinter.Compute(_root).Value);
    }

    [Fact]
    public void Compute_ChangesWhenFileIsRenamed()
    {
        var before = SourceFingerprinter.Compute(_root);
        File.Move(Path.Combine(_root, "src", "Code.cs"), Path.Combine(_root, "src", "Other.cs"));

        Assert.NotEqual(before.Value, SourceFingerprinter.Compute(_root).Value);
    }

    [Theory]
    [InlineData("bin/Debug/App.dll")]
    [InlineData("src/obj/project.assets.json")]
    [InlineData("TestResults/run.trx")]
    [InlineData(".piston/piston.db")]
    [InlineData(".git/HEAD")]
    public void Compute_IgnoresGeneratedAndToolDirectories(string relative)
    {
        var before = SourceFingerprinter.Compute(_root);
        Write(relative, "generated");

        Assert.Equal(before, SourceFingerprinter.Compute(_root));
    }

    [Fact]
    public void Compute_IncludesNearestAncestorBuildInputs()
    {
        var before = SourceFingerprinter.Compute(_root);
        File.WriteAllText(Path.Combine(_parent, "global.json"), """{ "sdk": { "version": "10.0.100" } }""");

        var withGlobalJson = SourceFingerprinter.Compute(_root);
        Assert.NotEqual(before.Value, withGlobalJson.Value);
        Assert.Equal(before.FileCount + 1, withGlobalJson.FileCount);

        File.WriteAllText(Path.Combine(_parent, "global.json"), """{ "sdk": { "version": "10.0.200" } }""");
        Assert.NotEqual(withGlobalJson.Value, SourceFingerprinter.Compute(_root).Value);
    }

    private SourceInputScope ProjectScope(params string[] declared) => SourceInputScope.ForProjects(
        Path.Combine(_root, "App.slnx"),
        [Path.Combine(_root, "src", "Lib", "Lib.csproj")],
        declared.Select(relative => Path.Combine(_root, relative)));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ComputeProjectScope_CompletesWithAPendingStdinRead(bool gitRepository)
    {
        if (gitRepository)
        {
            GitTestRepository.Init(_root, "*.tmp");
        }
        Write("src/Lib/Lib.csproj", "<Project />");
        Write("src/Lib/Code.cs", "class Lib { }");
        Write("src/Lib/cache.tmp", "ignored only in a git repository");
        var expected = SourceFingerprinter.Compute(ProjectScope()).Value;
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(typeof(ExecutionLeaseHolder.Program).Assembly.Location);
        start.ArgumentList.Add("fingerprint");
        start.ArgumentList.Add(Path.Combine(_root, "App.slnx"));
        start.ArgumentList.Add(Path.Combine(_root, "src", "Lib", "Lib.csproj"));
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(_ct);
        var error = process.StandardError.ReadToEndAsync(_ct);
        try
        {
            // Leave stdin open without sending data: Git must not inherit the blocked protocol reader.
            await Task.WhenAll(process.WaitForExitAsync(_ct), output, error)
                .WaitAsync(TimeSpan.FromSeconds(10), _ct);
            Assert.True(process.ExitCode == 0, await error);
            Assert.Equal(expected, (await output).Trim());
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(_ct).WaitAsync(TimeSpan.FromSeconds(5), _ct);
            }
        }
    }

    [Fact]
    public void ComputeProjectScope_HashesProjectInputsButNotOtherFilesOrGitIgnoredFiles()
    {
        GitTestRepository.Init(_root, "*.tmp");
        Write("src/Lib/Lib.csproj", "<Project />");
        Write("src/Lib/Code.cs", "class Lib { }");
        Write("src/Lib/Declared.tmp", "declared");
        Write("testdata/input.json", "{}");
        var scope = ProjectScope("testdata/input.json", "src/Lib/Declared.tmp");

        var before = SourceFingerprinter.Compute(scope);
        Assert.Equal(SourceInputScope.ProjectsAlgorithm, before.Algorithm);

        // Runtime files outside projects, and git-ignored files in them, are not inputs.
        Write(".compat-storage/spool/entry.bin", "runtime");
        Write("testdata/output.json", "{}");
        Write("src/Lib/cache.tmp", "runtime");
        Assert.Equal(before, SourceFingerprinter.Compute(scope));

        foreach (var input in new[]
                 {
                     "src/Lib/Code.cs", "src/Lib/Declared.tmp", "testdata/input.json", "App.slnx",
                     "src/Directory.Build.props", ".gitignore",
                 })
        {
            var previous = SourceFingerprinter.Compute(scope);
            Write(input, Guid.NewGuid().ToString());
            Assert.NotEqual(previous.Value, SourceFingerprinter.Compute(scope).Value);
        }
    }

    [Fact]
    public void ProjectScope_CoversIgnoreRulesAndDirectoriesOnTheWayToProjects()
    {
        var scope = ProjectScope();

        Assert.True(scope.Covers(Path.Combine(_root, ".gitignore")));
        Assert.True(scope.Covers(Path.Combine(_root, "src", ".gitignore")));
        Assert.True(scope.Covers(Path.Combine(_root, "src")));
        Assert.False(scope.Covers(Path.Combine(_root, "testdata")));
        Assert.False(scope.Covers(Path.Combine(_root, "testdata", ".gitignore")));
    }

    [Fact]
    public void ComputeProjectScope_WithoutGit_HashesEveryProjectFile()
    {
        Write("src/Lib/Lib.csproj", "<Project />");
        var scope = ProjectScope();
        var before = SourceFingerprinter.Compute(scope);

        Write("src/Lib/cache.tmp", "runtime");
        Assert.NotEqual(before.Value, SourceFingerprinter.Compute(scope).Value);

        Write("src/Lib/obj/project.assets.json", "{}");
        Write("outside/file.txt", "runtime");
        Assert.Equal(before.FileCount + 1, SourceFingerprinter.Compute(scope).FileCount);
    }

    [Fact]
    public void ComputeDirectoryScope_IsTheDirectoryFingerprint() =>
        Assert.Equal(SourceFingerprinter.Compute(_root), SourceFingerprinter.Compute(SourceInputScope.ForDirectory(_root)));
}
