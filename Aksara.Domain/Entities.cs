namespace Aksara.Domain;

public enum DocumentStatus { Draft, Posted, Cancelled }

public abstract class AuditableEntity
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public string CreatedBy { get; init; } = string.Empty;
    public DateTimeOffset? UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
    public long Version { get; set; }
}

public sealed class InventoryItem : AuditableEntity
{
    public required string Code { get; init; }
    public required string Name { get; set; }
    public string Unit { get; set; } = "UNIT";
    public decimal AverageCost { get; set; }
    public decimal RetailPrice { get; set; }
    public decimal MinimumStock { get; set; }
    public decimal MaximumStock { get; set; }
    public bool IsActive { get; set; } = true;
    public int? LegacyDataId { get; init; }
}

public sealed class Purchase : AuditableEntity
{
    private readonly List<PurchaseLine> _lines = [];

    public required string Number { get; init; }
    public DateOnly DocumentDate { get; init; }
    public required string SupplierCode { get; init; }
    public bool IsCredit { get; set; }
    public DateOnly? DueDate { get; set; }
    public decimal DiscountPercent { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal TaxPercent { get; set; }
    public decimal DownPayment { get; set; }
    public string WarehouseCode { get; set; } = "UTAMA";
    public string? Notes { get; set; }
    public DocumentStatus Status { get; private set; } = DocumentStatus.Draft;
    public IReadOnlyList<PurchaseLine> Lines => _lines;

    public decimal Subtotal => _lines.Sum(x => x.Total);
    public decimal CalculatedDiscount => Math.Min(Subtotal, Math.Round(Subtotal * DiscountPercent / 100m, 2) + DiscountAmount);
    public decimal NetBeforeTax => Subtotal - CalculatedDiscount;
    public decimal TaxAmount => Math.Round(NetBeforeTax * TaxPercent / 100m, 2);
    public decimal GrandTotal => NetBeforeTax + TaxAmount;
    public decimal BalanceDue => Math.Max(0m, GrandTotal - DownPayment);

    public void AddLine(PurchaseLine line)
    {
        if (Status != DocumentStatus.Draft)
            throw new InvalidOperationException("Rincian hanya dapat diubah saat dokumen masih draft.");
        if (line.Quantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(line), "Jumlah barang harus lebih dari nol.");
        _lines.Add(line);
    }

    public void Post()
    {
        if (_lines.Count == 0)
            throw new InvalidOperationException("Pembelian tidak dapat diposting tanpa rincian.");
        if (IsCredit && DueDate is null)
            throw new InvalidOperationException("Tanggal jatuh tempo wajib diisi untuk pembelian kredit.");
        if (IsCredit && DueDate < DocumentDate)
            throw new InvalidOperationException("Tanggal jatuh tempo tidak boleh lebih awal dari tanggal faktur.");
        if (DiscountPercent is < 0 or > 100 || DiscountAmount < 0)
            throw new InvalidOperationException("Diskon faktur tidak valid.");
        if (TaxPercent is < 0 or > 100)
            throw new InvalidOperationException("Persentase PPN tidak valid.");
        if (DownPayment < 0 || DownPayment > GrandTotal)
            throw new InvalidOperationException("Uang muka tidak valid.");
        Status = DocumentStatus.Posted;
    }

    public void Cancel()
    {
        if (Status != DocumentStatus.Posted)
            throw new InvalidOperationException("Hanya pembelian yang sudah disimpan yang dapat dibatalkan.");
        Status = DocumentStatus.Cancelled;
    }
}

public sealed record PurchaseLine(string ItemCode, string ItemName, decimal Quantity, string Unit, decimal UnitPrice, decimal DiscountPercent = 0m)
{
    public decimal Gross => Math.Round(Quantity * UnitPrice, 2);
    public decimal Discount => Math.Round(Gross * DiscountPercent / 100m, 2);
    public decimal Total => Gross - Discount;
}

public sealed class Sale : AuditableEntity
{
    private readonly List<SaleLine> _lines = [];

    public required string Number { get; init; }
    public DateOnly DocumentDate { get; init; }
    public required string CustomerCode { get; init; }
    public string WarehouseCode { get; init; } = "UTAMA";
    public string? SalesCode { get; init; }
    public bool IsCredit { get; init; }
    public DateOnly? DueDate { get; init; }
    public decimal DiscountPercent { get; init; }
    public decimal DiscountAmount { get; init; }
    public decimal TaxPercent { get; init; }
    public decimal DownPayment { get; init; }
    public string? Notes { get; init; }
    public DocumentStatus Status { get; private set; } = DocumentStatus.Draft;
    public IReadOnlyList<SaleLine> Lines => _lines;

    public decimal Subtotal => _lines.Sum(x => x.Total);
    public decimal CalculatedDiscount => Math.Min(Subtotal, Math.Round(Subtotal * DiscountPercent / 100m, 2) + DiscountAmount);
    public decimal NetBeforeTax => Subtotal - CalculatedDiscount;
    public decimal TaxAmount => Math.Round(NetBeforeTax * TaxPercent / 100m, 2);
    public decimal GrandTotal => NetBeforeTax + TaxAmount;
    public decimal BalanceDue => IsCredit ? Math.Max(0m, GrandTotal - DownPayment) : 0m;
    public decimal CostTotal => _lines.Sum(x => x.CostTotal);

    public void AddLine(SaleLine line)
    {
        if (Status != DocumentStatus.Draft)
            throw new InvalidOperationException("Rincian hanya dapat diubah saat nota masih draft.");
        if (line.Quantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(line), "Jumlah barang harus lebih dari nol.");
        if (line.UnitPrice < 0)
            throw new ArgumentOutOfRangeException(nameof(line), "Harga jual tidak boleh negatif.");
        if (line.DiscountPercent is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(line), "Diskon barang harus berada di antara 0 dan 100 persen.");
        _lines.Add(line);
    }

    public void Post()
    {
        if (_lines.Count == 0)
            throw new InvalidOperationException("Penjualan tidak dapat disimpan tanpa rincian barang.");
        if (IsCredit && DueDate is null)
            throw new InvalidOperationException("Tanggal jatuh tempo wajib diisi untuk penjualan kredit.");
        if (IsCredit && DueDate < DocumentDate)
            throw new InvalidOperationException("Tanggal jatuh tempo tidak boleh lebih awal dari tanggal nota.");
        if (DiscountPercent is < 0 or > 100 || DiscountAmount < 0)
            throw new InvalidOperationException("Diskon nota tidak valid.");
        if (TaxPercent is < 0 or > 100)
            throw new InvalidOperationException("Persentase PPN tidak valid.");
        if (DownPayment < 0 || DownPayment > GrandTotal)
            throw new InvalidOperationException("Uang muka tidak valid.");
        Status = DocumentStatus.Posted;
    }

    public void Cancel()
    {
        if (Status != DocumentStatus.Posted)
            throw new InvalidOperationException("Hanya penjualan yang sudah disimpan yang dapat dibatalkan.");
        Status = DocumentStatus.Cancelled;
    }
}

public sealed record SaleLine(
    string ItemCode,
    string ItemName,
    decimal Quantity,
    string Unit,
    decimal UnitPrice,
    decimal DiscountPercent,
    decimal UnitCost)
{
    public decimal Gross => Math.Round(Quantity * UnitPrice, 2);
    public decimal Discount => Math.Round(Gross * DiscountPercent / 100m, 2);
    public decimal Total => Gross - Discount;
    public decimal CostTotal => Math.Round(Quantity * UnitCost, 5);
}
