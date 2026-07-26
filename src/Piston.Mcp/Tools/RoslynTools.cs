using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;
using Piston.Engine;
using Piston.Roslyn;

namespace Piston.Mcp.Tools;

[McpServerToolType]
public sealed class RoslynTools(IRoslynWorkspace workspace, IMcpCallRecorder recorder)
{
    [McpServerTool, Description("Load a .sln or .csproj workspace for code analysis. Must be called before other Roslyn tools.")]
    public async Task<string> LoadWorkspace(string path, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var succeeded = true;
        string result = string.Empty;
        try
        {
            var info = await workspace.LoadAsync(path, ct).ConfigureAwait(false);
            result = $"Loaded {info.Projects.Count} projects from {info.SolutionPath}:\n" +
                     string.Join("\n", info.Projects.Select(p => $"  {p.Name} ({p.DocumentCount} files)"));
        }
        catch
        {
            succeeded = false;
            throw;
        }
        finally
        {
            sw.Stop();
            recorder.Record(nameof(LoadWorkspace), path, succeeded ? result! : null, sw.Elapsed.TotalMilliseconds, succeeded);
        }

        return result;
    }

    [McpServerTool, Description("Get compiler diagnostics (errors and warnings) for the loaded workspace. Pass empty string for all projects.")]
    public async Task<string> GetDiagnostics(string projectName, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var effectiveProjectName = string.IsNullOrEmpty(projectName) ? null : projectName;
        var response = await workspace.GetDiagnosticsAsync(effectiveProjectName, ct).ConfigureAwait(false);

        var result = response.Diagnostics.Count == 0
            ? "No diagnostics found."
            : string.Join("\n", response.Diagnostics.Select(d =>
                $"[{d.Severity}] {d.Id}: {d.Message} at {d.FilePath}:{d.Line}:{d.Column}"));

        sw.Stop();
        recorder.Record(nameof(GetDiagnostics), projectName, $"{response.Diagnostics.Count} diagnostic(s)", sw.Elapsed.TotalMilliseconds, true);
        return result;
    }

    [McpServerTool, Description("Find all references to a symbol by name using semantic analysis (not text search).")]
    public async Task<string> SemanticSearch(string symbolName, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var response = await workspace.SemanticSearchAsync(symbolName, ct).ConfigureAwait(false);

        var result = response.References.Count == 0
            ? $"No references found for '{symbolName}'."
            : $"Symbol: {response.SymbolName} ({response.SymbolKind})\n" +
              $"References ({response.References.Count}):\n" +
              string.Join("\n", response.References.Select(r =>
                  $"  {r.FilePath}:{r.Line}:{r.Column} in {r.ContainingMember}"));

        sw.Stop();
        recorder.Record(nameof(SemanticSearch), symbolName, $"{response.References.Count} reference(s)", sw.Elapsed.TotalMilliseconds, true);
        return result;
    }

    [McpServerTool, Description("Get a pruned Abstract Syntax Tree for a file showing declarations (classes, methods, properties, etc.). MaxDepth controls nesting depth.")]
    public async Task<string> GetAst(string filePath, int maxDepth, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var response = await workspace.GetAstAsync(filePath, maxDepth, ct).ConfigureAwait(false);
        var result = JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true });
        sw.Stop();
        recorder.Record(nameof(GetAst), $"{filePath} depth={maxDepth}", $"{result.Length} chars", sw.Elapsed.TotalMilliseconds, true);
        return result;
    }

    [McpServerTool, Description("Rename a symbol at a specific file location. Set preview to true to see changes without applying them.")]
    public async Task<string> Rename(
        string filePath,
        int line,
        int column,
        string newName,
        bool preview,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var succeeded = true;
        string result = string.Empty;
        try
        {
            var response = await workspace.RenameAsync(filePath, line, column, newName, preview, ct).ConfigureAwait(false);
            var sb = new StringBuilder();
            sb.AppendLine(preview ? "Preview (not applied):" : "Applied:");
            sb.AppendLine($"{response.Changes.Count} file(s) changed:");
            foreach (var change in response.Changes)
                sb.AppendLine($"  {change.FilePath}");
            result = sb.ToString();
        }
        catch
        {
            succeeded = false;
            throw;
        }
        finally
        {
            sw.Stop();
            recorder.Record(nameof(Rename), $"{filePath}:{line}:{column} → {newName}", succeeded ? result! : null, sw.Elapsed.TotalMilliseconds, succeeded);
        }

        return result;
    }

    [McpServerTool, Description("Notify the Roslyn workspace that a file has been modified externally, so it can update its in-memory state.")]
    public async Task<string> NotifyFileChanged(string filePath, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        await workspace.NotifyFileChangedAsync(filePath, ct).ConfigureAwait(false);
        var result = $"Workspace updated for: {filePath}";
        sw.Stop();
        recorder.Record(nameof(NotifyFileChanged), filePath, result, sw.Elapsed.TotalMilliseconds, true);
        return result;
    }
}
