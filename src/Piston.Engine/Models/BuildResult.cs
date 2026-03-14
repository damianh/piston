namespace Piston.Engine.Models;

public sealed record BuildResult(
    BuildStatus Status,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    TimeSpan Duration
);
