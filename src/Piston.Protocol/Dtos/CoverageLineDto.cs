namespace Piston.Protocol.Dtos;

/// <summary>Coverage status for a single line of source code.</summary>
public sealed record CoverageLineDto(
    int LineNumber,
    int HitCount,
    string Status
);
