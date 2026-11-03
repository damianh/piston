// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Diagnostics;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Piston.Engine.Execution;
using Xunit;

namespace Piston.Tests.Hosting;

public sealed class CoverageCommandLineTests : IDisposable
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _root = Directory.CreateTempSubdirectory("piston-coverage-cli-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData("daemon", "--coverage")]
    [InlineData("daemon", "--no-coverage")]
    [InlineData("daemon", "--coverage=false")]
    [InlineData("daemon", "--no-coverage=false")]
    [InlineData("mcp", "--coverage")]
    [InlineData("mcp", "--no-coverage")]
    [InlineData("mcp", "--coverage=false")]
    [InlineData("mcp", "--no-coverage=false")]
    public async Task DaemonAndMcpAcceptAndAdvertiseEachCoverageOption(string command, string option)
    {
        var result = await RunAsync(command, option, "--help");

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Error);
        Assert.Contains("--coverage", result.Output, StringComparison.Ordinal);
        Assert.Contains("--no-coverage", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("default: False", result.Output.Split('\n')
            .First(line => line.Contains("--coverage ", StringComparison.Ordinal)), StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(_root, ".piston")));
    }

    [Theory]
    [InlineData("daemon", "--coverage", "--no-coverage")]
    [InlineData("daemon", "--coverage=false", "--no-coverage")]
    [InlineData("daemon", "--coverage", "--no-coverage=false")]
    [InlineData("daemon", "--coverage=false", "--no-coverage=false")]
    [InlineData("daemon", "--no-coverage", "--coverage")]
    [InlineData("mcp", "--coverage", "--no-coverage")]
    [InlineData("mcp", "--coverage=false", "--no-coverage")]
    [InlineData("mcp", "--coverage", "--no-coverage=false")]
    [InlineData("mcp", "--coverage=false", "--no-coverage=false")]
    [InlineData("mcp", "--no-coverage", "--coverage")]
    public async Task DaemonAndMcpRejectBothOptionsEvenWhenAnExplicitValueIsFalse(
        string command, string first, string second)
    {
        var result = await RunAsync(command, first, second);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Output);
        Assert.Contains("--coverage and --no-coverage cannot be used together", result.Error, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(_root, ".piston")));
    }

    [Theory]
    [InlineData(true, "--coverage=false", false)]
    [InlineData(false, "--coverage=true", true)]
    [InlineData(false, "--coverage", true)]
    [InlineData(false, "--no-coverage=false", true)]
    [InlineData(true, "--no-coverage=true", false)]
    [InlineData(true, "--no-coverage", false)]
    public async Task DaemonExplicitBooleanCoverageOptionsOverrideConfig(
        bool configured, string option, bool expected)
    {
        WriteEmptySolutionAndConfig(configured);

        var result = await RunAsync("daemon", "App.slnx", "--stdio", option);

        Assert.Equal(0, result.ExitCode);
        var snapshots = new List<bool>();
        foreach (var line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using var json = JsonDocument.Parse(line);
            if (json.RootElement.TryGetProperty("method", out var method)
                && method.GetString() == "engine/stateSnapshot")
            {
                snapshots.Add(json.RootElement.GetProperty("params").GetProperty("coverageEnabled").GetBoolean());
            }
        }

        Assert.NotEmpty(snapshots);
        Assert.All(snapshots, enabled => Assert.Equal(expected, enabled));
    }

    [Theory]
    [InlineData(true, "--coverage=false", false)]
    [InlineData(false, "--coverage=true", true)]
    [InlineData(false, "--coverage", true)]
    [InlineData(false, "--no-coverage=false", true)]
    [InlineData(true, "--no-coverage=true", false)]
    [InlineData(true, "--no-coverage", false)]
    public async Task McpExplicitBooleanCoverageOptionsOverrideConfig(
        bool configured, string option, bool expected)
    {
        WriteEmptySolutionAndConfig(configured);
        using var process = Process.Start(StartInfo("mcp", "App.slnx", option))
            ?? throw new InvalidOperationException("Failed to start piston mcp.");
        var error = process.StandardError.ReadToEndAsync(_ct);
        try
        {
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(
                process.StandardInput.BaseStream, process.StandardOutput.BaseStream), cancellationToken: _ct)
                .WaitAsync(TimeSpan.FromSeconds(20), _ct);

            // Invalid base is validated only when coverage is enabled, without requiring a git repository.
            var result = await client.CallToolAsync("get_changed_line_coverage",
                new Dictionary<string, object?> { ["base"] = "invalid" }, cancellationToken: _ct);

            Assert.True(result.IsError);
            var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
            Assert.Contains(expected ? "'invalid' is not a base" : "Coverage is disabled", text, StringComparison.Ordinal);
        }
        finally
        {
            try { process.StandardInput.Close(); } catch (ObjectDisposedException) { } catch (IOException) { }
            try
            {
                await process.WaitForExitAsync(_ct).WaitAsync(TimeSpan.FromSeconds(20), _ct);
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None);
                }
            }
        }

        Assert.Equal(0, process.ExitCode);
        _ = await error;
    }

    private void WriteEmptySolutionAndConfig(bool coverage)
    {
        File.WriteAllText(Path.Combine(_root, "App.slnx"), "<Solution />");
        File.WriteAllText(Path.Combine(_root, ".piston.json"),
            $$"""{"coverageEnabled":{{coverage.ToString().ToLowerInvariant()}},"history":false,"persistResults":false}""");
    }

    private async Task<(int ExitCode, string Output, string Error)> RunAsync(params string[] args)
    {
        using var process = Process.Start(StartInfo(args))!;
        try
        {
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync(_ct);
            var error = process.StandardError.ReadToEndAsync(_ct);
            await process.WaitForExitAsync(_ct).WaitAsync(TimeSpan.FromSeconds(15), _ct);
            return (process.ExitCode, await output, await error);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }

    private ProcessStartInfo StartInfo(params string[] args)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = _root,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.Environment[ExecutionCoordinationDirectory.EnvironmentVariable] = Path.Combine(_root, "coordination");
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Piston.dll"));
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        return start;
    }
}
