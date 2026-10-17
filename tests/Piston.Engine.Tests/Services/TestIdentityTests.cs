// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Models;
using Piston.Engine.Services;
using Xunit;

namespace Piston.Engine.Tests.Services;

public sealed class TestIdentityTests
{
    [Theory]
    // xUnit: display names are already qualified.
    [InlineData("Ns.C.M", "Ns.C.M", "Ns.C.M")]
    [InlineData("Ns.C.M", "Ns.C.M(x: 1.5)", "Ns.C.M(x: 1.5)")]
    // MSTest / NUnit: display names are just the method (and arguments).
    [InlineData("Ns.C.M", "M", "Ns.C.M")]
    [InlineData("Ns.C.M(1,\"a.b\")", "M(1,\"a.b\")", "Ns.C.M(1,\"a.b\")")]
    [InlineData("Ns.C.M", "M (1,2)", "Ns.C.M (1,2)")]
    // Custom display names are qualified with the class.
    [InlineData("Ns.C.M", "nice name", "Ns.C.nice name")]
    [InlineData("Ns::C::M", "M", "Ns.C.M")]
    [InlineData("", "M", "M")]
    public void Of_QualifiesTheDisplayNameWithTheTestClass(string fqn, string displayName, string expected) =>
        Assert.Equal(expected, TestIdentity.Of(fqn, displayName));

    [Fact]
    public void Of_KeepsIdenticalMethodNamesInDifferentClassesApart() =>
        Assert.NotEqual(TestIdentity.Of("Ns.C1.M", "M"), TestIdentity.Of("Ns.C2.M", "M"));

    [Fact]
    public void Of_MatchesTheIdentityTrxResultParserGivesTheFinalResult()
    {
        // sample-mixed.trx: display name AddNumbers_ReturnsCorrectSum, class MyProject.Tests.MathTests.
        var path = Path.Combine(AppContext.BaseDirectory, "Services", "Fixtures", "sample-mixed.trx");
        var passed = new TrxResultParser().Parse(path)[0].Tests.Single(t => t.Status == TestStatus.Passed);

        Assert.Equal(
            passed.FullyQualifiedName,
            TestIdentity.Of("MyProject.Tests.MathTests.AddNumbers_ReturnsCorrectSum", "AddNumbers_ReturnsCorrectSum"));
    }
}
