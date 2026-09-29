namespace Aksara.Contracts;

public sealed record InventoryWarehouseDto(
    string Code,
    string Name,
    bool IsDefault,
    bool IsActive,
    long Version,
    string? Address1 = null,
    string? Address2 = null,
    string? District = null,
    string? City = null,
    string? PostalCode = null,
    string? Phone1 = null,
    string? Phone2 = null,
    string? Fax = null);

public sealed record SaveInventoryWarehouseRequest(
    string Code,
    string Name,
    bool IsDefault,
    bool IsActive,
    long? ExpectedVersion,
    string UserName,
    string ComputerName,
    string? Address1 = null,
    string? Address2 = null,
    string? District = null,
    string? City = null,
    string? PostalCode = null,
    string? Phone1 = null,
    string? Phone2 = null,
    string? Fax = null);

public sealed record InventoryAdjustmentTypeDto(
    string Code,
    string Name,
    string Direction,
    bool RequiresValue,
    bool IsOpeningBalance,
    bool IsActive,
    long Version);

public sealed record SaveInventoryAdjustmentTypeRequest(
    string Code,
    string Name,
    string Direction,
    bool RequiresValue,
    bool IsOpeningBalance,
    bool IsActive,
    long? ExpectedVersion,
    string UserName,
    string ComputerName);

public sealed record InventoryCategoryDto(
    string Code,
    string Name,
    string? ParentCode,
    bool IsActive,
    long Version,
    bool UsesIdPerTransaction = false,
    bool IsProductionMaterial = false,
    bool IsProduct = false,
    bool IsNotTradeGoods = false,
    bool IsNotServiceSupport = false,
    decimal MovR1Qty = 0,
    decimal MovR2Qty = 0,
    decimal MovR3Qty = 0,
    decimal PointValue = 0,
    int ItemUsageCount = 0,
    string CategoryType = "GOODS")
{
    public string Analysis => $"Dipakai {ItemUsageCount:N0} {(CategoryType == "SERVICE" ? "Kode Jasa" : "Kode Brg.")}";
}

public sealed record SaveInventoryCategoryRequest(
    string Code,
    string Name,
    string? ParentCode,
    bool IsActive,
    long? ExpectedVersion,
    string UserName,
    string ComputerName,
    bool UsesIdPerTransaction = false,
    bool IsProductionMaterial = false,
    bool IsProduct = false,
    bool IsNotTradeGoods = false,
    bool IsNotServiceSupport = false,
    decimal MovR1Qty = 0,
    decimal MovR2Qty = 0,
    decimal MovR3Qty = 0,
    decimal PointValue = 0,
    string CategoryType = "GOODS");

public sealed record InventoryPriceTierDto(
    string TierCode,
    string TierName,
    decimal Price,
    decimal DiscountPercent,
    decimal MinimumQuantity);

public sealed record InventoryPackageDto(
    string PackageName,
    decimal PackageSize,
    string? Barcode,
    int SortOrder);

public sealed record InventoryUnitDto(string Name, bool IsSystem);

public sealed record SaveInventoryUnitRequest(string Name, string UserName, string ComputerName);

public sealed record InventoryFormulaLineDto(
    string ComponentItemCode,
    string ComponentItemName,
    decimal Quantity,
    string Unit);

public sealed record InventoryItemBalanceDto(
    string WarehouseCode,
    string WarehouseName,
    decimal Quantity);

public sealed record InventoryItemSummaryDto(
    Guid Id,
    string Code,
    string Name,
    string Unit,
    string? CategoryCode,
    string? CategoryName,
    string? Barcode,
    decimal TotalStock,
    decimal AverageCost,
    decimal RetailPrice,
    decimal WholesalePrice,
    decimal MinimumStock,
    decimal MaximumStock,
    bool CheckStock,
    bool IsActive,
    long Version,
    string? PackageName = null,
    decimal? PackageSize = null,
    string ItemType = "GOODS");

