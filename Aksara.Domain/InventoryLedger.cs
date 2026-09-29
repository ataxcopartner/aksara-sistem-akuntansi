namespace Aksara.Domain;

public sealed record StockKey(string ItemCode, string WarehouseCode);
public sealed record StockIssueLine(string ItemCode, string WarehouseCode, decimal Quantity);
public sealed record StockReceiptLine(string ItemCode, string WarehouseCode, decimal Quantity);
public sealed record StockMovement(Guid SaleId, StockKey Stock, decimal QuantityIn, decimal QuantityOut, DateTimeOffset OccurredAt);

public sealed class InsufficientStockException(string itemCode, string warehouseCode, decimal requested, decimal available)
    : InvalidOperationException($"Stok {itemCode} di gudang {warehouseCode} tidak cukup. Diminta {requested:N3}, tersedia {available:N3}.")
{
    public string ItemCode { get; } = itemCode;
    public string WarehouseCode { get; } = warehouseCode;
    public decimal Requested { get; } = requested;
    public decimal Available { get; } = available;
}

/// <summary>
/// Model atomis aturan persediaan. Implementasi produksi memakai transaksi dan row lock PostgreSQL,
/// sedangkan kelas ini menjaga aturan yang sama tetap dapat diuji tanpa database.
/// </summary>
public sealed class InventoryLedger(IEnumerable<KeyValuePair<StockKey, decimal>> openingBalances)
{
    private readonly object _sync = new();
    private readonly Dictionary<StockKey, decimal> _balances = openingBalances.ToDictionary();
    private readonly Dictionary<Guid, IReadOnlyList<StockIssueLine>> _postedSales = [];
    private readonly HashSet<Guid> _cancelledSales = [];
    private readonly Dictionary<Guid, IReadOnlyList<StockReceiptLine>> _postedPurchases = [];
    private readonly HashSet<Guid> _cancelledPurchases = [];
    private readonly List<StockMovement> _movements = [];

    public IReadOnlyList<StockMovement> Movements
    {
        get { lock (_sync) return _movements.ToArray(); }
    }

    public decimal GetBalance(string itemCode, string warehouseCode)
    {
        lock (_sync) return _balances.GetValueOrDefault(new StockKey(itemCode, warehouseCode));
    }

    public void PostSale(Guid saleId, IEnumerable<StockIssueLine> requestedLines)
    {
        var lines = requestedLines
            .GroupBy(x => new StockKey(x.ItemCode, x.WarehouseCode))
            .Select(x => new StockIssueLine(x.Key.ItemCode, x.Key.WarehouseCode, x.Sum(y => y.Quantity)))
            .OrderBy(x => x.ItemCode, StringComparer.Ordinal)
            .ThenBy(x => x.WarehouseCode, StringComparer.Ordinal)
            .ToArray();

        if (lines.Length == 0 || lines.Any(x => x.Quantity <= 0))
            throw new InvalidOperationException("Rincian pengeluaran stok tidak valid.");

        lock (_sync)
        {
            if (_postedSales.ContainsKey(saleId))
                throw new InvalidOperationException("Penjualan ini sudah pernah diposting ke persediaan.");

            foreach (var line in lines)
            {
                var key = new StockKey(line.ItemCode, line.WarehouseCode);
                var available = _balances.GetValueOrDefault(key);
                if (available < line.Quantity)
                    throw new InsufficientStockException(line.ItemCode, line.WarehouseCode, line.Quantity, available);
            }

            foreach (var line in lines)
            {
                var key = new StockKey(line.ItemCode, line.WarehouseCode);
                _balances[key] -= line.Quantity;
                _movements.Add(new StockMovement(saleId, key, 0m, line.Quantity, DateTimeOffset.UtcNow));
            }

            _postedSales.Add(saleId, lines);
        }
    }

    public void CancelSale(Guid saleId)
    {
        lock (_sync)
        {
            if (!_postedSales.TryGetValue(saleId, out var lines))
                throw new InvalidOperationException("Penjualan belum pernah diposting ke persediaan.");
            if (!_cancelledSales.Add(saleId))
                throw new InvalidOperationException("Penjualan ini sudah pernah dibatalkan.");

            foreach (var line in lines)
            {
                var key = new StockKey(line.ItemCode, line.WarehouseCode);
                _balances[key] = _balances.GetValueOrDefault(key) + line.Quantity;
                _movements.Add(new StockMovement(saleId, key, line.Quantity, 0m, DateTimeOffset.UtcNow));
            }
        }
    }

    public void PostPurchase(Guid purchaseId, IEnumerable<StockReceiptLine> requestedLines)
    {
        var lines = requestedLines
            .GroupBy(x => new StockKey(x.ItemCode, x.WarehouseCode))
            .Select(x => new StockReceiptLine(x.Key.ItemCode, x.Key.WarehouseCode, x.Sum(y => y.Quantity)))
            .OrderBy(x => x.ItemCode, StringComparer.Ordinal)
            .ThenBy(x => x.WarehouseCode, StringComparer.Ordinal)
            .ToArray();
        if (lines.Length == 0 || lines.Any(x => x.Quantity <= 0))
            throw new InvalidOperationException("Rincian penerimaan stok tidak valid.");
        lock (_sync)
        {
            if (_postedPurchases.ContainsKey(purchaseId)) throw new InvalidOperationException("Pembelian ini sudah pernah diposting ke persediaan.");
            foreach (var line in lines)
            {
                var key = new StockKey(line.ItemCode, line.WarehouseCode);
                _balances[key] = _balances.GetValueOrDefault(key) + line.Quantity;
                _movements.Add(new StockMovement(purchaseId, key, line.Quantity, 0, DateTimeOffset.UtcNow));
            }
            _postedPurchases.Add(purchaseId, lines);
        }
    }

    public void CancelPurchase(Guid purchaseId)
    {
        lock (_sync)
        {
            if (!_postedPurchases.TryGetValue(purchaseId, out var lines)) throw new InvalidOperationException("Pembelian belum pernah diposting ke persediaan.");
            if (_cancelledPurchases.Contains(purchaseId)) throw new InvalidOperationException("Pembelian ini sudah pernah dibatalkan.");
            foreach (var line in lines)
            {
                var available = _balances.GetValueOrDefault(new StockKey(line.ItemCode, line.WarehouseCode));
                if (available < line.Quantity) throw new InsufficientStockException(line.ItemCode, line.WarehouseCode, line.Quantity, available);
            }
            _cancelledPurchases.Add(purchaseId);
            foreach (var line in lines)
            {
                var key = new StockKey(line.ItemCode, line.WarehouseCode);
                _balances[key] -= line.Quantity;
                _movements.Add(new StockMovement(purchaseId, key, 0, line.Quantity, DateTimeOffset.UtcNow));
            }
        }
    }
}
