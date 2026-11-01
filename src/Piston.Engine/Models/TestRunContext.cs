// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Models;

/// <summary>Build and selection context captured when a run started.</summary>
/// <param name="SolutionPath">Solution the run was issued for.</param>
/// <param name="TestProjects">Selected test projects; null means the solution was passed to the runner as a single target.</param>
/// <param name="BuildTargets">Projects built before testing; null means the whole solution was built.</param>
/// <param name="UserFilter">The user-configured test filter at run start.</param>
/// <param name="EffectiveFilter">The filter actually passed to <c>dotnet test</c> (user filter combined with any coverage selection).</param>
/// <param name="CoverageSelectedTestCount">Number of tests selected by coverage impact analysis, when that narrowed the run.</param>
/// <param name="CoverageEnabled">Whether coverage collection was enabled.</param>
/// <param name="BuildConfiguration">Piston does not pass <c>-c</c>, so the .NET CLI default configuration applies.</param>
/// <param name="SdkVersion">Output of <c>dotnet --version</c> in the solution directory, when available.</param>
/// <param name="Runtime">Runtime hosting the Piston engine.</param>
/// <param name="OperatingSystem">Host operating system description.</param>
/// <param name="WorkingDirectory">Working directory of the build process and SDK version probe.</param>
/// <param name="TestWorkingDirectories">Working directory per test target: solution directory for MTP, project directory for VSTest.</param>
/// <param name="SelectionExplanation">One sentence saying why the run has this scope.</param>
/// <param name="SelectionReasons">
/// Why each test was selected, when the impact map narrowed the run to tests by changed lines.
/// </param>
public sealed record TestRunContext(
    string SolutionPath,
    IReadOnlyList<string>? TestProjects,
    IReadOnlyList<string>? BuildTargets,
    string? UserFilter,
    string? EffectiveFilter,
    int? CoverageSelectedTestCount,
    bool CoverageEnabled,
    string BuildConfiguration,
    string? SdkVersion,
    string Runtime,
    string OperatingSystem,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string>? TestWorkingDirectories = null,
    string? SelectionExplanation = null,
    IReadOnlyDictionary<string, IReadOnlyList<TestSelectionReason>>? SelectionReasons = null);
