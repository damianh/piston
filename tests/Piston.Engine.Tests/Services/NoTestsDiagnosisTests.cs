// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Impact;
using Piston.Engine.Models;
using Piston.Engine.Services;
using Xunit;

namespace Piston.Engine.Tests.Services;

public sealed class NoTestsDiagnosisTests
{
    [Fact]
    public void MtpProjectThatIsNotAnExe_NamesTheOutputType()
    {
        var reason = NoTestsDiagnosis.Diagnose(Facts("Library", isMtp: true, "xunit.v3"), log: null);

        Assert.Contains("<OutputType>Exe</OutputType>", reason, StringComparison.Ordinal);
        Assert.Contains("'Library'", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void VSTestProjectWithoutTestSdk_AsksForIt()
    {
        var reason = NoTestsDiagnosis.Diagnose(Facts("Library", isMtp: false, "xunit", "xunit.runner.visualstudio"), log: null);

        Assert.Contains("Microsoft.NET.Test.Sdk", reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("xunit", "xunit.runner.visualstudio")]
    [InlineData("NUnit", "NUnit3TestAdapter")]
    [InlineData("MSTest.TestFramework", "MSTest.TestAdapter")]
    public void VSTestProjectWithoutAdapter_NamesTheAdapter(string framework, string adapter)
    {
        var reason = NoTestsDiagnosis.Diagnose(Facts("Library", isMtp: false, framework, "Microsoft.NET.Test.Sdk"), log: null);

        Assert.Contains(adapter, reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectWithoutFramework_SaysSo()
    {
        var reason = NoTestsDiagnosis.Diagnose(Facts("Library", isMtp: false, "Microsoft.NET.Test.Sdk"), log: null);

        Assert.Contains("no known test framework", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void WellFormedProject_FallsBackToTheLog()
    {
        var facts = Facts("Exe", isMtp: true, "xunit.v3");
        var log = new ExecutionLog("/repo/T.csproj") { ExitCode = 8 };

        Assert.Contains("exit code 8", NoTestsDiagnosis.Diagnose(facts, log), StringComparison.Ordinal);
    }

    [Fact]
    public void VSTestNoTestAvailable_PointsAtTestAttributes()
    {
        var log = new ExecutionLog("/repo/T.csproj");
        log.Append("VSTestRunner:stdout", "No test is available in /repo/bin/T.dll. Make sure that test discoverer & executors are registered");

        var reason = NoTestsDiagnosis.Diagnose(Facts("Library", isMtp: false, "xunit", "xunit.runner.visualstudio", "Microsoft.NET.Test.Sdk"), log);

        Assert.Contains("public", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownCause_NamesTheExitCodeOrTheLog()
    {
        Assert.Contains("code 3", NoTestsDiagnosis.Diagnose(null, new ExecutionLog("/repo/T.csproj") { ExitCode = 3 }), StringComparison.Ordinal);
        Assert.Contains("diagnostics.log", NoTestsDiagnosis.Diagnose(null, null), StringComparison.Ordinal);
    }

    private static TestProjectFacts Facts(string outputType, bool isMtp, params string[] packages) =>
        new(outputType, isMtp, packages.ToHashSet(StringComparer.OrdinalIgnoreCase));
}
