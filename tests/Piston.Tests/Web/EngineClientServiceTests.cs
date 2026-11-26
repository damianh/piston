// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Piston.Protocol.Dtos;
using Piston.Protocol.JsonRpc;
using Piston.Web.Services;
using Xunit;

namespace Piston.Tests.Web;

public sealed class EngineClientServiceTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task GetStoredRunPair_ReceivesTwoLargeRuns()
    {
        var error = new string('x', 2048);
        var results = Enumerable.Range(0, 4200)
            .Select(i => new StoredTestResultDto("Tests", $"Test{i}", "Failed", 1, error, null))
            .ToArray();
        StoredRunDto Run(string id) => new(
            new StoredRunSummaryDto(id, "Forced", "Completed", "Full", true, "Current",
                DateTimeOffset.UnixEpoch, null, null, 0, results.Length, 0), results);
        var run = Run("r2");
        var pair = new StoredRunPairDto(run, Run("r1"));
        var singleBytes = JsonSerializer.SerializeToUtf8Bytes(run, JsonRpcSerializer.Options);
        var pairNode = JsonSerializer.SerializeToNode(pair, JsonRpcSerializer.Options);
        var pairBytes = JsonRpcSerializer.Serialize(new JsonRpcResponse("1", pairNode));
        Assert.True(singleBytes.Length < 16 * 1024 * 1024);
        Assert.True(pairBytes.Length > 16 * 1024 * 1024);

        var builder = WebApplication.CreateSlimBuilder();
        _ = builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        _ = app.UseWebSockets();
        _ = app.Map("/ws", async context =>
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var buffer = new byte[4096];
            while (socket.State == WebSocketState.Open && !context.RequestAborted.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                ValueWebSocketReceiveResult received;
                do
                {
                    received = await socket.ReceiveAsync(buffer.AsMemory(), context.RequestAborted);
                    if (received.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }

                    await message.WriteAsync(buffer.AsMemory(0, received.Count), context.RequestAborted);
                }
                while (!received.EndOfMessage);

                var request = (JsonRpcRequest)JsonRpcSerializer.DeserializeMessage(message.ToArray());
                var response = new JsonRpcResponse(request.Id,
                    request.Method == ProtocolMethods.HistoryGetRunPair ? pairNode : null);
                await socket.SendAsync(JsonRpcSerializer.Serialize(response), WebSocketMessageType.Text, true, context.RequestAborted);
            }
        });
        await app.StartAsync(_ct);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        await using var client = new EngineClientService();
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionStateChanged += () =>
        {
            if (client.IsConnected)
            {
                _ = connected.TrySetResult();
            }
        };
        await client.StartAsync(address.Replace("http://", "ws://", StringComparison.Ordinal) + "/ws");
        await connected.Task.WaitAsync(_ct);

        var loaded = await client.GetStoredRunPairAsync("r2", "r1");

        Assert.Equal("r2", loaded?.Run?.Summary.RunId);
        Assert.Equal("r1", loaded?.Baseline?.Summary.RunId);
        Assert.Equal(results.Length, loaded?.Run?.Results.Count);
        Assert.Equal(results.Length, loaded?.Baseline?.Results.Count);
    }
}
