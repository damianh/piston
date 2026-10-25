// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Execution;

/// <summary>How a test project's runs are isolated from runs of the same project in other worktrees.</summary>
public enum TestResourceIsolation
{
    /// <summary>
    /// One run of the project at a time per repository, across all its worktrees on the machine. The
    /// default for projects that use containers, which commonly reuse fixed container names and ports.
    /// </summary>
    Repository,

    /// <summary>No project-level isolation: only the declared exclusive resources are coordinated.</summary>
    None,
}

/// <summary>
/// A <c>testResources</c> entry from <c>.piston.json</c>: overrides what Piston infers about the shared
/// resources of the test projects matching <see cref="Projects"/>. Every matching rule applies in order;
/// scalar settings from later rules win and exclusive resources accumulate.
/// </summary>
/// <param name="Projects">
/// Glob over project paths relative to the solution directory (<c>*</c>, <c>**</c>, <c>?</c>, <c>/</c>
/// separators). A pattern without <c>/</c> matches the project file name, e.g. <c>*.IntegrationTests.csproj</c>.
/// </param>
public sealed record TestResourceRule(string Projects)
{
    /// <summary>
    /// Resources only one test project on the machine may hold at a time: <c>port:&lt;number&gt;</c>,
    /// <c>container:&lt;name&gt;</c> or <c>resource:&lt;name&gt;</c>.
    /// </summary>
    public IReadOnlyList<string> Exclusive { get; init; } = [];

    /// <summary>Whether the project uses containers; null keeps the package-reference inference.</summary>
    public bool? Containers { get; init; }

    /// <summary>Project-level isolation; null uses <see cref="TestResourceIsolation.Repository"/> for container projects.</summary>
    public TestResourceIsolation? Isolation { get; init; }
}