public sealed record InventoryItemDto(
    Guid Id,
    string Code,
    string Name,
    string Unit,
    string? CategoryCode,
    string? CategoryName,
    string? Barcode,
    string? Rack,
    string? Brand,
    decimal PurchasePrice,
    decimal AverageCost,
    decimal MinimumStock,
    decimal MaximumStock,
    bool CheckStock,
    string? Notes,
    string? Specification,
    string? ImageContentType,
    string? ImageBase64,
    bool IsActive,
    long Version,
    IReadOnlyList<InventoryPriceTierDto> Prices,
    IReadOnlyList<InventoryPackageDto> Packages,
    IReadOnlyList<InventoryFormulaLineDto> Formula,
    IReadOnlyList<InventoryItemBalanceDto> Balances,
    string ItemType = "GOODS");

public sealed record SaveInventoryItemRequest(
    string Code,
    string Name,
    string Unit,
    string? CategoryCode,
    string? Barcode,
    string? Rack,
    string? Brand,
    decimal PurchasePrice,
    decimal AverageCost,
    decimal MinimumStock,
    decimal MaximumStock,
    bool CheckStock,
    string? Notes,
    string? Specification,
    string? ImageContentType,
    string? ImageBase64,
    bool IsActive,
    long? ExpectedVersion,
    IReadOnlyList<InventoryPriceTierDto> Prices,
    IReadOnlyList<InventoryPackageDto> Packages,
    IReadOnlyList<InventoryFormulaLineDto> Formula,
    string UserName,
    string ComputerName,
    string ItemType = "GOODS");

public sealed record InventoryDocumentLineRequest(
    string ItemCode,
    decimal Quantity,
    decimal UnitCost,
    string? WarehouseCode = null);

public sealed record InventoryDocumentLineDto(
    Guid Id,
    int LineNumber,
    string ItemCode,
    string ItemName,
    string WarehouseCode,
    decimal Quantity,
    string Unit,
    decimal UnitCost,
    decimal Amount);

public sealed record InventoryDocumentSummaryDto(
    Guid Id,
    string DocumentType,
    string DocumentNumber,
    DateOnly DocumentDate,
    string Status,
    string? SourceWarehouseCode,
    string? DestinationWarehouseCode,
    string? Counterparty,
    decimal TotalQuantity,
    decimal TotalAmount,
    DateTimeOffset CreatedAt);

public sealed record CreateInventoryTransferRequest(
    DateOnly DocumentDate,
    string SourceWarehouseCode,
    string DestinationWarehouseCode,
    string? Notes,
    string UserName,
    string ComputerName,
    IReadOnlyList<InventoryDocumentLineRequest> Lines);

public sealed record InventoryTransferDto(
    Guid Id,
    string DocumentNumber,
    DateOnly DocumentDate,
    string SourceWarehouseCode,
    string SourceWarehouseName,
    string DestinationWarehouseCode,
    string DestinationWarehouseName,
    string Status,
    string? Notes,
    decimal TotalQuantity,
    decimal TotalAmount,
    DateTimeOffset CreatedAt,
    IReadOnlyList<InventoryDocumentLineDto> Lines);

public sealed record CreateInventoryAdjustmentRequest(
    DateOnly DocumentDate,
    string AdjustmentTypeCode,
    string WarehouseCode,
    string? Notes,
    string UserName,
    string ComputerName,
    IReadOnlyList<InventoryDocumentLineRequest> Lines);

public sealed record InventoryAdjustmentDto(
    Guid Id,
    string DocumentNumber,
    DateOnly DocumentDate,
    string AdjustmentTypeCode,
    string AdjustmentTypeName,
    string Direction,
    string WarehouseCode,
    string WarehouseName,
    string Status,
    string? Notes,
    decimal TotalQuantity,
    decimal TotalAmount,
    DateTimeOffset CreatedAt,
    IReadOnlyList<InventoryDocumentLineDto> Lines);

