using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Piston.Hosting.Configuration;
using Piston.Engine;
using Piston.Hosting.Mapping;
using Piston.Protocol.JsonRpc;

namespace Piston.Hosting;

public static class HostHelpers
{
    public static string ResolveSolutionPath(FileInfo? solutionArg, string? workingDirectory = null) =>
        ResolveSolution(solutionArg, workingDirectory).SolutionPath;

    public static (string SolutionPath, PistonConfig Config) ResolveSolution(
        FileInfo? solutionArg, string? workingDirectory = null)
    {
        if (solutionArg is not null)
        {
            var path = ValidateSolutionPath(solutionArg.FullName);
            return (path, LoadConfig(Path.GetDirectoryName(path)!));
        }

        var cwd = Path.GetFullPath(workingDirectory ?? Directory.GetCurrentDirectory());
        var config = LoadConfig(cwd);
        if (config.Solution is not null)
        {
            var configPath = Path.Combine(cwd, ".piston.json");
            if (string.IsNullOrWhiteSpace(config.Solution))
                throw new InvalidOperationException($"The 'solution' field in '{configPath}' must not be empty.");

            try
            {
                var path = Path.GetFullPath(config.Solution, cwd);
                return (ValidateSolutionPath(path), config);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or InvalidOperationException)
            {
                throw new InvalidOperationException(
                    $"Invalid 'solution' in '{configPath}': {ex.Message}", ex);
            }
        }

        var candidates = Directory.GetFiles(cwd, "*.sln")
            .Concat(Directory.GetFiles(cwd, "*.slnx"))
            .Concat(Directory.GetFiles(cwd, "*.slnf"))
            .ToList();

        var discoveredPath = candidates.Count switch
        {
            0 => throw new InvalidOperationException(
                $"No .sln, .slnx, or .slnf file found in '{cwd}'. Pass the solution path explicitly."),
            1 => candidates[0],
            _ => throw new InvalidOperationException(
                $"Multiple solution files found in '{cwd}'. Pass the solution path explicitly:\n  " +
                string.Join("\n  ", candidates.Select(Path.GetFileName))),
        };
        return (discoveredPath, config);
    }

    private static string ValidateSolutionPath(string path)
    {
        if (!File.Exists(path))
            throw new InvalidOperationException($"Solution file not found: {path}");

        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not ".sln" and not ".slnx" and not ".slnf")
            throw new InvalidOperationException($"Expected a .sln, .slnx, or .slnf file, got: {Path.GetFileName(path)}");

        return path;
    }

    public static PistonConfig LoadConfig(string solutionDir)
    {
        var configPath = Path.Combine(solutionDir, ".piston.json");
        if (!File.Exists(configPath))
            return new PistonConfig();

        try
        {
            using var stream = File.OpenRead(configPath);
            var configuration = new ConfigurationBuilder()
                .AddJsonStream(stream)
                .Build();

            var config = new PistonConfig();
            configuration.Bind(config);
            return config;
        }
        catch (Exception ex) when (ex is IOException or FormatException or InvalidOperationException
                                  or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Could not load configuration '{configPath}': {ex.Message}", ex);
        }
    }

    public static PistonOptions BuildOptions(
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

    public static bool DotnetSdkAvailable()
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

    public static JsonRpcNotification BuildStateSnapshot(IEngine engine)
    {
        var snapshot = engine.State.ToSnapshot();
        return ToNotification(ProtocolMethods.EngineStateSnapshot, snapshot);
    }

    public static JsonRpcNotification ToNotification<T>(string method, T payload)
    {
        var paramsNode = JsonNode.Parse(
            System.Text.Json.JsonSerializer.Serialize(payload, JsonRpcSerializer.Options));
        return new JsonRpcNotification(method, paramsNode);
    }
}
