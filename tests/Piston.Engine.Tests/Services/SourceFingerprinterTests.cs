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
        Directory.CreateDirectory(Path.Combine(_root, "src"));
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
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
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
        // Pins the algorithm so the independent measurements-harness implementation stays compatible.
        var dir = Path.Combine(_parent, "vector");
        Directory.CreateDirectory(Path.Combine(dir, "src"));
        File.WriteAllText(Path.Combine(dir, "a.txt"), "A");
        File.WriteAllText(Path.Combine(dir, "src", "b.cs"), "B");

        var fp = SourceFingerprinter.Compute(dir);

        var expected = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("piston-source-v1\0a.txt\0A\0src/b.cs\0B\0")));
        Assert.Equal(expected, fp.Value);
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
}
