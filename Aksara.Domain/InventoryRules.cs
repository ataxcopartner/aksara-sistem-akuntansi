namespace Aksara.Domain;

public sealed record InventoryTierRule(string Code, decimal Price, decimal DiscountPercent, decimal MinimumQuantity);

public static class InventoryRules
{
    private static readonly string[] RequiredTierCodes = ["RETAIL", "WHOLESALE", "BULK", "SPECIAL1", "SPECIAL2", "SPECIAL3"];

    public static void ValidatePriceTiers(IReadOnlyCollection<InventoryTierRule> tiers)
    {
        if (tiers.Count != RequiredTierCodes.Length)
            throw new InvalidOperationException("Enam tingkat harga Barang wajib diisi.");

        var normalized = tiers.Select(x => x with { Code = x.Code.Trim().ToUpperInvariant() }).ToArray();
        if (normalized.Select(x => x.Code).Distinct().Count() != RequiredTierCodes.Length ||
            RequiredTierCodes.Except(normalized.Select(x => x.Code)).Any())
            throw new InvalidOperationException("Tingkat harga harus Eceran, Grosir, Partai, Khusus 1, Khusus 2, dan Khusus 3.");

        foreach (var tier in normalized)
        {
            if (tier.Price < 0) throw new InvalidOperationException($"Harga {tier.Code} tidak boleh negatif.");
            if (tier.DiscountPercent is < 0 or > 100) throw new InvalidOperationException($"Diskon {tier.Code} tidak valid.");
            if (tier.MinimumQuantity < 0) throw new InvalidOperationException($"Batas quantity {tier.Code} tidak boleh negatif.");
        }

        var ordered = RequiredTierCodes.Select(code => normalized.Single(x => x.Code == code)).ToArray();
        if (ordered[0].MinimumQuantity > 1)
            throw new InvalidOperationException("Batas quantity Eceran harus dimulai dari nol atau satu.");
        for (var index = 1; index < ordered.Length; index++)
            if (ordered[index].MinimumQuantity < ordered[index - 1].MinimumQuantity)
                throw new InvalidOperationException("Batas quantity tingkat harga harus berurutan dari Eceran sampai Khusus 3.");
    }

    public static decimal CalculateWeightedAverage(decimal currentQuantity, decimal currentCost, decimal incomingQuantity, decimal incomingCost)
    {
        if (currentQuantity < 0 || incomingQuantity <= 0 || currentCost < 0 || incomingCost < 0)
            throw new InvalidOperationException("Data perhitungan HPP rata-rata tidak valid.");
        var resultingQuantity = currentQuantity + incomingQuantity;
        return resultingQuantity == 0
            ? 0
            : Math.Round(((currentQuantity * currentCost) + (incomingQuantity * incomingCost)) / resultingQuantity, 5);
    }

    public static decimal ResolveAdjustmentQuantity(string direction, decimal quantity)
    {
        var normalized = direction.Trim().ToUpperInvariant();
        if (quantity == 0) throw new InvalidOperationException("Jumlah penyesuaian tidak boleh nol.");
        return normalized switch
        {
            "IN" when quantity > 0 => quantity,
            "OUT" when quantity > 0 => -quantity,
            "BOTH" => quantity,
            "IN" => throw new InvalidOperationException("Jenis penyesuaian masuk membutuhkan jumlah positif."),
            "OUT" => throw new InvalidOperationException("Jenis penyesuaian keluar membutuhkan jumlah positif."),
            _ => throw new InvalidOperationException("Arah penyesuaian tidak dikenal.")
        };
    }

    public static decimal CalculateStockCountDifference(decimal systemQuantity, decimal countedQuantity)
    {
        if (countedQuantity < 0) throw new InvalidOperationException("Hasil stok fisik tidak boleh negatif.");
        return countedQuantity - systemQuantity;
    }

    public static void ValidateTransfer(string sourceWarehouse, string destinationWarehouse)
    {
        if (string.IsNullOrWhiteSpace(sourceWarehouse) || string.IsNullOrWhiteSpace(destinationWarehouse))
            throw new InvalidOperationException("Gudang asal dan tujuan wajib dipilih.");
        if (string.Equals(sourceWarehouse.Trim(), destinationWarehouse.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Gudang asal dan tujuan Mutasi harus berbeda.");
    }

    public static decimal RemainingMbapQuantity(decimal sentQuantity, decimal receivedQuantity)
    {
        if (sentQuantity < 0 || receivedQuantity < 0 || receivedQuantity > sentQuantity)
            throw new InvalidOperationException("Jumlah MBAP Kirim/Terima tidak valid.");
        return sentQuantity - receivedQuantity;
    }

    public static void ValidateMbapReceipt(decimal sentQuantity, decimal alreadyReceived, decimal requested)
    {
        if (requested <= 0) throw new InvalidOperationException("Jumlah MBAP Terima harus lebih dari nol.");
        var remaining = RemainingMbapQuantity(sentQuantity, alreadyReceived);
        if (requested > remaining)
            throw new InvalidOperationException($"Jumlah MBAP Terima {requested:N3} melebihi sisa kirim {remaining:N3}.");
    }
}
