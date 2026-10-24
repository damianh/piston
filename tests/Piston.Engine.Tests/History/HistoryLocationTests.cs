// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Execution;
using Piston.Engine.History;
using Xunit;

namespace Piston.Engine.Tests.History;

public sealed class HistoryLocationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "piston-history-location", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Default_base_directory_honours_the_environment_variable()
    {
        var configured = Path.Combine(_root, "custom");

        Assert.Equal(configured, HistoryLocation.DefaultBaseDirectory(_ => configured, () => "/unused"));
    }

    [Fact]
    public void Default_base_directory_rejects_a_relative_environment_variable() =>
        Assert.Throws<InvalidOperationException>(() => HistoryLocation.DefaultBaseDirectory(_ => "relative", () => _root));

    [Fact]
    public void Default_base_directory_is_under_local_application_data() =>
        Assert.Equal(Path.Combine(_root, "Piston", "history"), HistoryLocation.DefaultBaseDirectory(_ => null, () => _root));

    [Fact]
    public void Repository_of_a_main_checkout_is_its_git_directory()
    {
        var worktree = CreateDirectory("main");
        var git = CreateDirectory(Path.Combine("main", ".git"));

        Assert.Equal(Canonical(git), HistoryLocation.FindRepository(worktree));
    }

    [Fact]
    public void Repository_of_a_linked_worktree_is_the_common_git_directory()
    {
        var mainGit = CreateDirectory(Path.Combine("main", ".git"));
        var worktreeGit = CreateDirectory(Path.Combine("main", ".git", "worktrees", "feature"));
        File.WriteAllText(Path.Combine(worktreeGit, "commondir"), "../..\n");
        var worktree = CreateDirectory("feature");
        File.WriteAllText(Path.Combine(worktree, ".git"), $"gitdir: {worktreeGit}\n");

        Assert.Equal(Canonical(mainGit), HistoryLocation.FindRepository(worktree));
    }

    [Fact]
    public void Repository_outside_git_is_null() =>
        Assert.Null(HistoryLocation.FindRepository(CreateDirectory("plain")));

    [Fact]
    public void Worktrees_of_one_repository_share_a_repository_folder_but_not_a_file()
    {
        var mainGit = CreateDirectory(Path.Combine("main", ".git"));
        var worktreeGit = CreateDirectory(Path.Combine("main", ".git", "worktrees", "feature"));
        File.WriteAllText(Path.Combine(worktreeGit, "commondir"), "../..");
        var feature = CreateDirectory("feature");
        File.WriteAllText(Path.Combine(feature, ".git"), $"gitdir: {worktreeGit}");
        var mainSolution = Path.Combine(_root, "main", "App.slnx");
        var featureSolution = Path.Combine(feature, "App.slnx");
        File.WriteAllText(mainSolution, "");
        File.WriteAllText(featureSolution, "");
        var baseDirectory = Path.Combine(_root, "history");

        var main = HistoryLocation.Resolve(baseDirectory, mainSolution);
        var linked = HistoryLocation.Resolve(baseDirectory, featureSolution);

        Assert.Equal(Canonical(mainGit), linked.RepositoryPath);
        Assert.Equal(linked.RepositoryPath, main.RepositoryPath);
        Assert.Equal(Path.GetDirectoryName(linked.Directory), Path.GetDirectoryName(main.Directory));
        Assert.NotEqual(linked.FilePath, main.FilePath);
        Assert.Equal("App.history.json", Path.GetFileName(main.FilePath));
        Assert.StartsWith(baseDirectory, main.FilePath, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static string Canonical(string path) => WorktreeIdentity.CanonicalizePath(path);

    private string CreateDirectory(string relative) => Directory.CreateDirectory(Path.Combine(_root, relative)).FullName;
}
