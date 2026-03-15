namespace Piston.Protocol.Dtos;

/// <summary>Per-file coverage data containing line-level hit counts.</summary>
public sealed record FileCoverageDto(
    string FilePath,
    IReadOnlyList<CoverageLineDto> Lines
);
