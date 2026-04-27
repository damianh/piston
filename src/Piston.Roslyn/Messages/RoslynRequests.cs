namespace Piston.Roslyn.Messages;

public sealed record LoadWorkspaceParams(string Path);

public sealed record ShutdownParams;

public sealed record GetDiagnosticsParams(string? ProjectName);

public sealed record SemanticSearchParams(string SymbolName);

public sealed record GetAstParams(string FilePath, int MaxDepth);

public sealed record RenameParams(string FilePath, int Line, int Column, string NewName, bool Preview);

public sealed record FileChangedParams(string FilePath);
