using Piston.Mcp.Tools;
using Xunit;

namespace Piston.Mcp.Tests;

public sealed class RoslynToolsTests
{
    [Fact]
    public async Task LoadWorkspaceReturnsProjectInfo()
    {
        var workspace = new StubRoslynWorkspace();

        var result = await RoslynTools.LoadWorkspace(workspace, "test.sln", CancellationToken.None);

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

        var result = await RoslynTools.GetDiagnostics(workspace, "", CancellationToken.None);

        Assert.Contains("CS0001", result);
        Assert.Contains("CS0002", result);
        Assert.Contains("Test error", result);
        Assert.Contains("Test warning", result);
    }

    [Fact]
    public async Task GetDiagnosticsFilteredByProject()
    {
        var workspace = new StubRoslynWorkspace();

        var result = await RoslynTools.GetDiagnostics(workspace, "TestProject", CancellationToken.None);

        Assert.Contains("CS0001", result);
        Assert.DoesNotContain("CS0002", result);
    }

    [Fact]
    public async Task SemanticSearchFindsReferences()
    {
        var workspace = new StubRoslynWorkspace();

        var result = await RoslynTools.SemanticSearch(workspace, "MyMethod", CancellationToken.None);

        Assert.Contains("Symbol: MyMethod", result);
        Assert.Contains("Method", result);
        Assert.Contains("File.cs:10:5", result);
        Assert.Contains("MyClass.MyMethod", result);
    }

    [Fact]
    public async Task SemanticSearchNoResultsReturnsMessage()
    {
        var workspace = new StubRoslynWorkspace();

        var result = await RoslynTools.SemanticSearch(workspace, "NotFound", CancellationToken.None);

        Assert.Contains("No references found", result);
    }

    [Fact]
    public async Task GetAstReturnsJson()
    {
        var workspace = new StubRoslynWorkspace();

        var result = await RoslynTools.GetAst(workspace, "File.cs", 3, CancellationToken.None);

        Assert.Contains("ClassDeclaration", result);
        Assert.Contains("MyClass", result);
    }

    [Fact]
    public async Task RenamePreviewDoesNotApply()
    {
        var workspace = new StubRoslynWorkspace();

        var result = await RoslynTools.Rename(workspace, "File.cs", 10, 5, "NewName", true, CancellationToken.None);

        Assert.Contains("Preview (not applied)", result);
        Assert.Contains("1 file(s) changed", result);
    }

    [Fact]
    public async Task RenameAppliesChanges()
    {
        var workspace = new StubRoslynWorkspace();

        var result = await RoslynTools.Rename(workspace, "File.cs", 10, 5, "NewName", false, CancellationToken.None);

        Assert.Contains("Applied:", result);
        Assert.Contains("File.cs", result);
    }

    [Fact]
    public async Task NotifyFileChangedUpdatesWorkspace()
    {
        var workspace = new StubRoslynWorkspace();

        var result = await RoslynTools.NotifyFileChanged(workspace, "Changed.cs", CancellationToken.None);

        Assert.Equal("Changed.cs", workspace.LastFileChangedPath);
        Assert.Contains("Workspace updated", result);
    }
}
