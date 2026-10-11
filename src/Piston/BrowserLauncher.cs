// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Piston.Cli;

/// <summary>
/// Cross-platform utility to open a URL in the default browser.
/// </summary>
internal static class BrowserLauncher
{
    public static void Open(string url)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                _ = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                _ = Process.Start("open", url);
            }
            else
            {
                _ = Process.Start("xdg-open", url);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[piston] Could not open browser: {ex.Message}");
        }
    }
}
