// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Runtime.CompilerServices;

namespace Piston.TestProcessHost;

/// <summary>Stands in for a hung test whose method hang diagnostics should find on a thread's stack.</summary>
public static class HangingFixture
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Hang(string readyFile)
    {
        File.WriteAllText(readyFile + ".tmp", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        File.Move(readyFile + ".tmp", readyFile);
        Thread.Sleep(TimeSpan.FromMinutes(2));
    }
}
