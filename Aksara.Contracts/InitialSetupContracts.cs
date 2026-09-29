namespace Aksara.Contracts;

public sealed record CompanyIdentityDto(
    Guid InstanceId,
    string CompanyCode,
    string CompanyName,
    string IsolationMode);

public sealed record InitialSetupStageDto(
    string Code,
    string Title,
    string Status,
    string Detail,
    string? Route);

public sealed record InitialSetupInventoryDraftDto(
    Guid? Id,
    string WarehouseCode,
    string ItemCode,
    string ItemName,
    string Unit,
    decimal Quantity,
    decimal UnitCost);

public sealed record InitialSetupPartnerDraftDto(
    Guid? Id,
    string PartnerType,
    string PartnerCode,
    string PartnerName,
    string DocumentNumber,
    DateOnly? DueDate,
    decimal Amount,
    string? Notes);

public sealed record InitialSetupLedgerDraftDto(
    Guid? Id,
    string AccountCode,
    string AccountName,
    string? Description,
    decimal Debit,
    decimal Credit);

public sealed record InitialSetupDto(
    Guid Id,
    string Status,
    DateOnly? StartDate,
    DateOnly? CutoffDate,
    bool LegacyProtected,
    DateTimeOffset? ActivatedAt,
    string? ActivatedByName,
    long Version,
    CompanyIdentityDto Company,
    IReadOnlyList<InitialSetupStageDto> Stages,
    IReadOnlyList<string> Errors,
    IReadOnlyList<InitialSetupInventoryDraftDto> Inventory,
    IReadOnlyList<InitialSetupPartnerDraftDto> Partners,
    IReadOnlyList<InitialSetupLedgerDraftDto> Ledger);

public sealed record SaveInitialSetupRequest(
    DateOnly StartDate,
    DateOnly CutoffDate,
    long ExpectedVersion,
    string UserName,
    string ComputerName,
    IReadOnlyList<InitialSetupInventoryDraftDto> Inventory,
    IReadOnlyList<InitialSetupPartnerDraftDto> Partners,
    IReadOnlyList<InitialSetupLedgerDraftDto> Ledger);

public sealed record ActivateInitialSetupRequest(long ExpectedVersion, string UserName, string ComputerName);

public sealed record ReopenInitialSetupRequest(long ExpectedVersion, string Reason, string UserName, string ComputerName);
