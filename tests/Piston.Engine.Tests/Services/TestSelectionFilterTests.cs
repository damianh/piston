// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Models;
using Piston.Engine.Services;
using Xunit;

namespace Piston.Engine.Tests.Services;

public sealed class TestSelectionFilterTests
{
    [Fact]
    public void NoTests_IsNoFilter()
    {
        Assert.Null(TestSelectionFilter.Build(null));
        Assert.Null(TestSelectionFilter.Build([]));
    }

    [Fact]
    public void Methods_AreMatchedByName_OnceEach()
    {
        var filter = TestSelectionFilter.Build(["Ns.C.B", "Ns.C.A(x: 1)", "Ns.C.A(x: \"a.b\")", "Ns.C.B"]);

        Assert.Equal("FullyQualifiedName=Ns.C.A|FullyQualifiedName=Ns.C.B", filter);
    }

    [Fact]
    public void FilterSyntax_IsEscaped()
    {
        var filter = TestSelectionFilter.Build([@"Ns.C<T>.A!=B~C\D"]);

        Assert.Equal(@"FullyQualifiedName=Ns.C<T>.A\!\=B\~C\\D", filter);
    }

    [Fact]
    public void CustomDisplayName_IsMatchedByItsMethod()
    {
        static TestResult T(string identity, string? method) =>
            new(identity, identity, TestStatus.Passed, TimeSpan.Zero, null, null, null, null) { Method = method };

        var known = new[]
        {
            T("Ns.C.nice name", "Ns.C.M"),
            T("Ns.C.Theory(x: 1)", "Ns.C.Theory"),
            T("Ns.C.Other", null),
        };

        var filter = TestSelectionFilter.Build(["Ns.C.nice name", "Ns.C.Theory", "Ns.C.Unknown"], known);

        Assert.Equal("FullyQualifiedName=Ns.C.M|FullyQualifiedName=Ns.C.Theory|FullyQualifiedName=Ns.C.Unknown", filter);
    }

    [Fact]
    public void CustomDisplayNames_SharingAName_SelectOnlyTheirOwnMethod()
    {
        static TestResult T(string identity, string method) =>
            new(identity, identity, TestStatus.Passed, TimeSpan.Zero, null, null, null, null) { Method = method };

        var known = new[]
        {
            T("Ns.C.checkout (guest)", "Ns.C.GuestCheckout"),
            T("Ns.C.checkout (member)", "Ns.C.MemberCheckout"),
        };

        Assert.Equal("FullyQualifiedName=Ns.C.GuestCheckout", TestSelectionFilter.Build(["Ns.C.checkout (guest)"], known));
        Assert.Equal(
            "FullyQualifiedName=Ns.C.GuestCheckout|FullyQualifiedName=Ns.C.MemberCheckout",
            TestSelectionFilter.Build(["Ns.C.checkout"], known));
    }

    [Theory]
    [InlineData("X\" --settings \"/tmp/extra.runsettings")]
    [InlineData("Ns.C.say \"hi\"")]
    public void DoubleQuote_IsRejected(string test)
    {
        var ex = Assert.Throws<ArgumentException>(() => TestSelectionFilter.Build([test]));

        Assert.Contains("double quote", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DoubleQuoteInArguments_IsIgnoredWithThem() =>
        Assert.Equal("FullyQualifiedName=Ns.C.M", TestSelectionFilter.Build(["Ns.C.M(s: \"a\")"]));

    [Fact]
    public void ManyMethods_AreMatchedByClass()
    {
        var tests = Enumerable.Range(0, TestSelectionFilter.MaxConditions + 1)
            .Select(i => $"Ns.C{i % 2}.M{i}")
            .ToList();

        var filter = TestSelectionFilter.Build(tests);

        Assert.Equal("FullyQualifiedName~Ns.C0.|FullyQualifiedName~Ns.C1.", filter);
    }

    [Fact]
    public void ManyClasses_RunEverything()
    {
        var tests = Enumerable.Range(0, TestSelectionFilter.MaxConditions + 1)
            .Select(i => $"Ns.C{i}.M")
            .ToList();

        Assert.Null(TestSelectionFilter.Build(tests));
    }
}
