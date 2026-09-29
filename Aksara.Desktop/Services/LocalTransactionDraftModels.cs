using Aksara.Contracts;

namespace Aksara.Desktop.Services;

public sealed record PurchaseLocalDraft(
    DateTime? DocumentDate,
    string DocumentNumber,
    string SupplierCode,
    string SupplierName,
    string SupplierAddress,
    int TransactionTypeIndex,
    DateTime? DueDate,
    string DueDays,
    string Notes,
    string DiscountPercent,
    string DiscountAmount,
    string TaxPercent,
    string DownPayment,
    IReadOnlyList<PurchaseLocalDraftLine> Lines);

public sealed record PurchaseLocalDraftLine(
    string WarehouseCode,
    string ItemCode,
    string ItemName,
    decimal Quantity,
    string Unit,
    decimal AvailableStock,
    decimal UnitPrice,
    decimal DiscountPercent,
    decimal PackageQuantity,
    string LineNote);

public sealed record SalesLocalDraft(
    DateTime? DocumentDate,
    string DocumentNumber,
    string CustomerCode,
    string CustomerName,
    string CustomerAddress,
    int TransactionTypeIndex,
    DateTime? DueDate,
    string DueDays,
    string SalesCode,
    string CashAccountCode,
    string Notes,
    string DiscountPercent,
    string DiscountAmount,
    string TaxPercent,
    string DownPayment,
    IReadOnlyList<SalesLocalDraftLine> Lines);

public sealed record SalesLocalDraftLine(
    string WarehouseCode,
    string ItemCode,
    string ItemName,
    decimal Quantity,
    string Unit,
    decimal AvailableStock,
    decimal UnitPrice,
    decimal DiscountPercent,
    decimal FifoUnitCost,
    decimal PackageQuantity,
    string LineNote);

public sealed record PurchaseReturnLocalDraft(
    DateTime? ReturnDate,
    string ReturnNumber,
    string SourceNumber,
    string SupplierCode,
    string SupplierName,
    string SupplierAddress,
    string TransactionType,
    string Warehouse,
    PurchaseReturnSourceDto? Source,
    string Notes,
    string DiscountPercent,
    string DiscountAmount,
    string TaxPercent,
    IReadOnlyList<ReturnQuantityLocalDraft> Lines);

public sealed record SalesReturnLocalDraft(
    DateTime? ReturnDate,
    string ReturnNumber,
    string SourceNumber,
    string CustomerCode,
    string CustomerName,
    string CustomerAddress,
    string TransactionType,
    string SalesCode,
    string Warehouse,
    SaleReturnSourceDto? Source,
    string Notes,
    string DiscountPercent,
    string DiscountAmount,
    string TaxPercent,
    IReadOnlyList<ReturnQuantityLocalDraft> Lines);

public sealed record ReturnQuantityLocalDraft(Guid SourceLineId, decimal Quantity);

public sealed record SupplierPaymentLocalDraft(
    DateTime? DocumentDate,
    string DocumentNumber,
    string SupplierCode,
    string SupplierName,
    string SupplierAddress,
    string Group,
    string SearchNumber,
    DateTime? PayablesUntilDate,
    string CurrentPayment,
    string CashCode,
    string CashName,
    string CashAmount,
    string BankCode,
    string BankName,
    string BankAmount,
    string GiroBankCode,
    string GiroBankName,
    string GiroNumber,
    string GiroDueDate,
    string GiroAmount,
    bool AutoCash,
    string Notes,
    IReadOnlyList<SupplierPaymentLineLocalDraft> Lines);

public sealed record SupplierPaymentLineLocalDraft(
    Guid PurchaseId,
    string Group,
    string DocumentNumber,
    DateOnly? DocumentDate,
    string? SupplierInvoiceNumber,
    DateOnly? DueDate,
    string SupplierName,
    decimal OriginalValue,
    decimal PaidBefore,
    decimal RemainingBalance,
    string PaymentText,
    string SourceType = "PURCHASE");

public sealed record CustomerReceiptLocalDraft(
    DateTime? DocumentDate,
    string DocumentNumber,
    string CustomerCode,
    string CustomerName,
    string CustomerAddress,
    string CollectorCode,
    string Group,
    string SearchNumber,
    DateTime? ReceivablesUntilDate,
    string CurrentReceipt,
    string CashCode,
    string CashName,
    string CashAmount,
    string BankCode,
    string BankName,
    string BankAmount,
    string GiroBankCode,
    string GiroBankName,
    string GiroNumber,
    string GiroAmount,
    bool AutoCash,
    string Notes,
    CustomerCheckGiroDto? SelectedCheckGiro,
    IReadOnlyList<CustomerReceiptLineLocalDraft> Lines);

public sealed record CustomerReceiptLineLocalDraft(
    Guid SaleId,
    string Group,
    string DocumentNumber,
    DateOnly? DocumentDate,
    string? SalesCode,
    DateOnly? DueDate,
    string CustomerName,
    decimal OriginalValue,
    decimal PaidBefore,
    decimal RemainingBalance,
    string ReceiptText,
    string SourceType = "SALE");

public sealed record CashBankLocalDraft(
    DateTime? DocumentDate,
    bool IsPayment,
    int MethodIndex,
    IReadOnlyList<CashBankLineLocalDraft> Lines);

public sealed record CashBankLineLocalDraft(
    string SettlementMethod,
    string SettlementCode,
    string SettlementName,
    string Description,
    string AmountText,
    string GroupCode,
    string AccountCode,
    string AccountName);
