using Microsoft.Build.Locator;
using Piston.Roslyn.Worker;

// Register MSBuild BEFORE any Roslyn types are loaded
MSBuildLocator.RegisterDefaults();

await WorkspaceHost.RunAsync(
    Console.OpenStandardInput(),
    Console.OpenStandardOutput(),
    CancellationToken.None).ConfigureAwait(false);
