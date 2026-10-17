// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Models;
using Xunit;

namespace Piston.Engine.Tests.Models;

public sealed class ExecutionLogTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [Fact]
    public void Append_KeepsEntriesInOrder()
    {
        var log = new ExecutionLog("/sln/A.Tests.csproj", () => Start);

        log.Append("stdout", "one");
        log.Write("Runner", "two");

        Assert.Equal(["one", "two"], log.Entries.Select(e => e.Message));
        Assert.Equal(["stdout", "Runner"], log.Entries.Select(e => e.Category));
        Assert.Equal(0, log.OmittedEntries);
        Assert.Equal("/sln/A.Tests.csproj", log.ProjectPath);
        Assert.Equal(Start, log.StartedAt);
    }

    [Fact]
    public void Append_BeyondCapacity_KeepsHeadAndTailAndMarksTheGap()
    {
        var log = new ExecutionLog("p", () => Start);
        const int extra = 42;
        var total = ExecutionLog.HeadCapacity + ExecutionLog.TailCapacity + extra;

        for (var i = 0; i < total; i++)
        {
            log.Append("stdout", i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        var entries = log.Entries;
        Assert.Equal(extra, log.OmittedEntries);
        Assert.Equal(ExecutionLog.HeadCapacity + 1 + ExecutionLog.TailCapacity, entries.Count);
        Assert.Equal("0", entries[0].Message);
        Assert.Equal((ExecutionLog.HeadCapacity - 1).ToString(System.Globalization.CultureInfo.InvariantCulture), entries[ExecutionLog.HeadCapacity - 1].Message);

        var marker = entries[ExecutionLog.HeadCapacity];
        Assert.Equal(ExecutionLog.OmittedCategory, marker.Category);
        Assert.Contains($"{extra} line(s) omitted", marker.Message, StringComparison.Ordinal);

        Assert.Equal((ExecutionLog.HeadCapacity + extra).ToString(System.Globalization.CultureInfo.InvariantCulture), entries[ExecutionLog.HeadCapacity + 1].Message);
        Assert.Equal((total - 1).ToString(System.Globalization.CultureInfo.InvariantCulture), entries[^1].Message);
    }

    [Fact]
    public void Append_TruncatesLongMessages()
    {
        var log = new ExecutionLog("p");

        log.Append("stdout", new string('x', ExecutionLog.MaxMessageLength + 100));

        var message = Assert.Single(log.Entries).Message;
        Assert.Equal(ExecutionLog.MaxMessageLength + 2, message.Length);
        Assert.EndsWith(" …", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Complete_RecordsTheFirstCompletionOnly()
    {
        var now = Start;
        var log = new ExecutionLog("p", () => now);
        Assert.Null(log.CompletedAt);

        now = Start.AddSeconds(3);
        log.Complete();
        now = Start.AddSeconds(9);
        log.Complete();

        Assert.Equal(Start.AddSeconds(3), log.CompletedAt);
    }

    [Fact]
    public void AttachTo_SetsTheLogOnEverySuite()
    {
        var log = new ExecutionLog("p") { Runner = "VSTest", ExitCode = 1 };
        IReadOnlyList<TestSuite> suites =
        [
            new("A (net8.0)", [], Start, TimeSpan.Zero),
            new("A (net10.0)", [], Start, TimeSpan.Zero) { RunId = "r1" },
        ];

        var attached = log.AttachTo(suites);

        Assert.All(attached, s => Assert.Same(log, s.ExecutionLog));
        Assert.Equal("r1", attached[1].RunId);
        Assert.Empty(log.AttachTo([]));
    }

    [Fact]
    public async Task Append_IsSafeFromManyThreads()
    {
        var log = new ExecutionLog("p");

        await Task.WhenAll(Enumerable.Range(0, 8).Select(t => Task.Run(() =>
        {
            for (var i = 0; i < 1000; i++)
            {
                log.Append("stdout", $"{t}:{i}");
            }
        }, TestContext.Current.CancellationToken)));

        Assert.Equal(8000 - ExecutionLog.HeadCapacity - ExecutionLog.TailCapacity, log.OmittedEntries);
    }
}
