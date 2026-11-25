// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Piston.Engine;
using Piston.Engine.Execution;
using Piston.Engine.Services;
using Piston.Hosting.Configuration;
using Piston.Hosting.Mapping;
using Piston.Protocol.JsonRpc;
using Piston.Protocol.Messages;

namespace Piston.Hosting;

public static class HostHelpers
{
    public static string ResolveSolutionPath(FileInfo? solutionArg, string? workingDirectory = null) =>
        ResolveSolution(solutionArg, workingDirectory).SolutionPath;

    public static (string SolutionPath, PistonConfig Config) ResolveSolution(
        FileInfo? solutionArg, string? workingDirectory = null, string? configurationDirectory = null)
    {
        if (solutionArg is not null)
        {
            var path = ValidateSolutionPath(solutionArg.FullName);
            return (path, LoadConfig(configurationDirectory ?? Path.GetDirectoryName(path)!));
        }

        var cwd = PathNormalizer.GetFullPath(workingDirectory ?? Directory.GetCurrentDirectory());
        var config = LoadConfig(cwd);
        if (config.Solution is not null)
        {
            var configPath = Path.Combine(cwd, ".piston.json");
            if (string.IsNullOrWhiteSpace(config.Solution))
            {
                throw new InvalidOperationException($"The 'solution' field in '{configPath}' must not be empty.");
            }

            try
            {
                var path = PathNormalizer.GetFullPath(config.Solution, cwd);
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
        return (ValidateSolutionPath(discoveredPath), config);
    }

    private static string ValidateSolutionPath(string path)
    {
        path = PathNormalizer.GetFullPath(path);
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"Solution file not found: {path}");
        }

        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not ".sln" and not ".slnx" and not ".slnf")
        {
            throw new InvalidOperationException($"Expected a .sln, .slnx, or .slnf file, got: {Path.GetFileName(path)}");
        }

