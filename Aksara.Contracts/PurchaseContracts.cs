namespace Aksara.Contracts;

public sealed record PurchaseSupplierDto(Guid Id, string Code, string CompanyName, string? Address, int CreditDays);

public sealed record PurchaseItemDto(
    Guid Id,
    string Code,
    string Name,
    string Unit,
    decimal PurchasePrice,
    string WarehouseCode,
    decimal AvailableStock,
    string ItemType = "GOODS");

public sealed record CreatePurchaseLineRequest(
    string ItemCode,
    decimal Quantity,
    decimal UnitPrice,
    decimal DiscountPercent,
    string? WarehouseCode = null,
    string? LineNote = null);

public sealed record CreatePurchaseRequest(
    DateOnly DocumentDate,
    string? DocumentNumber,
    string SupplierCode,
    string? SupplierInvoiceNumber,
    string TransactionType,
    DateOnly? DueDate,
    string WarehouseCode,
    decimal DiscountPercent,
    decimal DiscountAmount,
    decimal TaxPercent,
    decimal DownPayment,
    string? Notes,
    string UserName,
    string ComputerName,
    IReadOnlyList<CreatePurchaseLineRequest> Lines,
    string DocumentKind = "GOODS");

public sealed record PurchaseLineDto(
    Guid PurchaseLineId,
    int LineNumber,
    string ItemCode,
    string ItemName,
    decimal Quantity,
    string Unit,
    decimal UnitPrice,
    decimal DiscountPercent,
    decimal LineTotal,
    string WarehouseCode,
    string? LineNote);

public sealed record PurchaseDto(
    Guid Id,
    string DocumentNumber,
    DateOnly DocumentDate,
    string SupplierCode,
    string SupplierName,
    string? SupplierAddress,
    string? SupplierInvoiceNumber,
    string TransactionType,
    DateOnly? DueDate,
    string WarehouseCode,
    decimal Subtotal,
    decimal DiscountPercent,
    decimal DiscountAmount,
    decimal TaxPercent,
    decimal TaxAmount,
    decimal GrandTotal,
    decimal DownPayment,
    decimal BalanceDue,
    string Status,
    string? Notes,
    DateTimeOffset CreatedAt,
    IReadOnlyList<PurchaseLineDto> Lines,
    string DocumentKind = "GOODS");

public sealed record SupplierPayableDto(
    Guid PurchaseId,
    string DocumentNumber,
    DateOnly DocumentDate,
    string SupplierCode,
    string SupplierName,
    string? SupplierInvoiceNumber,
    DateOnly? DueDate,
    decimal GrandTotal,
    decimal DownPayment,
    decimal PaidAmount,
    decimal BalanceDue,
    decimal RemainingBalance,
    string SourceType = "PURCHASE",
    decimal CorrectionAmount = 0,
    string SourceLabel = "Pembelian");

public sealed record SupplierPaymentLineDto(
    Guid PurchaseId,
    string PurchaseNumber,
    DateOnly PurchaseDate,
    decimal OriginalBalance,
    decimal PaidBefore,
    decimal Amount,
    decimal RemainingAfter,
    string SourceType = "PURCHASE");

public sealed record CreateSupplierPaymentLineRequest(Guid PurchaseId, decimal Amount, string SourceType = "PURCHASE");

public sealed record CreateSupplierPaymentRequest(
    DateOnly DocumentDate,
    string? DocumentNumber,
    string SupplierCode,
    string Method,
    string? CashAccountCode,
    string? BankAccountCode,
    string? CheckGiroNumber,
    DateOnly? CheckGiroDueDate,
    decimal PaymentAmount,
    string? Notes,
    string UserName,
    string ComputerName,
    IReadOnlyList<CreateSupplierPaymentLineRequest> Lines);

public sealed record SupplierPaymentDto(
    Guid Id,
    string DocumentNumber,
    DateOnly DocumentDate,
    string SupplierCode,
    string SupplierName,
    string Method,
    string? CashAccountCode,
    string? CashAccountName,
    string? BankAccountCode,
    string? BankAccountName,
    string? CheckGiroNumber,
    DateOnly? CheckGiroDueDate,
    decimal PaymentAmount,
    string Status,
    string? Notes,
    string JournalNumber,
    DateTimeOffset CreatedAt,
    IReadOnlyList<SupplierPaymentLineDto> Lines);

public sealed record CancelSupplierPaymentRequest(string Reason, string UserName, string ComputerName);
public sealed record RestoreSupplierPaymentRequest(string UserName, string ComputerName);

public sealed record CancelPurchaseRequest(string Reason, string UserName, string ComputerName);
public sealed record RestorePurchaseRequest(string UserName, string ComputerName);

