namespace Piston.Engine.Coverage;

/// <summary>
/// Coverage data for a single test-to-line mapping entry.
/// </summary>
internal sealed record TestLineCoverage(string FilePath, int LineNumber, int HitCount);
