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

    [Theory]
    [InlineData("Ns.Class.Method", "Method")]
    [InlineData("Method", "Method")]
    [InlineData("Ns.Class.Method(path: \"x.y\")", "Method(path: \"x.y\")")]
    public void DisplayNameOf_ReturnsLastSegmentIgnoringArguments(string fqn, string expected) => Assert.Equal(expected, TestProcessRunner.DisplayNameOf(fqn));
}
