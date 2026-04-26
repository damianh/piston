namespace Piston.Roslyn;

public static class RoslynMethods
{
    public const string LoadWorkspace = "roslyn/loadWorkspace";
    public const string GetDiagnostics = "roslyn/getDiagnostics";
    public const string SemanticSearch = "roslyn/semanticSearch";
    public const string GetAst = "roslyn/getAst";
    public const string ApplyRefactoring = "roslyn/applyRefactoring";
    public const string FileChanged = "roslyn/fileChanged";
    public const string Shutdown = "roslyn/shutdown";
}
