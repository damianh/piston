// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Net.Sockets;
using System.Text;
using Piston.Engine.Models;
using Piston.Engine.Services;
using Xunit;

namespace Piston.Engine.Tests.Services;

public sealed class LiveTestEventListenerTests
{
    [Theory]
    [InlineData("S\tA.B.Test", "A.B.Test", TestStatus.Running)]
    [InlineData("E\tA.B.Test(n: 1)\tPassed", "A.B.Test(n: 1)", TestStatus.Passed)]
    [InlineData("E\tnice name\tFailed", "nice name", TestStatus.Failed)]
    [InlineData("E\tA\tSkipped", "A", TestStatus.Skipped)]
    [InlineData("E\tA\tNotFound", "A", TestStatus.NotRun)]
    public void Parse_ReadsStartAndEndEvents(string line, string name, TestStatus status) =>
        Assert.Equal(new LiveTestEvent(name, status), LiveTestEventListener.Parse(line));

    [Theory]
    [InlineData("S\tM\tNs.C.M", TestStatus.Running)]
    [InlineData("E\tM\tPassed\tNs.C.M", TestStatus.Passed)]
    public void Parse_QualifiesTheDisplayNameWithTheFullyQualifiedName(string line, TestStatus status)
    {
        var evt = LiveTestEventListener.Parse(line);

        Assert.Equal(new LiveTestEvent("M", status, "Ns.C.M"), evt);
        Assert.Equal("Ns.C.M", evt!.Identity);
    }

    [Theory]
    [InlineData("")]
    [InlineData("S")]
    [InlineData("S\t")]
    [InlineData("X\tA")]
    public void Parse_IgnoresMalformedLines(string line) =>
        Assert.Null(LiveTestEventListener.Parse(line));

    [Fact]
    public async Task ReceivesEventsFromConnectedCollectors()
    {
        var events = new List<LiveTestEvent>();
        var listener = LiveTestEventListener.TryStart(e =>
        {
            lock (events)
            {
                events.Add(e);
            }
        });
        Assert.NotNull(listener);

        await using (listener)
        {
            foreach (var payload in new[] { "S\tOne\nE\tOne\tPassed\n", "S\tTwo\n" })
            {
                using var client = new TcpClient();
                await client.ConnectAsync("127.0.0.1", listener.Port);
                await client.GetStream().WriteAsync(Encoding.UTF8.GetBytes(payload));
            }

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline && events.Count < 3)
            {
                await Task.Delay(20);
            }
        }

        Assert.Equal(
            [new("One", TestStatus.Running), new("One", TestStatus.Passed), new("Two", TestStatus.Running)],
            events.OrderBy(e => e.DisplayName, StringComparer.Ordinal).ThenBy(e => e.Status == TestStatus.Running ? 0 : 1));
    }

    [Fact]
    public void CollectorIsDeployedNextToTheEngine() =>
        Assert.NotNull(LiveTestEventListener.CollectorDirectory);
}
