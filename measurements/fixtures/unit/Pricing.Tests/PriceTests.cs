using Measurement.Pricing;
using Xunit;

namespace Measurement.Pricing.Tests;

public sealed class PriceTests
{
    public static IEnumerable<object[]> Cases() =>
        Enumerable.Range(1, 200).Select(i => new object[] { i });

    [Theory]
    [MemberData(nameof(Cases))]
    public void PricesAcrossDiscountBoundary(int cents)
    {
        var subtotal = cents + 0.1m;
        var expected = Math.Round(subtotal >= 100m ? subtotal * 0.9m : subtotal,
            2, MidpointRounding.ToEven);
        Assert.Equal(expected, PriceRules.Total(subtotal));
    }

    [Fact]
    public void DiscountBoundary() => Assert.Equal(90m, PriceRules.Total(100m));

    [Fact]
    public void RoundingBoundary() => Assert.Equal(1.00m, PriceRules.Round(1.005m));

    [Fact]
    public void ExistingFailure() => Assert.False(PriceRules.KnownFailure);
}
