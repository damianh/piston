namespace Piston.Engine.Coverage;

/// <summary>
/// Parsed representation of a Cobertura XML coverage report.
/// </summary>
internal sealed record CoberturaReport(IReadOnlyList<FileCoverage> Files);

/// <summary>
/// Coverage data for a single source file.
/// </summary>
internal sealed record FileCoverage(string FilePath, IReadOnlyList<LineCoverage> Lines);

/// <summary>
/// Coverage data for a single line in a source file.
/// </summary>
internal sealed record LineCoverage(int LineNumber, int HitCount);
