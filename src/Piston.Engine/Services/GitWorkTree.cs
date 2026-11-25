// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Piston.Engine.Services;

/// <summary>Reads git's view of the work tree; every query returns null when git cannot answer.</summary>
internal static class GitWorkTree
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    // Pathspecs are passed on the command line; beyond this the whole directory is listed instead.
    private const int MaxPathspecLength = 8000;

    /// <summary>
    /// Tracked files (present or deleted) and untracked files git does not ignore under
    /// <paramref name="relativeDirectories"/>, as '/'-separated paths relative to <paramref name="root"/>.
    /// Nested repositories and submodules are listed as directories.
    /// </summary>
    public static IReadOnlyList<string>? ListFiles(string root, IReadOnlyList<string> relativeDirectories)
    {
        List<string> args = ["--literal-pathspecs", "ls-files", "-z", "--cached", "--others", "--exclude-standard"];
        if (!relativeDirectories.Contains(".") && relativeDirectories.Sum(d => d.Length + 1) <= MaxPathspecLength)
        {
            args.Add("--");
            args.AddRange(relativeDirectories);
        }

        return Run(root, args, stdin: null) is (0, var output) ? Split(output) : null;
    }

    /// <summary>
    /// Which of <paramref name="relativePaths"/> git ignores. Tracked files are never ignored.
    /// </summary>
    public static IReadOnlySet<string>? FindIgnored(string root, IReadOnlyList<string> relativePaths)
    {
        if (relativePaths.Count == 0)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        // check-ignore matches each path against the ignore rules; it rejects --literal-pathspecs.
        var stdin = string.Join('\0', relativePaths) + "\0";
        return Run(root, ["check-ignore", "-z", "--stdin"], stdin) switch
        {
            (0, var output) => Split(output).ToHashSet(StringComparer.Ordinal),
            (1, _) => new HashSet<string>(StringComparer.Ordinal),
            _ => null,
        };
    }

    private static List<string> Split(string output) =>
        [.. output.Split('\0', StringSplitOptions.RemoveEmptyEntries)];

    private static string Command(IReadOnlyList<string> args) => args.First(arg => !arg.StartsWith('-'));

    private static (int ExitCode, string Output)? Run(string root, IReadOnlyList<string> args, string? stdin)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = root,
            RedirectStandardInput = stdin is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
        };
        if (stdin is not null)
        {
            psi.StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }

        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        _ = ChildProcessEnvironment.Prepare(psi);
        psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        try
        {
            using var process = Process.Start(psi);
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (stdin is not null)
            {
                process.StandardInput.Write(stdin);
                process.StandardInput.Close();
            }

            if (!process.WaitForExit(Timeout))
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                DiagnosticLog.Instance?.Write("Git", $"git {Command(args)} timed out in {root}.");
                return null;
            }

            process.WaitForExit();
            return (process.ExitCode, output.GetAwaiter().GetResult());
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            DiagnosticLog.Instance?.Write("Git", $"git {Command(args)} failed in {root}: {ex.Message}");
            return null;
        }
    }
}
