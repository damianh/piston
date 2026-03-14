using Piston.Engine.Coverage;
using Xunit;

namespace Piston.Engine.Tests.Coverage;

public sealed class CoverageProcessorTests : IDisposable
{
    private readonly string _tempDir;

    public CoverageProcessorTests()
    {
        _tempDir = Directory.CreateTempSubdirectory("piston-coverage-proc-test-").FullName;
    }

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
        var store     = new StubCoverageStore();
        var processor = new CoverageProcessor(new CoberturaParser());

        var xmlPath = WriteCoberturaXml(SimpleXml("src/Lib/Code.cs", 5, 6, 7));
        var testFqns = new[] { "Lib.Tests.Test1", "Lib.Tests.Test2" };
        var runId    = store.CreateRunId();

        await processor.ProcessCoverageAsync(runId, [xmlPath], testFqns, store);

        Assert.Single(store.StoredCoverage);
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
    public async Task ProcessCoverageAsync_MultipleReports_UnionOfCoverage()
    {
        var store     = new StubCoverageStore();
        var processor = new CoverageProcessor(new CoberturaParser());

        var xml1 = WriteCoberturaXml(SimpleXml("src/Lib/File1.cs", 1, 2));
        var xml2 = WriteCoberturaXml(SimpleXml("src/Lib/File2.cs", 10, 11));
        var testFqns = new[] { "Lib.Tests.Test1" };
        var runId    = store.CreateRunId();

        await processor.ProcessCoverageAsync(runId, [xml1, xml2], testFqns, store);

        Assert.Single(store.StoredCoverage);
        var (_, map) = store.StoredCoverage[0];

        var entries = map["Lib.Tests.Test1"];
        var files   = entries.Select(e => e.FilePath).Distinct().ToList();

        // Both files should appear
        Assert.Contains(files, f => f.EndsWith("File1.cs", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(files, f => f.EndsWith("File2.cs", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ProcessCoverageAsync_EmptyXmlPaths_DoesNotStoreAnything()
    {
        var store     = new StubCoverageStore();
        var processor = new CoverageProcessor(new CoberturaParser());
        var runId     = store.CreateRunId();

        await processor.ProcessCoverageAsync(runId, [], ["Lib.Tests.Test1"], store);

        Assert.Empty(store.StoredCoverage);
    }

    [Fact]
    public async Task ProcessCoverageAsync_EmptyTestFqns_DoesNotStoreAnything()
    {
        var store     = new StubCoverageStore();
        var processor = new CoverageProcessor(new CoberturaParser());

        var xmlPath = WriteCoberturaXml(SimpleXml("src/Lib/Code.cs", 5));
        var runId   = store.CreateRunId();

        await processor.ProcessCoverageAsync(runId, [xmlPath], [], store);

        Assert.Empty(store.StoredCoverage);
    }

    [Fact]
    public async Task ProcessCoverageAsync_ZeroHitLines_ExcludedFromMapping()
    {
        var store     = new StubCoverageStore();
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
        var store     = new StubCoverageStore();
        var processor = new CoverageProcessor(new CoberturaParser());

        var badXml  = WriteCoberturaXml("this is not xml");
        var goodXml = WriteCoberturaXml(SimpleXml("src/Lib/Code.cs", 5));
        var runId   = store.CreateRunId();

        // Should not throw; bad file is skipped, good file processed
        await processor.ProcessCoverageAsync(runId, [badXml, goodXml], ["Lib.Tests.Test1"], store);

        // Good file coverage is stored
        Assert.Single(store.StoredCoverage);
    }
}
