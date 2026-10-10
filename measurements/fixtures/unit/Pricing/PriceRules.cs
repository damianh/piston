namespace Measurement.Pricing;

public static class PriceRules
{
    public const bool KnownFailure = false;
    public const decimal DiscountThreshold = 100m;
    public const MidpointRounding Rounding = MidpointRounding.ToEven;

    public static decimal Round(decimal amount) => Math.Round(amount, 2, Rounding);

    public static decimal Total(decimal subtotal) =>
        Round(subtotal >= DiscountThreshold ? subtotal * 0.9m : subtotal);
}
