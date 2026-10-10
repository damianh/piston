using Piston.Engine.Services;
using Xunit;

namespace Piston.Engine.Tests.Services;

public sealed class SolutionFilterBuilderTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("piston-slnf-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    private string P(params string[] parts) => Path.GetFullPath(Path.Combine([_root, .. parts]));

    [Fact]
    public void ReadSolution_Slnx_ReturnsProjectsIncludingThoseInFolders()
    {
        var slnx = P("All.slnx");
        File.WriteAllText(slnx, """
            <Solution>
              <Folder Name="/src/">
                <Project Path="src/Lib/Lib.csproj" />
              </Folder>
              <Project Path="tests\Lib.Tests\Lib.Tests.csproj" />
            </Solution>
            """);

        var result = SolutionFilterBuilder.ReadSolution(slnx);

        Assert.NotNull(result);
        Assert.Equal(slnx, result.Value.SolutionPath);
        Assert.Equal(2, result.Value.Projects.Count);
        Assert.Contains(P("src", "Lib", "Lib.csproj"), result.Value.Projects);
        Assert.Contains(P("tests", "Lib.Tests", "Lib.Tests.csproj"), result.Value.Projects);
    }

    [Fact]
    public void ReadSolution_Sln_ReturnsProjectsButNotSolutionFolders()
    {
        var sln = P("All.sln");
        File.WriteAllText(sln, """
            Microsoft Visual Studio Solution File, Format Version 12.00
            Project("{2150E333-8FDC-42A3-9474-1A3956D46DE8}") = "src", "src", "{AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA}"
            EndProject
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Lib", "src\Lib\Lib.csproj", "{11111111-1111-1111-1111-111111111111}"
            EndProject
            Global
            EndGlobal
            """);

        var result = SolutionFilterBuilder.ReadSolution(sln);

        Assert.NotNull(result);
        Assert.Equal([P("src", "Lib", "Lib.csproj")], result.Value.Projects);
    }

    [Fact]
    public void ReadSolution_Slnf_ResolvesUnderlyingSolutionAndFilteredProjects()
    {
        Directory.CreateDirectory(P("sub"));
        var slnx = P("All.slnx");
        File.WriteAllText(slnx, """<Solution><Project Path="A/A.csproj" /><Project Path="B/B.csproj" /></Solution>""");
        var slnf = P("sub", "Only.slnf");
        File.WriteAllText(slnf, """{"solution":{"path":"..\\All.slnx","projects":["A\\A.csproj"]}}""");

        var result = SolutionFilterBuilder.ReadSolution(slnf);

        Assert.NotNull(result);
        Assert.Equal(slnx, result.Value.SolutionPath);
        Assert.Equal([P("A", "A.csproj")], result.Value.Projects);
    }

    [Theory]
    [InlineData("missing.slnx")]
    [InlineData("project.csproj")]
    public void ReadSolution_UnsupportedOrMissing_ReturnsNull(string name)
    {
        var path = P(name);
        if (name.EndsWith(".csproj"))
            File.WriteAllText(path, "<Project />");

        Assert.Null(SolutionFilterBuilder.ReadSolution(path));
    }

    [Fact]
    public void WriteTemporaryFilter_RoundTripsThroughReadSolution()
    {
        var slnx = P("All.slnx");
        File.WriteAllText(slnx, """<Solution><Project Path="A/A.csproj" /><Project Path="B/B.csproj" /></Solution>""");

        var filter = SolutionFilterBuilder.WriteTemporaryFilter(slnx, [P("B", "B.csproj")]);
        try
        {
            Assert.False(filter.StartsWith(_root, StringComparison.OrdinalIgnoreCase));

            var result = SolutionFilterBuilder.ReadSolution(filter);
            Assert.NotNull(result);
            Assert.Equal(slnx, result.Value.SolutionPath);
            Assert.Equal([P("B", "B.csproj")], result.Value.Projects);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(filter)!, recursive: true);
        }
    }
}
