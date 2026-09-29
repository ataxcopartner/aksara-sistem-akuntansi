namespace Aksara.Domain;

public enum InventoryCostFlowMethod
{
    Fifo,
    Lifo
}

public sealed record InventoryCostLot(
    Guid Id,
    string WarehouseCode,
    DateOnly DocumentDate,
    int Sequence,
    decimal Quantity,
    decimal UnitCost);

public sealed record InventoryCostLotAllocation(
    Guid LotId,
    decimal ConsumedQuantity,
    decimal RemainingQuantity,
    decimal ConsumedValue,
    decimal RemainingValue);

public sealed record InventoryCostFlowResult(
    IReadOnlyList<InventoryCostLotAllocation> Lots,
    decimal RequestedQuantity,
    decimal AllocatedQuantity,
    decimal CostOfGoodsSold,
    decimal RemainingQuantity,
    decimal RemainingValue,
    decimal NextIssueUnitCost)
{
    public decimal HppPerUnit => AllocatedQuantity == 0 ? 0 : CostOfGoodsSold / AllocatedQuantity;
    public decimal UnallocatedQuantity => Math.Max(0, RequestedQuantity - AllocatedQuantity);
}

public static class InventoryCostAnalysis
{
    public static InventoryCostFlowResult Allocate(
        IReadOnlyCollection<InventoryCostLot> lots,
        IReadOnlyDictionary<string, decimal> issuedByWarehouse,
        InventoryCostFlowMethod method)
    {
        if (lots.Any(x => x.Quantity < 0 || x.UnitCost < 0))
            throw new InvalidOperationException("Lot persediaan tidak boleh mempunyai quantity atau biaya negatif.");
        if (issuedByWarehouse.Any(x => x.Value < 0))
            throw new InvalidOperationException("Quantity barang keluar tidak boleh negatif.");

        var normalizedIssues = issuedByWarehouse
            .GroupBy(x => NormalizeWarehouse(x.Key), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Sum(y => y.Value), StringComparer.OrdinalIgnoreCase);
        var allocations = new Dictionary<Guid, InventoryCostLotAllocation>();
        foreach (var warehouseLots in lots.GroupBy(x => NormalizeWarehouse(x.WarehouseCode), StringComparer.OrdinalIgnoreCase))
        {
            var ordered = method == InventoryCostFlowMethod.Fifo
                ? warehouseLots.OrderBy(x => x.DocumentDate).ThenBy(x => x.Sequence).ThenBy(x => x.Id)
                : warehouseLots.OrderByDescending(x => x.DocumentDate).ThenByDescending(x => x.Sequence).ThenByDescending(x => x.Id);
            var remainingIssue = normalizedIssues.TryGetValue(warehouseLots.Key, out var issue) ? issue : 0;

            foreach (var lot in ordered)
            {
                var consumed = Math.Min(lot.Quantity, remainingIssue);
                var remaining = lot.Quantity - consumed;
                allocations[lot.Id] = new(
                    lot.Id,
                    consumed,
                    remaining,
                    consumed * lot.UnitCost,
                    remaining * lot.UnitCost);
                remainingIssue -= consumed;
            }
        }

        foreach (var lot in lots.Where(x => !allocations.ContainsKey(x.Id)))
            allocations[lot.Id] = new(lot.Id, 0, lot.Quantity, 0, lot.Quantity * lot.UnitCost);

        var orderedResult = lots.Select(x => allocations[x.Id]).ToArray();
        var requested = issuedByWarehouse.Values.Sum();
        var nextIssueUnitCost = GetNextIssueUnitCost(lots, orderedResult, method);
        return new(
            orderedResult,
            requested,
            orderedResult.Sum(x => x.ConsumedQuantity),
            orderedResult.Sum(x => x.ConsumedValue),
            orderedResult.Sum(x => x.RemainingQuantity),
            orderedResult.Sum(x => x.RemainingValue),
            nextIssueUnitCost);
    }

    public static decimal GetNextIssueUnitCost(
        IReadOnlyCollection<InventoryCostLot> lots,
        IReadOnlyCollection<InventoryCostLotAllocation> currentAllocations,
        InventoryCostFlowMethod method)
    {
        var remainingByLot = currentAllocations.ToDictionary(x => x.LotId, x => x.RemainingQuantity);
        var ordered = method == InventoryCostFlowMethod.Fifo
            ? lots.OrderBy(x => x.DocumentDate).ThenBy(x => x.Sequence).ThenBy(x => x.Id)
            : lots.OrderByDescending(x => x.DocumentDate).ThenByDescending(x => x.Sequence).ThenByDescending(x => x.Id);
        return ordered.FirstOrDefault(x => remainingByLot.GetValueOrDefault(x.Id) > 0)?.UnitCost ?? 0;
    }

    private static string NormalizeWarehouse(string warehouseCode) => warehouseCode.Trim().ToUpperInvariant();
}
