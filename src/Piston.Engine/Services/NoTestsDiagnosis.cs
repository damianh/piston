// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Impact;
using Piston.Engine.Models;

namespace Piston.Engine.Services;

/// <summary>
/// Explains why a test project finished an unfiltered run without reporting a single test, from its MSBuild
/// facts and the run's log, so the user is not left with a bare "no test results".
/// </summary>
internal static class NoTestsDiagnosis
{
    private const string TestSdk = "Microsoft.NET.Test.Sdk";

    // Framework package → the VSTest adapter package it needs (null when the framework package brings it).
    private static readonly (string Framework, string? Adapter)[] Frameworks =
    [
        ("xunit", "xunit.runner.visualstudio"),
        ("xunit.v3", "xunit.runner.visualstudio"),
        ("xunit.v3.core", "xunit.runner.visualstudio"),
        ("NUnit", "NUnit3TestAdapter"),
        ("MSTest", null),
        ("MSTest.TestFramework", "MSTest.TestAdapter"),
        ("TUnit", null),
        ("TUnit.Core", null),
    ];

    /// <summary>The most likely reason, ending with where to look next.</summary>
    public static string Diagnose(TestProjectFacts? facts, ExecutionLog? log)
    {
        var entries = log?.Entries ?? [];
        bool LogContains(string text) =>
            entries.Any(e => e.Message.Contains(text, StringComparison.OrdinalIgnoreCase));

        if (facts is not null && FromFacts(facts) is { } fromFacts)
        {
            return fromFacts;
        }

        if (LogContains("No test is available") || LogContains("No test matches"))
        {
            return "the test host found no tests: check that test classes are public and test methods carry the " +
                "framework's test attribute";
        }

        if (LogContains("Zero tests ran") || log?.ExitCode == 8)
        {
            return "the test app ran but discovered zero tests (exit code 8): check that test classes are public " +
                "and test methods carry the framework's test attribute";
        }

        if (LogContains("Testhost process") && LogContains("exited") || LogContains("test run was aborted"))
        {
            return "the test host exited before reporting results; see the project's run log";
        }

        if (log?.ExitCode is { } exitCode and not 0)
        {
            return $"the test process exited with code {exitCode} without reporting results; see the project's run log";
        }

        return "the test process reported no tests; see .piston/diagnostics.log";
    }

    private static string? FromFacts(TestProjectFacts facts)
    {
        var framework = Frameworks.FirstOrDefault(f => facts.References(f.Framework));

        if (facts.IsMtp)
        {
            return string.Equals(facts.OutputType, "Exe", StringComparison.OrdinalIgnoreCase)
                ? null
                : $"it runs on Microsoft.Testing.Platform, which needs <OutputType>Exe</OutputType>, but builds " +
                    $"'{(facts.OutputType.Length == 0 ? "Library" : facts.OutputType)}'";
        }

        if (framework.Framework is null)
        {
            return "it references no known test framework package (xunit, NUnit, MSTest, TUnit)";
        }

        if (!facts.References(TestSdk) && !facts.References("MSTest"))
        {
            return $"it references {framework.Framework} but not {TestSdk}, which VSTest needs to run its tests; " +
                "add it, or enable Microsoft.Testing.Platform";
        }

        return framework.Adapter is { } adapter && !facts.References(adapter)
            ? $"VSTest needs the {adapter} package to find {framework.Framework} tests; add it, or enable " +
                "Microsoft.Testing.Platform"
            : null;
    }
}
