using Piston.Protocol;
using Piston.Tui.Views;
using SharpConsoleUI;
using SharpConsoleUI.Drivers;

namespace Piston.Tui;

/// <summary>
/// Entry point for the Piston TUI layer. Receives an <see cref="IEngineClient"/>
/// from the controller and runs the console window system.
/// </summary>
public static class PistonTui
{
    /// <summary>
    /// Creates the console window system, wires the engine client, and runs
    /// the interactive UI until the user quits.
    /// </summary>
    public static void Run(IEngineClient client)
    {
        var driver = new NetConsoleDriver(RenderMode.Buffer);
        var windowSystem = new ConsoleWindowSystem(driver);

        // Route Ctrl+C through the TUI's own shutdown path so the driver can
        // run its full terminal-cleanup sequence (disabling mouse tracking etc.)
        // rather than relying on the emergency AppDomain.ProcessExit handler.
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;         // prevent abrupt process kill
            windowSystem.Shutdown(); // triggers normal driver.Stop() cleanup
        };

        // Switch to the alternate screen buffer so the TUI doesn't mix with the
        // shell's scroll-back history. Restore it on both normal and abrupt exit.
        Console.Write("\x1b[?1049h"); // enter alternate screen
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Console.Write("\x1b[?1049l");

        PistonWindow.Create(windowSystem, client);

        windowSystem.Run();

        Console.Write("\x1b[?1049l"); // leave alternate screen
    }
}
