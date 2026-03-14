namespace Piston.Engine.Models;

public sealed record FileChangeEvent(
    string FilePath,
    WatcherChangeTypes ChangeType,
    DateTimeOffset Timestamp
);
