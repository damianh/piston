// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Piston.Engine.Services;
using Xunit;

namespace Piston.Engine.Tests.Services;

public sealed class MtpNodeCaptureTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task CaptureFrameworkPayloads_WhenOptedIn()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("PISTON_CAPTURE_MTP_NODES"), "1", StringComparison.Ordinal))
        {
            TestContext.Current.TestOutputHelper?.WriteLine("Set PISTON_CAPTURE_MTP_NODES=1 to capture MTP node payloads.");
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var probeRoot = Path.Combine(repositoryRoot, "tests", "fixtures", "mtp-probe");
        await BuildProbeAsync(probeRoot);

        foreach (var (framework, projectName) in new[]
        {
            ("mstest", "MSTestProbe"),
            ("nunit", "NUnitProbe"),
            ("tunit", "TUnitProbe"),
            ("xunit-v3", "XUnitV3Probe"),
        })
        {
            var appPath = Path.Combine(probeRoot, projectName, "bin", "Release", "net10.0", $"{projectName}.dll");
            var captures = new Dictionary<string, IReadOnlyList<JsonElement>>(StringComparer.Ordinal);
            await using var warmHosts = new MtpWarmHostPool();
            foreach (var (name, method) in new[]
            {
                ("discover", MtpServerSession.DiscoverMethod),
                ("run", MtpServerSession.RunMethod),
                ("cold-rerun", MtpServerSession.RunMethod),
                ("warm-run", MtpServerSession.RunMethod),
                ("warm-rerun", MtpServerSession.RunMethod),
            })
            {
                var nodes = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                var result = await MtpServerSession.RunAsync(
                    appPath,
                    probeRoot,
                    method,
                    tests: null,
                    updates =>
                    {
                        foreach (var node in updates)
                        {
                            var key = StringProperty(node, "uid")
                                ?? $"{StringProperty(node, "location.type")}.{StringProperty(node, "location.method")}";
                            nodes[key] = node.Clone();
                        }
                    },
                    _ct,
                    warmHosts: name.StartsWith("warm-", StringComparison.Ordinal) ? warmHosts : null);
                Assert.Equal(MtpServerSession.Outcome.Completed, result.Outcome);
                Assert.Null(result.Error);
                captures.Add(name, nodes.Values.ToList());
            }

            var fixtureDirectory = Path.Combine(repositoryRoot, "tests", "Piston.Engine.Tests", "Services", "MtpNodes");
            _ = Directory.CreateDirectory(fixtureDirectory);
            await WriteCaptureAsync(Path.Combine(fixtureDirectory, $"{framework}.json"), framework, captures, probeRoot);

            if (framework == "mstest")
            {
                var coverage = await CaptureCoverageServerModeAsync(appPath, probeRoot, _ct);
                coverage["dotnetTestFallback"] = await CaptureCoverageFallbackAsync(
                    Path.Combine(probeRoot, "MSTestProbe", "MSTestProbe.csproj"),
                    probeRoot,
                    _ct);
                await File.WriteAllTextAsync(
                    Path.Combine(fixtureDirectory, "coverage-server-mode.json"),
                    coverage.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                    _ct);
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Piston.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not find Piston.slnx above the test output directory.");
    }

    private static Task BuildProbeAsync(string probeRoot) =>
        TestProcess.RunDotnetAsync(
            "build MtpProbe.slnx --configuration Release --verbosity quiet",
            probeRoot);

    private static async Task WriteCaptureAsync(
        string path,
        string framework,
        IReadOnlyDictionary<string, IReadOnlyList<JsonElement>> captures,
        string probeRoot)
    {
        var root = new JsonObject
        {
            ["framework"] = framework,
            ["captures"] = new JsonObject(),
        };
        var captureObject = (JsonObject)root["captures"]!;
        foreach (var (name, nodes) in captures)
        {
            var array = new JsonArray();
            foreach (var node in nodes
                .OrderBy(n => StringProperty(n, "uid"), StringComparer.Ordinal)
                .ThenBy(n => StringProperty(n, "display-name"), StringComparer.Ordinal))
            {
                var normalized = JsonNode.Parse(node.GetRawText());
                Normalize(normalized, probeRoot);
                array.Add(normalized);
            }

            captureObject[name] = array;
        }

        await File.WriteAllTextAsync(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task<JsonObject> CaptureCoverageServerModeAsync(
        string appPath,
        string workingDirectory,
        CancellationToken ct)
    {
        var resultsDirectory = Path.Combine(Path.GetTempPath(), $"piston-mtp-coverage-{Guid.NewGuid():N}");
        _ = Directory.CreateDirectory(resultsDirectory);
        var nodes = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var stdout = new List<string>();
        var stderr = new List<string>();
        var outcome = "Failed";
        string? error = null;
        var reportCount = 0;
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var startInfo = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in new[]
            {
                "exec", appPath,
                "--server", "--client-host", "127.0.0.1", "--client-port", port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--coverage", "--coverage-output-format", "cobertura", "--results-directory", resultsDirectory,
            })
            {
                startInfo.ArgumentList.Add(argument);
            }

            _ = ChildProcessEnvironment.Prepare(startInfo);
            using var ownedProcess = new OwnedTestProcess(startInfo);
            var process = ownedProcess.Process;
            process.OutputDataReceived += (_, args) =>
            {
                if (args.Data is not null)
                {
                    lock (stdout)
                    {
                        stdout.Add(args.Data);
                    }
                }
            };
            process.ErrorDataReceived += (_, args) =>
            {
                if (args.Data is not null)
                {
                    lock (stderr)
                    {
                        stderr.Add(args.Data);
                    }
                }
            };
            ownedProcess.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            var accept = listener.AcceptTcpClientAsync(timeout.Token).AsTask();
            var exited = process.WaitForExitAsync();
            if (await Task.WhenAny(accept, exited).ConfigureAwait(false) == exited)
            {
                await exited.ConfigureAwait(false);
                throw new InvalidOperationException(
                    $"Coverage-enabled server-mode host exited before connecting with code {process.ExitCode}: {string.Join(Environment.NewLine, stderr)}");
            }

            using var client = await accept.ConfigureAwait(false);
            using var connection = new MtpServerSession.Connection(client.GetStream());
            var initialized = await connection.InitializeAsync(TimeSpan.FromSeconds(30), timeout.Token).ConfigureAwait(false);
            if (initialized.Outcome != MtpServerSession.Outcome.Completed)
            {
                outcome = initialized.Outcome.ToString();
                error = initialized.Error;
            }
            else
            {
                var request = await connection.RequestAsync(
                    MtpServerSession.RunMethod,
                    tests: null,
                    updates =>
                    {
                        foreach (var node in updates)
                        {
                            var key = StringProperty(node, "uid")
                                ?? $"{StringProperty(node, "location.type")}.{StringProperty(node, "location.method")}";
                            nodes[key] = node.Clone();
                        }
                    },
                    MtpServerSession.StageTimeouts.Default,
                    timeout.Token).ConfigureAwait(false);
                outcome = timeout.IsCancellationRequested ? "TimedOut" : request.Outcome.ToString();
                error = timeout.IsCancellationRequested
                    ? "MTP server did not respond while collecting coverage before the probe timeout."
                    : request.Error;
                if (request.Outcome == MtpServerSession.Outcome.Completed)
                {
                    await connection.TrySendExitAsync(timeout.Token).ConfigureAwait(false);
                }
            }

            await MtpServerSession.WaitForExitOrKillAsync(ownedProcess, TimeSpan.FromSeconds(30), null).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            outcome = "TimedOut";
            error = "Coverage-enabled server mode did not complete within the probe timeout.";
        }
        catch (InvalidOperationException ex)
        {
            outcome = "HostExited";
            error = ex.Message;
        }
        finally
        {
            if (Directory.Exists(resultsDirectory))
            {
                reportCount = Directory.GetFiles(resultsDirectory, "*.cobertura.xml", SearchOption.AllDirectories).Length;
                Directory.Delete(resultsDirectory, recursive: true);
            }
        }

        return new JsonObject
        {
            ["outcome"] = outcome,
            ["error"] = error,
            ["nodeCount"] = nodes.Count,
            ["coverageReportCount"] = reportCount,
            ["hostOutputLineCount"] = stdout.Count,
            ["serverConnectMessageCount"] = stdout.Count(line => line.StartsWith("Connecting to client host", StringComparison.Ordinal)),
            ["nodes"] = new JsonArray(nodes.Values
                .OrderBy(node => StringProperty(node, "uid"), StringComparer.Ordinal)
                .Select(node => JsonNode.Parse(node.GetRawText()))
                .ToArray()),
        };
    }

    private static async Task<JsonObject> CaptureCoverageFallbackAsync(
        string projectPath,
        string workingDirectory,
        CancellationToken ct)
    {
        var resultsDirectory = Path.Combine(Path.GetTempPath(), $"piston-mtp-coverage-fallback-{Guid.NewGuid():N}");
        _ = Directory.CreateDirectory(resultsDirectory);
        try
        {
            var startInfo = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in new[]
            {
                "test", "--project", projectPath, "--configuration", "Release", "--no-build", "--output", "Detailed", "--no-ansi",
                "--coverage", "--coverage-output-format", "cobertura", "--results-directory", resultsDirectory,
            })
            {
                startInfo.ArgumentList.Add(argument);
            }

            _ = ChildProcessEnvironment.Prepare(startInfo);
            startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
            using var ownedProcess = new OwnedTestProcess(startInfo);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            ownedProcess.Start();
            var process = ownedProcess.Process;
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            string output;
            string error;
            try
            {
                await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(timeout.Token)).ConfigureAwait(false);
                output = await stdout.ConfigureAwait(false);
                error = await stderr.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException("The coverage fallback did not complete within the two-minute probe timeout.");
            }
            finally
            {
                await RedirectedProcessDrain.KillAndWaitAsync(
                    ownedProcess,
                    CancellationToken.None,
                    message => TestContext.Current.TestOutputHelper?.WriteLine(message)).ConfigureAwait(false);
            }

            var summary = Regex.Match(
                output,
                @"total:\s*(\d+)\s+failed:\s*(\d+)\s+succeeded:\s*(\d+)\s+skipped:\s*(\d+)",
                RegexOptions.CultureInvariant);
            if (!summary.Success)
            {
                throw new InvalidOperationException(
                    $"The coverage fallback did not emit a test summary (exit {process.ExitCode}).{Environment.NewLine}{output}{Environment.NewLine}{error}");
            }

            return new JsonObject
            {
                ["exitCode"] = process.ExitCode,
                ["coverageReportCount"] = Directory.GetFiles(resultsDirectory, "*.cobertura.xml", SearchOption.AllDirectories).Length,
                ["total"] = int.Parse(summary.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
                ["failed"] = int.Parse(summary.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture),
                ["passed"] = int.Parse(summary.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture),
                ["skipped"] = int.Parse(summary.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture),
            };
        }
        finally
        {
            if (Directory.Exists(resultsDirectory))
            {
                Directory.Delete(resultsDirectory, recursive: true);
            }
        }
    }

    private static void Normalize(JsonNode? node, string probeRoot)
    {
        if (node is JsonObject obj)
        {
            foreach (var (key, value) in obj.ToList())
            {
                if (key == "time.duration-ms")
                {
                    obj[key] = 0;
                }
                else
                {
                    Normalize(value, probeRoot);
                }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var value in array)
            {
                Normalize(value, probeRoot);
            }
        }
        else if (node is JsonValue valueNode
            && valueNode.TryGetValue<string>(out var value)
            && value.Contains(probeRoot, StringComparison.Ordinal))
        {
            valueNode.ReplaceWith(JsonValue.Create(value.Replace(probeRoot, "$PROBE", StringComparison.Ordinal)));
        }
    }

    private static string? StringProperty(JsonElement node, string name) =>
        node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
