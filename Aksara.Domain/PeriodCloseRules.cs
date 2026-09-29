namespace Aksara.Domain;

public sealed record PeriodCloseInventoryMovement(
    Guid Id,
    DateTimeOffset OccurredAt,
    string ItemCode,
    string ItemName,
    string WarehouseCode,
    string SourceType,
    Guid SourceId,
    string SourceNumber,
    decimal QuantityIn,
    decimal QuantityOut,
    decimal UnitCost);

public sealed record PeriodCloseInventoryLayer(
    string ItemCode,
    string ItemName,
    string WarehouseCode,
    DateTimeOffset ReceivedAt,
    string SourceType,
    Guid SourceId,
    string SourceNumber,
    decimal OriginalQuantity,
    decimal RemainingQuantity,
    decimal UnitCost);

public sealed record PeriodCloseInventoryBalance(
    string ItemCode,
    string ItemName,
    string WarehouseCode,
    decimal Quantity,
    decimal UnitCost,
    decimal Value);

public sealed record PeriodCloseInventorySnapshot(
    IReadOnlyList<PeriodCloseInventoryBalance> Balances,
    IReadOnlyList<PeriodCloseInventoryLayer> Layers,
    IReadOnlyList<string> Errors);

public static class PeriodCloseRules
{
    public static string ValidateRequest(DateOnly cutoffDate, DateOnly openingDate, string? periodName)
    {
        if (cutoffDate == default) throw new InvalidOperationException("Tanggal akhir periode wajib diisi.");
        if (openingDate == default) throw new InvalidOperationException("Tanggal saldo awal periode berikutnya wajib diisi.");
        if (openingDate < cutoffDate) throw new InvalidOperationException("Tanggal saldo awal baru tidak boleh sebelum tanggal akhir periode yang ditutup.");
        var normalizedName = (periodName ?? string.Empty).Trim();
        if (normalizedName.Length == 0) throw new InvalidOperationException("Nama periode wajib diisi.");
        if (normalizedName.Length > 100) throw new InvalidOperationException("Nama periode maksimal 100 karakter.");
        return normalizedName;
    }

    public static void ValidateReopenSelection(Guid selectedId, string? status, Guid? latestPostedId)
    {
        if (!string.Equals(status, "POSTED", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Hanya histori Tutup Buku yang masih aktif yang dapat dibatalkan.");
        if (latestPostedId is null)
            throw new InvalidOperationException("Tidak ada periode aktif yang dapat dibatalkan.");
        if (selectedId != latestPostedId.Value)
            throw new InvalidOperationException("Hanya Tutup Buku paling akhir yang dapat dibatalkan.");
    }

    public static bool CanReopen(string? status, bool isLatestPosted, bool isAuthorized) =>
        isAuthorized && isLatestPosted && string.Equals(status, "POSTED", StringComparison.OrdinalIgnoreCase);

    public static PeriodCloseInventorySnapshot BuildInventorySnapshot(IEnumerable<PeriodCloseInventoryMovement> movements)
    {
        var errors = new List<string>();
        var states = new Dictionary<string, InventoryState>(StringComparer.OrdinalIgnoreCase);
        foreach (var movement in movements.OrderBy(x => x.OccurredAt).ThenBy(x => x.Id))
        {
            var key = $"{movement.ItemCode}\u001f{movement.WarehouseCode}";
            if (!states.TryGetValue(key, out var state))
            {
                state = new InventoryState(movement.ItemCode, movement.ItemName, movement.WarehouseCode);
                states[key] = state;
            }

            var quantityIn = decimal.Round(movement.QuantityIn, 3);
            var quantityOut = decimal.Round(movement.QuantityOut, 3);
            var unitCost = decimal.Round(movement.UnitCost, 5);
            if (quantityIn < 0 || quantityOut < 0 || (quantityIn > 0 && quantityOut > 0))
            {
                errors.Add($"Mutasi {movement.SourceNumber} untuk {movement.ItemCode}/{movement.WarehouseCode} memiliki kuantitas tidak valid.");
                continue;
            }

            if (quantityIn > 0)
            {
                state.Layers.Add(new MutableLayer(
                    movement.ItemCode, movement.ItemName, movement.WarehouseCode, movement.OccurredAt,
                    movement.SourceType, movement.SourceId, movement.SourceNumber, quantityIn, quantityIn, unitCost));
            }

            var remainingIssue = quantityOut;
            foreach (var layer in state.Layers.Where(x => x.RemainingQuantity > 0).OrderBy(x => x.ReceivedAt))
            {
                if (remainingIssue <= 0) break;
                var used = Math.Min(layer.RemainingQuantity, remainingIssue);
                layer.RemainingQuantity -= used;
                remainingIssue -= used;
            }
            if (remainingIssue > 0)
                errors.Add($"Stok {movement.ItemCode}/{movement.WarehouseCode} kurang {remainingIssue:N3} saat mutasi {movement.SourceNumber}.");
        }

        var layers = states.Values
            .SelectMany(x => x.Layers)
            .Where(x => x.RemainingQuantity > 0)
            .OrderBy(x => x.ItemCode).ThenBy(x => x.WarehouseCode).ThenBy(x => x.ReceivedAt)
            .Select(x => new PeriodCloseInventoryLayer(
                x.ItemCode, x.ItemName, x.WarehouseCode, x.ReceivedAt, x.SourceType, x.SourceId,
                x.SourceNumber, x.OriginalQuantity, x.RemainingQuantity, x.UnitCost))
            .ToArray();
        var balances = layers
            .GroupBy(x => new { x.ItemCode, x.ItemName, x.WarehouseCode })
            .Select(group =>
            {
                var quantity = group.Sum(x => x.RemainingQuantity);
                var value = group.Sum(x => x.RemainingQuantity * x.UnitCost);
                return new PeriodCloseInventoryBalance(
                    group.Key.ItemCode, group.Key.ItemName, group.Key.WarehouseCode,
                    quantity, quantity == 0 ? 0 : value / quantity, value);
            })
            .OrderBy(x => x.ItemCode).ThenBy(x => x.WarehouseCode)
            .ToArray();
        return new(balances, layers, errors.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private sealed class InventoryState(string itemCode, string itemName, string warehouseCode)
    {
        public string ItemCode { get; } = itemCode;
        public string ItemName { get; } = itemName;
        public string WarehouseCode { get; } = warehouseCode;
        public List<MutableLayer> Layers { get; } = [];
    }

    private sealed class MutableLayer(
        string itemCode,
        string itemName,
        string warehouseCode,
        DateTimeOffset receivedAt,
        string sourceType,
        Guid sourceId,
        string sourceNumber,
        decimal originalQuantity,
        decimal remainingQuantity,
        decimal unitCost)
    {
        public string ItemCode { get; } = itemCode;
        public string ItemName { get; } = itemName;
        public string WarehouseCode { get; } = warehouseCode;
        public DateTimeOffset ReceivedAt { get; } = receivedAt;
        public string SourceType { get; } = sourceType;
        public Guid SourceId { get; } = sourceId;
        public string SourceNumber { get; } = sourceNumber;
        public decimal OriginalQuantity { get; } = originalQuantity;
        public decimal RemainingQuantity { get; set; } = remainingQuantity;
        public decimal UnitCost { get; } = unitCost;
    }
}
