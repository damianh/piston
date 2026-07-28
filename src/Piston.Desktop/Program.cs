using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Photino.NET;
using Piston.Hosting;
using Piston.Protocol.Transports;
using Velopack;

namespace Piston.Desktop;

public static class Program
{
    private static PhotinoWindow? _window;
    private static WindowsTrayIcon? _tray;
    private static DesktopSettings _settings = new();
    private static DaemonHost? _host;
    private static volatile bool _quitting;

    [STAThread]
    public static void Main(string[] args)
    {
        // Must run first: handles Velopack install/update/uninstall hooks.
        VelopackApp.Build().Run();

        _settings = DesktopSettings.Load();

        var solutionPath = ResolveSolution(args);

        _window = new PhotinoWindow()
            .SetTitle("Piston")
            .SetSize(1280, 860)
            .Center()
            .RegisterWebMessageReceivedHandler(OnWebMessage)
            .RegisterWindowClosingHandler(OnWindowClosing);

        if (OperatingSystem.IsWindows())
        {
            _tray = new WindowsTrayIcon("Piston")
            {
                OnOpen = ShowWindow,
                OnQuit = Quit,
                IsAutostartEnabled = () => _settings.AutostartEnabled,
                OnAutostartToggled = ToggleAutostart,
            };
        }

        if (solutionPath is not null)
            StartDaemonInBackground(solutionPath);
        else
            _window.LoadRawString(PickerHtml);

        _window.WaitForClose();

        if (OperatingSystem.IsWindows())
            _tray?.Dispose();
        StopDaemon();
    }

    // ── Solution resolution ─────────────────────────────────────────────────────

    private static string? ResolveSolution(string[] args)
    {
        if (args.Length > 0 && File.Exists(args[0]) && IsSolutionFile(args[0]))
            return Path.GetFullPath(args[0]);

        if (_settings.LastSolutionPath is { } saved && File.Exists(saved))
            return saved;

        var cwd = Directory.GetCurrentDirectory();
        var candidates = Directory.GetFiles(cwd, "*.sln")
            .Concat(Directory.GetFiles(cwd, "*.slnx"))
            .Concat(Directory.GetFiles(cwd, "*.slnf"))
            .ToList();

        return candidates.Count == 1 ? candidates[0] : null;
    }

