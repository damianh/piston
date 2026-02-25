namespace Piston.Core.Models;

public sealed record BuildResult(
    BuildStatus Status,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    TimeSpan Duration
);
