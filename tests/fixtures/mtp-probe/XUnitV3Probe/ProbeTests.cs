using Xunit;

namespace XUnitV3Probe;

public sealed class AlphaTests
{
    [Fact]
    [Trait("Category", "probe-category")]
    public void SameName() { }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Parameterized(int value) => Assert.True(value > 0);

    [Fact(DisplayName = "Custom visible name")]
    public void CustomName() { }

    [Fact]
    public void AssertionFailure() => Assert.Equal(2, 1);

    [Fact]
    public void ThrownException() => throw new InvalidOperationException("probe exception");

    [Fact]
    public void WritesOutput() => Console.WriteLine("probe output");

    [Fact(Skip = "probe skip reason")]
    public void Skipped() { }
}

public sealed class BetaTests
{
    [Fact]
    public void SameName() { }
}
