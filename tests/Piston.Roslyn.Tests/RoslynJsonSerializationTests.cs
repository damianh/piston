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
}
