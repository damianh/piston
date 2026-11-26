// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Text.Json;
using Piston.Engine.Models;
using Piston.Engine.Services;
using Xunit;

namespace Piston.Engine.Tests.Integration;

/// <summary>
/// Feasibility prototype for associating instrumented line hits with individual xUnit v3 tests
/// inside one MTP test process. The fixture injects probes into two known source statements;
/// it does not yet instrument arbitrary assemblies.
/// </summary>
public sealed class InProcessCoveragePrototypeTests : IAsyncLifetime
{
    private const string TargetSource = """
        namespace CoveragePrototype;

        internal static class TargetCode
        {
            public static int First()
            {
                return 11;
            }

            public static int Second()
            {
                return 22;
            }
        }
        """;

    private string _root = string.Empty;
    private bool _restored;
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private string ProjectPath => Path.Combine(_root, "CoveragePrototype.csproj");
    private string AppPath => Path.Combine(_root, "bin", "Debug", "net10.0", "CoveragePrototype.dll");
    private string ResultsPath => Path.Combine(Path.GetDirectoryName(AppPath)!, "coverage-prototype.jsonl");

    public async ValueTask InitializeAsync()
    {
        _root = Directory.CreateTempSubdirectory("piston-coverage-prototype-").FullName;
        await File.WriteAllTextAsync(Path.Combine(_root, "global.json"), """
            {
              "test": {
                "runner": "Microsoft.Testing.Platform"
              }
            }
            """, _ct);
        await File.WriteAllTextAsync(ProjectPath, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <OutputType>Exe</OutputType>
                <IsTestingPlatformApplication>true</IsTestingPlatformApplication>
                <NoWarn>$(NoWarn);NU1507</NoWarn>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="xunit.v3.core.mtp-v2" Version="4.0.3" />
                <PackageReference Include="xunit.v3.assert" Version="4.0.3" />
              </ItemGroup>
            </Project>
            """, _ct);
        await File.WriteAllTextAsync(Path.Combine(_root, "Tests.cs"), TestSource, _ct);

        var targetPath = Path.Combine(_root, "Targets.cs");
        await File.WriteAllTextAsync(targetPath, TargetSource, _ct);
        await InstrumentReturnAsync(targetPath, "return 11;", _ct);
        await InstrumentReturnAsync(targetPath, "return 22;", _ct);

        var restore = await TestProcess.RunDotnetResultAsync("restore", _root);
        _restored = restore.ExitCode == 0;
        if (!_restored)
        {
            return;
        }

        _ = await TestProcess.RunDotnetAsync("build --configuration Debug --no-restore", _root);
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task RunAsync_CapturesInstrumentedLinesPerTestInOneConcurrentProcess()
    {
        Assert.SkipUnless(_restored, "MTP fixture packages could not be restored.");
        File.Delete(ResultsPath);

        var result = await MtpTestProcessRunner.RunAsync(
            ProjectPath, AppPath, _root, filter: null, onProgress: null, _ct);

        Assert.Null(result.RunnerError);
        var tests = result.Suites.SelectMany(suite => suite.Tests).ToList();
        Assert.Equal(2, tests.Count);
        Assert.All(tests, test => Assert.Equal(TestStatus.Passed, test.Status));

        var rows = new List<JsonElement>();
        foreach (var line in File.ReadAllLines(ResultsPath))
        {
            using var document = JsonDocument.Parse(line);
            rows.Add(document.RootElement.Clone());
        }
        Assert.Equal(2, rows.Count);

        var first = Assert.Single(rows, row => row.GetProperty("TestName").GetString() == "FirstTest");
        var second = Assert.Single(rows, row => row.GetProperty("TestName").GetString() == "SecondTest");
        Assert.Equal(
            first.GetProperty("ProcessId").GetInt32(),
            second.GetProperty("ProcessId").GetInt32());
        Assert.True(
            first.GetProperty("StartedAt").GetInt64() < second.GetProperty("FinishedAt").GetInt64()
            && second.GetProperty("StartedAt").GetInt64() < first.GetProperty("FinishedAt").GetInt64(),
            "The fixture tests should overlap to check attribution under test parallelism.");

        var firstLines = ReadHitLines(first);
        var secondLines = ReadHitLines(second);
        var firstSourceLine = FindLine("return 11;");
        var secondSourceLine = FindLine("return 22;");
        Assert.Contains(firstSourceLine, firstLines);
        Assert.DoesNotContain(secondSourceLine, firstLines);
        Assert.Contains(secondSourceLine, secondLines);
        Assert.DoesNotContain(firstSourceLine, secondLines);
    }

    private static HashSet<int> ReadHitLines(JsonElement row) =>
        row.GetProperty("Lines").EnumerateArray()
            .Select(hit => hit.GetProperty("LineNumber").GetInt32())
            .ToHashSet();

    private static int FindLine(string statement)
    {
        var lines = TargetSource.Split('\n');
        return Array.FindIndex(lines, line => line.Trim() == statement) + 1;
    }

    private static async Task InstrumentReturnAsync(string path, string statement, CancellationToken ct)
    {
        var source = await File.ReadAllTextAsync(path, ct);
        var lines = source.Split('\n');
        var lineIndex = Array.FindIndex(lines, line => line.Trim() == statement);
        if (lineIndex < 0)
        {
            throw new InvalidOperationException($"Could not find prototype statement '{statement}'.");
        }

        var indentation = lines[lineIndex][..(lines[lineIndex].Length - lines[lineIndex].TrimStart().Length)];
        var sourceLine = lineIndex + 1;
        lines[lineIndex] = $"{indentation}CoverageProbe.Hit(\"Targets.cs\", {sourceLine}); {statement}";
        await File.WriteAllTextAsync(path, string.Join('\n', lines), ct);
    }

    private static readonly string TestSource = """
        using System;
        using System.Collections.Concurrent;
        using System.Diagnostics;
        using System.IO;
        using System.Linq;
        using System.Reflection;
        using System.Text.Json;
        using System.Threading;
        using System.Threading.Tasks;
        using Xunit;
        using Xunit.v3;

        [assembly: Parallelization(MaxThreads = 4)]

        namespace CoveragePrototype;

        internal sealed record Hit(string FilePath, int LineNumber);

        internal sealed class Recording(string testName)
        {
            public string TestName { get; } = testName;
            public int ProcessId { get; } = Environment.ProcessId;
            public long StartedAt { get; } = Stopwatch.GetTimestamp();
            public ConcurrentDictionary<Hit, byte> Lines { get; } = new();
        }

        internal static class CoverageProbe
        {
            private static readonly AsyncLocal<Recording?> Current = new();
            private static readonly object OutputLock = new();

            public static void Begin(string testName) => Current.Value = new Recording(testName);

            public static void Hit(string filePath, int lineNumber)
            {
                var recording = Current.Value
                    ?? throw new InvalidOperationException("A coverage hit occurred outside a test scope.");
                _ = recording.Lines.TryAdd(new Hit(filePath, lineNumber), 0);
            }

            public static void Complete()
            {
                var recording = Current.Value
                    ?? throw new InvalidOperationException("A test completed without a coverage scope.");
                var row = new
                {
                    recording.TestName,
                    recording.ProcessId,
                    recording.StartedAt,
                    FinishedAt = Stopwatch.GetTimestamp(),
                    Lines = recording.Lines.Keys.OrderBy(hit => hit.FilePath).ThenBy(hit => hit.LineNumber),
                };
                lock (OutputLock)
                {
                    File.AppendAllText(
                        Path.Combine(AppContext.BaseDirectory, "coverage-prototype.jsonl"),
                        JsonSerializer.Serialize(row) + Environment.NewLine);
                }

                Current.Value = null;
            }
        }

        public sealed class CoverageScopeAttribute : BeforeAfterTestAttribute
        {
            public override void Before(MethodInfo methodUnderTest, IXunitTest test) =>
                CoverageProbe.Begin(methodUnderTest.Name);

            public override void After(MethodInfo methodUnderTest, IXunitTest test) =>
                CoverageProbe.Complete();
        }

        internal static class ParallelGate
        {
            private static int _arrivals;
            private static readonly TaskCompletionSource BothStarted =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public static async Task WaitAsync()
            {
                if (Interlocked.Increment(ref _arrivals) == 2)
                {
                    _ = BothStarted.TrySetResult();
                }

                await BothStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
            }
        }

        [Collection("first")]
        [CoverageScope]
        public sealed class FirstTests
        {
            [Fact]
            public async Task FirstTest()
            {
                await ParallelGate.WaitAsync();
                Assert.Equal(11, TargetCode.First());
            }
        }

        [Collection("second")]
        [CoverageScope]
        public sealed class SecondTests
        {
            [Fact]
            public async Task SecondTest()
            {
                await ParallelGate.WaitAsync();
                Assert.Equal(22, TargetCode.Second());
            }
        }
        """;
}
