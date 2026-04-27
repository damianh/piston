namespace Piston.Roslyn.Messages;

public sealed record WorkspaceInfo(
    string SolutionPath,
    IReadOnlyList<ProjectInfo> Projects);

public sealed record ProjectInfo(
    string Name,
    string FilePath,
    int DocumentCount);

public sealed record DiagnosticResult(
    string Id,
    string Message,
    string Severity,
    string FilePath,
    int Line,
    int Column);

public sealed record DiagnosticsResponse(IReadOnlyList<DiagnosticResult> Diagnostics);

public sealed record SymbolReference(
    string FilePath,
    int Line,
    int Column,
    string ContainingMember);

public sealed record SemanticSearchResponse(
    string SymbolName,
    string SymbolKind,
    IReadOnlyList<SymbolReference> References);

public sealed record AstNode(
    string Kind,
    string? Name,
    string? ReturnType,
    string? Modifiers,
    int StartLine,
    int EndLine,
    IReadOnlyList<AstNode>? Children);

public sealed record AstResponse(
    string FilePath,
    AstNode Root);
