namespace Aksara.Domain;

public sealed record SalesReturnLineAmount(decimal Quantity, decimal UnitPrice, decimal DiscountPercent, decimal UnitCost)
{
    public decimal LineTotal => Math.Round(Quantity * UnitPrice * (1m - DiscountPercent / 100m), 2);
    public decimal CostTotal => Math.Round(Quantity * UnitCost, 5);
}

public sealed record SalesReturnAmounts(decimal Subtotal, decimal Discount, decimal Tax, decimal GrandTotal, decimal CostTotal);

public static class SalesReturnCalculation
{
    public static SalesReturnAmounts Calculate(
        IEnumerable<SalesReturnLineAmount> sourceLines,
        decimal discountPercent,
        decimal discountAmount,
        decimal taxPercent)
    {
        ArgumentNullException.ThrowIfNull(sourceLines);
        if (discountPercent is < 0 or > 100 || discountAmount < 0 || taxPercent is < 0 or > 100)
            throw new InvalidOperationException("Diskon atau PPN retur tidak valid.");

        var lines = sourceLines.ToArray();
        if (lines.Length == 0 || lines.Any(x => x.Quantity <= 0 || x.UnitPrice < 0 || x.UnitCost < 0 || x.DiscountPercent is < 0 or > 100))
            throw new InvalidOperationException("Rincian retur tidak valid.");

        var subtotal = lines.Sum(x => x.LineTotal);
        var discount = Math.Min(subtotal, Math.Round(subtotal * discountPercent / 100m, 2) + discountAmount);
        var taxable = subtotal - discount;
        var tax = Math.Round(taxable * taxPercent / 100m, 2);
        return new SalesReturnAmounts(subtotal, discount, tax, taxable + tax, lines.Sum(x => x.CostTotal));
    }
}
