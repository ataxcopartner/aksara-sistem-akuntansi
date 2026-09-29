namespace Aksara.Contracts;

public sealed record PeriodClosePreflightRequest(
    DateOnly CutoffDate,
    DateOnly OpeningDate,
    string PeriodName);

public sealed record CreatePeriodCloseRequest(
    DateOnly CutoffDate,
    DateOnly OpeningDate,
    string PeriodName,
    string BackupLocation,
    string ArchiveLocation,
    string IdempotencyKey,
    string? ComputerName);

public sealed record ReopenPeriodCloseRequest(
    string? Reason,
    string? ComputerName);

public sealed record PeriodCloseDocumentCountsDto(
    int Journals,
    int CashBankTransactions,
    int Purchases,
    int PurchaseReturns,
    int Sales,
    int SaleReturns,
    int SupplierPayments,
    int CustomerReceipts,
    int CheckGiros,
    int PartnerBalanceDocuments,
    int InventoryDocuments,
    int Total);

public sealed record PeriodCloseCarryForwardDto(
    int LedgerAccounts,
    decimal LedgerDebit,
    decimal LedgerCredit,
    int CashBankAccounts,
    decimal CashBankBalance,
    int SupplierBalances,
    decimal SupplierOutstanding,
    int CustomerBalances,
    decimal CustomerOutstanding,
    int InventoryBalances,
    decimal InventoryQuantity,
    decimal InventoryValue,
    int InventoryCostLayers);

public sealed record PeriodClosePreflightDto(
    DateOnly CutoffDate,
    DateOnly OpeningDate,
    string PeriodName,
    bool CanClose,
    DateOnly? LatestClosedCutoff,
    IReadOnlyList<string> Blockers,
    PeriodCloseDocumentCountsDto Documents,
    PeriodCloseCarryForwardDto CarryForward);

public sealed record PeriodCloseDto(
    Guid Id,
    string PeriodName,
    DateOnly CutoffDate,
    DateOnly OpeningDate,
    string Status,
    string CreatedBy,
    string? ComputerName,
    DateTimeOffset CreatedAt,
    string BackupLocation,
    string BackupFileName,
    string SnapshotSha256,
    string ArchiveLocation,
    string ArchiveFileName,
    string ArchiveSha256,
    PeriodCloseDocumentCountsDto Documents,
    PeriodCloseCarryForwardDto CarryForward,
    bool CanReopen,
    string? ReopenedBy,
    string? ReopenComputerName,
    DateTimeOffset? ReopenedAt,
    string? ReopenReason);

public sealed record PeriodCloseExportFileDto(
    string FileName,
    string Sha256,
    string Content);

public sealed record PeriodCloseExportBundleDto(
    PeriodCloseExportFileDto TotalBackup,
    PeriodCloseExportFileDto ThroughCutoffArchive);
