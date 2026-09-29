namespace Aksara.Contracts;

public sealed record PartnerBalanceDocumentDto(
    Guid Id,
    string PartnerType,
    string Kind,
    string Direction,
    string DocumentNumber,
    DateOnly DocumentDate,
    string PartnerCode,
    string PartnerName,
    string? PartnerAddress,
    DateOnly? DueDate,
    decimal Amount,
    decimal SettledAmount,
    decimal ReducedAmount,
    decimal RemainingBalance,
    string? TargetSourceType,
    Guid? TargetSourceId,
    string? TargetDocumentNumber,
    string? Notes,
    string Status,
    string JournalNumber,
    DateTimeOffset CreatedAt,
    long Version);

public sealed record SavePartnerBalanceDocumentRequest(
    Guid? Id,
    string PartnerType,
    string Kind,
    string Direction,
    DateOnly DocumentDate,
    string? DocumentNumber,
    string PartnerCode,
    DateOnly? DueDate,
    decimal Amount,
    string? TargetSourceType,
    Guid? TargetSourceId,
    string? Notes,
    long? ExpectedVersion,
    string UserName,
    string ComputerName);

public sealed record CancelPartnerBalanceDocumentRequest(
    long ExpectedVersion,
    string Reason,
    string UserName,
    string ComputerName);

public sealed record RestorePartnerBalanceDocumentRequest(
    long ExpectedVersion,
    string UserName,
    string ComputerName);
