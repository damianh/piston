// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Services;
using Xunit;

namespace Piston.Engine.Tests.Services;

public sealed class SourceFingerprinterTests : IDisposable
{
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
                     "src/Directory.Build.props",
                 })
        {
            var previous = SourceFingerprinter.Compute(scope);
            Write(input, Guid.NewGuid().ToString());
            Assert.NotEqual(previous.Value, SourceFingerprinter.Compute(scope).Value);
        }
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
