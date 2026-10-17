// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

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
