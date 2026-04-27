using System.Text.Json;
using Piston.Roslyn;
using Piston.Roslyn.Messages;
using Xunit;

namespace Piston.Roslyn.Tests;

public sealed class RoslynJsonSerializationTests
{
    [Fact]
    public void RenameParamsRoundTrips()
    {
        var original = new RenameParams("C:\\src\\Foo.cs", 10, 5, "NewName", true);
        var json = JsonSerializer.Serialize(original, RoslynJsonContext.Default.RenameParams);
        var deserialized = JsonSerializer.Deserialize(json, RoslynJsonContext.Default.RenameParams);

        Assert.NotNull(deserialized);
        Assert.Equal(original.FilePath, deserialized.FilePath);
        Assert.Equal(original.Line, deserialized.Line);
        Assert.Equal(original.Column, deserialized.Column);
        Assert.Equal(original.NewName, deserialized.NewName);
        Assert.Equal(original.Preview, deserialized.Preview);
    }

    [Fact]
    public void RenameParamsWithPreviewFalseRoundTrips()
    {
        var original = new RenameParams("C:\\src\\Bar.cs", 1, 1, "X", false);
        var json = JsonSerializer.Serialize(original, RoslynJsonContext.Default.RenameParams);
        var deserialized = JsonSerializer.Deserialize(json, RoslynJsonContext.Default.RenameParams);

        Assert.NotNull(deserialized);
        Assert.False(deserialized.Preview);
    }

    [Fact]
    public void FileChangedParamsRoundTrips()
    {
        var original = new FileChangedParams("C:\\src\\Foo.cs");
        var json = JsonSerializer.Serialize(original, RoslynJsonContext.Default.FileChangedParams);
        var deserialized = JsonSerializer.Deserialize(json, RoslynJsonContext.Default.FileChangedParams);

        Assert.NotNull(deserialized);
        Assert.Equal(original.FilePath, deserialized.FilePath);
    }

    [Fact]
    public void FileChangeRoundTrips()
    {
        var original = new FileChange("C:\\src\\Foo.cs", "old content", "new content");
        var json = JsonSerializer.Serialize(original, RoslynJsonContext.Default.FileChange);
        var deserialized = JsonSerializer.Deserialize(json, RoslynJsonContext.Default.FileChange);

        Assert.NotNull(deserialized);
        Assert.Equal(original.FilePath, deserialized.FilePath);
        Assert.Equal(original.OldText, deserialized.OldText);
        Assert.Equal(original.NewText, deserialized.NewText);
    }

    [Fact]
    public void RenameResponseWithAppliedTrueRoundTrips()
    {
        var changes = new List<FileChange>
        {
            new("C:\\src\\Foo.cs", "class Old {}", "class New {}"),
            new("C:\\src\\Bar.cs", "var x = new Old();", "var x = new New();"),
        };
        var original = new RenameResponse(changes, true);
        var json = JsonSerializer.Serialize(original, RoslynJsonContext.Default.RenameResponse);
        var deserialized = JsonSerializer.Deserialize(json, RoslynJsonContext.Default.RenameResponse);

        Assert.NotNull(deserialized);
        Assert.True(deserialized.Applied);
        Assert.Equal(2, deserialized.Changes.Count);
        Assert.Equal("C:\\src\\Foo.cs", deserialized.Changes[0].FilePath);
        Assert.Equal("class New {}", deserialized.Changes[0].NewText);
    }

    [Fact]
    public void RenameResponseWithAppliedFalseRoundTrips()
    {
        var original = new RenameResponse([], false);
        var json = JsonSerializer.Serialize(original, RoslynJsonContext.Default.RenameResponse);
        var deserialized = JsonSerializer.Deserialize(json, RoslynJsonContext.Default.RenameResponse);

        Assert.NotNull(deserialized);
        Assert.False(deserialized.Applied);
        Assert.Empty(deserialized.Changes);
    }

    [Fact]
    public void RenameResponseUseCamelCasePropertyNames()
    {
        var original = new RenameResponse([new("file.cs", "old", "new")], true);
        var json = JsonSerializer.Serialize(original, RoslynJsonContext.Default.RenameResponse);

        Assert.Contains("\"changes\"", json);
        Assert.Contains("\"applied\"", json);
        Assert.Contains("\"filePath\"", json);
        Assert.Contains("\"oldText\"", json);
        Assert.Contains("\"newText\"", json);
    }

