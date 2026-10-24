// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Runtime.CompilerServices;

namespace Piston.Engine.Tests;

internal static class HistoryIsolation
{
    // Engines and daemons started by tests (and their child processes) keep run history in a temp directory,
    // never in the user's Piston history.
    [ModuleInitializer]
    internal static void Initialize()
    {
        var directory = Path.Combine(Path.GetTempPath(), "piston-test-history", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Environment.SetEnvironmentVariable("PISTON_HISTORY_DIRECTORY", directory);
    }
}
