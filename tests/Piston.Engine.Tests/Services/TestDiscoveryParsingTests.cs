// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Models;
using Piston.Engine.Services;
using Xunit;

namespace Piston.Engine.Tests.Services;

public sealed class TestDiscoveryParsingTests
{
    [Fact]
    public void ParseListTestsOutput_ReadsIndentedNamesAfterHeader()
    {
        string[] lines =
        [
            "  Determining projects to restore...",
            "Test run for /src/A.Tests/bin/Debug/net10.0/A.Tests.dll (.NETCoreApp,Version=v10.0)",
            "The following Tests are available:",
            "    A.Tests.FooTests.Adds",
            "    A.Tests.FooTests.Parses(input: \"a.b\", expected: 1)",
            "    A.Tests.FooTests.Adds",
            "Test run for /src/B.Tests/bin/Debug/net10.0/B.Tests.dll (.NETCoreApp,Version=v10.0)",
            "    Not.A.Test",
            "The following Tests are available:",
            "    B.Tests.BarTests.Works",
        ];

        var tests = TestProcessRunner.ParseListTestsOutput(lines);

        Assert.Equal(
            ["A.Tests.FooTests.Adds", "A.Tests.FooTests.Parses(input: \"a.b\", expected: 1)", "B.Tests.BarTests.Works"],
            tests.Select(t => t.FullyQualifiedName));
        Assert.All(tests, t => Assert.Equal(TestStatus.NotRun, t.Status));
        Assert.Equal("Parses(input: \"a.b\", expected: 1)", tests[1].DisplayName);
    }

    [Fact]
    public void ParseListTestsOutput_NoHeader_ReturnsEmpty() => Assert.Empty(TestProcessRunner.ParseListTestsOutput(["Build FAILED.", "    error CS1002"]));

    [Fact]
    public void MtpParseListTestsOutput_ReadsNamesUnderEachAssemblyHeader()
    {
        // Captured from `dotnet test --project X.csproj --no-build --list-tests` (SDK 10, xunit.v3 3.2.2).
        string[] lines =
        [
            "Discovering tests from /tmp/X/bin/Debug/net10.0/X.dll (net10.0|x64)",
            "",
            "Discovered 5 tests in assembly - /tmp/X/bin/Debug/net10.0/X.dll (net10.0|x64)",
            "  Probe.Deep.SampleTests.Passing",
            "  Probe.Deep.SampleTests.Theory(s: \"a.b\", n: 1)",
            "  custom display name",
            "  Probe.Deep.SampleTests+Nested.Inner",
            "",
            "Discovered 1 test in assembly - /tmp/X/bin/Debug/net9.0/X.dll (net9.0|x64)",
            "  Probe.Deep.SampleTests.Passing",
            "  Probe.Deep.SampleTests.OnlyOnNet9",
            "",
            "Discovered 6 tests.",
            "  Not.A.Test",
        ];

        var tests = MtpTestProcessRunner.ParseListTestsOutput(lines);

        Assert.Equal(
            [
                "Probe.Deep.SampleTests.Passing",
                "Probe.Deep.SampleTests.Theory(s: \"a.b\", n: 1)",
                "custom display name",
                "Probe.Deep.SampleTests+Nested.Inner",
                "Probe.Deep.SampleTests.OnlyOnNet9",
            ],
            tests.Select(t => t.FullyQualifiedName));
        Assert.All(tests, t => Assert.Equal(TestStatus.NotRun, t.Status));
        Assert.Equal("Theory(s: \"a.b\", n: 1)", tests[1].DisplayName);
    }

    [Fact]
    public void MtpParseListTestsOutput_FailedDiscovery_ReturnsEmpty() =>
        Assert.Empty(MtpTestProcessRunner.ParseListTestsOutput(
            ["The following exception occurred when running the test module", "", "Discovered 0 tests."]));

    [Fact]
    public void TestListParser_ReportsEachNewTestAsItsLineArrives()
    {
        var parser = new TestListParser(line => line == "The following Tests are available:");

        Assert.False(parser.ProcessLine("Build succeeded."));
        Assert.False(parser.ProcessLine("The following Tests are available:"));
        Assert.True(parser.ProcessLine("    A.B.One"));
        _ = Assert.Single(parser.Results);
        Assert.False(parser.ProcessLine("    A.B.One"));
        Assert.False(parser.ProcessLine("   "));
        Assert.True(parser.ProcessLine("    A.B.Two"));
        Assert.False(parser.ProcessLine("Done."));
        Assert.False(parser.ProcessLine("    Not.A.Test"));

        Assert.Equal(["A.B.One", "A.B.Two"], parser.Results.Select(t => t.FullyQualifiedName));
    }

    [Theory]
    [InlineData("Ns.Class.Method", "Method")]
    [InlineData("Method", "Method")]
    [InlineData("Ns.Class.Method(path: \"x.y\")", "Method(path: \"x.y\")")]
    public void DisplayNameOf_ReturnsLastSegmentIgnoringArguments(string fqn, string expected) => Assert.Equal(expected, TestProcessRunner.DisplayNameOf(fqn));
}
