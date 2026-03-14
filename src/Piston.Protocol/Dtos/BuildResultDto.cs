namespace Piston.Protocol.Dtos;

public sealed record BuildResultDto(
    BuildStatusDto Status,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    double DurationMs
);
