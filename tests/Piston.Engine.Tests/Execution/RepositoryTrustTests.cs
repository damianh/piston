// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Execution;
using Xunit;

namespace Piston.Engine.Tests.Execution;

public sealed class RepositoryTrustTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"piston-trust-{Guid.NewGuid():N}");

    public RepositoryTrustTests() => _ = Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void CloneIdentity_SharesTrustAcrossVerifiedLinkedWorktrees()
    {
        var clone = Path.Combine(_root, "clone");
        var worktree = Path.Combine(_root, "linked");
        var git = Path.Combine(clone, ".git");
        var admin = Path.Combine(git, "worktrees", "linked");
        _ = Directory.CreateDirectory(admin);
        _ = Directory.CreateDirectory(worktree);
        var worktreeGitFile = Path.Combine(worktree, ".git");
        File.WriteAllText(worktreeGitFile, $"gitdir: {Path.GetRelativePath(worktree, admin)}{Environment.NewLine}");
        File.WriteAllText(Path.Combine(admin, "gitdir"), $"{worktreeGitFile}{Environment.NewLine}");
        File.WriteAllText(Path.Combine(admin, "commondir"), $"../..{Environment.NewLine}");

        var mainIdentity = CloneIdentity.FromWorktreePath(clone);
        var linkedIdentity = CloneIdentity.FromWorktreePath(worktree);

        Assert.Equal(mainIdentity, linkedIdentity);
        Assert.Equal(WorktreeIdentity.CanonicalizePath(git), mainIdentity.RootPath);
    }

    [Fact]
    public void CloneIdentity_DoesNotTrustForgedGitFileMetadata()
    {
        var trustedClone = Path.Combine(_root, "trusted");
        var forgedWorktree = Path.Combine(_root, "forged");
        var admin = Path.Combine(trustedClone, ".git", "worktrees", "real");
        _ = Directory.CreateDirectory(admin);
        _ = Directory.CreateDirectory(forgedWorktree);
        var forgedGitFile = Path.Combine(forgedWorktree, ".git");
        File.WriteAllText(forgedGitFile, $"gitdir: {admin}{Environment.NewLine}");
        File.WriteAllText(Path.Combine(admin, "gitdir"), Path.Combine(_root, "real", ".git"));
        File.WriteAllText(Path.Combine(admin, "commondir"), "../..");

        var identity = CloneIdentity.FromWorktreePath(forgedWorktree);

        Assert.Equal(WorktreeIdentity.CanonicalizePath(forgedWorktree), identity.RootPath);
        Assert.NotEqual(CloneIdentity.FromWorktreePath(trustedClone).Key, identity.Key);
    }

    [Fact]
    public void CloneIdentity_UsesWorktreeRootWhenGitMetadataIsUnavailable()
    {
        var plain = Path.Combine(_root, "plain");
        var malformed = Path.Combine(_root, "malformed");
        _ = Directory.CreateDirectory(plain);
        _ = Directory.CreateDirectory(malformed);
        File.WriteAllText(Path.Combine(malformed, ".git"), "not a gitdir file");

        Assert.Equal(WorktreeIdentity.CanonicalizePath(plain), CloneIdentity.FromWorktreePath(plain).RootPath);
        Assert.Equal(WorktreeIdentity.CanonicalizePath(malformed), CloneIdentity.FromWorktreePath(malformed).RootPath);
    }

    [Fact]
    public void TrustStore_PersistsTrustAndCanRevokeIt()
    {
        var clone = CloneIdentity.FromWorktreePath(_root);
        var path = Path.Combine(_root, "user-data", "repositories.json");
        var store = new RepositoryTrustStore(path);

        Assert.False(store.IsTrusted(clone));
        store.Trust(clone);
        Assert.True(new RepositoryTrustStore(path).IsTrusted(clone));
        Assert.Equal(clone.RootPath, Assert.Single(store.List()).Path);
        Assert.True(store.Revoke(clone));
        Assert.False(store.IsTrusted(clone));
        Assert.False(store.Revoke(clone));
    }
}
