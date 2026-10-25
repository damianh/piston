// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Piston.Protocol.Dtos;
using Piston.Protocol.JsonRpc;
using Piston.Protocol.Messages;

namespace Piston.Web.Services;

/// <summary>
/// Connects to the Piston daemon via WebSocket JSON-RPC and dispatches
/// engine state notifications and activity events to subscribers.
/// </summary>
public sealed class EngineClientService : IAsyncDisposable
{
    private const int ActivityLogCapacity = 500;

    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _cts;

    private readonly List<ActivityEvent> _activityLog = [];
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonRpcResponse?>> _pendingRequests = new();

    // ClientWebSocket allows only one outstanding SendAsync at a time.
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    public event Action<StateSnapshotNotification>? StateChanged;
    public event Action? ConnectionStateChanged;
    public event Action<ActivityEvent>? ActivityEventReceived;

    /// <summary>Raised when <see cref="ActivityLog"/> is replaced wholesale (history backfill on connect).</summary>
    public event Action? ActivityLogReset;

    /// <summary>Raised when machine-wide user settings are fetched (on every connect) or saved.</summary>
    public event Action? SettingsChanged;

    public StateSnapshotNotification? CurrentSnapshot { get; private set; }

    /// <summary>Machine-wide user settings as last read from or written to the daemon.</summary>
    public UserSettings Settings { get; private set; } = new();

    /// <summary>The daemon's settings file; null until loaded or when the daemon has no settings store.</summary>
    public string? SettingsFilePath { get; private set; }
    public bool IsConnected => _webSocket?.State == WebSocketState.Open;
    public bool IsConnecting { get; private set; }

    public IReadOnlyList<ActivityEvent> ActivityLog
    {
        get
        {
            lock (_activityLog)
            {
                return _activityLog.ToList();
            }
        }
    }

    private int _requestCounter;