    // ── Phase 1+2 DTO serialization tests ─────────────────────────────────────

    [Fact]
    public void LoadWorkspaceParamsRoundTrips()
    {
        var original = new LoadWorkspaceParams("C:\\src\\My.sln");
        var json = JsonSerializer.Serialize(original, RoslynJsonContext.Default.LoadWorkspaceParams);
        var deserialized = JsonSerializer.Deserialize(json, RoslynJsonContext.Default.LoadWorkspaceParams);

        Assert.NotNull(deserialized);
        Assert.Equal(original.Path, deserialized.Path);
    }

    [Fact]
    public void WorkspaceInfoRoundTrips()
    {
        var projects = new List<ProjectInfo>
        {
            new("App.Core", "C:\\src\\App.Core\\App.Core.csproj", 15),
            new("App.Tests", "C:\\src\\App.Tests\\App.Tests.csproj", 8),
        };
        var original = new WorkspaceInfo("C:\\src\\My.sln", projects);
        var json = JsonSerializer.Serialize(original, RoslynJsonContext.Default.WorkspaceInfo);
        var deserialized = JsonSerializer.Deserialize(json, RoslynJsonContext.Default.WorkspaceInfo);

        Assert.NotNull(deserialized);
        Assert.Equal(original.SolutionPath, deserialized.SolutionPath);
        Assert.Equal(2, deserialized.Projects.Count);
        Assert.Equal("App.Core", deserialized.Projects[0].Name);
        Assert.Equal("C:\\src\\App.Core\\App.Core.csproj", deserialized.Projects[0].FilePath);
        Assert.Equal(15, deserialized.Projects[0].DocumentCount);
        Assert.Equal("App.Tests", deserialized.Projects[1].Name);
        Assert.Equal(8, deserialized.Projects[1].DocumentCount);
    }

    [Fact]
    public void GetDiagnosticsParamsRoundTrips()
    {
        var original = new GetDiagnosticsParams("App.Core");
        var json = JsonSerializer.Serialize(original, RoslynJsonContext.Default.GetDiagnosticsParams);
        var deserialized = JsonSerializer.Deserialize(json, RoslynJsonContext.Default.GetDiagnosticsParams);

        Assert.NotNull(deserialized);
        Assert.Equal("App.Core", deserialized.ProjectName);
    }

    [Fact]
    public void GetDiagnosticsParamsWithNullProjectNameRoundTrips()
    {
        var original = new GetDiagnosticsParams(null);
        var json = JsonSerializer.Serialize(original, RoslynJsonContext.Default.GetDiagnosticsParams);
        var deserialized = JsonSerializer.Deserialize(json, RoslynJsonContext.Default.GetDiagnosticsParams);

        Assert.NotNull(deserialized);
        Assert.Null(deserialized.ProjectName);
    }

    [Fact]
    public void DiagnosticsResponseRoundTrips()
    {
        var diagnostics = new List<DiagnosticResult>
        {
            new("CS0001", "Something broke", "Error", "C:\\src\\Foo.cs", 10, 5),
            new("CS0219", "Unused variable", "Warning", "C:\\src\\Bar.cs", 3, 12),
        };
        var original = new DiagnosticsResponse(diagnostics);
        var json = JsonSerializer.Serialize(original, RoslynJsonContext.Default.DiagnosticsResponse);
        var deserialized = JsonSerializer.Deserialize(json, RoslynJsonContext.Default.DiagnosticsResponse);

        Assert.NotNull(deserialized);
        Assert.Equal(2, deserialized.Diagnostics.Count);
        Assert.Equal("CS0001", deserialized.Diagnostics[0].Id);
        Assert.Equal("Something broke", deserialized.Diagnostics[0].Message);
        Assert.Equal("Error", deserialized.Diagnostics[0].Severity);
        Assert.Equal("C:\\src\\Foo.cs", deserialized.Diagnostics[0].FilePath);
        Assert.Equal(10, deserialized.Diagnostics[0].Line);
        Assert.Equal(5, deserialized.Diagnostics[0].Column);
        Assert.Equal("CS0219", deserialized.Diagnostics[1].Id);
    }

