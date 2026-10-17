// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Diagnostics;
using System.Text.Json;
using Piston.Protocol.Dtos;
using Piston.Protocol.JsonRpc;
using Piston.Protocol.Messages;
using Piston.Protocol.Transports;
using Xunit;

namespace Piston.Tests;

public sealed class StatusCommandTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task MetadataUnavailable_PrintsAvailableStatusWithinTimeout(bool sendSnapshot, bool disconnect)
    {
        var root = Path.Combine(Path.GetTempPath(), $"piston-status-{Guid.NewGuid():N}");
        _ = Directory.CreateDirectory(root);
        var solution = Path.Combine(root, "App.slnx");
        await File.WriteAllTextAsync(solution, "<Solution />", _ct);
        var pipeName = $"piston-status-{Guid.NewGuid():N}";
        await using var server = new NamedPipeServerTransport(pipeName);
        var accept = server.AcceptClientAsync(_ct);
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Piston.dll"));
        startInfo.ArgumentList.Add("status");
        startInfo.ArgumentList.Add(solution);
        startInfo.ArgumentList.Add("--pipe-name");
        startInfo.ArgumentList.Add(pipeName);

        using var process = new Process { StartInfo = startInfo };
        var started = false;
        try
        {
            started = process.Start();
            Assert.True(started);
            var stdout = process.StandardOutput.ReadToEndAsync(_ct);
            var stderr = process.StandardError.ReadToEndAsync(_ct);
            var stream = await accept.WaitAsync(TimeSpan.FromSeconds(10), _ct);
            if (sendSnapshot)
            {
                var snapshot = new StateSnapshotNotification(
                    PistonPhaseDto.Idle, [], [], null, null, null, null, null, 0, 0, 0, null, solution,
                    null, null, null, false, false, null, 0, 0, null);
                var notification = new JsonRpcNotification(ProtocolMethods.EngineStateSnapshot,
                    JsonSerializer.SerializeToNode(snapshot, JsonRpcSerializer.Options));
                await MessageFramer.WriteMessageAsync(stream, JsonRpcSerializer.Serialize(notification), _ct);
            }

            var requestBytes = await MessageFramer.ReadMessageAsync(stream, _ct)
                .WaitAsync(TimeSpan.FromSeconds(10), _ct);
            var request = Assert.IsType<JsonRpcRequest>(
                JsonRpcSerializer.DeserializeMessage(Assert.NotNull(requestBytes)));
            Assert.Equal(ProtocolMethods.DaemonInfo, request.Method);
            if (disconnect)
            {
                await stream.DisposeAsync();
            }

            await process.WaitForExitAsync(_ct).WaitAsync(TimeSpan.FromSeconds(10), _ct);
            var output = await stdout;
            var error = await stderr;
            Assert.Equal(0, process.ExitCode);
            Assert.Contains($"Solution : {solution}", output);
            Assert.Contains($"Pipe     : {pipeName}", output);
            Assert.Contains(sendSnapshot ? "Phase    : Idle" : "no state available yet", output);
            Assert.Contains("Could not retrieve daemon metadata", error);
        }
        finally
        {
            if (started && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }

            Directory.Delete(root, recursive: true);
        }
    }
}
