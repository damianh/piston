using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Piston.Protocol.JsonRpc;
using Piston.Protocol.Transports;

namespace Piston.Roslyn;

internal sealed class RoslynWorkerProcess : IAsyncDisposable
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonRpcResponse>> _pending = new();
    private readonly Lock _processLock = new();
    private Process? _process;
    private StdioDuplexStream? _stream;
    private Task? _readLoop;
    private CancellationTokenSource? _cts;
    private int _nextId;
    private bool _disposed;

    public bool IsRunning => _process is { HasExited: false };

    public Task EnsureStartedAsync(CancellationToken ct)
    {
        if (IsRunning)
        {
            return Task.CompletedTask;
        }

        lock (_processLock)
        {
            if (IsRunning)
            {
                return Task.CompletedTask;
            }

            _cts?.Cancel();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            var workerPath = FindWorkerExecutable();
            var psi = new ProcessStartInfo
            {
                FileName = workerPath,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            _process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start Roslyn worker process.");

            _stream = new StdioDuplexStream(
                _process.StandardOutput.BaseStream,
                _process.StandardInput.BaseStream);

            _process.Exited += OnProcessExited;
            _process.EnableRaisingEvents = true;

            _readLoop = Task.Run(() => ReadLoopAsync(_cts.Token), _cts.Token);
        }

        return Task.CompletedTask;
    }

    public async Task<JsonNode?> SendRequestAsync(string method, JsonNode? @params, CancellationToken ct)
    {
        if (!IsRunning)
        {
            throw new InvalidOperationException("Roslyn worker process is not running.");
        }

        var id = Interlocked.Increment(ref _nextId).ToString();
        var request = new JsonRpcRequest(id, method, @params);
        var tcs = new TaskCompletionSource<JsonRpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);

        _pending[id] = tcs;

        try
        {
            var bytes = JsonRpcSerializer.Serialize(request);

            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await MessageFramer.WriteMessageAsync(_stream!, bytes, ct).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }

            using var registration = ct.Register(() => tcs.TrySetCanceled(ct));
            var response = await tcs.Task.ConfigureAwait(false);

            if (response.Error is not null)
            {
                throw new InvalidOperationException(
                    $"Roslyn worker error [{response.Error.Code}]: {response.Error.Message}");
            }

            return response.Result;
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _stream is not null)
            {
                var data = await MessageFramer.ReadMessageAsync(_stream, ct).ConfigureAwait(false);
                if (data is null)
                {
                    break; // EOF
                }

                var message = JsonRpcSerializer.DeserializeMessage(data.Value);
                if (message is JsonRpcResponse response && _pending.TryGetValue(response.Id, out var tcs))
                {
                    tcs.TrySetResult(response);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown
        }
        catch (Exception ex)
        {
            FaultAllPending(ex);
        }
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        FaultAllPending(new InvalidOperationException("Roslyn worker process exited unexpectedly."));
    }

    private void FaultAllPending(Exception ex)
    {
        foreach (var kvp in _pending)
        {
            if (_pending.TryRemove(kvp.Key, out var tcs))
            {
                tcs.TrySetException(ex);
            }
        }
    }

    private static string FindWorkerExecutable()
    {
        var assemblyDir = Path.GetDirectoryName(typeof(RoslynWorkerProcess).Assembly.Location)
            ?? throw new InvalidOperationException("Cannot determine assembly directory.");

        var candidates = new[]
        {
            Path.Combine(assemblyDir, "Piston.Roslyn.Worker.exe"),
            Path.Combine(assemblyDir, "Piston.Roslyn.Worker"),
            Path.Combine(assemblyDir, "..", "Piston.Roslyn.Worker", "Piston.Roslyn.Worker.exe"),
            Path.Combine(assemblyDir, "..", "Piston.Roslyn.Worker", "Piston.Roslyn.Worker"),
        };

        return candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException(
                "Cannot find Piston.Roslyn.Worker executable. " +
                $"Searched: {string.Join(", ", candidates)}");
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _cts?.Cancel();
        FaultAllPending(new ObjectDisposedException(nameof(RoslynWorkerProcess)));

        if (_readLoop is not null)
        {
            try
            {
                await _readLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown
            }
        }

        if (_stream is not null)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }

        if (_process is { HasExited: false })
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch
            {
                // best-effort
            }
        }

        _process?.Dispose();
        _cts?.Dispose();
        _writeLock.Dispose();
    }
}
