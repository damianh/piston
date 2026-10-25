// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Execution;
using Piston.Engine.Impact;
using Piston.Engine.Models;
using Xunit;

namespace Piston.Engine.Tests.Execution;

public sealed class TestResourceInferenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"piston-resources-{Guid.NewGuid():N}");

    public TestResourceInferenceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Theory]
    [InlineData("port:5432", "port:5432")]
    [InlineData(" PORT: 080 ", "port:80")]
    [InlineData("container:api-postgres_1.db", "container:api-postgres_1.db")]
    [InlineData("Resource:shared sql database", "resource:shared sql database")]
    public void TryNormalize_AcceptsAndNormalizesKeys(string raw, string expected)
    {
        Assert.True(TestResourceKey.TryNormalize(raw, out var key, out _));
        Assert.Equal(expected, key);
    }

    [Theory]
    [InlineData("5432")]
    [InlineData("port:0")]
    [InlineData("port:65536")]
    [InlineData("port:-1")]
    [InlineData("port:abc")]
    [InlineData("container:-leading-dash")]
    [InlineData("container:has space")]
    [InlineData("resource:")]
    [InlineData("project:anything")]
    [InlineData("volume:data")]
    [InlineData("")]
    public void TryNormalize_RejectsInvalidKeys_WithAReason(string raw)
    {
        Assert.False(TestResourceKey.TryNormalize(raw, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void TryGetPort_ReadsOnlyPortKeys()
    {
        Assert.Equal(5432, TestResourceKey.TryGetPort("port:5432"));
        Assert.Null(TestResourceKey.TryGetPort("container:5432"));
    }

    [Theory]
    [InlineData("Api.IntegrationTests.csproj", "tests/Api.IntegrationTests/Api.IntegrationTests.csproj", true)]
    [InlineData("api.integrationtests.csproj", "tests/Api.IntegrationTests/Api.IntegrationTests.csproj", true)]
    [InlineData("*.IntegrationTests.csproj", "Api.IntegrationTests.csproj", true)]
    [InlineData("tests/*/*.csproj", "tests/Api/Api.csproj", true)]
    [InlineData("tests/*/*.csproj", "tests/a/b/Api.csproj", false)]
    [InlineData("tests/**/*.csproj", "tests/a/b/Api.csproj", true)]
    [InlineData("tests/**/*.csproj", "tests/Api.csproj", true)]
    [InlineData("./tests/Api?/Api1.csproj", "tests/Api1/Api1.csproj", true)]
    [InlineData("tests\\Api\\Api.csproj", "tests/Api/Api.csproj", true)]
    [InlineData("src/*.csproj", "tests/src/Api.csproj", false)]
    [InlineData("Api.csproj", "tests/Other.Api.csproj", false)]
    public void GlobToRegex_MatchesRelativeProjectPaths(string pattern, string path, bool expected) =>
        Assert.Equal(expected, TestResourceInference.GlobToRegex(pattern).IsMatch(path));

    [Theory]
    [InlineData("Testcontainers", true)]
    [InlineData("Testcontainers.PostgreSql", true)]
    [InlineData("DotNet.Testcontainers", true)]
    [InlineData("Aspire.Hosting.Testing", true)]
    [InlineData("Docker.DotNet", true)]
    [InlineData("Ductus.FluentDocker", true)]
    [InlineData("Squadron.SqlServer", true)]
    [InlineData("xunit.v3", false)]
    [InlineData("TestcontainersExtras", false)]
    [InlineData("Aspire.Hosting", false)]
    public void IsContainerPackage(string package, bool expected) =>
        Assert.Equal(expected, TestResourceInference.IsContainerPackage(package));

    [Fact]
    public void ContainerPackage_IsIsolatedPerRepository_ByDefault()
    {
        var (solution, project) = Layout("repo", "tests/Db.Tests/Db.Tests.csproj");
        var inference = Inference(solution, [], Packages("Testcontainers.PostgreSql", "xunit.v3"));

        var needs = inference.Resolve(project);

        Assert.True(needs.UsesContainers);
        var key = Assert.Single(needs.Keys);
        Assert.StartsWith("project:repo-", key, StringComparison.Ordinal);
        Assert.EndsWith("/tests/Db.Tests/Db.Tests.csproj", key, StringComparison.Ordinal);
        Assert.Contains(needs.Reasons, r => r.Contains("Testcontainers.PostgreSql", StringComparison.Ordinal));
    }

    [Fact]
    public void ProjectWithoutContainerPackagesOrRules_NeedsNothing()
    {
        var (solution, project) = Layout("repo", "tests/Unit/Unit.csproj");

        var needs = Inference(solution, [], Packages("xunit.v3")).Resolve(project);

        Assert.True(needs.IsEmpty);
        Assert.Empty(needs.Reasons);
    }

    [Fact]
    public void UnknownFacts_BeforeTheGraphIsLoaded_NeedNothing()
    {
        var (solution, project) = Layout("repo", "tests/Unit/Unit.csproj");

        Assert.True(Inference(solution, [], _ => null).Resolve(project).IsEmpty);
    }

    [Fact]
    public void Rules_ApplyInOrder_UnionKeys_AndLastContainerAndIsolationWin()
    {
        var (solution, project) = Layout("repo", "tests/Db.Tests/Db.Tests.csproj");
        TestResourceRule[] rules =
        [
            new("tests/**") { Exclusive = ["port:5432"], Isolation = TestResourceIsolation.Repository },
            new("Db.Tests.csproj") { Exclusive = ["Container:db", "port:5432"], Isolation = TestResourceIsolation.None, Containers = false },
            new("Other.csproj") { Exclusive = ["port:1"] },
        ];

        var needs = Inference(solution, rules, Packages("Testcontainers")).Resolve(project);

        Assert.Equal(["container:db", "port:5432"], needs.Keys);
        Assert.False(needs.UsesContainers);
        Assert.Equal(3, needs.Reasons.Count);
        Assert.Contains("testResources 'Db.Tests.csproj'", needs.Reasons[2], StringComparison.Ordinal);
    }

    [Fact]
    public void ContainersRule_InfersContainers_AndRepositoryIsolation_WithoutPackages()
    {
        var (solution, project) = Layout("repo", "tests/Legacy/Legacy.csproj");

        var needs = Inference(solution, [new("Legacy.csproj") { Containers = true }], Packages()).Resolve(project);

        Assert.True(needs.UsesContainers);
        Assert.StartsWith("project:", Assert.Single(needs.Keys), StringComparison.Ordinal);
    }

    [Fact]
    public void RepositoryIsolation_CanApplyToProjectsWithoutContainers()
    {
        var (solution, project) = Layout("repo", "tests/Fs/Fs.csproj");

        var needs = Inference(solution, [new("Fs.csproj") { Isolation = TestResourceIsolation.Repository }], Packages()).Resolve(project);

        Assert.False(needs.UsesContainers);
        Assert.StartsWith("project:", Assert.Single(needs.Keys), StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidRuleKey_Throws()
    {
        var (solution, _) = Layout("repo", "tests/A/A.csproj");

        var ex = Assert.Throws<ArgumentException>(() => Inference(solution, [new("A.csproj") { Exclusive = ["port:99999"] }], Packages()));
        Assert.Contains("port:99999", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectKey_IsSharedAcrossWorktreesOfARepository_ButNotAcrossRepositories()
    {
        // A main checkout and a linked worktree whose .git file points into the main repository.
        var main = Path.Combine(_root, "main");
        _ = Directory.CreateDirectory(Path.Combine(main, ".git", "worktrees", "feature"));
        File.WriteAllText(Path.Combine(main, ".git", "worktrees", "feature", "commondir"), "../..");
        var feature = Path.Combine(_root, "feature");
        _ = Directory.CreateDirectory(feature);
        File.WriteAllText(Path.Combine(feature, ".git"), $"gitdir: {Path.Combine(main, ".git", "worktrees", "feature")}");
        var other = Path.Combine(_root, "other");
        _ = Directory.CreateDirectory(Path.Combine(other, ".git"));

        string KeyIn(string worktree) =>
            new TestResourceInference(Path.Combine(worktree, "App.slnx"), [], Packages("Testcontainers"))
                .Resolve(Path.Combine(worktree, "tests", "Db", "Db.csproj")).Keys.Single();

        Assert.Equal(KeyIn(main), KeyIn(feature));
        Assert.StartsWith("project:main-", KeyIn(main), StringComparison.Ordinal);
        Assert.NotEqual(KeyIn(main), KeyIn(other));
    }

    [Fact]
    public void UnmatchedRules_AreReported()
    {
        var (solution, project) = Layout("repo", "tests/A/A.csproj");
        var inference = Inference(solution, [new("A.csproj"), new("Missing.csproj")], Packages());

        Assert.Equal(["Missing.csproj"], inference.UnmatchedRules([project]));
    }

    [Fact]
    public void Resolver_CombinesAllTestProjects_ForASolutionTarget_AndLogsUnmatchedRulesOnce()
    {
        var (solution, a) = Layout("repo", "tests/A/A.csproj");
        var b = Path.Combine(Path.GetDirectoryName(solution)!, "tests", "B", "B.csproj");
        var inference = Inference(solution,
            [new("A.csproj") { Exclusive = ["port:1"] }, new("B.csproj") { Exclusive = ["port:2"] }, new("Nope.csproj")],
            path => path == a ? Packages("Testcontainers")(path) : Packages()(path));
        var logged = new List<string>();
        var resolver = new TestResourceResolver(inference, () => [a, b], logged.Add);

        var combined = resolver.Resolve(solution);
        _ = resolver.Resolve(b);

        Assert.Equal(solution, combined.ProjectPath);
        Assert.True(combined.UsesContainers);
        Assert.Contains("port:1", combined.Keys);
        Assert.Contains("port:2", combined.Keys);
        Assert.Contains(combined.Keys, k => k.StartsWith("project:", StringComparison.Ordinal));
        Assert.Contains(combined.Reasons, r => r.StartsWith("A:", StringComparison.Ordinal));
        Assert.Equal(["port:2"], resolver.Resolve(b).Keys);
        var message = Assert.Single(logged);
        Assert.Contains("Nope.csproj", message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConflictScanner_IgnoresPassingProjects()
    {
        var result = Result(TestStatus.Passed, "Bind for 0.0.0.0:5432 failed: port is already allocated");

        Assert.Null(ResourceConflictScanner.Scan(result));
    }

    [Theory]
    [InlineData("System.Net.Sockets.SocketException (98): Address already in use")]
    [InlineData("Error: listen EADDRINUSE: address already in use :::3000")]
    [InlineData("Only one usage of each socket address (protocol/network address/port) is normally permitted.")]
    [InlineData("Docker API responded with status code=InternalServerError, response={\"message\":\"driver failed: Bind for 0.0.0.0:5432 failed: port is already allocated\"}")]
    [InlineData("Conflict. The container name \"/api-postgres\" is already in use by container \"abc\".")]
    public void ConflictScanner_RecognisesConflictSignatures_InFailedTests(string message)
    {
        var description = ResourceConflictScanner.Scan(Result(TestStatus.Failed, "setup failed\n" + message + "\nat Foo()"));

        Assert.NotNull(description);
        Assert.Contains(message.Length > 200 ? message[..200] : message, description, StringComparison.Ordinal);
        Assert.Contains("testResources", description, StringComparison.Ordinal);
        Assert.DoesNotContain("setup failed", description, StringComparison.Ordinal);
    }

    [Fact]
    public void ConflictScanner_ReadsRunnerErrorsAndLogs_OfCrashedProjects()
    {
        var log = new ExecutionLog("/w/A.csproj");
        log.Append("Output", "Unhandled exception. System.IO.IOException: Failed to bind to address http://127.0.0.1:5000: address already in use.");
        var crashed = new ProjectTestResult("/w/A.csproj", [], "Test host exited with code 134", [], Crashed: true) { Log = log };

        Assert.NotNull(ResourceConflictScanner.Scan(crashed));
    }

    [Fact]
    public void ConflictScanner_IgnoresUnrelatedFailures() =>
        Assert.Null(ResourceConflictScanner.Scan(Result(TestStatus.Failed, "Expected 1 but was 2")));

    private static ProjectTestResult Result(TestStatus status, string message)
    {
        var test = new TestResult("A.T", "T", status, TimeSpan.Zero, null, message, null, null);
        return new ProjectTestResult("/w/A.csproj", [new TestSuite("A", [test], DateTimeOffset.UtcNow, TimeSpan.Zero)], null, [], Crashed: false);
    }

    private (string Solution, string Project) Layout(string repository, string project)
    {
        var root = Path.Combine(_root, repository);
        _ = Directory.CreateDirectory(Path.Combine(root, ".git"));
        return (Path.Combine(root, "App.slnx"), Path.Combine(root, project.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static TestResourceInference Inference(
        string solution, IReadOnlyList<TestResourceRule> rules, Func<string, TestProjectFacts?> facts) =>
        new(solution, rules, facts);

    private static Func<string, TestProjectFacts?> Packages(params string[] packages) =>
        _ => new TestProjectFacts("Exe", IsMtp: true, new HashSet<string>(packages, StringComparer.OrdinalIgnoreCase));
}
