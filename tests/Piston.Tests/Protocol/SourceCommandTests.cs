// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Text;
using System.Text.Json.Nodes;
using Piston.Hosting.Protocol;
using Piston.Hosting.Services;
using Piston.Protocol.JsonRpc;
using Piston.Tests.Mcp;
using Xunit;

namespace Piston.Tests.Protocol;

public sealed class SourceCommandTests : IDisposable
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _parent = Directory.CreateTempSubdirectory("piston-source-cmd-").FullName;
    private readonly string _root;
    private readonly EngineCommandDispatcher _dispatcher;

    public SourceCommandTests()
    {
        _root = Directory.CreateDirectory(Path.Combine(_parent, "repo")).FullName;
        var engine = new StubEngine();
        engine.State.SolutionPath = Path.Combine(_root, "App.slnx");
        _dispatcher = new EngineCommandDispatcher(engine);
    }

    public void Dispose() => Directory.Delete(_parent, recursive: true);

    private async Task<JsonNode> GetAsync(string path) =>
        (await _dispatcher.HandleCommandAsync(
            ProtocolMethods.SourceGet, new JsonObject { ["filePath"] = path }, _ct))!;

    [Fact]
    public async Task ReturnsTheLinesOfAFileInTheSolution()
    {
        var path = Path.Combine(_root, "src", "A.cs");
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "\uFEFFclass A\r\n{\n}", _ct);

        var result = await GetAsync(path);

        Assert.Equal(path, result["filePath"]!.GetValue<string>());
        Assert.Equal(["class A", "{", "}"], result["lines"]!.AsArray().Select(l => l!.GetValue<string>()));
        Assert.Null(result["unavailable"]);
    }

    [Fact]
    public async Task ResolvesPathsRelativeToTheSolution()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "B.cs"), "b", _ct);

        var result = await GetAsync("B.cs");

        Assert.Equal("b", result["lines"]![0]!.GetValue<string>());
    }

    [Theory]
    [InlineData("utf-16")]
    [InlineData("utf-16BE")]
    [InlineData("utf-32")]
    [InlineData("utf-32BE")]
    public async Task ReadsBomMarkedUnicodeSource(string encodingName)
    {
        var encoding = Encoding.GetEncoding(encodingName);
        await File.WriteAllTextAsync(Path.Combine(_root, "Unicode.cs"), "class Unicode\n{\n}", encoding, _ct);

        var result = await GetAsync("Unicode.cs");

        Assert.Null(result["unavailable"]);
        Assert.Equal(["class Unicode", "{", "}"], result["lines"]!.AsArray().Select(l => l!.GetValue<string>()));
    }

    [Fact]
    public async Task RefusesCaseDistinctSiblingOnCaseSensitiveFileSystems()
    {
        var sibling = Path.Combine(_parent, "REPO");
        _ = Directory.CreateDirectory(sibling);
        var path = Path.Combine(sibling, "Secret.cs");
        await File.WriteAllTextAsync(path, "secret", _ct);
        if (File.Exists(Path.Combine(_root, "Secret.cs")))
        {
            return;
        }

        var result = await GetAsync(path);

        Assert.Null(result["lines"]);
        Assert.Contains("outside the solution", result["unavailable"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../secret.txt")]
    [InlineData("src/../../secret.txt")]
    public async Task RefusesFilesOutsideTheSolution(string relative)
    {
        await File.WriteAllTextAsync(Path.Combine(_parent, "secret.txt"), "secret", _ct);

        var result = await GetAsync(Path.Combine(_root, relative));

        Assert.Null(result["lines"]);
        Assert.Contains("outside the solution", result["unavailable"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefusesALinkThatPointsOutsideTheSolution()
    {
        var secret = Path.Combine(_parent, "secret.txt");
        await File.WriteAllTextAsync(secret, "secret", _ct);
        var outside = Directory.CreateDirectory(Path.Combine(_parent, "outside")).FullName;
        await File.WriteAllTextAsync(Path.Combine(outside, "C.cs"), "c", _ct);
        try
        {
            _ = File.CreateSymbolicLink(Path.Combine(_root, "link.cs"), secret);
            _ = Directory.CreateSymbolicLink(Path.Combine(_root, "linked"), outside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("Creating symbolic links is not permitted here.");
        }

        Assert.Null((await GetAsync(Path.Combine(_root, "link.cs")))["lines"]);
        Assert.Null((await GetAsync(Path.Combine(_root, "linked", "C.cs")))["lines"]);
    }

    [Fact]
    public async Task RefusesBinaryMissingAndOversizedFiles()
    {
        await File.WriteAllBytesAsync(Path.Combine(_root, "bin.dll"), [0x4D, 0x5A, 0x00, 0x01], _ct);
        await File.WriteAllTextAsync(Path.Combine(_root, "big.cs"), new string('x', 1024 * 1024 + 1), _ct);

        Assert.Contains("not text", (await GetAsync("bin.dll"))["unavailable"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("no longer exists", (await GetAsync("gone.cs"))["unavailable"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("too large", (await GetAsync("big.cs"))["unavailable"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadsSourceWhenTheSolutionDirectoryIsAFileSystemRoot()
    {
        var path = Path.Combine(_root, "Root.cs");
        await File.WriteAllTextAsync(path, "class Root {}", _ct);

        var result = SourceFileReader.Read(path, Path.GetPathRoot(_root));

        Assert.Null(result.Unavailable);
        Assert.Equal(["class Root {}"], result.Lines);
    }

    [Fact]
    public async Task RefusesASiblingDirectoryThatSharesThePrefix()
    {
        var sibling = Directory.CreateDirectory(_root + "-other").FullName;
        await File.WriteAllTextAsync(Path.Combine(sibling, "D.cs"), "d", _ct);

        Assert.Null((await GetAsync(Path.Combine(sibling, "D.cs")))["lines"]);
    }
}
