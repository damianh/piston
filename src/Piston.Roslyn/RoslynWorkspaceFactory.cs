namespace Piston.Roslyn;

public static class RoslynWorkspaceFactory
{
    public static IRoslynWorkspace Create()
    {
        return new RoslynWorkspaceProxy();
    }
}
