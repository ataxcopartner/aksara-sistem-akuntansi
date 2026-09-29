namespace Aksara.Contracts;

public sealed record SaleReturnSourceLineDto(
    Guid SaleLineId,
    int LineNumber,
    string ItemCode,
    string ItemName,
    string WarehouseCode,
    decimal SoldQuantity,
    decimal ReturnedQuantity,
    decimal RemainingQuantity,
    string Unit,
    decimal UnitPrice,
    decimal DiscountPercent,
    decimal UnitCost);

public sealed record SaleReturnSourceDto(
    Guid SaleId,
    string DocumentNumber,
    DateOnly DocumentDate,
    string CustomerCode,
    string CustomerName,
    string? CustomerAddress,
    string TransactionType,
    string? SalesCode,
    string WarehouseCode,
    IReadOnlyList<SaleReturnSourceLineDto> Lines);

public sealed record CreateSaleReturnLineRequest(Guid SaleLineId, decimal Quantity);

public sealed record CreateSaleReturnRequest(
    Guid SaleId,
    DateOnly DocumentDate,
    string? DocumentNumber,
    decimal DiscountPercent,
    decimal DiscountAmount,
    decimal TaxPercent,
    string? Notes,
    string UserName,
    string ComputerName,
    IReadOnlyList<CreateSaleReturnLineRequest> Lines);

public sealed record SaleReturnLineDto(
    Guid SaleLineId,
    int LineNumber,
    string SourceDocumentNumber,
    string ItemCode,
    string ItemName,
    string WarehouseCode,
    decimal Quantity,
    string Unit,
    decimal UnitPrice,
    decimal DiscountPercent,
    decimal LineTotal,
    decimal CostAmount);

public sealed record SaleReturnDto(
    Guid Id,
    string DocumentNumber,
    DateOnly DocumentDate,
    Guid SaleId,
    string SourceDocumentNumber,
    string CustomerCode,
    string CustomerName,
    string? CustomerAddress,
    string TransactionType,
    string? SalesCode,
    string WarehouseCode,
    decimal Subtotal,
    decimal DiscountAmount,
    decimal TaxAmount,
    decimal GrandTotal,
    decimal CostTotal,
    string Status,
    string? Notes,
    DateTimeOffset CreatedAt,
    IReadOnlyList<SaleReturnLineDto> Lines);

public sealed record CancelSaleReturnRequest(string Reason, string UserName, string ComputerName);
public sealed record RestoreSaleReturnRequest(string UserName, string ComputerName);

public sealed record SalesDetailReportRowDto(
    DateOnly DocumentDate,
    string DocumentNumber,
    string CustomerCode,
    string CustomerName,
    string TransactionType,
    string? SalesCode,
    string WarehouseCode,
    string ItemCode,
    string ItemName,
    string Unit,
    decimal SoldQuantity,
    decimal ReturnedQuantity,
    decimal NetQuantity,
    decimal UnitPrice,
    decimal DiscountPercent,
    decimal GrossSales,
    decimal SalesReturn,
    decimal NetSales,
    decimal GrossCost,
    decimal ReturnCost,
    decimal NetCost,
    decimal GrossProfit);

public sealed record SalesHeaderReportRowDto(
    DateOnly DocumentDate,
    string DocumentNumber,
    string CustomerCode,
    string CustomerName,
    string TransactionType,
    DateOnly? DueDate,
    string? SalesCode,
    string WarehouseCode,
    decimal GrossSales,
    decimal SalesReturn,
    decimal NetSales,
    decimal GrossCost,
    decimal ReturnCost,
    decimal NetCost,
    decimal GrossProfit);

public sealed record SalesReportTotalsDto(
    decimal GrossSales,
    decimal SalesReturn,
    decimal NetSales,
    decimal GrossCost,
    decimal ReturnCost,
    decimal NetCost,
    decimal GrossProfit);

public sealed record SalesDetailReportDto(IReadOnlyList<SalesDetailReportRowDto> Rows, SalesReportTotalsDto Totals);
public sealed record SalesHeaderReportDto(IReadOnlyList<SalesHeaderReportRowDto> Rows, SalesReportTotalsDto Totals);

public sealed record SalesReturnDetailReportRowDto(
    DateOnly DocumentDate,
    string DocumentNumber,
    string SourceDocumentNumber,
    string CustomerCode,
    string CustomerName,
    string TransactionType,
    string? SalesCode,
    string WarehouseCode,
    string ItemCode,
    string ItemName,
    string Unit,
    decimal ReturnQuantity,
    decimal UnitPrice,
    decimal DiscountPercent,
    decimal ReturnAmount,
    decimal ReturnCost,
    decimal ProfitReduction);

public sealed record SalesReturnHeaderReportRowDto(
    DateOnly DocumentDate,
    string DocumentNumber,
    string SourceDocumentNumber,
    string CustomerCode,
    string CustomerName,
    string TransactionType,
    string? SalesCode,
    string WarehouseCode,
    decimal Subtotal,
    decimal DiscountAmount,
    decimal TaxAmount,
    decimal ReturnAmount,
    decimal ReturnCost,
    decimal ProfitReduction,
    string? Notes);

public sealed record SalesReturnReportTotalsDto(
    decimal ReturnAmount,
    decimal ReturnCost,
    decimal ProfitReduction);

public sealed record SalesReturnDetailReportDto(IReadOnlyList<SalesReturnDetailReportRowDto> Rows, SalesReturnReportTotalsDto Totals);
public sealed record SalesReturnHeaderReportDto(IReadOnlyList<SalesReturnHeaderReportRowDto> Rows, SalesReturnReportTotalsDto Totals);