    private static bool IsSolutionFile(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".sln" or ".slnx" or ".slnf";

    // ── Daemon lifecycle ────────────────────────────────────────────────────────

    private static void StartDaemonInBackground(string solutionPath)
    {
        _window!.Load(new Uri("data:text/html," + Uri.EscapeDataString(LoadingHtml)));

        _ = Task.Run(async () =>
        {
            try
            {
                var host = await StartDaemonAsync(solutionPath);
                _settings.LastSolutionPath = solutionPath;
                _settings.Save();
                _window!.Invoke(() => _window.Load(new Uri(host.WebUrl)));
            }
            catch (Exception ex)
            {
                _window!.Invoke(() =>
                {
                    _window.ShowMessage("Piston",
                        $"Failed to start Piston for:\n{solutionPath}\n\n{ex.Message}",
                        PhotinoDialogButtons.Ok, PhotinoDialogIcon.Error);
                    _window.LoadRawString(PickerHtml);
                });
            }
        });
    }

    private static async Task<DaemonHost> StartDaemonAsync(string solutionPath)
    {
        var solutionDir = Path.GetDirectoryName(solutionPath)!;
        var config = HostHelpers.LoadConfig(solutionDir);
        var engineOptions = HostHelpers.BuildOptions(
            solutionPath, cliDebounceMs: 0, cliFilter: null,
            cliCoverage: false, cliParallelism: 0, config);

        var webPort = _settings.WebPort > 0 ? _settings.WebPort : 5199;
        if (!IsPortFree(webPort))
            webPort = GetFreePort();

        var host = new DaemonHost(new DaemonHostOptions
        {
            SolutionPath  = solutionPath,
            EngineOptions = engineOptions,
            PipeName      = config.PipeName ?? NamedPipeListener.GeneratePipeName(solutionPath),
            WebPort       = webPort,
            McpPort       = config.McpPort,
        });

        await host.StartAsync(CancellationToken.None);
        _host = host;
        return host;
    }

    private static void StopDaemon()
    {
        var host = Interlocked.Exchange(ref _host, null);
        if (host is null) return;
        try
        {
            host.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch
        {
            // Best-effort shutdown on exit.
        }
    }

    private static void SwitchSolution(string solutionPath)
    {
        StopDaemon();
        StartDaemonInBackground(solutionPath);
    }

    private static bool IsPortFree(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    // ── Window / tray behavior ──────────────────────────────────────────────────

    private static bool OnWindowClosing(object sender, EventArgs e)
    {
        // Windows: closing the window hides to tray; Quit is in the tray menu.
        if (OperatingSystem.IsWindows() && !_quitting && _tray is not null)
        {
            HideWindow();
            return true; // cancel close
        }

        return false;
    }

    private static void ShowWindow()
    {
        if (_window is null || !OperatingSystem.IsWindows()) return;
        var hwnd = _window.WindowHandle;
        ShowWindowNative(hwnd, 9 /* SW_RESTORE */);
        SetForegroundWindow(hwnd);
    }

    private static void HideWindow()
    {
        if (_window is null || !OperatingSystem.IsWindows()) return;
        ShowWindowNative(_window.WindowHandle, 0 /* SW_HIDE */);
    }

    private static void Quit()
    {
        _quitting = true;
        _window?.Invoke(() => _window.Close());
    }

    private static void ToggleAutostart(bool enabled)
    {
        try
        {
            AutostartManager.SetEnabled(enabled);
            _settings.AutostartEnabled = enabled;
            _settings.Save();
        }
        catch
        {
            // Registry/file access failure: leave setting unchanged.
        }
    }

    // ── First-run solution picker ───────────────────────────────────────────────

    private static void OnWebMessage(object? sender, string message)
    {
        if (message != "pick-solution") return;

        _window!.Invoke(() =>
        {
            var picked = _window.ShowOpenFile(
                "Choose a solution",
                defaultPath: Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                multiSelect: false,
                filters: [("Solution files", ["*.sln", "*.slnx", "*.slnf"])]);

            if (picked is { Length: > 0 } && IsSolutionFile(picked[0]))
                SwitchSolution(picked[0]);
        });
    }

    private const string SharedStyles = """
        <style>
          body { font-family: -apple-system, 'Segoe UI', Ubuntu, sans-serif;
                 background: #1b1e24; color: #d5d9e0; display: flex; flex-direction: column;
                 align-items: center; justify-content: center; height: 100vh; margin: 0; }
          h1 { font-weight: 600; font-size: 1.6rem; margin-bottom: 0.5rem; }
          p  { color: #8b93a1; margin-top: 0; }
          button { background: #3b82f6; color: white; border: none; border-radius: 6px;
                   padding: 0.7rem 1.6rem; font-size: 1rem; cursor: pointer; margin-top: 1.5rem; }
          button:hover { background: #2563eb; }
        </style>
        """;

    private const string PickerHtml = $"""
        <!DOCTYPE html>
        <html><head><meta charset="utf-8">{SharedStyles}</head>
        <body>
          <h1>Piston</h1>
          <p>Continuous testing for .NET. Choose a solution to start watching.</p>
          <button onclick="window.external.sendMessage('pick-solution')">Open Solution&hellip;</button>
        </body></html>
        """;

    private const string LoadingHtml = $"""
        <!DOCTYPE html>
        <html><head><meta charset="utf-8">{SharedStyles}</head>
        <body>
          <h1>Piston</h1>
          <p>Starting engine and loading solution&hellip;</p>
        </body></html>
        """;

    [DllImport("user32", EntryPoint = "ShowWindow")]
    private static extern bool ShowWindowNative(IntPtr hWnd, int nCmdShow);

    [DllImport("user32")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
