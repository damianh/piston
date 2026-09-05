using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Piston.Desktop;

/// <summary>
/// Minimal Win32 notification-area (tray) icon with a right-click context menu.
/// Runs its own hidden window + message loop on a dedicated thread.
/// Trial scope: Windows only; macOS/Linux run without a tray icon.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsTrayIcon : IDisposable
{
    private const int WmApp = 0x8000;
    private const int WmTrayCallback = WmApp + 1;
    private const int WmDestroy = 0x0002;
    private const int WmCommand = 0x0111;
    private const int WmRButtonUp = 0x0205;
    private const int WmLButtonUp = 0x0202;

    private const int CmdOpen = 1;
    private const int CmdAutostart = 2;
    private const int CmdQuit = 3;

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private IntPtr _hwnd;
    private IntPtr _hicon;
    private WndProcDelegate? _wndProc; // rooted so the GC doesn't collect the callback
    private bool _disposed;

    public Action? OnOpen { get; init; }
    public Action? OnQuit { get; init; }
    public Action<bool>? OnAutostartToggled { get; init; }
    public Func<bool>? IsAutostartEnabled { get; init; }

    public WindowsTrayIcon(string tooltip)
    {
        _thread = new Thread(() => Run(tooltip)) { IsBackground = true, Name = "piston-tray" };
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(5));
    }

    private void Run(string tooltip)
    {
        _wndProc = WndProc;
        var hInstance = GetModuleHandle(null);

        var wc = new WndClass
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = hInstance,
            lpszClassName = "PistonTrayWndClass",
        };
        RegisterClass(ref wc);

        _hwnd = CreateWindowEx(0, wc.lpszClassName, "PistonTray", 0, 0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

        // Use the executable's own icon; fall back to the generic application icon.
        _hicon = ExtractIcon(hInstance, Environment.ProcessPath ?? string.Empty, 0);
        if (_hicon == IntPtr.Zero)
            _hicon = LoadIcon(IntPtr.Zero, new IntPtr(32512) /* IDI_APPLICATION */);

        var data = new NotifyIconData
        {
            cbSize = Marshal.SizeOf<NotifyIconData>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = 0x1 | 0x2 | 0x4, // NIF_MESSAGE | NIF_ICON | NIF_TIP
            uCallbackMessage = WmTrayCallback,
            hIcon = _hicon,
            szTip = tooltip,
        };
        Shell_NotifyIcon(0 /* NIM_ADD */, ref data);

        _ready.Set();

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        var removeData = new NotifyIconData
        {
            cbSize = Marshal.SizeOf<NotifyIconData>(),
            hWnd = _hwnd,
            uID = 1,
        };
        Shell_NotifyIcon(2 /* NIM_DELETE */, ref removeData);
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WmTrayCallback:
                var mouseMsg = (int)(lParam.ToInt64() & 0xFFFF);
                if (mouseMsg is WmRButtonUp or WmLButtonUp)
                    ShowMenu(hwnd);
                return IntPtr.Zero;

            case WmCommand:
                switch ((int)(wParam.ToInt64() & 0xFFFF))
                {
                    case CmdOpen:
                        OnOpen?.Invoke();
                        break;
                    case CmdAutostart:
                        OnAutostartToggled?.Invoke(!(IsAutostartEnabled?.Invoke() ?? false));
                        break;
                    case CmdQuit:
                        OnQuit?.Invoke();
                        break;
                }
                return IntPtr.Zero;

            case WmDestroy:
                PostQuitMessage(0);
                return IntPtr.Zero;
        }

        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void ShowMenu(IntPtr hwnd)
    {
        var menu = CreatePopupMenu();
        AppendMenu(menu, 0, CmdOpen, "Open Dashboard");

        var autostartFlags = (IsAutostartEnabled?.Invoke() ?? false) ? 0x8u /* MF_CHECKED */ : 0u;
        AppendMenu(menu, autostartFlags, CmdAutostart, "Start on login");

        AppendMenu(menu, 0x800 /* MF_SEPARATOR */, 0, null);
        AppendMenu(menu, 0, CmdQuit, "Quit Piston");

        GetCursorPos(out var pt);
        SetForegroundWindow(hwnd); // required so the menu dismisses on outside click
        TrackPopupMenu(menu, 0, pt.X, pt.Y, 0, hwnd, IntPtr.Zero);
        DestroyMenu(menu);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_hwnd != IntPtr.Zero)
            PostMessage(_hwnd, WmDestroy, IntPtr.Zero, IntPtr.Zero);

        _thread.Join(TimeSpan.FromSeconds(2));

        if (_hicon != IntPtr.Zero)
            DestroyIcon(_hicon);

        _ready.Dispose();
    }

    // ── P/Invoke ────────────────────────────────────────────────────────────────

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClass
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointStruct { public int X; public int Y; }

    [DllImport("kernel32", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClass(ref WndClass lpWndClass);

    [DllImport("user32", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(int dwExStyle, string lpClassName, string lpWindowName,
        int dwStyle, int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32")]
    private static extern int GetMessage(out Msg lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32")]
    private static extern bool TranslateMessage(ref Msg lpMsg);

    [DllImport("user32")]
    private static extern IntPtr DispatchMessage(ref Msg lpMsg);

    [DllImport("user32")]
    private static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("shell32", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int dwMessage, ref NotifyIconData lpData);

    [DllImport("shell32", CharSet = CharSet.Unicode)]
    private static extern IntPtr ExtractIcon(IntPtr hInst, string lpszExeFileName, int nIconIndex);

    [DllImport("user32")]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, int uIDNewItem, string? lpNewItem);

    [DllImport("user32")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32")]
    private static extern bool TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y,
        int nReserved, IntPtr hWnd, IntPtr prcRect);

    [DllImport("user32")]
    private static extern bool GetCursorPos(out PointStruct lpPoint);

    [DllImport("user32")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
