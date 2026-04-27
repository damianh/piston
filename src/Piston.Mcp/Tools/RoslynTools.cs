using System.ComponentModel;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;
using Piston.Roslyn;

namespace Piston.Mcp.Tools;

[McpServerToolType]
public static class RoslynTools
{
    [McpServerTool, Description("Load a .sln or .csproj workspace for code analysis. Must be called before other Roslyn tools.")]
    public static async Task<string> LoadWorkspace(IRoslynWorkspace workspace, string path, CancellationToken ct)
    {
        var info = await workspace.LoadAsync(path, ct).ConfigureAwait(false);
        return $"Loaded {info.Projects.Count} projects from {info.SolutionPath}:\n" +
               string.Join("\n", info.Projects.Select(p => $"  {p.Name} ({p.DocumentCount} files)"));
    }

    [McpServerTool, Description("Get compiler diagnostics (errors and warnings) for the loaded workspace. Pass empty string for all projects.")]
    public static async Task<string> GetDiagnostics(IRoslynWorkspace workspace, string projectName, CancellationToken ct)
    {
        var effectiveProjectName = string.IsNullOrEmpty(projectName) ? null : projectName;
        var response = await workspace.GetDiagnosticsAsync(effectiveProjectName, ct).ConfigureAwait(false);

        if (response.Diagnostics.Count == 0)
        {
            return "No diagnostics found.";
        }

        return string.Join("\n", response.Diagnostics.Select(d =>
            $"[{d.Severity}] {d.Id}: {d.Message} at {d.FilePath}:{d.Line}:{d.Column}"));
    }

    [McpServerTool, Description("Find all references to a symbol by name using semantic analysis (not text search).")]
    public static async Task<string> SemanticSearch(IRoslynWorkspace workspace, string symbolName, CancellationToken ct)
    {
        var response = await workspace.SemanticSearchAsync(symbolName, ct).ConfigureAwait(false);

        if (response.References.Count == 0)
        {
            return $"No references found for '{symbolName}'.";
        }

        return $"Symbol: {response.SymbolName} ({response.SymbolKind})\n" +
               $"References ({response.References.Count}):\n" +
               string.Join("\n", response.References.Select(r =>
                   $"  {r.FilePath}:{r.Line}:{r.Column} in {r.ContainingMember}"));
    }

    [McpServerTool, Description("Get a pruned Abstract Syntax Tree for a file showing declarations (classes, methods, properties, etc.). MaxDepth controls nesting depth.")]
    public static async Task<string> GetAst(IRoslynWorkspace workspace, string filePath, int maxDepth, CancellationToken ct)
    {
        var response = await workspace.GetAstAsync(filePath, maxDepth, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true });
    }

    [McpServerTool, Description("Rename a symbol at a specific file location. Set preview to true to see changes without applying them.")]
    public static async Task<string> Rename(
        IRoslynWorkspace workspace,
        string filePath,
        int line,
        int column,
        string newName,
        bool preview,
        CancellationToken ct)
    {
        var response = await workspace.RenameAsync(filePath, line, column, newName, preview, ct).ConfigureAwait(false);

        var sb = new StringBuilder();
        sb.AppendLine(preview ? "Preview (not applied):" : "Applied:");
        sb.AppendLine($"{response.Changes.Count} file(s) changed:");

        foreach (var change in response.Changes)
        {
            sb.AppendLine($"  {change.FilePath}");
        }

        return sb.ToString();
    }

    [McpServerTool, Description("Notify the Roslyn workspace that a file has been modified externally, so it can update its in-memory state.")]
    public static async Task<string> NotifyFileChanged(IRoslynWorkspace workspace, string filePath, CancellationToken ct)
    {
        await workspace.NotifyFileChangedAsync(filePath, ct).ConfigureAwait(false);
        return $"Workspace updated for: {filePath}";
    }
}