    [Fact]
    public void DiagnosticsResponseEmptyRoundTrips()
    {
        var original = new DiagnosticsResponse([]);
        var json = JsonSerializer.Serialize(original, RoslynJsonContext.Default.DiagnosticsResponse);
        var deserialized = JsonSerializer.Deserialize(json, RoslynJsonContext.Default.DiagnosticsResponse);

        Assert.NotNull(deserialized);
        Assert.Empty(deserialized.Diagnostics);
    }

    [Fact]
    public void SemanticSearchParamsRoundTrips()
    {
        var original = new SemanticSearchParams("MyClass");
        var json = JsonSerializer.Serialize(original, RoslynJsonContext.Default.SemanticSearchParams);
        var deserialized = JsonSerializer.Deserialize(json, RoslynJsonContext.Default.SemanticSearchParams);

        Assert.NotNull(deserialized);
        Assert.Equal("MyClass", deserialized.SymbolName);
    }

    [Fact]
    public void SemanticSearchResponseRoundTrips()
    {
        var references = new List<SymbolReference>
        {
            new("C:\\src\\Foo.cs", 10, 5, "FooClass.DoWork"),
            new("C:\\src\\Bar.cs", 22, 8, "BarClass.Init"),
        };
        var original = new SemanticSearchResponse("MyClass", "NamedType", references);
        var json = JsonSerializer.Serialize(original, RoslynJsonContext.Default.SemanticSearchResponse);
        var deserialized = JsonSerializer.Deserialize(json, RoslynJsonContext.Default.SemanticSearchResponse);

        Assert.NotNull(deserialized);
        Assert.Equal("MyClass", deserialized.SymbolName);
        Assert.Equal("NamedType", deserialized.SymbolKind);
        Assert.Equal(2, deserialized.References.Count);
        Assert.Equal("C:\\src\\Foo.cs", deserialized.References[0].FilePath);
        Assert.Equal(10, deserialized.References[0].Line);
        Assert.Equal(5, deserialized.References[0].Column);
        Assert.Equal("FooClass.DoWork", deserialized.References[0].ContainingMember);
        Assert.Equal("BarClass.Init", deserialized.References[1].ContainingMember);
    }

    [Fact]
    public void GetAstParamsRoundTrips()
    {
        var original = new GetAstParams("C:\\src\\Foo.cs", 3);
        var json = JsonSerializer.Serialize(original, RoslynJsonContext.Default.GetAstParams);
        var deserialized = JsonSerializer.Deserialize(json, RoslynJsonContext.Default.GetAstParams);

        Assert.NotNull(deserialized);
        Assert.Equal("C:\\src\\Foo.cs", deserialized.FilePath);
        Assert.Equal(3, deserialized.MaxDepth);
    }

    [Fact]
    public void AstResponseRoundTrips()
    {
        var childNode = new AstNode("MethodDeclaration", "DoWork", "void", "public", 5, 10, null);
        var rootNode = new AstNode("ClassDeclaration", "MyClass", null, "public sealed", 1, 12, [childNode]);
        var original = new AstResponse("C:\\src\\Foo.cs", rootNode);
        var json = JsonSerializer.Serialize(original, RoslynJsonContext.Default.AstResponse);
        var deserialized = JsonSerializer.Deserialize(json, RoslynJsonContext.Default.AstResponse);

        Assert.NotNull(deserialized);
        Assert.Equal("C:\\src\\Foo.cs", deserialized.FilePath);
        Assert.Equal("ClassDeclaration", deserialized.Root.Kind);
        Assert.Equal("MyClass", deserialized.Root.Name);
        Assert.Equal("public sealed", deserialized.Root.Modifiers);
        Assert.Equal(1, deserialized.Root.StartLine);
        Assert.Equal(12, deserialized.Root.EndLine);
        Assert.NotNull(deserialized.Root.Children);
        Assert.Single(deserialized.Root.Children);
        Assert.Equal("MethodDeclaration", deserialized.Root.Children[0].Kind);
        Assert.Equal("DoWork", deserialized.Root.Children[0].Name);
        Assert.Equal("void", deserialized.Root.Children[0].ReturnType);
        Assert.Equal("public", deserialized.Root.Children[0].Modifiers);
        Assert.Null(deserialized.Root.Children[0].Children);
    }
}