public sealed record PurchaseReturnSourceLineDto(
    Guid PurchaseLineId,
    int LineNumber,
    string ItemCode,
    string ItemName,
    string WarehouseCode,
    decimal PurchasedQuantity,
    decimal ReturnedQuantity,
    decimal RemainingQuantity,
    string Unit,
    decimal UnitPrice,
    decimal DiscountPercent);

public sealed record PurchaseReturnSourceDto(
    Guid PurchaseId,
    string DocumentNumber,
    DateOnly DocumentDate,
    string SupplierCode,
    string SupplierName,
    string? SupplierAddress,
    string TransactionType,
    string WarehouseCode,
    IReadOnlyList<PurchaseReturnSourceLineDto> Lines);

public sealed record CreatePurchaseReturnLineRequest(Guid PurchaseLineId, decimal Quantity);

public sealed record CreatePurchaseReturnRequest(
    Guid PurchaseId,
    DateOnly DocumentDate,
    string? DocumentNumber,
    decimal DiscountPercent,
    decimal DiscountAmount,
    decimal TaxPercent,
    string? Notes,
    string UserName,
    string ComputerName,
    IReadOnlyList<CreatePurchaseReturnLineRequest> Lines);

public sealed record PurchaseReturnLineDto(
    Guid PurchaseLineId,
    int LineNumber,
    string SourceDocumentNumber,
    string ItemCode,
    string ItemName,
    string WarehouseCode,
    decimal Quantity,
    string Unit,
    decimal UnitPrice,
    decimal DiscountPercent,
    decimal LineTotal);

public sealed record PurchaseReturnDto(
    Guid Id,
    string DocumentNumber,
    DateOnly DocumentDate,
    Guid PurchaseId,
    string SourceDocumentNumber,
    string SupplierCode,
    string SupplierName,
    string? SupplierAddress,
    string TransactionType,
    string WarehouseCode,
    decimal Subtotal,
    decimal DiscountAmount,
    decimal TaxAmount,
    decimal GrandTotal,
    string Status,
    string? Notes,
    DateTimeOffset CreatedAt,
    IReadOnlyList<PurchaseReturnLineDto> Lines);

public sealed record CancelPurchaseReturnRequest(string Reason, string UserName, string ComputerName);
public sealed record RestorePurchaseReturnRequest(string UserName, string ComputerName);

public sealed record PurchaseDetailReportRowDto(
    DateOnly DocumentDate,
    string DocumentNumber,
    string SupplierCode,
    string SupplierName,
    string TransactionType,
    string WarehouseCode,
    string ItemCode,
    string ItemName,
    string Unit,
    decimal PurchasedQuantity,
    decimal ReturnedQuantity,
    decimal NetQuantity,
    decimal UnitPrice,
    decimal DiscountPercent,
    decimal GrossPurchase,
    decimal PurchaseReturn,
    decimal NetPurchase);

public sealed record PurchaseHeaderReportRowDto(
    DateOnly DocumentDate,
    string DocumentNumber,
    string SupplierCode,
    string SupplierName,
    string? SupplierInvoiceNumber,
    string TransactionType,
    DateOnly? DueDate,
    string WarehouseCode,
    decimal GrossPurchase,
    decimal PurchaseReturn,
    decimal NetPurchase);

public sealed record PurchaseReportTotalsDto(decimal GrossPurchase, decimal PurchaseReturn, decimal NetPurchase);
public sealed record PurchaseDetailReportDto(IReadOnlyList<PurchaseDetailReportRowDto> Rows, PurchaseReportTotalsDto Totals);
public sealed record PurchaseHeaderReportDto(IReadOnlyList<PurchaseHeaderReportRowDto> Rows, PurchaseReportTotalsDto Totals);

public sealed record PurchaseReturnDetailReportRowDto(
    DateOnly DocumentDate,
    string DocumentNumber,
    string SourceDocumentNumber,
    string SupplierCode,
    string SupplierName,
    string TransactionType,
    string WarehouseCode,
    string ItemCode,
    string ItemName,
    string Unit,
    decimal ReturnQuantity,
    decimal UnitPrice,
    decimal DiscountPercent,
    decimal ReturnAmount);

public sealed record PurchaseReturnHeaderReportRowDto(
    DateOnly DocumentDate,
    string DocumentNumber,
    string SourceDocumentNumber,
    string SupplierCode,
    string SupplierName,
    string TransactionType,
    string WarehouseCode,
    decimal Subtotal,
    decimal DiscountAmount,
    decimal TaxAmount,
    decimal ReturnAmount,
    string? Notes);

public sealed record PurchaseReturnReportTotalsDto(decimal ReturnAmount);
public sealed record PurchaseReturnDetailReportDto(IReadOnlyList<PurchaseReturnDetailReportRowDto> Rows, PurchaseReturnReportTotalsDto Totals);
public sealed record PurchaseReturnHeaderReportDto(IReadOnlyList<PurchaseReturnHeaderReportRowDto> Rows, PurchaseReturnReportTotalsDto Totals);