public sealed record InventoryStockCountLineRequest(
    string ItemCode,
    decimal CountedQuantity,
    decimal UnitCost);

public sealed record CreateInventoryStockCountRequest(
    DateOnly DocumentDate,
    string WarehouseCode,
    string? Notes,
    string UserName,
    string ComputerName,
    IReadOnlyList<InventoryStockCountLineRequest> Lines);

public sealed record InventoryStockCountLineDto(
    Guid Id,
    int LineNumber,
    string ItemCode,
    string ItemName,
    string Unit,
    decimal SystemQuantity,
    decimal CountedQuantity,
    decimal DifferenceQuantity,
    decimal UnitCost,
    decimal DifferenceAmount);

public sealed record InventoryStockCountDto(
    Guid Id,
    string DocumentNumber,
    DateOnly DocumentDate,
    string WarehouseCode,
    string WarehouseName,
    string Status,
    string? Notes,
    decimal TotalDifferenceQuantity,
    decimal TotalDifferenceAmount,
    DateTimeOffset CreatedAt,
    IReadOnlyList<InventoryStockCountLineDto> Lines);

public sealed record InventoryBusinessUnitDto(string Code, string Name, bool IsActive);

public sealed record CreateInventoryMbapSendRequest(
    DateOnly DocumentDate,
    string SourceWarehouseCode,
    string DestinationBusinessUnitCode,
    DateOnly? SettlementDate,
    string? BankCode,
    string? Notes,
    string UserName,
    string ComputerName,
    IReadOnlyList<InventoryDocumentLineRequest> Lines);

public sealed record InventoryMbapSendDto(
    Guid Id,
    string DocumentNumber,
    DateOnly DocumentDate,
    string SourceWarehouseCode,
    string DestinationBusinessUnitCode,
    string DestinationBusinessUnitName,
    DateOnly? SettlementDate,
    string? BankCode,
    string Status,
    string? Notes,
    decimal TotalQuantity,
    decimal TotalAmount,
    DateTimeOffset CreatedAt,
    IReadOnlyList<InventoryDocumentLineDto> Lines);

public sealed record InventoryMbapReceiveLineRequest(Guid SendLineId, decimal Quantity);

public sealed record CreateInventoryMbapReceiveRequest(
    Guid SendId,
    DateOnly DocumentDate,
    string DestinationWarehouseCode,
    string? Notes,
    string UserName,
    string ComputerName,
    IReadOnlyList<InventoryMbapReceiveLineRequest> Lines);

public sealed record InventoryMbapReceiveSourceLineDto(
    Guid SendLineId,
    string ItemCode,
    string ItemName,
    string Unit,
    decimal SentQuantity,
    decimal ReceivedQuantity,
    decimal RemainingQuantity,
    decimal UnitCost);

public sealed record InventoryMbapReceiveSourceDto(
    Guid SendId,
    string SendNumber,
    DateOnly SendDate,
    string SourceWarehouseCode,
    string DestinationBusinessUnitCode,
    string DestinationBusinessUnitName,
    IReadOnlyList<InventoryMbapReceiveSourceLineDto> Lines);

public sealed record InventoryMbapReceiveDto(
    Guid Id,
    string DocumentNumber,
    DateOnly DocumentDate,
    Guid SendId,
    string SendNumber,
    string DestinationWarehouseCode,
    string Status,
    string? Notes,
    decimal TotalQuantity,
    decimal TotalAmount,
    DateTimeOffset CreatedAt,
    IReadOnlyList<InventoryDocumentLineDto> Lines);

public sealed record CancelInventoryDocumentRequest(string Reason, string UserName, string ComputerName);

public sealed record InventoryPriceReportRowDto(
    string CategoryCode,
    string ItemCode,
    string ItemName,
    string Unit,
    decimal PurchasePrice,
    decimal AverageCost,
    decimal RetailPrice,
    decimal WholesalePrice,
    decimal BulkPrice,
    decimal SpecialPrice1,
    decimal SpecialPrice2,
    decimal RetailMarginPercent,
    decimal TotalStock);

