// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Execution;
using Piston.Hosting;
using Xunit;

namespace Piston.Tests.Hosting;

public sealed class OwnerLookupTests : IDisposable
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"piston-owners-{Guid.NewGuid():N}");
    private readonly ExecutionCoordinationDirectory _directory;

    public OwnerLookupTests()
    {
        _directory = new ExecutionCoordinationDirectory(Path.Combine(_root, "coordination"));
        _directory.EnsureReady();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string CreateSolution(string worktreeName)
    {
        var worktree = Path.Combine(_root, worktreeName);
        _ = Directory.CreateDirectory(Path.Combine(worktree, ".git"));
        _ = Directory.CreateDirectory(Path.Combine(worktree, "src"));
        var solution = Path.Combine(worktree, "src", "App.slnx");
        File.WriteAllText(solution, "<Solution />");
        return solution;
    }

    [Fact]
    public void FindLive_MatchesSolutionWorktreeOrPathInside()
    {
        var a = CreateSolution("a");
        var b = CreateSolution("b");
        using var ownerA = WorktreeOwnership.Acquire(_directory, a, "mcp");
        using var ownerB = WorktreeOwnership.Acquire(_directory, b, "daemon");
        var records = OwnerLookup.ListAll(_directory);

        Assert.Equal(2, records.Count);
        Assert.Equal("mcp", Assert.Single(OwnerLookup.FindLive(records, a)).Owner!.Host);
        Assert.Equal("mcp", Assert.Single(OwnerLookup.FindLive(records, Path.Combine(_root, "a"))).Owner!.Host);
        Assert.Equal("daemon", Assert.Single(OwnerLookup.FindLive(records, Path.Combine(_root, "b", "src"))).Owner!.Host);
        Assert.Empty(OwnerLookup.FindLive(records, Path.Combine(_root, "c")));
        Assert.Empty(OwnerLookup.FindLive(records, _root));
    }

    [Fact]
    public void FindLive_StillFindsOwnerOfDeletedWorktree()
    {
        var a = CreateSolution("gone");
        using var owner = WorktreeOwnership.Acquire(_directory, a, "daemon");
        Directory.Delete(Path.Combine(_root, "gone"), recursive: true);

        var record = Assert.Single(OwnerLookup.FindLive(OwnerLookup.ListAll(_directory), Path.Combine(_root, "gone")));
        Assert.Contains("[worktree missing]", OwnerLookup.Describe(record));
        Assert.Contains($"PID {Environment.ProcessId} (daemon)", OwnerLookup.Describe(record));
    }

    [Fact]
    public void FindLive_RespectsVolumeCaseSensitivity()
    {
        var upper = CreateSolution("Case");
        var lower = CreateSolution("case");
        using var owner = WorktreeOwnership.Acquire(_directory, upper);
        var records = OwnerLookup.ListAll(_directory);
        var names = Directory.GetDirectories(_root).Select(Path.GetFileName).ToList();
        var distinct = names.Contains("Case") && names.Contains("case");

        Assert.Equal(distinct ? 0 : 1, OwnerLookup.FindLive(records, lower).Count);
        Assert.Equal(distinct ? 0 : 1, OwnerLookup.FindLive(records, Path.GetDirectoryName(lower)!).Count);
        _ = Assert.Single(OwnerLookup.FindLive(records, upper));
    }

    private string CreateNestedSolution(string parent, string name)
    {
        var worktree = Path.Combine(_root, parent, ".worktrees", name);
        _ = Directory.CreateDirectory(worktree);
        File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: elsewhere");
        var solution = Path.Combine(worktree, "App.slnx");
        File.WriteAllText(solution, "<Solution />");
        return solution;
    }

    [Fact]
    public void FindLive_NestedWorktree_NeverMatchesParentOwner()
    {
        var parent = CreateSolution("repo");
        var nested = CreateNestedSolution("repo", "feat");
        using var parentOwner = WorktreeOwnership.Acquire(_directory, parent, "daemon");
        using var nestedOwner = WorktreeOwnership.Acquire(_directory, nested, "mcp");
        var records = OwnerLookup.ListAll(_directory);
        var nestedDir = Path.GetDirectoryName(nested)!;

        Assert.Equal("mcp", Assert.Single(OwnerLookup.FindLive(records, nestedDir)).Owner!.Host);
        Assert.Equal("mcp", Assert.Single(OwnerLookup.FindLive(records, nested)).Owner!.Host);
        Assert.Equal("daemon", Assert.Single(OwnerLookup.FindLive(records, Path.Combine(_root, "repo"))).Owner!.Host);
        Assert.Equal("daemon", Assert.Single(OwnerLookup.FindLive(records, Path.Combine(_root, "repo", "src"))).Owner!.Host);
    }

    [Fact]
    public void FindLive_DeletedNestedWorktree_MatchesDeepestOwnerOnly()
    {
        var parent = CreateSolution("repo");
        var nested = CreateNestedSolution("repo", "feat");
        using var parentOwner = WorktreeOwnership.Acquire(_directory, parent, "daemon");
        using var nestedOwner = WorktreeOwnership.Acquire(_directory, nested, "mcp");
        var nestedDir = Path.GetDirectoryName(nested)!;
        Directory.Delete(nestedDir, recursive: true);

        var records = OwnerLookup.ListAll(_directory);
        Assert.Equal("mcp", Assert.Single(OwnerLookup.FindLive(records, nestedDir)).Owner!.Host);
        Assert.Equal("mcp", Assert.Single(OwnerLookup.FindLive(records, Path.Combine(nestedDir, "src"))).Owner!.Host);
    }

    [Fact]
    public void VerifyOwnerProcess_CurrentOwner_ReturnsProcess()
    {
        var a = CreateSolution("a");
        using var owner = WorktreeOwnership.Acquire(_directory, a, "daemon");
        var records = OwnerLookup.ListAll(_directory);

        using var process = OwnerLookup.VerifyOwnerProcess(Assert.Single(records), records, out var reason);
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        if (!OwnerLookup.IsPlausibleOwner(self.ProcessName, self.StartTime.ToUniversalTime(), records[0].Owner!, out _))
        {
            // On Windows the test host runs as testhost.exe, which is correctly not treated as piston/dotnet.
            Assert.Null(process);
            Assert.Contains("not piston/dotnet", reason);
            return;
        }

        Assert.True(process is not null, reason);
        Assert.Equal(Environment.ProcessId, process.Id);
    }

    [Fact]
    public void VerifyOwnerProcess_RecordReplacedOrReleased_Skips()
    {
        var a = CreateSolution("a");
        OwnershipRecord record;
        using (var owner = WorktreeOwnership.Acquire(_directory, a, "daemon"))
        {
            record = Assert.Single(OwnerLookup.ListAll(_directory));
        }

        Assert.Null(OwnerLookup.VerifyOwnerProcess(record, OwnerLookup.ListAll(_directory), out var released));
        Assert.Contains("changed or was released", released);

        // A new owner took the worktree after the record was read: its details differ.
        using var next = WorktreeOwnership.Acquire(_directory, a, "mcp");
        var replaced = record with { Owner = record.Owner! with { AcquiredAt = record.Owner.AcquiredAt.AddMinutes(-5) } };
        Assert.Null(OwnerLookup.VerifyOwnerProcess(replaced, OwnerLookup.ListAll(_directory), out var changed));
        Assert.Contains("changed or was released", changed);
    }

    [Theory]
    [InlineData("piston", -60, true)]
    [InlineData("Piston.exe", -60, true)]
    [InlineData("dotnet", 1, true)]
    [InlineData("bash", -60, false)]
    [InlineData("dotnet", 60, false)]
    public void IsPlausibleOwner_RequiresPistonOrDotnetStartedBeforeAcquisition(string name, int startOffsetSeconds, bool plausible)
    {
        var acquired = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var owner = new ExecutionOwnerInfo(1234, "/w/App.slnx", "/w", acquired, "daemon");

        Assert.Equal(plausible, OwnerLookup.IsPlausibleOwner(
            name, acquired.UtcDateTime.AddSeconds(startOffsetSeconds), owner, out var reason));
        Assert.Equal(plausible, reason.Length == 0);
    }

    [Fact]
    public async Task Terminate_ExitedProcess_ReturnsTrue()
    {
        var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("dotnet", "--version")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        })!;
        await process.WaitForExitAsync(_ct);
        Assert.True(await OwnerLookup.TerminateAsync(process, TimeSpan.FromSeconds(1)));
    }
}
