// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Diagnostics;

namespace Piston.Engine.Tests.Services;

internal static class GitTestRepository
{
    /// <summary>Makes <paramref name="directory"/> a git work tree that ignores <paramref name="ignored"/>.</summary>
    public static void Init(string directory, params string[] ignored)
    {
        var psi = new ProcessStartInfo("git", ["init", "-q"])
        {
            WorkingDirectory = directory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(psi)!;
        process.StandardInput.Close();
        _ = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(TimeSpan.FromSeconds(30)) || process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git init failed: {error}");
        }

        File.WriteAllLines(Path.Combine(directory, ".gitignore"), ignored);
    }
}
