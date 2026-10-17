// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Services;

namespace Piston.Engine.Models;

/// <summary>One timestamped line of an <see cref="ExecutionLog"/>.</summary>
public sealed record ExecutionLogEntry(DateTimeOffset Timestamp, string Category, string Message);

/// <summary>
/// What Piston did to run one test process (one project, every framework it ran under): the runner it chose,
/// the command, fallbacks, the process's console output, exit code and result summary. Bounded: it keeps the
/// first <see cref="HeadCapacity"/> and last <see cref="TailCapacity"/> entries and counts the ones between.
/// Entries written with <see cref="Write"/> also go to the solution's <c>.piston/diagnostics.log</c>.
/// Thread-safe.
/// </summary>
public sealed class ExecutionLog
{
    internal const int HeadCapacity = 1500;
    internal const int TailCapacity = 500;
    internal const int MaxMessageLength = 4000;

    private readonly Lock _lock = new();
    private readonly List<ExecutionLogEntry> _head = [];
    private readonly Queue<ExecutionLogEntry> _tail = new();
    private readonly Func<DateTimeOffset> _clock;
    private int _omitted;
    private string? _runner;
    private int? _exitCode;
    private DateTimeOffset? _completedAt;

    public ExecutionLog(string projectPath)
        : this(projectPath, () => DateTimeOffset.UtcNow)
    {
    }

    internal ExecutionLog(string projectPath, Func<DateTimeOffset> clock)
    {
        ProjectPath = projectPath;
        _clock = clock;
        StartedAt = clock();
    }

    /// <summary>The test project the process ran.</summary>
    public string ProjectPath { get; }

    public DateTimeOffset StartedAt { get; }

    /// <summary>When the process finished, or <see langword="null"/> while it runs.</summary>
    public DateTimeOffset? CompletedAt
    {
        get { lock (_lock) { return _completedAt; } }
    }

    /// <summary>How the tests were run: <c>VSTest</c>, <c>MTP server</c> or <c>MTP dotnet test</c>.</summary>
    public string? Runner
    {
        get { lock (_lock) { return _runner; } }
        set { lock (_lock) { _runner = value; } }
    }

    /// <summary>Exit code of the process that produced the results, when it exited on its own.</summary>
    public int? ExitCode
    {
        get { lock (_lock) { return _exitCode; } }
        set { lock (_lock) { _exitCode = value; } }
    }

    /// <summary>Number of entries dropped between the retained head and tail.</summary>
    public int OmittedEntries
    {
        get { lock (_lock) { return _omitted; } }
    }

    /// <summary>
    /// The retained entries, oldest first. When entries were dropped, a <see cref="OmittedCategory"/> entry
    /// marks where.
    /// </summary>
    public IReadOnlyList<ExecutionLogEntry> Entries
    {
        get
        {
            lock (_lock)
            {
                if (_omitted == 0)
                {
                    return [.. _head, .. _tail];
                }

                var marker = new ExecutionLogEntry(_tail.Peek().Timestamp, OmittedCategory, $"… {_omitted} line(s) omitted …");
                return [.. _head, marker, .. _tail];
            }
        }
    }

    /// <summary>Category of the entry marking dropped entries.</summary>
    public const string OmittedCategory = "Omitted";

    /// <summary>Records a diagnostic and also writes it to the solution's diagnostics log.</summary>
    public void Write(string category, string message)
    {
        DiagnosticLog.Instance?.Write(category, message);
        Append(category, message);
    }

    /// <summary>Records a line, such as raw process output, that the diagnostics log does not need.</summary>
    public void Append(string category, string message)
    {
        if (message.Length > MaxMessageLength)
        {
            message = string.Concat(message.AsSpan(0, MaxMessageLength), " …");
        }

        var entry = new ExecutionLogEntry(_clock(), category, message);
        lock (_lock)
        {
            if (_head.Count < HeadCapacity)
            {
                _head.Add(entry);
                return;
            }

            _tail.Enqueue(entry);
            if (_tail.Count > TailCapacity)
            {
                _ = _tail.Dequeue();
                _omitted++;
            }
        }
    }

    /// <summary>Marks the process finished.</summary>
    public void Complete()
    {
        lock (_lock)
        {
            _completedAt ??= _clock();
        }
    }

    /// <summary>Returns <paramref name="suites"/> with this log attached to each.</summary>
    internal IReadOnlyList<TestSuite> AttachTo(IReadOnlyList<TestSuite> suites) =>
        suites.Count == 0 ? suites : [.. suites.Select(s => s with { ExecutionLog = this })];
}
