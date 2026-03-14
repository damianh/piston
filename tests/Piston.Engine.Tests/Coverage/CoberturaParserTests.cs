using Piston.Engine.Coverage;
using Xunit;

namespace Piston.Engine.Tests.Coverage;

public sealed class CoberturaParserTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _fixtureDir;

    public CoberturaParserTests()
    {
        _tempDir = Directory.CreateTempSubdirectory("piston-cobertura-test-").FullName;

        // The fixture directory is in the output directory, copied from the test project
        _fixtureDir = Path.Combine(
            Path.GetDirectoryName(typeof(CoberturaParserTests).Assembly.Location)!,
            "Coverage", "Fixtures");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* ignore */ }
    }

    [Fact]
    public void Parse_SampleFixture_ExtractsAllFiles()
    {
        // The fixture uses /repo/src as source root; paths are relative
        var sut = new CoberturaParser();

        var xmlPath = Path.Combine(_fixtureDir, "sample-coverage.cobertura.xml");
        var report = sut.Parse(xmlPath);

        Assert.Equal(2, report.Files.Count);
    }

    [Fact]
    public void Parse_SampleFixture_ExtractsCorrectLineNumbers()
    {
        var sut = new CoberturaParser();
        var xmlPath = Path.Combine(_fixtureDir, "sample-coverage.cobertura.xml");

        var report = sut.Parse(xmlPath);

        var codeFile = report.Files.First(f => f.FilePath.EndsWith("Code.cs", StringComparison.OrdinalIgnoreCase));
        var lineNumbers = codeFile.Lines.Select(l => l.LineNumber).ToList();

        Assert.Contains(5, lineNumbers);
        Assert.Contains(6, lineNumbers);
        Assert.Contains(7, lineNumbers);
    }

    [Fact]
    public void Parse_SampleFixture_ExtractsHitCounts()
    {
        var sut = new CoberturaParser();
        var xmlPath = Path.Combine(_fixtureDir, "sample-coverage.cobertura.xml");

        var report = sut.Parse(xmlPath);

        var codeFile = report.Files.First(f => f.FilePath.EndsWith("Code.cs", StringComparison.OrdinalIgnoreCase));
        var line5 = codeFile.Lines.First(l => l.LineNumber == 5);

        Assert.Equal(3, line5.HitCount);
    }

    [Fact]
    public void Parse_SampleFixture_IncludesZeroHitLines()
    {
        var sut = new CoberturaParser();
        var xmlPath = Path.Combine(_fixtureDir, "sample-coverage.cobertura.xml");

        var report = sut.Parse(xmlPath);

        var otherFile = report.Files.First(f => f.FilePath.EndsWith("Other.cs", StringComparison.OrdinalIgnoreCase));
        var line20 = otherFile.Lines.FirstOrDefault(l => l.LineNumber == 20);

        Assert.NotNull(line20);
        Assert.Equal(0, line20.HitCount);
    }

    [Fact]
    public void Parse_EmptyPackages_ReturnsEmptyReport()
    {
        var sut = new CoberturaParser();
        var xmlPath = WriteTempXml("""
            <?xml version="1.0" encoding="utf-8"?>
            <coverage line-rate="0" version="1.9" timestamp="1700000000">
              <sources><source>/repo/src</source></sources>
              <packages />
            </coverage>
            """);

        var report = sut.Parse(xmlPath);

        Assert.Empty(report.Files);
    }

    [Fact]
    public void Parse_MultipleClassesForSameFile_AccumulatesHits()
    {
        // Coverlet can emit multiple <class> elements with the same filename
        // (e.g. partial classes). Hits should be accumulated.
        var sut = new CoberturaParser();
        var xmlPath = WriteTempXml("""
            <?xml version="1.0" encoding="utf-8"?>
            <coverage version="1.9" timestamp="1700000000">
              <sources><source>/repo</source></sources>
              <packages>
                <package name="Lib">
                  <classes>
                    <class filename="src/Foo.cs">
                      <lines>
                        <line number="1" hits="2" />
                        <line number="2" hits="1" />
                      </lines>
                    </class>
                    <class filename="src/Foo.cs">
                      <lines>
                        <line number="1" hits="3" />
                        <line number="5" hits="1" />
                      </lines>
                    </class>
                  </classes>
                </package>
              </packages>
            </coverage>
            """);

        var report = sut.Parse(xmlPath);

        // Deduplicated to one file
        Assert.Single(report.Files);
        var file = report.Files[0];

        // Line 1 hits accumulated: 2 + 3 = 5
        var line1 = file.Lines.First(l => l.LineNumber == 1);
        Assert.Equal(5, line1.HitCount);

        // Line 2 only in first class
        var line2 = file.Lines.First(l => l.LineNumber == 2);
        Assert.Equal(1, line2.HitCount);

        // Line 5 only in second class
        var line5 = file.Lines.First(l => l.LineNumber == 5);
        Assert.Equal(1, line5.HitCount);
    }

    [Fact]
    public void Parse_AbsoluteFilePath_ReturnedAsIs()
    {
        // On Windows the path separator differs, but the path should still be absolute
        var absPath = Path.IsPathRooted("/some/abs/path.cs")
            ? "/some/abs/path.cs"
            : @"C:\some\abs\path.cs";

        var sut = new CoberturaParser();
        var xmlPath = WriteTempXml($"""
            <?xml version="1.0" encoding="utf-8"?>
            <coverage version="1.9" timestamp="1700000000">
              <sources><source>/repo</source></sources>
              <packages>
                <package name="Lib">
                  <classes>
                    <class filename="{absPath}">
                      <lines>
                        <line number="1" hits="1" />
                      </lines>
                    </class>
                  </classes>
                </package>
              </packages>
            </coverage>
            """);

        var report = sut.Parse(xmlPath);

        Assert.Single(report.Files);
        Assert.True(Path.IsPathRooted(report.Files[0].FilePath));
    }

    [Fact]
    public void Parse_NoSourceElement_UsesRelativePathFallback()
    {
        var sut = new CoberturaParser();
        var xmlPath = WriteTempXml("""
            <?xml version="1.0" encoding="utf-8"?>
            <coverage version="1.9" timestamp="1700000000">
              <packages>
                <package name="Lib">
                  <classes>
                    <class filename="relative/file.cs">
                      <lines>
                        <line number="1" hits="1" />
                      </lines>
                    </class>
                  </classes>
                </package>
              </packages>
            </coverage>
            """);

        var report = sut.Parse(xmlPath);

        Assert.Single(report.Files);
        // Path should be normalized even without a source element
        Assert.True(Path.IsPathRooted(report.Files[0].FilePath));
    }

    private string WriteTempXml(string content)
    {
        var path = Path.Combine(_tempDir, $"{Guid.NewGuid():N}.xml");
        File.WriteAllText(path, content);
        return path;
    }
}
