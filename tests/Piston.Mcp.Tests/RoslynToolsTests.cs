using Piston.Engine;
using Piston.Mcp.Tools;
using Xunit;

namespace Piston.Mcp.Tests;

public sealed class RoslynToolsTests
{
    private static readonly IMcpCallRecorder NullRecorder = NullMcpCallRecorder.Instance;

    [Fact]
    public async Task LoadWorkspaceReturnsProjectInfo()
    {
        var workspace = new StubRoslynWorkspace();

        var tools = new RoslynTools(workspace, NullRecorder);
        var result = await tools.LoadWorkspace("test.sln", CancellationToken.None);

        Assert.True(workspace.IsLoaded);
        Assert.Equal("test.sln", workspace.LastLoadedPath);
        Assert.Contains("1 projects", result);
        Assert.Contains("TestProject", result);
        Assert.Contains("5 files", result);
    }

    [Fact]
    public async Task GetDiagnosticsAllProjectsReturnsBoth()
    {
        var workspace = new StubRoslynWorkspace();

        var tools = new RoslynTools(workspace, NullRecorder);
        var result = await tools.GetDiagnostics("", CancellationToken.None);

        Assert.Contains("CS0001", result);
        Assert.Contains("CS0002", result);
        Assert.Contains("Test error", result);
        Assert.Contains("Test warning", result);
    }

    [Fact]
    public async Task GetDiagnosticsFilteredByProject()
    {
        var workspace = new StubRoslynWorkspace();

        var tools = new RoslynTools(workspace, NullRecorder);
        var result = await tools.GetDiagnostics("TestProject", CancellationToken.None);

        Assert.Contains("CS0001", result);
        Assert.DoesNotContain("CS0002", result);
    }

    [Fact]
    public async Task SemanticSearchFindsReferences()
    {
        var workspace = new StubRoslynWorkspace();

        var tools = new RoslynTools(workspace, NullRecorder);
        var result = await tools.SemanticSearch("MyMethod", CancellationToken.None);

        Assert.Contains("Symbol: MyMethod", result);
        Assert.Contains("Method", result);
        Assert.Contains("File.cs:10:5", result);
        Assert.Contains("MyClass.MyMethod", result);
    }

    [Fact]
    public async Task SemanticSearchNoResultsReturnsMessage()
    {
        var workspace = new StubRoslynWorkspace();

        var tools = new RoslynTools(workspace, NullRecorder);
        var result = await tools.SemanticSearch("NotFound", CancellationToken.None);

        Assert.Contains("No references found", result);
    }

    [Fact]
    public async Task GetAstReturnsJson()
    {
        var workspace = new StubRoslynWorkspace();

        var tools = new RoslynTools(workspace, NullRecorder);
        var result = await tools.GetAst("File.cs", 3, CancellationToken.None);

        Assert.Contains("ClassDeclaration", result);
        Assert.Contains("MyClass", result);
    }

    [Fact]
    public async Task RenamePreviewDoesNotApply()
    {
        var workspace = new StubRoslynWorkspace();

        var tools = new RoslynTools(workspace, NullRecorder);
        var result = await tools.Rename("File.cs", 10, 5, "NewName", true, CancellationToken.None);

        Assert.Contains("Preview (not applied)", result);
        Assert.Contains("1 file(s) changed", result);
    }

    [Fact]
    public async Task RenameAppliesChanges()
    {
        var workspace = new StubRoslynWorkspace();

        var tools = new RoslynTools(workspace, NullRecorder);
        var result = await tools.Rename("File.cs", 10, 5, "NewName", false, CancellationToken.None);

        Assert.Contains("Applied:", result);
        Assert.Contains("File.cs", result);
    }

    [Fact]
    public async Task NotifyFileChangedUpdatesWorkspace()
    {
        var workspace = new StubRoslynWorkspace();

        var tools = new RoslynTools(workspace, NullRecorder);
        var result = await tools.NotifyFileChanged("Changed.cs", CancellationToken.None);

        Assert.Equal("Changed.cs", workspace.LastFileChangedPath);
        Assert.Contains("Workspace updated", result);
    }
}
