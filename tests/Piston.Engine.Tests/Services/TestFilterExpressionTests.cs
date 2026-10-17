// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Services;
using Xunit;

namespace Piston.Engine.Tests.Services;

public sealed class TestFilterExpressionTests
{
    private static bool Matches(string filter, string fqn, string? category = null)
    {
        Assert.True(TestFilterExpression.TryParse(filter, out var expression));
        return expression!.Matches(property => property.ToUpperInvariant() switch
        {
            "FULLYQUALIFIEDNAME" => [fqn],
            "NAME" => [fqn[(fqn.LastIndexOf('.') + 1)..]],
            "CATEGORY" when category is not null => [category],
            _ => [],
        });
    }

    [Theory]
    [InlineData("FullyQualifiedName=A.B.Test", true)]
    [InlineData("fullyqualifiedname=a.b.test", true)]
    [InlineData("FullyQualifiedName=A.B", false)]
    [InlineData("FullyQualifiedName~B.Te", true)]
    [InlineData("FullyQualifiedName!=A.B.Test", false)]
    [InlineData("FullyQualifiedName!~Other", true)]
    [InlineData("B.Test", true)]
    [InlineData("Name=Test", true)]
    [InlineData("Name=Other|Name=Test", true)]
    [InlineData("Name=Test&FullyQualifiedName~Other", false)]
    [InlineData("(Name=Other|Name=Test)&FullyQualifiedName~A.", true)]
    [InlineData(" Name = Test ", true)]
    public void EvaluatesConditionsAndOperators(string filter, bool expected) =>
        Assert.Equal(expected, Matches(filter, "A.B.Test"));

    [Fact]
    public void MissingPropertyMatchesOnlyNegatedConditions()
    {
        Assert.False(Matches("Category=Slow", "A.B.Test"));
        Assert.True(Matches("Category!=Slow", "A.B.Test"));
        Assert.True(Matches("Category=Slow", "A.B.Test", category: "slow"));
    }

    [Fact]
    public void EscapedAndParenthesizedValuesAreLiteral()
    {
        Assert.True(Matches("FullyQualifiedName=A.T(x: a\\|b)", "A.T(x: a|b)"));
        Assert.True(Matches("FullyQualifiedName=A.T(n: 1)", "A.T(n: 1)"));
        Assert.True(Matches("(FullyQualifiedName=A.T(n: 1))|Name=Z", "A.T(n: 1)"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Name=")]
    [InlineData("(Name=Test")]
    [InlineData("Name=Test)")]
    [InlineData("Name=Test|")]
    [InlineData("&Name=Test")]
    public void RejectsUnsupportedSyntax(string filter) =>
        Assert.False(TestFilterExpression.TryParse(filter, out _));
}
