// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Execution;

/// <summary>
/// Per-user directory shared by every Piston daemon of the user on this machine. It holds worktree
/// owner locks and the test resource queue and leases under a format-versioned subdirectory.
/// It must be on a local filesystem: lock semantics on network filesystems are not relied upon.
/// </summary>
public sealed class ExecutionCoordinationDirectory
{
    /// <summary>Overrides the default location; must be an absolute path.</summary>
    public const string EnvironmentVariable = "PISTON_EXECUTION_DIRECTORY";

    private const string FormatVersion = "v1";

    public ExecutionCoordinationDirectory(string baseDirectory)
    {
        if (!Path.IsPathFullyQualified(baseDirectory))
        {
            throw new ExecutionCoordinationException(
                $"The execution coordination directory must be an absolute path; got '{baseDirectory}'.");
        }

        BaseDirectory = Path.GetFullPath(baseDirectory);
        StateDirectory = Path.Combine(BaseDirectory, FormatVersion);
    }

    public string BaseDirectory { get; }

    internal string LegacyBudgetPath => Path.Combine(BaseDirectory, "budget.json");

    internal string StateDirectory { get; }
    internal string OwnersDirectory => Path.Combine(StateDirectory, "owners");
    internal string ResourceQueueDirectory => Path.Combine(StateDirectory, "resource-queue");
    internal string ResourceLeasesDirectory => Path.Combine(StateDirectory, "resource-leases");
    internal string ResourceLockPath => Path.Combine(StateDirectory, "resources.lock");

    internal void RejectLegacyBudget()
    {
        try
        {
            _ = File.GetAttributes(LegacyBudgetPath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ExecutionCoordinationException(
                $"Cannot inspect obsolete machine budget '{LegacyBudgetPath}': {ex.Message}", ex);
        }

        throw new ExecutionCoordinationException(
            $"Machine-wide execution budgets are no longer supported. Remove '{LegacyBudgetPath}' " +
            "before starting Piston, and restart older Piston daemon and MCP processes.");
    }

    internal static bool DirectoryExistsOrThrow(string path)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }

        if ((attributes & FileAttributes.Directory) == 0)
        {
            throw new IOException($"Execution coordination path '{path}' is not a directory.");
        }

        return true;
    }

    /// <summary>
    /// <see cref="EnvironmentVariable"/> when set, otherwise <c>Piston/execution</c> under the per-user
    /// local application data folder (<c>%LOCALAPPDATA%</c>, <c>$XDG_DATA_HOME</c> or
    /// <c>~/.local/share</c>, <c>~/Library/Application Support</c>).
    /// </summary>
    public static ExecutionCoordinationDirectory ResolveDefault() =>
        ResolveDefault(
            Environment.GetEnvironmentVariable,
            () => Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify));

    internal static ExecutionCoordinationDirectory ResolveDefault(
        Func<string, string?> getEnvironmentVariable, Func<string> getLocalApplicationData)
    {
        var configured = getEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrEmpty(configured))
        {
            if (!Path.IsPathFullyQualified(configured))
            {
                throw new ExecutionCoordinationException(
                    $"{EnvironmentVariable} must be an absolute path; got '{configured}'.");
            }

            return new ExecutionCoordinationDirectory(configured);
        }

        var local = getLocalApplicationData();
        if (string.IsNullOrEmpty(local))
        {
            throw new ExecutionCoordinationException(
                "Cannot determine the per-user local application data folder for machine-wide execution " +
                $"coordination. Set {EnvironmentVariable} to an absolute path.");
        }

        return new ExecutionCoordinationDirectory(Path.Combine(local, "Piston", "execution"));
    }

    /// <summary>
    /// Creates the directories (user-only on Unix) and verifies that exclusive file locks are actually
    /// enforced; for example <c>DOTNET_SYSTEM_IO_DISABLEFILELOCKING</c> disables them on Unix.
    /// </summary>
    public void EnsureReady()
    {
        try
        {
            foreach (var dir in new[]
            {
                BaseDirectory, StateDirectory, OwnersDirectory,
                ResourceQueueDirectory, ResourceLeasesDirectory,
            })
            {
                if (OperatingSystem.IsWindows())
                {
                    _ = Directory.CreateDirectory(dir);
                }
                else
                {
                    _ = Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                    File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
            }
            FileLocking.VerifyEnforced(StateDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ExecutionCoordinationException(
                $"Cannot prepare the execution coordination directory '{BaseDirectory}': {ex.Message}", ex);
        }
    }
}
