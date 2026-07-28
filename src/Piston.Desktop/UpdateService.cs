using Velopack;
using Velopack.Sources;

namespace Piston.Desktop;

/// <summary>
/// Background auto-update for trial builds: checks GitHub Releases on launch and
/// every 6 hours, downloads silently, and stages the update to apply on exit.
/// No-op when the app is not running from a Velopack install (e.g. dev builds).
/// </summary>
public sealed class UpdateService : IDisposable
{
    private const string RepoUrl = "https://github.com/damianh/piston";
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    private readonly UpdateManager _manager;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _cts = new();
    private VelopackAsset? _pendingUpdate;

    public UpdateService(Action<string>? log = null)
    {
        _log = log ?? (_ => { });
        _manager = new UpdateManager(new GithubSource(RepoUrl, accessToken: null, prerelease: false));
    }

    /// <summary>True when an update has been downloaded and will apply on exit.</summary>
    public bool UpdatePending => _pendingUpdate is not null;

    public void Start()
    {
        if (!_manager.IsInstalled)
        {
            _log("[update] not a Velopack install; auto-update disabled");
            return;
        }

        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                await CheckOnceAsync();
                try
                {
                    await Task.Delay(CheckInterval, _cts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        });
    }

    private async Task CheckOnceAsync()
    {
        try
        {
            var update = await _manager.CheckForUpdatesAsync();
            if (update is null)
                return;

            _log($"[update] downloading {update.TargetFullRelease.Version}");
            await _manager.DownloadUpdatesAsync(update, null, _cts.Token);
            _pendingUpdate = update.TargetFullRelease;
            _log($"[update] {update.TargetFullRelease.Version} staged; applies on exit");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log($"[update] check failed: {ex.Message}");
        }
    }

    /// <summary>Call during shutdown: applies a staged update after the process exits.</summary>
    public void ApplyPendingOnExit()
    {
        if (_pendingUpdate is null)
            return;

        try
        {
            _manager.WaitExitThenApplyUpdates(_pendingUpdate, silent: true, restart: false);
        }
        catch (Exception ex)
        {
            _log($"[update] failed to stage apply-on-exit: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
