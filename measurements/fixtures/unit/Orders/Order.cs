using Measurement.Pricing;

namespace Measurement.Orders;

public sealed record Line(decimal Price, int Quantity);

public static class Order
{
    public static decimal Total(IEnumerable<Line> lines)
    {
        var subtotal = 0m;
        foreach (var line in lines)
        {
            if (line.Quantity <= 0 || line.Price < 0)
                throw new ArgumentOutOfRangeException(nameof(lines));
            subtotal += line.Price * line.Quantity;
        }
        return PriceRules.Total(subtotal);
    }

    public static int Reserve(int available, int requested)
    {
        if (requested <= 0 || requested > available)
            throw new ArgumentOutOfRangeException(nameof(requested));
        return available - requested;
    }
}
