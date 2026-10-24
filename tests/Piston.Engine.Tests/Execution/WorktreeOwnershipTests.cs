// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Execution;
using Xunit;

namespace Piston.Engine.Tests.Execution;

public sealed class WorktreeOwnershipTests : IDisposable
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

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
    public void Identity_ResolvesSolutionFileSymbolicLinkOutsideWorktree()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var solution = CreateSolution(CreateWorktree("real"));
        var alias = Path.Combine(_root, "alias.slnx");
        _ = File.CreateSymbolicLink(alias, solution);

        Assert.Equal(WorktreeIdentity.FromSolutionPath(solution), WorktreeIdentity.FromSolutionPath(alias));
        using var owner = WorktreeOwnership.Acquire(_directory, solution);
        _ = Assert.Throws<WorktreeOwnershipConflictException>(() => WorktreeOwnership.Acquire(_directory, alias));
    }

    [Fact]
    public void Identity_MacDirectorySpelling_RespectsVolumeCaseSensitivity()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var upper = CreateWorktree("Case");
        var lower = CreateWorktree("case");
        var directories = Directory.GetDirectories(_root).Select(Path.GetFileName).ToList();
        var a = WorktreeIdentity.FromSolutionPath(CreateSolution(upper));
        var b = WorktreeIdentity.FromSolutionPath(CreateSolution(lower));

        if (directories.Contains("Case") && directories.Contains("case"))
        {
            Assert.NotEqual(a.Key, b.Key);
        }
        else
        {
            Assert.Equal(a.Key, b.Key);
        }
    }

    [Fact]
    public void ExistingPathSpelling_PreservesCaseDistinctEntries()
    {
        var upper = CreateWorktree("Case");
        var lower = CreateWorktree("case");
        var directories = Directory.GetDirectories(_root);
        var canonicalRoot = WorktreeIdentity.GetExistingPathSpelling(_root);

        Assert.Equal(Path.Combine(canonicalRoot, Path.GetFileName(directories.Single(d => string.Equals(d, upper, StringComparison.OrdinalIgnoreCase)
            && (!directories.Contains(upper) || d == upper)))), WorktreeIdentity.GetExistingPathSpelling(upper));
        Assert.Equal(Path.Combine(canonicalRoot, Path.GetFileName(directories.Single(d => string.Equals(d, lower, StringComparison.OrdinalIgnoreCase)
            && (!directories.Contains(lower) || d == lower)))), WorktreeIdentity.GetExistingPathSpelling(lower));
    }

    [Fact]
    public void Identity_MacTraverseOnlyAncestor_DoesNotRequireDirectoryListing()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var parent = Path.Combine(_root, "traverse-only");
        var worktree = CreateWorktree(Path.Combine("traverse-only", "Repo"));
        var solution = CreateSolution(worktree);
        var expected = WorktreeIdentity.FromSolutionPath(solution);
        var canonicalSolution = WorktreeIdentity.CanonicalizePath(solution);
        var missing = Path.Combine(worktree, "missing", "New.slnx");
        var canonicalMissing = WorktreeIdentity.CanonicalizePath(missing);
        var mode = File.GetUnixFileMode(parent);
        try
        {
            File.SetUnixFileMode(parent, UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            _ = Assert.Throws<UnauthorizedAccessException>(() => Directory.GetFileSystemEntries(parent));

            Assert.Equal(expected, WorktreeIdentity.FromSolutionPath(solution));
            Assert.Equal(canonicalSolution, WorktreeIdentity.CanonicalizePath(solution));
            Assert.Equal(canonicalMissing, WorktreeIdentity.CanonicalizePath(missing));
            using var owner = WorktreeOwnership.Acquire(_directory, solution);
            Assert.Equal(expected, owner.Worktree);
        }
        finally
        {
            File.SetUnixFileMode(parent, mode);
        }
    }

    [Fact]
    public void Identity_UnicodeSpelling_FollowsFileSystemEquivalence()
    {
        var composed = CreateWorktree("caf\u00e9");
        var decomposed = Path.Combine(_root, "cafe\u0301");
        var solution = CreateSolution(composed);

        if (Directory.Exists(decomposed))
        {
            Assert.Equal(WorktreeIdentity.GetExistingPathSpelling(composed), WorktreeIdentity.GetExistingPathSpelling(decomposed));
            Assert.Equal(WorktreeIdentity.CanonicalizePath(solution),
                WorktreeIdentity.CanonicalizePath(Path.Combine(decomposed, "App.slnx")));
            Assert.Equal(WorktreeIdentity.FromSolutionPath(solution),
                WorktreeIdentity.FromSolutionPath(Path.Combine(decomposed, "App.slnx")));
        }
        else
        {
            _ = CreateWorktree("cafe\u0301");
            var otherSolution = CreateSolution(decomposed);
            Assert.NotEqual(WorktreeIdentity.GetExistingPathSpelling(composed), WorktreeIdentity.GetExistingPathSpelling(decomposed));
            Assert.NotEqual(WorktreeIdentity.FromSolutionPath(solution).Key, WorktreeIdentity.FromSolutionPath(otherSolution).Key);
        }
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
    public async Task Acquire_RetriesBriefly_WhenLockIsReleasedWithinWindow()
    {
        // Models `status`/`stop` probing the lock at the instant a new owner starts.
        var solution = CreateSolution(CreateWorktree("wt"));
        var holder = WorktreeOwnership.Acquire(_directory, solution, "probe");
        var release = Task.Delay(100, _ct).ContinueWith(_ => holder.Dispose(), TaskScheduler.Default);

        using var owner = WorktreeOwnership.Acquire(_directory, solution, "mcp", TimeSpan.FromSeconds(5));
        await release;
        Assert.Equal("mcp", owner.Owner.Host);
    }

    [Fact]
    public void Acquire_StillConflicts_AfterRetryWindow()
    {
        var solution = CreateSolution(CreateWorktree("wt"));
        using var owner = WorktreeOwnership.Acquire(_directory, solution);

        var started = DateTime.UtcNow;
        _ = Assert.Throws<WorktreeOwnershipConflictException>(
            () => WorktreeOwnership.Acquire(_directory, solution, contentionRetry: TimeSpan.FromMilliseconds(150)));
        Assert.True(DateTime.UtcNow - started >= TimeSpan.FromMilliseconds(140));
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

    [Fact]
    public void ListOwners_ReportsLiveOwnerWithHost_AndNothingAfterRelease()
    {
        var solution = CreateSolution(CreateWorktree("listed"));
        var owner = WorktreeOwnership.Acquire(_directory, solution, "mcp");

        var record = Assert.Single(WorktreeOwnership.ListOwners(_directory));
        Assert.Equal(OwnershipLiveness.Live, record.Liveness);
        Assert.Equal(owner.Worktree.Key, record.Key);
        Assert.Equal("mcp", record.Owner!.Host);
        Assert.Equal(Environment.ProcessId, record.Owner.ProcessId);
        Assert.Equal(Path.GetFullPath(solution), record.Owner.SolutionPath);

        owner.Dispose();
        Assert.Empty(WorktreeOwnership.ListOwners(_directory));
    }

    [Fact]
    public void ListOwners_ReportsStaleDetailsLeftByCrashedOwner_WithoutHostField()
    {
        var worktree = WorktreeIdentity.FromSolutionPath(CreateSolution(CreateWorktree("crashed")));
        var owners = Path.Combine(_directory.StateDirectory, "owners");
        File.WriteAllText(Path.Combine(owners, worktree.Key + ".lock"), "");
        File.WriteAllText(Path.Combine(owners, worktree.Key + ".json"),
            $$"""{ "processId": 99999, "solutionPath": "/x/App.slnx", "worktreePath": "/x", "acquiredAt": "2026-01-01T00:00:00+00:00" }""");

        var record = Assert.Single(WorktreeOwnership.ListOwners(_directory));
        Assert.Equal(OwnershipLiveness.Stale, record.Liveness);
        Assert.Null(record.Owner!.Host);
        Assert.Equal(99999, record.Owner.ProcessId);
    }

    [Fact]
    public void ListOwners_EmptyWhenOwnersDirectoryMissing()
    {
        var fresh = new ExecutionCoordinationDirectory(Path.Combine(_root, "never-created"));
        Assert.Empty(WorktreeOwnership.ListOwners(fresh));
    }

    [Fact]
    public void Conflict_NamesOwnerHost()
    {
        var solution = CreateSolution(CreateWorktree("hosted"));
        using var owner = WorktreeOwnership.Acquire(_directory, solution, "mcp");
        var ex = Assert.Throws<WorktreeOwnershipConflictException>(() => WorktreeOwnership.Acquire(_directory, solution));
        Assert.Equal("mcp", ex.Owner!.Host);
        Assert.Contains("Piston mcp process", ex.Message);
    }
}
