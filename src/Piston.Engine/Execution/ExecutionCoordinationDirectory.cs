// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Execution;

/// <summary>
/// Per-user directory shared by every Piston daemon of the user on this machine. It holds the
/// machine budget (<c>budget.json</c>) and, under a format-versioned subdirectory, worktree owner
/// locks, the admission queue and admitted leases. It must be on a local filesystem: lock semantics
/// on network filesystems are not relied upon.
/// </summary>
public sealed class ExecutionCoordinationDirectory
{
    /// <summary>Overrides the default location; must be an absolute path.</summary>
    public const string EnvironmentVariable = "PISTON_EXECUTION_DIRECTORY";

    private const string FormatVersion = "v1";

    public ExecutionCoordinationDirectory(string baseDirectory)
    {
        if (!Path.IsPathFullyQualified(baseDirectory))
            throw new ExecutionCoordinationException(
                $"The execution coordination directory must be an absolute path; got '{baseDirectory}'.");
        BaseDirectory = Path.GetFullPath(baseDirectory);
        StateDirectory = Path.Combine(BaseDirectory, FormatVersion);
    }

    public string BaseDirectory { get; }

    /// <summary>Optional machine budget. Absent means the default budget.</summary>
    public string BudgetPath => Path.Combine(BaseDirectory, "budget.json");

    internal string StateDirectory { get; }
    internal string OwnersDirectory => Path.Combine(StateDirectory, "owners");
    internal string QueueDirectory => Path.Combine(StateDirectory, "queue");
    internal string LeasesDirectory => Path.Combine(StateDirectory, "leases");
    internal string AdmissionLockPath => Path.Combine(StateDirectory, "admission.lock");

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
                throw new ExecutionCoordinationException(
                    $"{EnvironmentVariable} must be an absolute path; got '{configured}'.");
            return new ExecutionCoordinationDirectory(configured);
        }

        var local = getLocalApplicationData();
        if (string.IsNullOrEmpty(local))
            throw new ExecutionCoordinationException(
                "Cannot determine the per-user local application data folder for machine-wide execution " +
                $"coordination. Set {EnvironmentVariable} to an absolute path.");
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
            foreach (var dir in new[] { BaseDirectory, StateDirectory, OwnersDirectory, QueueDirectory, LeasesDirectory })
            {
                if (OperatingSystem.IsWindows())
                    Directory.CreateDirectory(dir);
                else
                    Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ExecutionCoordinationException(
                $"Cannot create the execution coordination directory '{BaseDirectory}': {ex.Message}", ex);
        }

        VerifyLockingIsEnforced();
    }

    private void VerifyLockingIsEnforced()
    {
        var probePath = Path.Combine(StateDirectory, $"lock-probe-{Environment.ProcessId}-{Guid.NewGuid():N}");
        try
        {
            using var first = ExclusiveFileLock.TryAcquire(probePath, FileMode.CreateNew, FileOptions.DeleteOnClose)
                ?? throw new ExecutionCoordinationException($"Could not lock new probe file '{probePath}'.");
            using var second = ExclusiveFileLock.TryAcquire(probePath, FileMode.Open);
            if (second is not null)
                throw new ExecutionCoordinationException(
                    $"Exclusive file locks are not enforced in '{StateDirectory}', so worktree ownership and the " +
                    "machine execution budget cannot be guaranteed. Unset DOTNET_SYSTEM_IO_DISABLEFILELOCKING " +
                    $"(and the System.IO.DisableFileLocking switch) or set {EnvironmentVariable} to a local filesystem.");
        }
        catch (IOException ex)
        {
            throw new ExecutionCoordinationException(
                $"Cannot verify exclusive file locking in '{StateDirectory}': {ex.Message}", ex);
        }
    }
}
