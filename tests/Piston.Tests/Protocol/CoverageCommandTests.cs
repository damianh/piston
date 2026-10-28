// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Piston.Engine.Coverage;
using Piston.Hosting.Mapping;
using Piston.Hosting.Protocol;
using Piston.Mcp.Tools;
using Piston.Protocol.Dtos;
using Piston.Protocol.JsonRpc;
using Piston.Protocol.Messages;
using Piston.Tests.Mcp;
using Xunit;

namespace Piston.Tests.Protocol;

public sealed class CoverageCommandTests : IDisposable
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _root = Directory.CreateTempSubdirectory("piston-coverage-cmd-").FullName;

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_root, recursive: true);
    }

    private static CoverageFileSummary Summary(string path, int total, int covered, bool stale = false) =>
        new(path, "Lib", total, covered, 4, 3, stale, IsPartial: false, "hash", 3, DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task GetSummary_WithCoverageDisabled_ReportsDisabled()
    {
        var dispatcher = new EngineCommandDispatcher(new StubEngine());

        var result = await dispatcher.HandleCommandAsync(ProtocolMethods.CoverageGetSummary, null, _ct);

        Assert.False(result!["enabled"]!.GetValue<bool>());
        Assert.Empty(result["files"]!.AsArray());
    }

    [Fact]
    public async Task GetSummary_ReturnsTotalsAndSolutionRelativePaths()
    {
        var engine = new StubEngine();
        engine.State.SolutionPath = Path.Combine(_root, "App.slnx");
        engine.CoverageReport = new CoverageReportSnapshot([
            Summary(Path.Combine(_root, "src", "A.cs"), 10, 5),
            Summary(Path.Combine(_root, "src", "B.cs"), 4, 4, stale: true),
        ]);
        var dispatcher = new EngineCommandDispatcher(engine);

        var result = await dispatcher.HandleCommandAsync(ProtocolMethods.CoverageGetSummary, null, _ct);

        Assert.True(result!["enabled"]!.GetValue<bool>());
        Assert.Equal(14, result["linesTotal"]!.GetValue<int>());
        Assert.Equal(9, result["linesCovered"]!.GetValue<int>());
        Assert.Equal(6, result["branchesCovered"]!.GetValue<int>());
        var files = result["files"]!.AsArray();
        Assert.Equal("src/A.cs", files[0]!["relativePath"]!.GetValue<string>());
        Assert.True(files[1]!["isStale"]!.GetValue<bool>());
    }

    [Fact]
    public async Task GetForFile_ReturnsLineStatusesAndRequestedSource()
    {
        var path = Path.Combine(_root, "A.cs");
        await File.WriteAllLinesAsync(path, ["if (x)", "  y();", "z();"], _ct);
        var engine = new StubEngine();
        engine.State.SolutionPath = Path.Combine(_root, "App.slnx");
        engine.FileCoverage["A.cs"] = new FileCoverageSnapshot(
            Summary(path, 3, 2),
            [new LineCoverage(1, 2, 1, 2), new LineCoverage(2, 2), new LineCoverage(3, 0)]);
        var dispatcher = new EngineCommandDispatcher(engine);

        var result = await dispatcher.HandleCommandAsync(
            ProtocolMethods.CoverageGetForFile, JsonNode.Parse("""{"filePath":"A.cs","includeSource":true}"""), _ct);

        Assert.Equal(path, result!["filePath"]!.GetValue<string>());
        var statuses = result["lines"]!.AsArray().Select(l => l!["status"]!.GetValue<string>());
        Assert.Equal(["partial", "covered", "uncovered"], statuses);
        Assert.Equal("A.cs", result["summary"]!["relativePath"]!.GetValue<string>());
        Assert.Equal("  y();", result["source"]![1]!.GetValue<string>());
    }

    [Fact]
    public async Task GetForFile_StaleCoverageForExternalSymlink_DoesNotReturnSource()
    {
        var solution = Directory.CreateDirectory(Path.Combine(_root, "repo")).FullName;
        var path = Path.Combine(solution, "A.cs");
        var secret = Path.Combine(_root, "secret.txt");
        await File.WriteAllTextAsync(path, "class A {}", _ct);
        await File.WriteAllTextAsync(secret, "secret", _ct);
        var engine = new StubEngine();
        engine.State.SolutionPath = Path.Combine(solution, "App.slnx");
        engine.FileCoverage["A.cs"] = new FileCoverageSnapshot(
            Summary(path, 1, 1, stale: true), [new LineCoverage(1, 1)]);
        File.Delete(path);
        try
        {
            _ = File.CreateSymbolicLink(path, secret);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("Creating symbolic links is not permitted here.");
        }

        var result = await new EngineCommandDispatcher(engine).HandleCommandAsync(
            ProtocolMethods.CoverageGetForFile,
            new JsonObject { ["filePath"] = "A.cs", ["includeSource"] = true }, _ct);

        Assert.NotNull(result!["summary"]);
        _ = Assert.Single(result["lines"]!.AsArray());
        Assert.Null(result["source"]);
    }

    [Theory]
    [InlineData("binary")]
    [InlineData("oversized")]
    [InlineData("missing")]
    public async Task GetForFile_UnavailableSource_PreservesCoverageWithoutSource(string kind)
    {
        var path = Path.Combine(_root, "A.cs");
        if (kind == "binary")
        {
            await File.WriteAllBytesAsync(path, [0x4D, 0x5A, 0x00, 0x01], _ct);
        }
        else if (kind == "oversized")
        {
            await File.WriteAllTextAsync(path, new string('x', 1024 * 1024 + 1), _ct);
        }

        var engine = new StubEngine();
        engine.State.SolutionPath = Path.Combine(_root, "App.slnx");
        engine.FileCoverage["A.cs"] = new FileCoverageSnapshot(Summary(path, 1, 1), [new LineCoverage(1, 1)]);

        var result = await new EngineCommandDispatcher(engine).HandleCommandAsync(
            ProtocolMethods.CoverageGetForFile,
            new JsonObject { ["filePath"] = "A.cs", ["includeSource"] = true }, _ct);

        Assert.NotNull(result!["summary"]);
        _ = Assert.Single(result["lines"]!.AsArray());
        Assert.Null(result["source"]);
    }

    [Fact]
    public async Task GetForFile_UnmeasuredFile_ReturnsNoLinesSummaryOrSource()
    {
        var dispatcher = new EngineCommandDispatcher(new StubEngine());

        var result = await dispatcher.HandleCommandAsync(
            ProtocolMethods.CoverageGetForFile, JsonNode.Parse("""{"filePath":"/etc/passwd","includeSource":true}"""), _ct);

        Assert.Empty(result!["lines"]!.AsArray());
        Assert.Null(result["summary"]);
        Assert.Null(result["source"]);
    }

    [Fact]
    public async Task GetCoveringTests_ReturnsEachTestWithItsGranularityAndLines()
    {
        var engine = new StubEngine();
        engine.CoveringTests["A.cs"] =
        [
            new CoveringTest("T.A.One", "/t/T.csproj", "class", [new CoveredLineRange(3, 5)], IsCurrent: true),
            new CoveringTest("T.B.One", "/t/T.csproj", "run", [], IsCurrent: false),
        ];
        var dispatcher = new EngineCommandDispatcher(engine);

        var result = await dispatcher.HandleCommandAsync(
            ProtocolMethods.CoverageGetCoveringTests, JsonNode.Parse("""{"filePath":"A.cs"}"""), _ct);

        var tests = result!["tests"]!.AsArray();
        Assert.Equal(["T.A.One", "T.B.One"], tests.Select(t => t!["testFqn"]!.GetValue<string>()));
        Assert.Equal("class", tests[0]!["granularity"]!.GetValue<string>());
        Assert.Equal(5, tests[0]!["executedLines"]![0]!["end"]!.GetValue<int>());
        Assert.False(tests[1]!["isCurrent"]!.GetValue<bool>());
    }

    [Fact]
    public async Task GetCoveringTests_NoPathOrNoRecording_ReturnsNoTests()
    {
        var dispatcher = new EngineCommandDispatcher(new StubEngine());

        var missing = await dispatcher.HandleCommandAsync(
            ProtocolMethods.CoverageGetCoveringTests, JsonNode.Parse("""{"filePath":"B.cs"}"""), _ct);
        var empty = await dispatcher.HandleCommandAsync(ProtocolMethods.CoverageGetCoveringTests, null, _ct);

        Assert.Empty(missing!["tests"]!.AsArray());
        Assert.Empty(empty!["tests"]!.AsArray());
    }

    [Fact]
    public async Task GetChangedLines_WithCoverageDisabled_ReportsDisabled()
    {
        var dispatcher = new EngineCommandDispatcher(new StubEngine());

        var result = await dispatcher.HandleCommandAsync(ProtocolMethods.CoverageGetChangedLines, null, _ct);

        Assert.False(result!["enabled"]!.GetValue<bool>());
        Assert.Equal(0, result["totals"]!["changedLines"]!.GetValue<int>());
        Assert.Empty(result["files"]!.AsArray());
    }

    [Fact]
    public async Task GetChangedLines_RequestedWhileARunHoldsTheCoverageLock_IsCancellable()
    {
        Git("init", "-q", "-b", "main");
        var path = Path.Combine(_root, "A.cs");
        await File.WriteAllLinesAsync(path, ["a"], _ct);
        Git("add", ".");
        Git("-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "init");
        await File.WriteAllLinesAsync(path, ["A"], _ct);

        var engine = new StubEngine { CoverageReport = new CoverageReportSnapshot([]) };
        engine.State.SolutionPath = Path.Combine(_root, "App.slnx");
        using var release = new ManualResetEventSlim();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        engine.OnGetCoverageReport = () =>
        {
            CoverageToolsTests.CancelAndBlock(cts, release);
            return engine.CoverageReport;
        };
        engine.OnGetFileCoverage = _ => CoverageToolsTests.CancelAndBlock(cts, release);
        var dispatcher = new EngineCommandDispatcher(engine);
        try
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatcher.HandleCommandAsync(
                ProtocolMethods.CoverageGetChangedLines, JsonNode.Parse("""{"base":"working"}"""), cts.Token));

            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"Took {stopwatch.Elapsed}.");
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task GetChangedLines_OptionLikeBase_IsInvalidParams()
    {
        var engine = new StubEngine { CoverageReport = new CoverageReportSnapshot([]) };
        engine.State.SolutionPath = Path.Combine(_root, "App.slnx");
        var dispatcher = new EngineCommandDispatcher(engine);

        var ex = await Assert.ThrowsAsync<JsonRpcException>(() => dispatcher.HandleCommandAsync(
            ProtocolMethods.CoverageGetChangedLines, JsonNode.Parse("""{"base":"--output=/tmp/x"}"""), _ct));

        Assert.Equal(JsonRpcErrorCodes.InvalidParams, ex.Code);
    }

    [Fact]
    public async Task GetChangedLines_ReturnsLineRangesPerVerdict()
    {
        Git("init", "-q", "-b", "main");
        var path = Path.Combine(_root, "src", "A.cs");
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllLinesAsync(path, ["a", "b", "c", "d"], _ct);
        Git("add", ".");
        Git("-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "init");
        await File.WriteAllLinesAsync(path, ["A", "B", "C", "D"], _ct);

        var engine = new StubEngine { CoverageReport = new CoverageReportSnapshot([]) };
        engine.State.SolutionPath = Path.Combine(_root, "App.slnx");
        engine.FileCoverage[path] = new FileCoverageSnapshot(Summary(path, 3, 2), [new(1, 2, 1, 2), new(2, 1), new(3, 0)]);
        engine.CoveringTests[path] = [new CoveringTest("T.Fail", "/t/T.csproj", "test", [new CoveredLineRange(1, 2)], IsCurrent: true)];
        engine.State.TestSuites =
        [
            new Engine.Models.TestSuite(
                "T",
                [new Engine.Models.TestResult("T.Fail", "Fail", Engine.Models.TestStatus.Failed, TimeSpan.Zero, null, null, null, null)],
                DateTimeOffset.UnixEpoch,
                TimeSpan.Zero),
        ];
        var dispatcher = new EngineCommandDispatcher(engine);

        var node = await dispatcher.HandleCommandAsync(
            ProtocolMethods.CoverageGetChangedLines, JsonNode.Parse("""{"base":"working"}"""), _ct);
        var result = node.Deserialize<ChangedLineCoverageDto>(JsonRpcSerializer.Options)!;

        Assert.True(result.Enabled);
        Assert.Equal(new ChangedLineTotalsDto(4, 0, 2, 1, 1, 0), result.Totals);
        Assert.Equal(0, result.UnknownReasons["stale"]);
        Assert.Equal(0, result.FilesOmitted);
        var file = Assert.Single(result.Files);
        Assert.Equal("src/A.cs", file.RelativePath);
        Assert.False(file.DetailOmitted);
        Assert.Empty(file.CoveredByPassing);
        Assert.Equal([new LineRangeDto(1, 2)], file.CoveredOnlyByFailing);
        Assert.Equal([new LineRangeDto(3, 3)], file.Uncovered);
        Assert.Equal([new LineRangeDto(4, 4)], file.NonExecutable);
        Assert.Empty(file.Unknown);
        Assert.Equal([new ChangedLineBranchesDto(1, 1, 2)], file.PartialBranches);
        Assert.Equal(["T.Fail"], file.FailingTests);
        Assert.Equal(0, file.FailingTestsOmitted);
    }

    [Fact]
    public void ChangedLines_ManyLinesAndRepeatedFailingTests_FitInOneMessageWithExactTotals()
    {
        const int Files = 20, LinesPerFile = 5_000, FailingTests = 50;
        var failing = Enumerable.Range(0, FailingTests)
            .Select(i => new CoveringTest($"T.{new string('x', 300)}.Fail{i:D3}", "/t/T.csproj", "test", [new CoveredLineRange(1, LinesPerFile)], IsCurrent: true))
            .ToList();
        // Odd lines ran under failing tests and even lines did not, so no two adjacent lines share a verdict.
        var lineCoverage = Enumerable.Range(1, LinesPerFile).Select(n => new LineCoverage(n, n % 2)).ToList();
        var paths = Enumerable.Range(0, Files).Select(i => $"/repo/src/F{i:D2}.cs").ToList();
        var changes = new ChangedLinesResult(
            "working tree",
            paths.ToDictionary(p => p, _ => new SortedSet<int>(Enumerable.Range(1, LinesPerFile))));

        var dto = ChangedLineVerdicts.Classify(
                changes,
                p => new FileCoverageSnapshot(Summary(p, LinesPerFile, LinesPerFile / 2), lineCoverage),
                _ => failing,
                _ => CoveringTestOutcome.Failed)
            .ToDto("/repo");

        var size = JsonRpcSerializer.Serialize(new JsonRpcResponse("1", JsonSerializer.SerializeToNode(dto, JsonRpcSerializer.Options))).Length;
        TestContext.Current.TestOutputHelper?.WriteLine($"Serialized reply: {size} bytes");
        Assert.True(size < MessageFramer.DefaultMaxMessageSize, $"{size} bytes");
        Assert.Equal(new ChangedLineTotalsDto(Files * LinesPerFile, 0, Files * LinesPerFile / 2, Files * LinesPerFile / 2, 0, 0), dto.Totals);
        Assert.Equal(Files, dto.Files.Count + dto.FilesOmitted);
        Assert.Contains(dto.Files, f => f.DetailOmitted);
        Assert.Contains(dto.Warnings, w => w.Contains("too large", StringComparison.Ordinal));

        var first = dto.Files[0];
        Assert.False(first.DetailOmitted);
        Assert.Equal(LinesPerFile / 2, first.CoveredOnlyByFailing.Count);
        Assert.Equal(CoverageTools.MaxFailingTestsPerFile, first.FailingTests.Count);
        Assert.Equal(FailingTests - CoverageTools.MaxFailingTestsPerFile, first.FailingTestsOmitted);
        Assert.All(dto.Files, f => Assert.Equal(LinesPerFile, f.Totals.ChangedLines));
    }

    [Fact]
    public void ChangedLines_OverBudget_KeepsTotalsThenOmitsFiles()
    {
        var paths = Enumerable.Range(0, 3).Select(i => $"/repo/src/F{i}.cs").ToList();
        var changes = new ChangedLinesResult(
            "working tree", paths.ToDictionary(p => p, _ => new SortedSet<int>([1, 3, 5, 7, 9])));
        var result = ChangedLineVerdicts.Classify(changes, _ => null, _ => null, _ => CoveringTestOutcome.NoResult);
        var full = result.ToDto("/repo");
        var detailSize = JsonSerializer.SerializeToUtf8Bytes(full.Files[0], JsonRpcSerializer.Options).Length + 1;
        var summarySize = JsonSerializer.SerializeToUtf8Bytes(
            full.Files[1] with { Unknown = [], FailingTests = [], DetailOmitted = true }, JsonRpcSerializer.Options).Length + 1;

        var dto = result.ToDto("/repo", budget: detailSize + summarySize);

        Assert.Equal(new ChangedLineTotalsDto(15, 0, 0, 0, 0, 15), dto.Totals);
        Assert.Equal(15, dto.UnknownReasons["notMeasured"]);
        Assert.Equal(2, dto.Files.Count);
        Assert.Equal(1, dto.FilesOmitted);
        Assert.False(dto.Files[0].DetailOmitted);
        Assert.Equal("notMeasured", Assert.Single(dto.Files[0].Unknown).Reason);
        Assert.True(dto.Files[1].DetailOmitted);
        Assert.Empty(dto.Files[1].Unknown);
        Assert.Equal(5, dto.Files[1].Totals.Unknown);
        Assert.Contains(dto.Warnings, w => w.Contains("1 file(s) give only totals and 1 file(s) are omitted", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Export_ReturnsLcovContent()
    {
        var engine = new StubEngine();
        engine.State.SolutionPath = Path.Combine(_root, "App.slnx");
        var path = Path.Combine(_root, "A.cs");
        engine.CoverageReport = new CoverageReportSnapshot([Summary(path, 2, 1)]);
        engine.FileCoverage[path] = new FileCoverageSnapshot(Summary(path, 2, 1), [new(1, 1), new(2, 0)]);
        var dispatcher = new EngineCommandDispatcher(engine);

        var result = await dispatcher.HandleCommandAsync(
            ProtocolMethods.CoverageExport, new JsonObject { ["format"] = "lcov" }, _ct);

        Assert.Equal("lcov", result!["format"]!.GetValue<string>());
        Assert.Equal("lcov.info", result["fileName"]!.GetValue<string>());
        Assert.Equal(1, result["fileCount"]!.GetValue<int>());
        Assert.Contains("DA:2,0", result["content"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_ChunksOneSnapshotAndPreservesUnicodeAtTheBoundary()
    {
        var engine = new StubEngine();
        var path = new string('x', EngineCommandDispatcher.CoverageExportChunkChars - 8) + "\U0001F600.cs";
        var summary = Summary(path, 1, 1);
        engine.CoverageReport = new CoverageReportSnapshot([summary]);
        engine.FileCoverage[path] = new FileCoverageSnapshot(summary, [new(1, 1)]);
        var expected = CoverageExporter.Export(engine, CoverageExportFormat.Lcov)!.Content;
        var dispatcher = new EngineCommandDispatcher(engine);
        var result = await dispatcher.HandleCommandAsync(
            ProtocolMethods.CoverageExport, new JsonObject { ["format"] = "lcov" }, _ct);
        var chunk = result!.Deserialize<CoverageExportDto>(JsonRpcSerializer.Options)!;
        var content = new StringBuilder(chunk.Content);
        _ = Assert.NotNull(chunk.NextOffset);
        Assert.Equal(EngineCommandDispatcher.CoverageExportChunkChars - 1, chunk.Content.Length);
        Assert.False(char.IsHighSurrogate(chunk.Content[^1]));
        Assert.True(JsonRpcSerializer.Serialize(new JsonRpcResponse("1", result)).Length < MessageFramer.DefaultMaxMessageSize);

        var id = chunk.ExportId;
        foreach (var command in new[]
        {
            new ExportCoverageCommand("lcov", id, chunk.NextOffset.Value + 1),
            new ExportCoverageCommand("cobertura", id, chunk.NextOffset.Value)
        })
        {
            var invalid = await Assert.ThrowsAsync<JsonRpcException>(() => dispatcher.HandleCommandAsync(
                ProtocolMethods.CoverageExport, JsonSerializer.SerializeToNode(command, JsonRpcSerializer.Options), _ct));
            Assert.Equal(JsonRpcErrorCodes.InvalidParams, invalid.Code);
        }

        var otherConnection = new EngineCommandDispatcher(engine);
        var unknown = await Assert.ThrowsAsync<JsonRpcException>(() => otherConnection.HandleCommandAsync(
            ProtocolMethods.CoverageExport,
            JsonSerializer.SerializeToNode(new ExportCoverageCommand("lcov", id, chunk.NextOffset.Value), JsonRpcSerializer.Options), _ct));
        Assert.Equal(JsonRpcErrorCodes.InvalidParams, unknown.Code);

        engine.CoverageReport = null;
        while (chunk.NextOffset is { } offset)
        {
            result = await dispatcher.HandleCommandAsync(
                ProtocolMethods.CoverageExport,
                JsonSerializer.SerializeToNode(new ExportCoverageCommand("lcov", id, offset), JsonRpcSerializer.Options), _ct);
            chunk = result!.Deserialize<CoverageExportDto>(JsonRpcSerializer.Options)!;
            Assert.Equal(id, chunk.ExportId);
            Assert.True(JsonRpcSerializer.Serialize(new JsonRpcResponse("1", result)).Length < MessageFramer.DefaultMaxMessageSize);
            _ = content.Append(chunk.Content);
        }

        Assert.Equal(expected, content.ToString());
        var completed = await Assert.ThrowsAsync<JsonRpcException>(() => dispatcher.HandleCommandAsync(
            ProtocolMethods.CoverageExport,
            JsonSerializer.SerializeToNode(new ExportCoverageCommand("lcov", id, 1), JsonRpcSerializer.Options), _ct));
        Assert.Equal(JsonRpcErrorCodes.InvalidParams, completed.Code);
    }

    [Theory]
    [InlineData(null, 1)]
    [InlineData("unknown", 0)]
    public async Task Export_RejectsInvalidContinuation(string? exportId, int offset)
    {
        var dispatcher = new EngineCommandDispatcher(new StubEngine());
        var error = await Assert.ThrowsAsync<JsonRpcException>(() => dispatcher.HandleCommandAsync(
            ProtocolMethods.CoverageExport,
            JsonSerializer.SerializeToNode(new ExportCoverageCommand("lcov", exportId, offset), JsonRpcSerializer.Options), _ct));
        Assert.Equal(JsonRpcErrorCodes.InvalidParams, error.Code);
    }

    [Fact]
    public async Task Export_RejectsUnknownFormatAndDisabledCoverage()
    {
        var dispatcher = new EngineCommandDispatcher(new StubEngine());

        var badFormat = await Assert.ThrowsAsync<JsonRpcException>(() => dispatcher.HandleCommandAsync(
            ProtocolMethods.CoverageExport, new JsonObject { ["format"] = "html" }, _ct));
        var disabled = await Assert.ThrowsAsync<JsonRpcException>(() => dispatcher.HandleCommandAsync(
            ProtocolMethods.CoverageExport, new JsonObject { ["format"] = "cobertura" }, _ct));

        Assert.Equal(JsonRpcErrorCodes.InvalidParams, badFormat.Code);
        Assert.Contains("disabled", disabled.Message, StringComparison.Ordinal);
    }

    private void Git(params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = _root, RedirectStandardError = true };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = System.Diagnostics.Process.Start(psi)!;
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
    }
}
