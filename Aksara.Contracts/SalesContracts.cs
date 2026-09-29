namespace Aksara.Contracts;

public sealed record SalesCustomerDto(Guid Id, string Code, string CompanyName, string? Address, int CreditDays, decimal CreditLimit);

public sealed record SalespersonDto(string Code, string Name);

public sealed record SalesItemDto(
    Guid Id,
    string Code,
    string Name,
    string Unit,
    decimal RetailPrice,
    string WarehouseCode,
    decimal AvailableStock,
    string ItemType = "GOODS");

public sealed record CreateSaleLineRequest(
    string ItemCode,
    decimal Quantity,
    decimal UnitPrice,
    decimal DiscountPercent,
    string? WarehouseCode = null);

public sealed record CreateSaleRequest(
    DateOnly DocumentDate,
    string? DocumentNumber,
    string CustomerCode,
    string TransactionType,
    string? CashAccountCode,
    DateOnly? DueDate,
    string? SalesCode,
    string WarehouseCode,
    decimal DiscountPercent,
    decimal DiscountAmount,
    decimal TaxPercent,
    decimal DownPayment,
    string? Notes,
    string UserName,
    string ComputerName,
    IReadOnlyList<CreateSaleLineRequest> Lines,
    string DocumentKind = "GOODS");

public sealed record CreateGunggunganSaleRequest(
    DateOnly DocumentDate,
    string? DocumentNumber,
    string CashAccountCode,
    string WarehouseCode,
    decimal CashReceived,
    string? Notes,
    string UserName,
    string ComputerName,
    IReadOnlyList<CreateSaleLineRequest> Lines);

public sealed record SaleLineDto(
    int LineNumber,
    string ItemCode,
    string ItemName,
    decimal Quantity,
    string Unit,
    decimal UnitPrice,
    decimal DiscountPercent,
    decimal LineTotal,
    decimal CostAmount,
    string WarehouseCode);

public sealed record SaleDto(
    Guid Id,
    string DocumentNumber,
    DateOnly DocumentDate,
    string CustomerCode,
    string CustomerName,
    string TransactionType,
    string? CashAccountCode,
    string? CashAccountName,
    DateOnly? DueDate,
    string? SalesCode,
    string WarehouseCode,
    decimal Subtotal,
    decimal DiscountAmount,
    decimal TaxAmount,
    decimal GrandTotal,
    decimal DownPayment,
    decimal BalanceDue,
    decimal CostTotal,
    string Status,
    string? Notes,
    DateTimeOffset CreatedAt,
    IReadOnlyList<SaleLineDto> Lines,
    string DocumentKind = "GOODS");

public sealed record GunggunganSaleResultDto(
    SaleDto Sale,
    decimal CashReceived,
    decimal Change);

public sealed record GunggunganSaleReportRowDto(
    Guid SaleId,
    DateOnly DocumentDate,
    string DocumentNumber,
    string WarehouseCode,
    string CashAccountCode,
    string CashAccountName,
    string ItemCode,
    string ItemName,
    decimal Quantity,
    string Unit,
    decimal UnitPrice,
    decimal LineTotal,
    string Status);

public sealed record GunggunganSaleReportDto(
    DateOnly From,
    DateOnly To,
    int TransactionCount,
    decimal TotalQuantity,
    decimal TotalSales,
    IReadOnlyList<GunggunganSaleReportRowDto> Rows);

public sealed record CancelSaleRequest(string Reason, string UserName, string ComputerName);
public sealed record RestoreSaleRequest(string UserName, string ComputerName);

public sealed record CustomerReceivableDto(
    Guid SaleId,
    string DocumentNumber,
    DateOnly DocumentDate,
    string CustomerCode,
    string CustomerName,
    string? SalesCode,
    DateOnly? DueDate,
    decimal GrandTotal,
    decimal DownPayment,
    decimal PaidAmount,
    decimal BalanceDue,
    decimal RemainingBalance,
    string SourceType = "SALE",
    decimal CorrectionAmount = 0,
    string SourceLabel = "Penjualan");

