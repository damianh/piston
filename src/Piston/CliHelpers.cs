using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Piston.Cli.Configuration;
using Piston.Engine;
using Piston.Cli.Mapping;
using Piston.Protocol.JsonRpc;

namespace Piston.Cli;

internal static class CliHelpers
{
    internal static string ResolveSolutionPath(FileInfo? solutionArg)
    {
        if (solutionArg is not null)
        {
            if (!solutionArg.Exists)
                throw new InvalidOperationException($"Solution file not found: {solutionArg.FullName}");

            var ext = solutionArg.Extension.ToLowerInvariant();
            if (ext is not ".sln" and not ".slnx" and not ".slnf")
                throw new InvalidOperationException($"Expected a .sln, .slnx, or .slnf file, got: {solutionArg.Name}");

            return solutionArg.FullName;
        }

        var cwd        = Directory.GetCurrentDirectory();
        var candidates = Directory.GetFiles(cwd, "*.sln")
            .Concat(Directory.GetFiles(cwd, "*.slnx"))
            .Concat(Directory.GetFiles(cwd, "*.slnf"))
            .ToList();

        return candidates.Count switch
        {
            0 => throw new InvalidOperationException(
                $"No .sln, .slnx, or .slnf file found in '{cwd}'. Pass the solution path explicitly."),
            1 => candidates[0],
            _ => throw new InvalidOperationException(
                $"Multiple solution files found in '{cwd}'. Pass the solution path explicitly:\n  " +
                string.Join("\n  ", candidates.Select(Path.GetFileName))),
        };
    }

    internal static PistonConfig LoadConfig(string solutionDir)
    {
        var configPath = Path.Combine(solutionDir, ".piston.json");
        if (!File.Exists(configPath))
            return new PistonConfig();

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddJsonFile(configPath, optional: true, reloadOnChange: false)
                .Build();

            var config = new PistonConfig();
            configuration.Bind(config);
            return config;
        }
        catch
        {
            return new PistonConfig();
        }
    }

    internal static PistonOptions BuildOptions(
        string solutionPath,
        int cliDebounceMs,
        string? cliFilter,
        bool cliCoverage,
        int cliParallelism,
        PistonConfig config)
    {
        var debounceMs = cliDebounceMs > 0 ? cliDebounceMs
            : config.DebounceMs is > 0 ? config.DebounceMs.Value
            : 300;

        var filter = cliFilter ?? config.TestFilter;
        var coverageEnabled = cliCoverage || (config.CoverageEnabled ?? false);

        var processPoolSize = cliParallelism > 0 ? cliParallelism
            : config.Parallelism is > 0 ? config.Parallelism.Value
            : 0;

        var processRecycleAfter = config.ProcessRecycleAfter is > 0 ? config.ProcessRecycleAfter.Value : 50;

        var testExecutionMode = TestExecutionMode.Auto;
        if (config.TestExecutionMode is not null &&
            Enum.TryParse<TestExecutionMode>(config.TestExecutionMode, ignoreCase: true, out var parsedMode))
        {
            testExecutionMode = parsedMode;
        }

        return new PistonOptions
        {
            SolutionPath        = solutionPath,
            DebounceInterval    = TimeSpan.FromMilliseconds(debounceMs),
            TestFilter          = filter,
            CoverageEnabled     = coverageEnabled,
            ProcessPoolSize     = processPoolSize,
            ProcessRecycleAfter = processRecycleAfter,
            TestExecutionMode   = testExecutionMode,
        };
    }

    internal static bool DotnetSdkAvailable()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("dotnet", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            p?.WaitForExit(3_000);
            return p?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    internal static JsonRpcNotification BuildStateSnapshot(IEngine engine)
    {
        var snapshot = engine.State.ToSnapshot();
        return ToNotification(ProtocolMethods.EngineStateSnapshot, snapshot);
    }

    internal static JsonRpcNotification ToNotification<T>(string method, T payload)
    {
        var paramsNode = JsonNode.Parse(
            System.Text.Json.JsonSerializer.Serialize(payload, JsonRpcSerializer.Options));
        return new JsonRpcNotification(method, paramsNode);
    }
}
