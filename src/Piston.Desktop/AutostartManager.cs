using System.Runtime.InteropServices;

namespace Piston.Desktop;

/// <summary>
/// Registers/unregisters the desktop app to start on login.
/// Windows: HKCU Run key. macOS: LaunchAgent plist. Linux: XDG autostart .desktop entry.
/// </summary>
public static class AutostartManager
{
    private const string AppName = "Piston";

    public static bool IsSupported =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsLinux();

    public static void SetEnabled(bool enabled)
    {
        var exePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot determine the executable path.");

        if (OperatingSystem.IsWindows())
            SetWindows(enabled, exePath);
        else if (OperatingSystem.IsMacOS())
            SetMacOs(enabled, exePath);
        else if (OperatingSystem.IsLinux())
            SetLinux(enabled, exePath);
        else
            throw new PlatformNotSupportedException();
    }

    private static void SetWindows(bool enabled, string exePath)
    {
        if (!OperatingSystem.IsWindows()) return;

        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);

        if (enabled)
            key.SetValue(AppName, $"\"{exePath}\"");
        else
            key.DeleteValue(AppName, throwOnMissingValue: false);
    }

    private static void SetMacOs(bool enabled, string exePath)
    {
        var agentsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents");
        var plistPath = Path.Combine(agentsDir, "net.piston.desktop.plist");

        if (!enabled)
        {
            if (File.Exists(plistPath)) File.Delete(plistPath);
            return;
        }

        Directory.CreateDirectory(agentsDir);
        File.WriteAllText(plistPath, $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>Label</key>
                <string>net.piston.desktop</string>
                <key>ProgramArguments</key>
                <array>
                    <string>{exePath}</string>
                </array>
                <key>RunAtLoad</key>
                <true/>
            </dict>
            </plist>
            """);
    }

    private static void SetLinux(bool enabled, string exePath)
    {
        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrEmpty(configHome))
            configHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");

        var autostartDir = Path.Combine(configHome, "autostart");
        var desktopPath = Path.Combine(autostartDir, "piston.desktop");

        if (!enabled)
        {
            if (File.Exists(desktopPath)) File.Delete(desktopPath);
            return;
        }

        Directory.CreateDirectory(autostartDir);
        File.WriteAllText(desktopPath, $"""
            [Desktop Entry]
            Type=Application
            Name=Piston
            Comment=Continuous test runner for .NET
            Exec="{exePath}"
            X-GNOME-Autostart-enabled=true
            """);
    }
}