public sealed record CustomerReceiptLineDto(
    Guid SaleId,
    string SaleNumber,
    DateOnly SaleDate,
    string? SalesCode,
    decimal OriginalBalance,
    decimal PaidBefore,
    decimal Amount,
    decimal RemainingAfter,
    string SourceType = "SALE");

public sealed record CreateCustomerReceiptLineRequest(Guid SaleId, decimal Amount, string SourceType = "SALE");

public sealed record CustomerCheckGiroDto(
    Guid Id,
    string SequenceNumber,
    DateOnly DocumentDate,
    string BankName,
    string CheckGiroNumber,
    string? CheckGiroType,
    DateOnly? DueDate,
    decimal FaceAmount,
    decimal AdditionalAmount,
    decimal Amount,
    string CustomerCode,
    string CustomerName,
    string? CustomerAddress,
    string OriginArea,
    bool InitialBalance,
    bool IsRejected,
    DateOnly? RejectedDate,
    DateOnly? SettlementDate,
    string Status,
    string? Notes);

public sealed record CreateCustomerCheckGiroRequest(
    string CustomerCode,
    string? SequenceNumber,
    DateOnly DocumentDate,
    string BankName,
    string CheckGiroNumber,
    string? CheckGiroType,
    DateOnly? DueDate,
    decimal FaceAmount,
    decimal AdditionalAmount,
    decimal Amount,
    string? Notes,
    string UserName,
    string ComputerName,
    string? OriginArea = null,
    bool InitialBalance = false,
    bool IsRejected = false,
    DateOnly? RejectedDate = null,
    DateOnly? SettlementDate = null);

public sealed record CancelCustomerCheckGiroRequest(string Reason, string UserName, string ComputerName);
public sealed record RestoreCustomerCheckGiroRequest(string UserName, string ComputerName);

public sealed record CreateCustomerReceiptRequest(
    DateOnly DocumentDate,
    string? DocumentNumber,
    string CustomerCode,
    string? CollectorCode,
    string Method,
    string? CashAccountCode,
    string? BankAccountCode,
    Guid? CheckGiroId,
    decimal ReceiptAmount,
    string? Notes,
    string UserName,
    string ComputerName,
    IReadOnlyList<CreateCustomerReceiptLineRequest> Lines);

public sealed record CustomerReceiptDto(
    Guid Id,
    string DocumentNumber,
    DateOnly DocumentDate,
    string CustomerCode,
    string CustomerName,
    string? CollectorCode,
    string Method,
    string? CashAccountCode,
    string? CashAccountName,
    string? BankAccountCode,
    string? BankAccountName,
    CustomerCheckGiroDto? CheckGiro,
    decimal ReceiptAmount,
    string Status,
    string? Notes,
    string JournalNumber,
    DateTimeOffset CreatedAt,
    IReadOnlyList<CustomerReceiptLineDto> Lines);

public sealed record CancelCustomerReceiptRequest(string Reason, string UserName, string ComputerName);
public sealed record RestoreCustomerReceiptRequest(string UserName, string ComputerName);

public sealed record FifoCostPreviewLineRequest(string ItemCode, string WarehouseCode, decimal Quantity);
public sealed record FifoCostPreviewRequest(IReadOnlyList<FifoCostPreviewLineRequest> Lines);
public sealed record FifoCostPreviewLineDto(int LineNumber, string ItemCode, string WarehouseCode, decimal Quantity, decimal UnitCost, decimal CostAmount);
public sealed record FifoCostPreviewDto(IReadOnlyList<FifoCostPreviewLineDto> Lines, decimal TotalCost);

public sealed record InventoryBalanceDto(
    string ItemCode,
    string ItemName,
    string Unit,
    string WarehouseCode,
    decimal Quantity,
    long Version);

public sealed record ApiError(string Code, string Message, IReadOnlyDictionary<string, string[]>? Errors = null);
