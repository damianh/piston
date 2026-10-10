using Measurement.Orders;
using Xunit;

namespace Measurement.Orders.Tests;

public sealed class OrderTests
{
    public static IEnumerable<object[]> Cases() =>
        Enumerable.Range(1, 200).Select(i => new object[] { i });

    [Theory]
    [MemberData(nameof(Cases))]
    public void ReservePreservesInventory(int quantity) =>
        Assert.Equal(500 - quantity, Order.Reserve(500, quantity));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(501)]
    public void InvalidReservation(int quantity) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Order.Reserve(500, quantity));

    [Fact]
    public void OrderDiscountBoundary() =>
        Assert.Equal(90m, Order.Total([new Line(25m, 4)]));

    [Fact]
    public void SharedRoundingBoundary() =>
        Assert.Equal(1.00m, Order.Total([new Line(1.005m, 1)]));

    [Fact]
    public void RejectNegativePrice() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Order.Total([new Line(-1m, 1)]));
}