    public async Task StartAsync(string wsUrl)
    {
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => ConnectLoopAsync(wsUrl, _cts.Token));
        await Task.CompletedTask;
    }

    private async Task ConnectLoopAsync(string wsUrl, CancellationToken ct)
    {
        var backoffMs = 500;
        const int MaxBackoffMs = 10_000;

        while (!ct.IsCancellationRequested)
        {
            IsConnecting = true;
            ConnectionStateChanged?.Invoke();

            _webSocket?.Dispose();
            _webSocket = new ClientWebSocket();

            try
            {
                await _webSocket.ConnectAsync(new Uri(wsUrl), ct).ConfigureAwait(false);
                IsConnecting = false;
                backoffMs = 500;
                ConnectionStateChanged?.Invoke();

                _ = BackfillActivityLogAsync();
                _ = LoadSettingsAsync();
                await ReceiveLoopAsync(_webSocket, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // Connection failed — retry with backoff
            }

            IsConnecting = false;
            ConnectionStateChanged?.Invoke();

            try
            {
                await Task.Delay(backoffMs, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            backoffMs = Math.Min(backoffMs * 2, MaxBackoffMs);
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        const int InitialBufferSize = 64 * 1024;
        const int MaxMessageSize = 16 * 1024 * 1024;
        var buffer = new byte[InitialBufferSize];

        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            var totalBytes = 0;
            WebSocketReceiveResult? result = null;

            do
            {
                if (totalBytes >= buffer.Length)
                {
                    if (buffer.Length >= MaxMessageSize)
                    {
                        break; // discard oversized message
                    }

                    var newSize = Math.Min(buffer.Length * 2, MaxMessageSize);
                    Array.Resize(ref buffer, newSize);
                }

                var segment = new ArraySegment<byte>(buffer, totalBytes, buffer.Length - totalBytes);
                result = await ws.ReceiveAsync(segment, ct).ConfigureAwait(false);
                totalBytes += result.Count;
            }
            while (!result.EndOfMessage);

            if (result is null || totalBytes >= MaxMessageSize)
            {
                continue; // skip oversized or empty messages
            }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                break;
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                continue;
            }

            var raw = new ReadOnlyMemory<byte>(buffer, 0, totalBytes);
            HandleMessage(raw);
        }
    }

    private void HandleMessage(ReadOnlyMemory<byte> raw)
    {
        try
        {
            var msg = JsonRpcSerializer.DeserializeMessage(raw);

            if (msg is JsonRpcResponse response)
            {
                HandleResponse(response);
                return;
            }

            if (msg is not JsonRpcNotification notification)
            {
                return;
            }

            HandleNotification(notification);
        }
        catch
        {
            // Ignore malformed messages
        }
    }

    private void HandleResponse(JsonRpcResponse response)
    {
        if (_pendingRequests.TryRemove(response.Id, out var tcs))
        {
            _ = tcs.TrySetResult(response);
        }
    }

    private void HandleNotification(JsonRpcNotification notification)
    {
        if (notification.Method == ProtocolMethods.EngineStateSnapshot)
        {
            var snapshot = notification.Params?.Deserialize<StateSnapshotNotification>(JsonRpcSerializer.Options);
            if (snapshot is not null)
            {
                CurrentSnapshot = snapshot;
                StateChanged?.Invoke(snapshot);
            }
            return;
        }

        if (notification.Method == ProtocolMethods.ActivityEvent)
        {
            var evt = notification.Params?.Deserialize<ActivityEvent>(JsonRpcSerializer.Options);
            if (evt is not null)
            {
                AppendActivityEvent(evt);
                ActivityEventReceived?.Invoke(evt);
            }
        }
    }

    private void AppendActivityEvent(ActivityEvent evt)
    {
        lock (_activityLog)
        {
            if (evt.Id is not null && _activityLog.Exists(e => e.Id == evt.Id))
            {
                return;
            }

            if (_activityLog.Count >= ActivityLogCapacity)
            {
                _activityLog.RemoveAt(0);
            }

            _activityLog.Add(evt);
        }
    }

    public Task SendCommandAsync(string method) => SendCommandAsync(method, (object?)null);

    public async Task SendCommandAsync(string method, object? @params)
    {
        if (_webSocket?.State != WebSocketState.Open)
        {
            return;
        }

        var id = Interlocked.Increment(ref _requestCounter).ToString();
        var paramsNode = @params is null
            ? null
            : JsonNode.Parse(JsonSerializer.Serialize(@params, JsonRpcSerializer.Options));

        var request = new JsonRpcRequest(id, method, paramsNode);
        var bytes = JsonRpcSerializer.Serialize(request);

        try
        {
            await SendSerializedAsync(_webSocket, bytes).ConfigureAwait(false);
        }
        catch
        {
            // Connection lost — reconnect loop will handle it
        }
    }

    private async Task SendSerializedAsync(ClientWebSocket ws, ReadOnlyMemory<byte> bytes)
    {
        var ct = _cts?.Token ?? CancellationToken.None;
        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
        }
        finally
        {
            _ = _sendGate.Release();
        }
    }

    public Task<JsonNode?> SendRequestAsync(string method) => SendRequestAsync(method, null);

    public async Task<JsonNode?> SendRequestAsync(string method, object? @params) =>
        (await SendForResponseAsync(method, @params).ConfigureAwait(false))?.Result;

    public async Task<JsonNode?> SendRequestAsync(
        string method, object? @params, CancellationToken cancellationToken) =>
        (await SendForResponseAsync(method, @params, cancellationToken).ConfigureAwait(false))?.Result;

    /// <summary>
    /// Sends a request and returns its response, including any error, or <see langword="null"/> when the
    /// daemon is unreachable or does not reply in time.
    /// </summary>
    private async Task<JsonRpcResponse?> SendForResponseAsync(
        string method, object? @params, CancellationToken cancellationToken = default)
    {
        if (_webSocket?.State != WebSocketState.Open)
        {
            return null;
        }

        var id = Interlocked.Increment(ref _requestCounter).ToString();
        var paramsNode = @params is null
            ? null
            : JsonNode.Parse(JsonSerializer.Serialize(@params, JsonRpcSerializer.Options));

        var request = new JsonRpcRequest(id, method, paramsNode);
        var bytes = JsonRpcSerializer.Serialize(request);

        var tcs = new TaskCompletionSource<JsonRpcResponse?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequests[id] = tcs;

        try
        {
            await SendSerializedAsync(_webSocket, bytes).ConfigureAwait(false);
        }
        catch
        {
            _ = _pendingRequests.TryRemove(id, out _);
            return null;
        }

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var timeoutRegistration = timeoutCts.Token.Register(() => tcs.TrySetResult(null));
        try
        {
            return await tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _ = _pendingRequests.TryRemove(id, out _);
            throw;
        }
    }

    /// <summary>
    /// Starts a run of the given tests. Returns <see langword="null"/> once the run is requested, or why it
    /// could not start (for example, another run is in progress).
    /// </summary>
    public async Task<string?> RunTestsAsync(RunTestsCommand command)
    {
        var response = await SendForResponseAsync(ProtocolMethods.EngineRunTests, command).ConfigureAwait(false);
        return response switch
        {
            null => "Piston did not respond. Check that the daemon is running.",
            { Error: { } error } => error.Message,
            _ => null,
        };
    }

    public async Task<ActivityLogResponse?> GetActivityLogAsync()
    {
        var result = await SendRequestAsync(ProtocolMethods.ActivityGetLog).ConfigureAwait(false);
        return result?.Deserialize<ActivityLogResponse>(JsonRpcSerializer.Options);
    }

    /// <summary>
    /// The log of a test project's latest run, or <see langword="null"/> when none was recorded or the
    /// daemon is unreachable.
    /// </summary>
    public async Task<ExecutionLogDto?> GetRunLogAsync(string projectId)
    {
        var result = await SendRequestAsync(ProtocolMethods.TestsGetRunLog, new GetRunLogCommand(projectId))
            .ConfigureAwait(false);
        return result?.Deserialize<RunLogResponse>(JsonRpcSerializer.Options)?.Log;
    }

    /// <summary>The solution-wide coverage report, or <see langword="null"/> when the daemon is unreachable.</summary>
    public async Task<CoverageSummaryDto?> GetCoverageSummaryAsync()
    {
        var result = await SendRequestAsync(ProtocolMethods.CoverageGetSummary).ConfigureAwait(false);
        return result?.Deserialize<CoverageSummaryDto>(JsonRpcSerializer.Options);
    }

    /// <summary>One file's line coverage, with its source text when <paramref name="includeSource"/> is set.</summary>
    public async Task<FileCoverageDto?> GetFileCoverageAsync(string filePath, bool includeSource)
    {
        var result = await SendRequestAsync(ProtocolMethods.CoverageGetForFile, new GetFileCoverageCommand(filePath, includeSource))
            .ConfigureAwait(false);
        return result?.Deserialize<FileCoverageDto>(JsonRpcSerializer.Options);
    }

    /// <summary>A source file of the watched solution, e.g. one a stack frame names; null when the daemon is unreachable.</summary>
    public async Task<SourceFileDto?> GetSourceAsync(string filePath)
    {
        var result = await SendRequestAsync(ProtocolMethods.SourceGet, new GetSourceCommand(filePath)).ConfigureAwait(false);
        return result?.Deserialize<SourceFileDto>(JsonRpcSerializer.Options);
    }

    /// <summary>The runs kept in the run history, newest first; null when the daemon is unreachable.</summary>
    public async Task<StoredRunsDto?> GetStoredRunsAsync()
    {
        var result = await SendRequestAsync(ProtocolMethods.HistoryGetRuns).ConfigureAwait(false);
        return result?.Deserialize<StoredRunsDto>(JsonRpcSerializer.Options);
    }

    /// <summary>The terminal runs of the engine's lifetime, oldest first; null when the daemon is unreachable.</summary>
    public async Task<SessionTimelineDto?> GetSessionTimelineAsync()
    {
        var result = await SendRequestAsync(ProtocolMethods.SessionGetTimeline).ConfigureAwait(false);
        return result?.Deserialize<SessionTimelineDto>(JsonRpcSerializer.Options);
    }

    /// <summary>One stored run with its per-test results; null when it is not kept or the daemon is unreachable.</summary>
    public async Task<StoredRunDto?> GetStoredRunAsync(string runId)
    {
        var result = await SendRequestAsync(ProtocolMethods.HistoryGetRun, new GetStoredRunCommand(runId)).ConfigureAwait(false);
        return result?.Deserialize<StoredRunDto>(JsonRpcSerializer.Options);
    }

    /// <summary>Failure kinds of failing tests and the flaky tests; null when the daemon is unreachable.</summary>
    public async Task<TestMarksDto?> GetTestMarksAsync()
    {
        var result = await SendRequestAsync(ProtocolMethods.HistoryGetTestMarks).ConfigureAwait(false);
        return result?.Deserialize<TestMarksDto>(JsonRpcSerializer.Options);
    }

    /// <summary>Recorded results of a test; null when the daemon is unreachable.</summary>
    public async Task<TestHistoriesDto?> GetTestHistoryAsync(string test, string? projectId)
    {
        var result = await SendRequestAsync(ProtocolMethods.HistoryGetTestHistory, new GetTestHistoryCommand(test, projectId)).ConfigureAwait(false);
        return result?.Deserialize<TestHistoriesDto>(JsonRpcSerializer.Options);
    }

    public async Task<CoveringTestsDto?> GetCoveringTestsAsync(string filePath)
    {
        var result = await SendRequestAsync(ProtocolMethods.CoverageGetCoveringTests, new GetCoveringTestsCommand(filePath))
            .ConfigureAwait(false);
        return result?.Deserialize<CoveringTestsDto>(JsonRpcSerializer.Options);
    }

    /// <summary>
    /// Merges the daemon's retained activity history into the local log so events that
    /// happened before this page connected (e.g. agent MCP calls) appear in the timeline.
    /// </summary>
    private async Task BackfillActivityLogAsync()
    {
        IReadOnlyList<ActivityEvent>? history;
        try
        {
            history = (await GetActivityLogAsync().ConfigureAwait(false))?.Events;
        }
        catch
        {
            return;
        }

        if (history is null || history.Count == 0)
        {
            return;
        }

        lock (_activityLog)
        {
            var merged = ActivityLogMerger.Merge(history, _activityLog, ActivityLogCapacity);
            _activityLog.Clear();
            _activityLog.AddRange(merged);
        }

        ActivityLogReset?.Invoke();
    }

    private async Task LoadSettingsAsync()
    {
        UserSettingsResponse? response;
        try
        {
            var result = await SendRequestAsync(ProtocolMethods.SettingsGet).ConfigureAwait(false);
            response = result?.Deserialize<UserSettingsResponse>(JsonRpcSerializer.Options);
        }
        catch
        {
            return;
        }

        if (response is not null)
        {
            ApplySettings(response);
        }
    }

    /// <summary>
    /// Saves the non-null fields of <paramref name="patch"/> to the machine-wide user settings.
    /// Returns false when the daemon is unreachable or could not save them.
    /// </summary>
    public async Task<bool> UpdateSettingsAsync(UserSettings patch)
    {
        UserSettingsResponse? response;
        try
        {
            var result = await SendRequestAsync(ProtocolMethods.SettingsUpdate, patch).ConfigureAwait(false);
            response = result?.Deserialize<UserSettingsResponse>(JsonRpcSerializer.Options);
        }
        catch
        {
            return false;
        }

        if (response is null)
        {
            return false;
        }

        ApplySettings(response);
        return true;
    }

    private void ApplySettings(UserSettingsResponse response)
    {
        Settings = response.Settings;
        SettingsFilePath = response.FilePath;
        SettingsChanged?.Invoke();
    }

    public async Task<MachineOverviewDto?> GetMachineOverviewAsync(CancellationToken cancellationToken = default)
    {
        var result = await SendRequestAsync(ProtocolMethods.MachineGetOverview, null, cancellationToken).ConfigureAwait(false);
        return result?.Deserialize<MachineOverviewDto>(JsonRpcSerializer.Options);
    }

    public Task ResetAsync() => SendCommandAsync(ProtocolMethods.EngineReset);
    public Task SetFilterAsync(string? filter) => SendCommandAsync(ProtocolMethods.EngineSetFilter, new SetFilterCommand(filter));

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync();
            _cts.Dispose();
        }

        _webSocket?.Dispose();
        _sendGate.Dispose();
    }
}
