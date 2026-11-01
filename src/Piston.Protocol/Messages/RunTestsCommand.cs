// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Protocol.Dtos;

namespace Piston.Protocol.Messages;

/// <summary>Parameters for <c>engine/runTests</c>: the tests to run.</summary>
/// <param name="ProjectIds">Solution-relative IDs of the test projects to run, as reported in <see cref="Dtos.TestSuiteDto.ProjectId"/>.</param>
/// <param name="Tests">
/// Fully qualified names of the tests to run within those projects, as reported in
/// <see cref="Dtos.TestResultDto.FullyQualifiedName"/>; null runs every test in them. All cases of a
/// parameterised test run. A name that, less its argument list, contains a double quote is rejected.
/// </param>
public sealed record RunTestsCommand(IReadOnlyList<string> ProjectIds, IReadOnlyList<string>? Tests = null)
{
    public static RunTestsCommand? ForFailed(IReadOnlyList<TestSuiteDto> suites)
    {
        var failed = suites
            .SelectMany(s => s.Tests.Where(t => t.Status == TestStatusDto.Failed).Select(t => (Project: s.ProjectId ?? s.Name, Test: t)))
            .ToList();
        return failed.Count == 0 ? null : new RunTestsCommand(
            [.. failed.Select(f => f.Project).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            [.. failed.Select(f => f.Test.FullyQualifiedName).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]);
    }
}
