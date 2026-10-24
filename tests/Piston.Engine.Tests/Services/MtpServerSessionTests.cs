// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Piston.Engine.Services;
using Xunit;

namespace Piston.Engine.Tests.Services;

/// <summary>
/// Drives <see cref="MtpServerSession.ExchangeAsync"/> against an in-process fake test host over a
/// loopback socket.
/// </summary>
public sealed class MtpServerSessionTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private static readonly MtpServerSession.StageTimeouts Short = new(TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(200));

    // For exchanges expected to complete: a loaded CI runner can take longer than Short to answer.
    private static readonly MtpServerSession.StageTimeouts Generous = new(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));

    [Fact]
    public async Task MissingHostExecutable_ReturnsNotStarted()
    {
        var result = await MtpServerSession.RunAsync(
            $"piston-missing-host-{Guid.NewGuid():N}",
            Path.GetTempPath(),
            MtpServerSession.DiscoverMethod,
            tests: null,
            _ => { },
            _ct);

        Assert.Equal(MtpServerSession.Outcome.NotStarted, result.Outcome);
        Assert.StartsWith("Could not start the test host:", result.Error, StringComparison.Ordinal);
        Assert.Empty(result.Stderr);
    }

    [Fact]
    public async Task HostExitingBeforeConnect_ReturnsDrainedStderr()
    {
        var result = await MtpServerSession.RunAsync(
            typeof(TestProcessHost.Program).Assembly.Location,
            Path.GetTempPath(),
            MtpServerSession.DiscoverMethod,
            tests: null,
            _ => { },
            _ct);

        Assert.Equal(MtpServerSession.Outcome.NotStarted, result.Outcome);
        Assert.Equal(["Test host failed before connecting."], result.Stderr);
    }

    [Fact]
    public async Task HostStallingBeforeInitialize_TimesOut()
    {
        var (outcome, error) = await ExchangeAsync(MtpServerSession.DiscoverMethod, Short, async (host, ct) =>
        {
            _ = await ReadAsync(host, ct);
            await Task.Delay(Timeout.Infinite, ct);
        }, _ct);

        Assert.Equal(MtpServerSession.Outcome.TimedOut, outcome);
        Assert.Contains("initialization", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HostStallingDuringDiscovery_TimesOut()
    {
        var (outcome, error) = await ExchangeAsync(MtpServerSession.DiscoverMethod, Short, async (host, ct) =>
        {
            await InitializeAsync(host, ct);
            _ = await ReadAsync(host, ct);
            await Task.Delay(Timeout.Infinite, ct);
        }, _ct);

        Assert.Equal(MtpServerSession.Outcome.TimedOut, outcome);
        Assert.Contains("discovery", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SlowTestExecution_IsNotBoundedByStageTimeouts()
    {
        // Initialization gets enough time to succeed under load; execution then outlasts both stage timeouts.
        var timeouts = new MtpServerSession.StageTimeouts(TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(200));
        var (outcome, error) = await ExchangeAsync(MtpServerSession.RunMethod, timeouts, async (host, ct) =>
        {
            await InitializeAsync(host, ct);
            _ = await ReadAsync(host, ct);
            await Task.Delay(TimeSpan.FromSeconds(2.5), ct);
            await WriteAsync(host, """{"jsonrpc":"2.0","id":2,"result":{}}""", ct);
            await Task.Delay(Timeout.Infinite, ct);
        }, _ct);

        Assert.Equal(MtpServerSession.Outcome.Completed, outcome);
        Assert.Null(error);
    }

    [Fact]
    public async Task RejectedDiscovery_CompletesWithError()
    {
        var (outcome, error) = await ExchangeAsync(MtpServerSession.DiscoverMethod, Generous, async (host, ct) =>
        {
            await InitializeAsync(host, ct);
            _ = await ReadAsync(host, ct);
            await WriteAsync(host, """{"jsonrpc":"2.0","id":2,"error":{"code":-32000,"message":"nope"}}""", ct);
            await Task.Delay(Timeout.Infinite, ct);
        }, _ct);

        Assert.Equal(MtpServerSession.Outcome.Completed, outcome);
        Assert.Equal("nope", error);
    }

    [Fact]
    public async Task CallerCancellation_IsDistinctFromTimeout()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var (outcome, error) = await ExchangeAsync(
            MtpServerSession.DiscoverMethod,
            new MtpServerSession.StageTimeouts(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1)),
            async (host, ct) =>
            {
                _ = await ReadAsync(host, ct);
                await Task.Delay(Timeout.Infinite, ct);
            },
            cts.Token);

        Assert.Equal(MtpServerSession.Outcome.Cancelled, outcome);
        Assert.Null(error);
    }

    [Fact]
    public async Task Connection_ServesSeveralRequestsAfterOneInitialize()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, _ct);
        using var server = await listener.AcceptTcpClientAsync(_ct);

        var hostTask = Task.Run(async () =>
        {
            var host = server.GetStream();
            await InitializeAsync(host, _ct);
            var ids = new List<int>();
            for (var i = 0; i < 2; i++)
            {
                using var request = await ReadAsync(host, _ct);
                var id = request.RootElement.GetProperty("id").GetInt32();
                ids.Add(id);
                await WriteAsync(host, $$$"""{"jsonrpc":"2.0","method":"testing/testUpdates/tests","params":{"changes":[{"node":{"uid":"t{{{id}}}"}}]}}""", _ct);
                await WriteAsync(host, $$$"""{"jsonrpc":"2.0","id":{{{id}}},"result":{}}""", _ct);
            }

            return ids;
        }, _ct);

        using var connection = new MtpServerSession.Connection(client.GetStream());
        var uids = new List<string?>();
        Assert.Equal(MtpServerSession.Outcome.Completed, (await connection.InitializeAsync(Generous.Initialize, _ct)).Outcome);
        var first = await connection.RequestAsync(MtpServerSession.DiscoverMethod, null, n => uids.Add(n[0].GetProperty("uid").GetString()), Generous, _ct);
        var second = await connection.RequestAsync(MtpServerSession.RunMethod, null, n => uids.Add(n[0].GetProperty("uid").GetString()), Generous, _ct);

        Assert.Equal((MtpServerSession.Outcome.Completed, (string?)null), first);
        Assert.Equal((MtpServerSession.Outcome.Completed, (string?)null), second);
        Assert.True(connection.ReportedNodes);
        Assert.Equal([2, 3], await hostTask.WaitAsync(TimeSpan.FromSeconds(20), _ct));
        Assert.Equal(["t2", "t3"], uids);
    }

    [Fact]
    public void CreateStartInfo_DoesNotMarkColdHostsWarm()
    {
        // Even when this test itself runs in a warm host.
        var psi = MtpServerSession.CreateStartInfo("/sln/bin/Tests.dll", "/sln", 1234);

        Assert.False(psi.Environment.ContainsKey(MtpServerSession.WarmHostVariable));
    }

    [Theory]
    [InlineData("/sln/bin/Tests.dll", "dotnet")]
    [InlineData("/sln/bin/Tests", "/sln/bin/Tests")]
    public void CreateStartInfo_StartsTheHostInTheSolutionDirectory(string appPath, string fileName)
    {
        var psi = MtpServerSession.CreateStartInfo(appPath, "/sln", 1234);

        Assert.Equal("/sln", psi.WorkingDirectory);
        Assert.Equal(fileName, psi.FileName);
        Assert.EndsWith("--server --client-host 127.0.0.1 --client-port 1234", psi.Arguments, StringComparison.Ordinal);
    }

    // ── Fake host ─────────────────────────────────────────────────────────────

    private static async Task<(MtpServerSession.Outcome Outcome, string? Error)> ExchangeAsync(
        string method,
        MtpServerSession.StageTimeouts timeouts,
        Func<NetworkStream, CancellationToken, Task> host,
        CancellationToken ct = default)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var server = await listener.AcceptTcpClientAsync();

        using var stopHost = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var hostTask = Task.Run(async () =>
        {
            try
            {
                await host(server.GetStream(), stopHost.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException)
            {
            }
        });

        try
        {
            return await MtpServerSession.ExchangeAsync(client.GetStream(), method, tests: null, _ => { }, timeouts, ct)
                .WaitAsync(TimeSpan.FromSeconds(20));
        }
        finally
        {
            await stopHost.CancelAsync();
            await hostTask;
        }
    }

    private static async Task InitializeAsync(NetworkStream host, CancellationToken ct)
    {
        using var initialize = await ReadAsync(host, ct);
        Assert.Equal("initialize", initialize.RootElement.GetProperty("method").GetString());
        await WriteAsync(host, """{"jsonrpc":"2.0","id":1,"result":{}}""", ct);
    }

    private static async Task<JsonDocument> ReadAsync(NetworkStream stream, CancellationToken ct)
    {
        var header = new StringBuilder();
        var one = new byte[1];
        while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            if (await stream.ReadAsync(one, ct) == 0)
            {
                throw new IOException("Closed.");
            }

            _ = header.Append((char)one[0]);
        }

        var length = int.Parse(header.ToString().Split(':')[1].Trim(), CultureInfo.InvariantCulture);
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, ct);
        return JsonDocument.Parse(body);
    }

    private static async Task WriteAsync(NetworkStream stream, string json, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(json);
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"Content-Length: {body.Length.ToString(CultureInfo.InvariantCulture)}\r\n\r\n"), ct);
        await stream.WriteAsync(body, ct);
    }
}
