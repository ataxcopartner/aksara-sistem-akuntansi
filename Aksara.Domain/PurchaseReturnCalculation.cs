namespace Aksara.Domain;

public sealed record PurchaseReturnLineAmount(decimal Quantity, decimal UnitPrice, decimal DiscountPercent)
{
    public decimal LineTotal => Math.Round(Quantity * UnitPrice * (1m - DiscountPercent / 100m), 2);
}

public sealed record PurchaseReturnAmounts(decimal Subtotal, decimal Discount, decimal Tax, decimal GrandTotal);

public static class PurchaseReturnCalculation
{
    public static PurchaseReturnAmounts Calculate(IEnumerable<PurchaseReturnLineAmount> sourceLines, decimal discountPercent, decimal discountAmount, decimal taxPercent)
    {
        ArgumentNullException.ThrowIfNull(sourceLines);
        if (discountPercent is < 0 or > 100 || discountAmount < 0 || taxPercent is < 0 or > 100)
            throw new InvalidOperationException("Diskon atau PPN retur pembelian tidak valid.");
        var lines = sourceLines.ToArray();
        if (lines.Length == 0 || lines.Any(x => x.Quantity <= 0 || x.UnitPrice < 0 || x.DiscountPercent is < 0 or > 100))
            throw new InvalidOperationException("Rincian retur pembelian tidak valid.");
        var subtotal = lines.Sum(x => x.LineTotal);
        var discount = Math.Min(subtotal, Math.Round(subtotal * discountPercent / 100m, 2) + discountAmount);
        var taxable = subtotal - discount;
        var tax = Math.Round(taxable * taxPercent / 100m, 2);
        return new(subtotal, discount, tax, taxable + tax);
    }
}