public sealed record InventoryPriceHistoryRowDto(
    DateOnly DocumentDate,
    string DocumentNumber,
    string HistoryType,
    string PartyCode,
    string PartyName,
    string ItemCode,
    string ItemName,
    decimal Quantity,
    string Unit,
    decimal UnitPrice,
    decimal DiscountPercent);

public sealed record InventoryStockCardRowDto(
    DateTimeOffset OccurredAt,
    string WarehouseCode,
    string ItemCode,
    string ItemName,
    string SourceType,
    string SourceNumber,
    decimal QuantityIn,
    decimal QuantityOut,
    decimal BalanceQuantity,
    decimal UnitCost,
    decimal BalanceValue);

public sealed record InventoryValuationRowDto(
    string WarehouseCode,
    string ItemCode,
    string ItemName,
    string Unit,
    decimal Quantity,
    decimal AverageCost,
    decimal InventoryValue,
    decimal MinimumStock,
    decimal MaximumStock,
    string StockStatus);

public sealed record InventoryDailyActivityRowDto(
    DateOnly ActivityDate,
    string WarehouseCode,
    string ItemCode,
    string ItemName,
    decimal QuantityIn,
    decimal QuantityOut,
    decimal EndingQuantity,
    decimal AverageCost,
    decimal EndingValue);

public sealed record InventoryTurnoverRowDto(
    string WarehouseCode,
    string ItemCode,
    string ItemName,
    decimal OpeningQuantity,
    decimal QuantityIn,
    decimal QuantityOut,
    decimal EndingQuantity,
    decimal AverageQuantity,
    decimal TurnoverRate,
    string MovementClass);

public sealed record InventoryTransactionReportRowDto(
    DateOnly DocumentDate,
    string DocumentType,
    string DocumentNumber,
    string Status,
    string? SourceWarehouseCode,
    string? DestinationWarehouseCode,
    string? Counterparty,
    string ItemCode,
    string ItemName,
    decimal Quantity,
    string Unit,
    decimal UnitCost,
    decimal Amount);

public sealed record PurchasePriceCheckRowDto(
    DateOnly PurchaseDate,
    string PurchaseNumber,
    string SupplierCode,
    string SupplierName,
    string WarehouseCode,
    decimal PurchaseQuantity,
    decimal ReturnQuantity,
    decimal SoldQuantity,
    decimal RemainingQuantity,
    decimal UnitPurchaseCost,
    decimal RemainingValue);

public sealed record PurchasePriceCostMethodDto(
    decimal HppPerUnit,
    decimal CostOfGoodsSold,
    decimal RemainingInventoryValue);

public sealed record PurchasePriceCheckSummaryDto(
    decimal TotalPurchaseQuantity,
    decimal TotalReturnQuantity,
    decimal TotalSoldQuantity,
    decimal TotalRemainingQuantity,
    decimal UnallocatedSoldQuantity,
    PurchasePriceCostMethodDto Average,
    PurchasePriceCostMethodDto Fifo,
    PurchasePriceCostMethodDto Lifo);

public sealed record PurchasePriceCheckReportDto(
    string ItemCode,
    string ItemName,
    string Unit,
    decimal CurrentStock,
    IReadOnlyList<PurchasePriceCheckRowDto> Rows,
    PurchasePriceCheckSummaryDto Summary,
    bool IsCompatibilityMode = false);

public sealed record InventoryReportTotalsDto(
    decimal QuantityIn,
    decimal QuantityOut,
    decimal EndingQuantity,
    decimal InventoryValue,
    decimal TransactionAmount,
    int RowCount);

public sealed record InventoryReportDto<T>(IReadOnlyList<T> Rows, InventoryReportTotalsDto Totals);
