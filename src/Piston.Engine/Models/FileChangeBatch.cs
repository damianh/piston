namespace Piston.Engine.Models;

public sealed record FileChangeBatch(
    IReadOnlyList<FileChangeEvent> Changes,
    DateTimeOffset Timestamp
);
