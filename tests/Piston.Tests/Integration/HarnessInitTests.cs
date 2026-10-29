// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Text.Json.Nodes;
using Piston.Integration;
using Xunit;

namespace Piston.Tests.Integration;

public sealed class HarnessInitTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"piston-init-{Guid.NewGuid():N}");
    private readonly InitLocations _locations;

    public HarnessInitTests()
    {
        _locations = new(Path.Combine(_root, "repo"), Path.Combine(_root, "copilot"), Path.Combine(_root, "claude"));
        _ = Directory.CreateDirectory(_locations.RepoDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Theory]
    [InlineData("Copilot", "User")]
    [InlineData("Copilot", "Repo")]
    [InlineData("Claude", "User")]
    [InlineData("Claude", "Repo")]
    public void WithoutWrite_WritesNothing(string harness, string scope)
    {
        var (exitCode, output) = Run(new(Enum.Parse<Harness>(harness), Enum.Parse<InitScope>(scope),
            Write: false, Force: false, Dashboard: false));

        Assert.Equal(0, exitCode);
        Assert.Contains("Dry run: nothing was written.", output, StringComparison.Ordinal);
        Assert.Equal([], Directory.GetFileSystemEntries(_locations.RepoDirectory));
        Assert.False(Directory.Exists(_locations.CopilotHome));
        Assert.False(Directory.Exists(_locations.ClaudeHome));
    }

    [Fact]
    public void CopilotUser_Write_InstallsEntrySkillAndCanvas_AndKeepsOtherServers()
    {
        var config = Path.Combine(_locations.CopilotHome, "mcp-config.json");
        _ = Directory.CreateDirectory(_locations.CopilotHome);
        File.WriteAllText(config, """{ "mcpServers": { "other": { "type": "local", "command": "x" } }, "extra": 1 }""");

        var (exitCode, _) = Run(new(Harness.Copilot, InitScope.User, Write: true, Force: false, Dashboard: false));

        Assert.Equal(0, exitCode);
        var root = JsonNode.Parse(File.ReadAllText(config))!;
        Assert.Equal("x", (string?)root["mcpServers"]!["other"]!["command"]);
        Assert.Equal(1, (int?)root["extra"]);
        var piston = root["mcpServers"]!["piston"]!;
        Assert.Equal("local", (string?)piston["type"]);
        Assert.Equal("piston", (string?)piston["command"]);
        Assert.Equal(["mcp"], piston["args"]!.AsArray().Select(a => (string?)a));
        Assert.Equal(["*"], piston["tools"]!.AsArray().Select(a => (string?)a));
        Assert.Equal(HarnessAssets.Skill, File.ReadAllBytes(Path.Combine(_locations.CopilotHome, "skills", "piston", "SKILL.md")));
        foreach (var (name, content) in HarnessAssets.CanvasFiles)
        {
            Assert.Equal(content, File.ReadAllBytes(Path.Combine(_locations.CopilotHome, "extensions", "piston-dashboard", name)));
        }
    }

    [Fact]
    public void Write_IsIdempotent()
    {
        var options = new InitOptions(Harness.Copilot, InitScope.User, Write: true, Force: false, Dashboard: false);
        _ = Run(options);
        var before = Snapshot(_locations.CopilotHome);

        var (exitCode, output) = Run(options);

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("created", output, StringComparison.Ordinal);
        Assert.Contains("unchanged", output, StringComparison.Ordinal);
        Assert.Equal(before, Snapshot(_locations.CopilotHome));
    }

    [Fact]
    public void DifferingContent_IsKeptWithoutForce_AndOverwrittenWithForce()
    {
        var skill = Path.Combine(_locations.CopilotHome, "skills", "piston", "SKILL.md");
        var config = Path.Combine(_locations.CopilotHome, "mcp-config.json");
        _ = Directory.CreateDirectory(Path.GetDirectoryName(skill)!);
        File.WriteAllText(skill, "my edits");
        File.WriteAllText(config, """{ "mcpServers": { "piston": { "command": "/opt/piston" } } }""");

        var (exitCode, output) = Run(new(Harness.Copilot, InitScope.User, Write: true, Force: false, Dashboard: false));

        Assert.Equal(1, exitCode);
        Assert.Contains("skipped", output, StringComparison.Ordinal);
        Assert.Equal("my edits", File.ReadAllText(skill));
        Assert.Equal("/opt/piston", (string?)JsonNode.Parse(File.ReadAllText(config))!["mcpServers"]!["piston"]!["command"]);
        Assert.True(File.Exists(Path.Combine(_locations.CopilotHome, "extensions", "piston-dashboard", "extension.mjs")));

        (exitCode, output) = Run(new(Harness.Copilot, InitScope.User, Write: true, Force: true, Dashboard: false));

        Assert.Equal(0, exitCode);
        Assert.Contains("overwritten", output, StringComparison.Ordinal);
        Assert.Equal(HarnessAssets.Skill, File.ReadAllBytes(skill));
        Assert.Equal("piston", (string?)JsonNode.Parse(File.ReadAllText(config))!["mcpServers"]!["piston"]!["command"]);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[]")]
    [InlineData("""{ "playwright": { "command": "npx" } }""")]
    [InlineData("""{ "mcpServers": [] }""")]
    public void InvalidConfig_IsNeverWritten(string content)
    {
        var config = Path.Combine(_locations.RepoDirectory, ".mcp.json");
        File.WriteAllText(config, content);

        var (exitCode, output) = Run(new(Harness.Claude, InitScope.Repo, Write: true, Force: true, Dashboard: false));

        Assert.Equal(1, exitCode);
        Assert.Contains("invalid", output, StringComparison.Ordinal);
        Assert.Equal(content, File.ReadAllText(config));
    }

    [Fact]
    public void Dashboard_AddsTheDashboardArgument()
    {
        _ = Run(new(Harness.Claude, InitScope.Repo, Write: true, Force: false, Dashboard: true));

        var piston = JsonNode.Parse(File.ReadAllText(Path.Combine(_locations.RepoDirectory, ".mcp.json")))!["mcpServers"]!["piston"]!;
        Assert.Equal(["mcp", "--dashboard"], piston["args"]!.AsArray().Select(a => (string?)a));
    }

    [Fact]
    public void Write_KeepsSymbolicLinksAndFileModes()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Symbolic links and Unix file modes.
        }

        var dotfiles = Directory.CreateDirectory(Path.Combine(_root, "dotfiles")).FullName;
        var real = Path.Combine(dotfiles, "mcp-config.json");
        File.WriteAllText(real, """{ "mcpServers": {} }""");
        File.SetUnixFileMode(real, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        _ = Directory.CreateDirectory(_locations.CopilotHome);
        var link = Path.Combine(_locations.CopilotHome, "mcp-config.json");
        _ = File.CreateSymbolicLink(link, real);

        var (exitCode, _) = Run(new(Harness.Copilot, InitScope.User, Write: true, Force: false, Dashboard: false));

        Assert.Equal(0, exitCode);
        Assert.Equal(real, new FileInfo(link).LinkTarget);
        Assert.NotNull(JsonNode.Parse(File.ReadAllText(real))!["mcpServers"]!["piston"]);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(real));
    }

    [Fact]
    public void ClaudeUser_WritesOnlyTheSkill_AndPrintsTheRegistrationCommand()
    {
        var (exitCode, output) = Run(new(Harness.Claude, InitScope.User, Write: true, Force: false, Dashboard: false));

        Assert.Equal(0, exitCode);
        Assert.Contains("claude mcp add --scope user --transport stdio piston -- piston mcp", output, StringComparison.Ordinal);
        Assert.Equal([Path.Combine(_locations.ClaudeHome, "skills", "piston", "SKILL.md")],
            Directory.GetFiles(_root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void RepoScope_CopilotAndClaude_ShareOneMcpEntry()
    {
        var (copilotExit, _) = Run(new(Harness.Copilot, InitScope.Repo, Write: true, Force: false, Dashboard: false));
        var (claudeExit, claudeOutput) = Run(new(Harness.Claude, InitScope.Repo, Write: true, Force: false, Dashboard: false));

        Assert.Equal(0, copilotExit);
        Assert.Equal(0, claudeExit);
        Assert.Contains("unchanged", claudeOutput, StringComparison.Ordinal);
        var repo = _locations.RepoDirectory;
        var piston = JsonNode.Parse(File.ReadAllText(Path.Combine(repo, ".mcp.json")))!["mcpServers"]!["piston"]!;
        Assert.Equal("stdio", (string?)piston["type"]);
        Assert.True(File.Exists(Path.Combine(repo, ".github", "skills", "piston", "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(repo, ".github", "extensions", "piston-dashboard", "extension.mjs")));
        Assert.True(File.Exists(Path.Combine(repo, ".claude", "skills", "piston", "SKILL.md")));
        Assert.False(Directory.Exists(_locations.CopilotHome));
        Assert.False(Directory.Exists(_locations.ClaudeHome));
    }

    [Fact]
    public void CanvasAssets_AreTheRepositoryExtensionsNonTestFiles()
    {
        var extension = Path.Combine(RepositoryRoot(), ".github", "extensions", "piston-dashboard");
        var expected = Directory.GetFiles(extension)
            .Where(f => !f.EndsWith(".test.mjs", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal);

        Assert.Equal(expected, HarnessAssets.CanvasFiles.Select(f => f.Name));
        Assert.Contains(HarnessAssets.CanvasFiles, f => f.Name == "extension.mjs");
        foreach (var (name, content) in HarnessAssets.CanvasFiles)
        {
            Assert.Equal(File.ReadAllBytes(Path.Combine(extension, name)), content);
        }
    }

    [Fact]
    public void Skill_HasFrontmatterNamedPiston()
    {
        var skill = System.Text.Encoding.UTF8.GetString(HarnessAssets.Skill);

        Assert.StartsWith("---", skill, StringComparison.Ordinal);
        Assert.Contains("name: piston", skill, StringComparison.Ordinal);
    }

    private (int ExitCode, string Output) Run(InitOptions options)
    {
        using var output = new StringWriter();
        var exitCode = HarnessInit.Run(options, _locations, output);
        TestContext.Current.TestOutputHelper?.WriteLine(output.ToString());
        return (exitCode, output.ToString());
    }

    private static Dictionary<string, string> Snapshot(string directory) =>
        Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
            .ToDictionary(f => f, f => Convert.ToBase64String(File.ReadAllBytes(f)) + File.GetLastWriteTimeUtc(f).Ticks);

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Piston.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Could not find the repository root (Piston.slnx).");
    }
}