        return path;
    }

    public static PistonConfig LoadConfig(string solutionDir)
    {
        var configPath = Path.Combine(solutionDir, ".piston.json");
        try
        {
            using var stream = File.OpenRead(configPath);
            var configuration = new ConfigurationBuilder()
                .AddJsonStream(stream)
                .Build();

            ValidateTestResourcesShape(configuration.GetSection("testResources"));
            var config = new PistonConfig();
            configuration.Bind(config);
            if (configuration.GetSection("execution").GetChildren()
                .Any(child => child.Key.Equals("memoryMb", StringComparison.OrdinalIgnoreCase)))
            {
                config.LegacyExecutionMemoryPath = configPath;
            }
            return config;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new PistonConfig();
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
        bool? cliCoverage,
        int cliParallelism,
        PistonConfig config,
        string? ownerHost = null,
        string? ownerDashboardUrl = null,
        Func<TimeSpan?>? testTimeoutProvider = null,
        Action<string>? warn = null)
    {
        var debounceMs = cliDebounceMs > 0 ? cliDebounceMs
            : config.DebounceMs is > 0 ? config.DebounceMs.Value
            : 300;

        var filter = cliFilter ?? config.TestFilter;
        var coverageEnabled = cliCoverage ?? config.CoverageEnabled ?? true;

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

        var impactBackfill = ImpactBackfillMode.Test;
        if (config.ImpactBackfill is not null
            && (!Enum.TryParse(config.ImpactBackfill, ignoreCase: true, out impactBackfill)
                || !Enum.IsDefined(impactBackfill)
                || int.TryParse(config.ImpactBackfill, out _)))
        {
            throw new InvalidOperationException(
                $"Invalid 'impactBackfill' in .piston.json: expected \"class\", \"test\" or \"off\"; got \"{config.ImpactBackfill}\".");
        }

        // Unset derives the warning from the test timeout; "off" (null) must stay off, so it maps to zero.
        TimeSpan? hangThreshold = config.HangThreshold is null
            ? null
            : ParseDuration(config.HangThreshold, "hangThreshold", null) ?? TimeSpan.Zero;
        if (config.TestTimeout is not null)
        {
            (warn ?? (message => Console.Error.WriteLine($"warning: {message}")))(
                "'testTimeout' in .piston.json is no longer used; set the test timeout in the dashboard's Settings.");
        }

        var retryFailures = config.RetryFailures ?? 0;
        if (retryFailures is < 0 or > PistonOptions.MaxRetryFailedTests)
        {
            throw new InvalidOperationException(
                $"Invalid 'retryFailures' in .piston.json: expected 0 to {PistonOptions.MaxRetryFailedTests}; got {retryFailures}.");
        }

        if (config.LegacyExecutionMemoryPath is { } configPath)
        {
            throw new InvalidOperationException(
                $"'execution.memoryMb' in '{configPath}' is no longer supported. Remove that setting " +
                "before starting Piston, and restart older Piston daemon and MCP processes.");
        }

        var testResourceRules = BuildTestResourceRules(config.TestResources);

        return new PistonOptions
        {
            SolutionPath = solutionPath,
            DebounceInterval = TimeSpan.FromMilliseconds(debounceMs),
            TestFilter = filter,
            CoverageEnabled = coverageEnabled,
            ImpactBackfill = coverageEnabled ? impactBackfill : ImpactBackfillMode.Off,
            HangThreshold = hangThreshold,
            TestTimeoutProvider = testTimeoutProvider ?? CreateTestTimeoutProvider(),
            HistoryEnabled = config.History ?? true,
            PersistResults = config.PersistResults ?? true,
            RetryFailedTests = retryFailures,
            WarmTestHosts = config.WarmTestHosts ?? false,
            ProcessPoolSize = processPoolSize,
            ProcessRecycleAfter = processRecycleAfter,
            TestExecutionMode = testExecutionMode,
            TestResourceRules = testResourceRules,
            OwnerHost = ownerHost,
            OwnerDashboardUrl = ownerDashboardUrl,
            RequireTrust = ownerHost == McpStdioHost.OwnerHost,
            TrustOnStart = ownerHost == "daemon",
        };
    }

    // Binding ignores unknown keys and accepts an object for a list, so check the raw shape first.
    private static void ValidateTestResourcesShape(IConfigurationSection section)
    {
        if (section.Value is not null)
        {
            throw new InvalidOperationException(
                "Invalid 'testResources' in .piston.json: expected an array of { \"projects\": \"<glob>\", ... } entries.");
        }

        foreach (var entry in section.GetChildren())
        {
            if (!int.TryParse(entry.Key, out _))
            {
                throw new InvalidOperationException(
                    "Invalid 'testResources' in .piston.json: expected an array of { \"projects\": \"<glob>\", ... } entries.");
            }

            foreach (var property in entry.GetChildren())
            {
                if (!PistonTestResourceConfig.PropertyNames.Contains(property.Key, StringComparer.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Invalid 'testResources[{entry.Key}]' in .piston.json: unknown property '{property.Key}'. " +
                        $"Supported: {string.Join(", ", PistonTestResourceConfig.PropertyNames)}.");
                }

                if (property.Key.Equals("exclusive", StringComparison.OrdinalIgnoreCase)
                    && (property.Value is not null || property.GetChildren().Any(child => !int.TryParse(child.Key, out _))))
                {
                    throw new InvalidOperationException(
                        $"Invalid 'testResources[{entry.Key}].exclusive' in .piston.json: expected an array of resource keys.");
                }
            }
        }
    }

    private static IReadOnlyList<TestResourceRule> BuildTestResourceRules(List<PistonTestResourceConfig>? entries)
    {
        var rules = new List<TestResourceRule>();
        for (var i = 0; i < (entries?.Count ?? 0); i++)
        {
            var entry = entries![i];
            var where = $"testResources[{i}]";
            if (string.IsNullOrWhiteSpace(entry.Projects))
            {
                throw new InvalidOperationException(
                    $"Invalid '{where}' in .piston.json: 'projects' (a glob such as \"tests/**/*.IntegrationTests.csproj\") is required.");
            }

            var exclusive = new List<string>();
            foreach (var raw in entry.Exclusive ?? [])
            {
                if (!TestResourceKey.TryNormalize(raw, out var key, out var error))
                {
                    throw new InvalidOperationException($"Invalid '{where}.exclusive' in .piston.json: {error}");
                }

                exclusive.Add(key);
            }

            TestResourceIsolation? isolation = null;
            if (entry.Isolation is not null)
            {
                isolation = entry.Isolation.Trim().ToLowerInvariant() switch
                {
                    "repository" => TestResourceIsolation.Repository,
                    "none" => TestResourceIsolation.None,
                    _ => throw new InvalidOperationException(
                        $"Invalid '{where}.isolation' in .piston.json: expected \"repository\" or \"none\"; got \"{entry.Isolation}\"."),
                };
            }

            rules.Add(new TestResourceRule(entry.Projects.Trim())
            {
                Exclusive = exclusive,
                Containers = entry.Containers,
                Isolation = isolation,
            });
        }

        return rules;
    }

    /// <summary>
    /// Reads the test timeout from the machine-wide user settings each time it is called, so a change made in the
    /// dashboard applies from the next run. Uses the default timeout when the settings are unavailable.
    /// </summary>
    public static Func<TimeSpan?> CreateTestTimeoutProvider()
    {
        UserSettingsStore? store = null;
        try
        {
            if (UserSettingsStore.ResolveDefaultPath() is { } path)
            {
                store = new UserSettingsStore(path);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            // The hosts report unavailable settings when they configure the dashboard.
        }

        return () => TestSettings.ResolveTimeout(store?.Get().Tests);
    }

    private static TimeSpan? ParseDuration(string? text, string key, TimeSpan? defaultValue)
    {
        if (text is null)
        {
            return defaultValue;
        }

        return Durations.TryParse(text, out var value)
            ? value
            : throw new InvalidOperationException(
                $"Invalid '{key}' in .piston.json: expected seconds (90), a number with a unit (500ms, 30s, 5m, 1.5h), 0 or \"off\"; got \"{text}\".");
    }

    public static bool DotnetSdkAvailable()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("dotnet", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            _ = (p?.WaitForExit(3_000));
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
