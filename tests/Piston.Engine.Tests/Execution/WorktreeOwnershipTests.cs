// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Execution;
using Xunit;

namespace Piston.Engine.Tests.Execution;

public sealed class WorktreeOwnershipTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"piston-ownership-{Guid.NewGuid():N}");
    private readonly ExecutionCoordinationDirectory _directory;

    public WorktreeOwnershipTests()
    {
        _ = Directory.CreateDirectory(_root);
        _directory = new ExecutionCoordinationDirectory(Path.Combine(_root, "coordination"));
        _directory.EnsureReady();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string CreateWorktree(string name, bool gitFile = false)
    {
        var dir = Path.Combine(_root, name);
        _ = Directory.CreateDirectory(dir);
        if (gitFile)
        {
            File.WriteAllText(Path.Combine(dir, ".git"), "gitdir: /elsewhere/.git/worktrees/" + name);
        }
        else
        {
            _ = Directory.CreateDirectory(Path.Combine(dir, ".git"));
        }

        return dir;
    }

    private static string CreateSolution(string dir, string name = "App.slnx")
    {
        _ = Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, "<Solution />");
        return path;
    }

    [Fact]
    public void Identity_UsesNearestGitRoot_ForSolutionsInSubdirectories()
    {
        var worktree = CreateWorktree("wt");
        var top = WorktreeIdentity.FromSolutionPath(CreateSolution(worktree));
        var nested = WorktreeIdentity.FromSolutionPath(CreateSolution(Path.Combine(worktree, "src", "sub"), "Other.sln"));

        Assert.Equal(top, nested);
        Assert.EndsWith(Path.DirectorySeparatorChar + "wt", top.RootPath, StringComparison.Ordinal);
        Assert.Equal(32, top.Key.Length);
    }

    [Fact]
    public void Identity_RecognizesGitWorktreeFile_AndDistinguishesWorktrees()
    {
        var a = WorktreeIdentity.FromSolutionPath(CreateSolution(CreateWorktree("a", gitFile: true)));
        var b = WorktreeIdentity.FromSolutionPath(CreateSolution(CreateWorktree("b", gitFile: true)));

        Assert.NotEqual(a.Key, b.Key);
        Assert.EndsWith("a", a.RootPath, StringComparison.Ordinal);
    }

    [Fact]
    public void Identity_WithoutGit_UsesSolutionDirectory()
    {
        var dir = Path.Combine(_root, "plain", "inner");
        var identity = WorktreeIdentity.FromSolutionPath(CreateSolution(dir));
        Assert.EndsWith("inner", identity.RootPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Identity_ResolvesSymbolicLinks()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Creating symlinks requires privileges on Windows.
        }

        var worktree = CreateWorktree("real");
        var solution = CreateSolution(Path.Combine(worktree, "src"));
        var link = Path.Combine(_root, "link");
        _ = Directory.CreateSymbolicLink(link, worktree);

        var viaLink = WorktreeIdentity.FromSolutionPath(Path.Combine(link, "src", "App.slnx"));

        Assert.Equal(WorktreeIdentity.FromSolutionPath(solution), viaLink);
    }

    [Fact]
    public void Acquire_RefusesSecondOwnerOfSameWorktree_EvenForAnotherSolution()
    {
        var worktree = CreateWorktree("wt");
        using var owner = WorktreeOwnership.Acquire(_directory, CreateSolution(worktree));

        var ex = Assert.Throws<WorktreeOwnershipConflictException>(
            () => WorktreeOwnership.Acquire(_directory, CreateSolution(Path.Combine(worktree, "other"), "Other.sln")));

        Assert.Equal(owner.Worktree, ex.Worktree);
        Assert.NotNull(ex.Owner);
        Assert.Equal(Environment.ProcessId, ex.Owner.ProcessId);
        Assert.Equal(owner.Owner.SolutionPath, ex.Owner.SolutionPath);
        Assert.Contains(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Acquire_AllowsDifferentWorktreesConcurrently()
    {
        using var a = WorktreeOwnership.Acquire(_directory, CreateSolution(CreateWorktree("a")));
        using var b = WorktreeOwnership.Acquire(_directory, CreateSolution(CreateWorktree("b")));
        Assert.NotEqual(a.Worktree.Key, b.Worktree.Key);
    }

    [Fact]
    public void Release_AllowsReacquire_AndRemovesOwnerDetails()
    {
        var solution = CreateSolution(CreateWorktree("wt"));
        var first = WorktreeOwnership.Acquire(_directory, solution);
        var infoPath = Path.Combine(_directory.OwnersDirectory, first.Worktree.Key + ".json");
        Assert.True(File.Exists(infoPath));

        first.Dispose();
        first.Dispose();
        Assert.False(File.Exists(infoPath));

        using var second = WorktreeOwnership.Acquire(_directory, solution);
        Assert.Equal(first.Worktree, second.Worktree);
    }

    [Fact]
    public void Acquire_ConflictsWithOwnerInAnotherProcess_AndRecoversAfterItIsKilled()
    {
        var solution = CreateSolution(CreateWorktree("wt"));
        using var holder = LeaseHolderProcess.Start("own", _directory.BaseDirectory, solution);
        Assert.Equal("READY", holder.WaitForLine("READY", "ERROR"));

        var ex = Assert.Throws<WorktreeOwnershipConflictException>(() => WorktreeOwnership.Acquire(_directory, solution));
        Assert.Equal(holder.ProcessId, ex.Owner?.ProcessId);

        holder.Kill();

        // The crashed owner left its lock file and details behind; the OS released the lock.
        using var recovered = WorktreeOwnership.Acquire(_directory, solution);
        Assert.Equal(Environment.ProcessId, recovered.Owner.ProcessId);
        Assert.Equal(Environment.ProcessId, WorktreeOwnership.TryReadInfo(
            Path.Combine(_directory.OwnersDirectory, recovered.Worktree.Key + ".json"))?.ProcessId);
    }

    [Fact]
    public void Acquire_InAnotherProcess_FailsWhileOwnedHere()
    {
        var solution = CreateSolution(CreateWorktree("wt"));
        using var owner = WorktreeOwnership.Acquire(_directory, solution);

        using var other = LeaseHolderProcess.Start("own", _directory.BaseDirectory, solution);
        var line = other.WaitForLine("READY", "ERROR");

        Assert.StartsWith("ERROR", line, StringComparison.Ordinal);
        Assert.Contains(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), line, StringComparison.Ordinal);
        Assert.Equal(2, other.WaitForExit());
    }
}
