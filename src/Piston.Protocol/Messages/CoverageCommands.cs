namespace Piston.Protocol.Messages;

/// <summary>Request coverage data for a single source file.</summary>
public sealed record GetFileCoverageCommand(string FilePath);
