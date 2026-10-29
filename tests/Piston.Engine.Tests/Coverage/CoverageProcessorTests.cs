// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Coverage;
using Piston.Engine.Impact;
using Xunit;

namespace Piston.Engine.Tests.Coverage;

public sealed class CoverageProcessorTests : IDisposable
{
    private readonly string _tempDir;

    public CoverageProcessorTests() => _tempDir = Directory.CreateTempSubdirectory("piston-coverage-proc-test-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* ignore */ }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private string WriteCoberturaXml(string content)
    {
        var path = Path.Combine(_tempDir, $"{Guid.NewGuid():N}.xml");
        File.WriteAllText(path, content);
        return path;
    }

    private string SimpleXml(string filePath, params int[] hitLines)
    {
        var lineElements = string.Join(
            Environment.NewLine,
            hitLines.Select(l => $"                        <line number=\"{l}\" hits=\"1\" />"));

        return
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
            "<coverage version=\"1.9\" timestamp=\"1700000000\">\n" +
            "  <sources><source>/repo</source></sources>\n" +
            "  <packages>\n" +
            "    <package name=\"Lib\">\n" +
            "      <classes>\n" +
            $"        <class filename=\"{filePath}\">\n" +
            "          <lines>\n" +
            lineElements + "\n" +
            "          </lines>\n" +
            "        </class>\n" +
            "      </classes>\n" +
            "    </package>\n" +
            "  </packages>\n" +
            "</coverage>";
    }

    // ── Tests ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ProcessCoverageAsync_SingleReport_StoresCorrectMappings()
    {
        var store = new StubCoverageStore();
        var processor = new CoverageProcessor(new CoberturaParser());

        var xmlPath = WriteCoberturaXml(SimpleXml("src/Lib/Code.cs", 5, 6, 7));
        var testFqns = new[] { "Lib.Tests.Test1", "Lib.Tests.Test2" };
        var runId = store.CreateRunId();

        await processor.ProcessCoverageAsync(runId, [xmlPath], testFqns, store);

        _ = Assert.Single(store.StoredCoverage);
        var (storedRunId, map) = store.StoredCoverage[0];
        Assert.Equal(runId, storedRunId);

        // Both test FQNs should be in the map
        Assert.True(map.ContainsKey("Lib.Tests.Test1"));
        Assert.True(map.ContainsKey("Lib.Tests.Test2"));

        // Each test should cover lines 5, 6, 7
        var test1Lines = map["Lib.Tests.Test1"].Select(l => l.LineNumber).ToList();
        Assert.Contains(5, test1Lines);
        Assert.Contains(6, test1Lines);
        Assert.Contains(7, test1Lines);
    }

    [Fact]
    public async Task ProcessCoverageAsync_SourceRoot_IgnoresFilesOutsideIt()
    {
        var store = new StubCoverageStore();
        var processor = new CoverageProcessor(new CoberturaParser());
        var inside = WriteCoberturaXml(SimpleXml("src/Lib/Code.cs", 1, 2));
        var outside = WriteCoberturaXml(SimpleXml("/tmp/fixture/Fixture.cs", 3));

        await processor.ProcessCoverageAsync(
            store.CreateRunId(), [inside, outside], ["Lib.Tests.Test1"], store, sourceRoot: "/repo");

        var report = Assert.Single(store.StoredReports);
        var expectedPath = Path.GetFullPath("/repo/src/Lib/Code.cs");
        Assert.Equal([expectedPath], report.Files.Select(f => f.Coverage.FilePath));
        var (_, map) = Assert.Single(store.StoredCoverage);
        Assert.All(map["Lib.Tests.Test1"], line => Assert.Equal(expectedPath, line.FilePath));
    }

    [Fact]
    public async Task ProcessCoverageAsync_MultipleReports_UnionOfCoverage()
    {
        var store = new StubCoverageStore();
        var processor = new CoverageProcessor(new CoberturaParser());

        var xml1 = WriteCoberturaXml(SimpleXml("src/Lib/File1.cs", 1, 2));
        var xml2 = WriteCoberturaXml(SimpleXml("src/Lib/File2.cs", 10, 11));
        var testFqns = new[] { "Lib.Tests.Test1" };
        var runId = store.CreateRunId();

        await processor.ProcessCoverageAsync(runId, [xml1, xml2], testFqns, store);

        _ = Assert.Single(store.StoredCoverage);
        var (_, map) = store.StoredCoverage[0];

        var entries = map["Lib.Tests.Test1"];
        var files = entries.Select(e => e.FilePath).Distinct().ToList();

        // Both files should appear
        Assert.Contains(files, f => f.EndsWith("File1.cs", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(files, f => f.EndsWith("File2.cs", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(-1, false)]
    public async Task ProcessCoverageAsync_Impact_RecordsEachUnitsSpansUnlessTheFileChangedSinceTheBuild(
        int startedOffsetMinutes, bool expectMapped)
    {
        var source = Path.Combine(_tempDir, "Code.cs");
        File.WriteAllText(source, string.Join("\n", Enumerable.Range(1, 10).Select(i => $"line {i}")));
        var reportA = WriteCoberturaXml(SimpleXml(source, 2, 3));
        var reportB = WriteCoberturaXml(SimpleXml(source, 8));
        using var store = new SqliteCoverageStore();
        await store.InitializeAsync(_tempDir);
        var impact = new CoverageImpactOptions(
            [
                new CoverageRunUnit("/repo/A.Tests.csproj", "run", [reportA], ["A.Test"], Merge: false),
                new CoverageRunUnit("/repo/B.Tests.csproj", "run", [reportB], ["B.Test"], Merge: false),
            ],
            ImpactGranularity.Run,
            DateTime.UtcNow.AddMinutes(startedOffsetMinutes));

        await new CoverageProcessor(new CoberturaParser()).ProcessCoverageAsync(
            store.CreateRunId(), [reportA, reportB], ["A.Test", "B.Test"], store, impact: impact);

        _ = Assert.Single(store.GetUnits("/repo/A.Tests.csproj", ImpactGranularity.Run));
        var edited = File.ReadAllText(source).Replace("line 3", "changed", StringComparison.Ordinal);
        var selection = store.SelectTests([new ChangedFile(source, edited)]);
        if (expectMapped)
        {
            var (fqn, reasons) = Assert.Single(selection.Tests);
            Assert.Equal("A.Test", fqn);
            Assert.Equal(new LineSpan(2, 3), Assert.Single(reasons).ExecutedSpan);
        }
        else
        {
            Assert.Empty(selection.Tests);
            Assert.Equal([source], selection.UnmappedFiles);
        }
    }

    [Fact]
    public async Task ProcessCoverageAsync_Impact_RecordsPerUnitBranchCountsIncludingZeroCoverage()
    {
        var source = Path.Combine(_tempDir, "BranchCode.cs");
        File.WriteAllText(source, "class C\n{\n  void M()\n  {\n    if (x) A();\n  }\n}");
        var xml = WriteCoberturaXml($"""
            <coverage version="1.9" timestamp="1700000000">
              <sources><source>{_tempDir}</source></sources>
              <packages><package name="Lib"><classes>
                <class filename="BranchCode.cs"><lines>
                  <line number="5" hits="1" branch="True" condition-coverage="50% (1/2)" />
                  <line number="6" hits="0" branch="True" condition-coverage="0% (0/2)" />
                </lines></class>
              </classes></package></packages>
            </coverage>
            """);
        using var store = new SqliteCoverageStore();
        await store.InitializeAsync(_tempDir);
        var impact = new CoverageImpactOptions(
            [new CoverageRunUnit("/repo/A.Tests.csproj", "A.Test", [xml], ["A.Test"], Merge: false)],
            ImpactGranularity.Test,
            DateTime.UtcNow.AddMinutes(1));

        await new CoverageProcessor(new CoberturaParser()).ProcessCoverageAsync(
            store.CreateRunId(), [xml], ["A.Test"], store, impact: impact);

        var reason = Assert.Single(store.SelectTests([
            new ChangedFile(source, File.ReadAllText(source).Replace("if (x)", "if (y)", StringComparison.Ordinal)),
        ]).Tests["A.Test"]);
        Assert.Equal([new BranchLine(5, 1, 2), new BranchLine(6, 0, 2)], reason.Branches);
        Assert.Equal([new BranchLine(5, 1, 2), new BranchLine(6, 0, 2)],
            Assert.Single(store.GetTestsExecuting(source)).Branches);
        Assert.True(Assert.Single(store.GetUnits("/repo/A.Tests.csproj", ImpactGranularity.Test)).HasBranches);
    }

    [Fact]
    public async Task ProcessCoverageAsync_EmptyXmlPaths_DoesNotStoreAnything()
    {
        var store = new StubCoverageStore();
        var processor = new CoverageProcessor(new CoberturaParser());
        var runId = store.CreateRunId();

        await processor.ProcessCoverageAsync(runId, [], ["Lib.Tests.Test1"], store);

        Assert.Empty(store.StoredCoverage);
    }

    [Fact]
    public async Task ProcessCoverageAsync_EmptyTestFqns_DoesNotStoreAnything()
    {
        var store = new StubCoverageStore();
        var processor = new CoverageProcessor(new CoberturaParser());

        var xmlPath = WriteCoberturaXml(SimpleXml("src/Lib/Code.cs", 5));
        var runId = store.CreateRunId();

        await processor.ProcessCoverageAsync(runId, [xmlPath], [], store);

        Assert.Empty(store.StoredCoverage);
    }

    [Fact]
    public async Task ProcessCoverageAsync_ZeroHitLines_ExcludedFromMapping()
    {
        var store = new StubCoverageStore();
        var processor = new CoverageProcessor(new CoberturaParser());

        var xmlPath = WriteCoberturaXml("""
            <?xml version="1.0" encoding="utf-8"?>
            <coverage version="1.9" timestamp="1700000000">
              <sources><source>/repo</source></sources>
              <packages>
                <package name="Lib">
                  <classes>
                    <class filename="src/Lib/Code.cs">
                      <lines>
                        <line number="5" hits="1" />
                        <line number="10" hits="0" />
                      </lines>
                    </class>
                  </classes>
                </package>
              </packages>
            </coverage>
            """);

        var runId = store.CreateRunId();
        await processor.ProcessCoverageAsync(runId, [xmlPath], ["Lib.Tests.Test1"], store);

        var (_, map) = store.StoredCoverage[0];
        var lineNumbers = map["Lib.Tests.Test1"].Select(l => l.LineNumber).ToList();

        Assert.Contains(5, lineNumbers);
        Assert.DoesNotContain(10, lineNumbers);
    }

    [Fact]
    public async Task ProcessCoverageAsync_MalformedXml_SkipsAndDoesNotThrow()
    {
        var store = new StubCoverageStore();
        var processor = new CoverageProcessor(new CoberturaParser());

        var badXml = WriteCoberturaXml("this is not xml");
        var goodXml = WriteCoberturaXml(SimpleXml("src/Lib/Code.cs", 5));
        var runId = store.CreateRunId();

        // Should not throw; bad file is skipped, good file processed
        await processor.ProcessCoverageAsync(runId, [badXml, goodXml], ["Lib.Tests.Test1"], store);

        // Good file coverage is stored
        _ = Assert.Single(store.StoredCoverage);
    }

    [Fact]
    public async Task ProcessCoverageAsync_RecordsMergedReportWithContentHash()
    {
        var store = new StubCoverageStore();
        var processor = new CoverageProcessor(new CoberturaParser());
        var source = Path.Combine(_tempDir, "Code.cs");
        File.WriteAllText(source, "class C {}");
        var xml1 = WriteCoberturaXml(SimpleXml(source, 1, 2));
        var xml2 = WriteCoberturaXml(SimpleXml(source, 2, 3));

        await processor.ProcessCoverageAsync(store.CreateRunId(), [xml1, xml2], [], store, isFullRun: true);

        var (_, files, isFullRun) = Assert.Single(store.StoredReports);
        Assert.True(isFullRun);
        var file = Assert.Single(files);
        Assert.Equal(CoverageReportFile.ComputeContentHash(source), file.ContentHash);
        Assert.NotNull(file.ContentHash);
        Assert.Equal("Lib", file.Coverage.Module);
        Assert.Equal([new LineCoverage(1, 1), new LineCoverage(2, 2), new LineCoverage(3, 1)], file.Coverage.Lines);
        Assert.Empty(store.StoredCoverage);
    }

    [Fact]
    public async Task ProcessCoverageAsync_OnlyMalformedReports_RecordsNoReport()
    {
        var store = new StubCoverageStore();
        var processor = new CoverageProcessor(new CoberturaParser());

        await processor.ProcessCoverageAsync(store.CreateRunId(), [WriteCoberturaXml("<not xml")], ["T"], store, isFullRun: true);

        Assert.Empty(store.StoredReports);
    }
}
