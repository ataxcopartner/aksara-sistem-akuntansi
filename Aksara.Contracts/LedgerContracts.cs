namespace Aksara.Contracts;

public sealed record LedgerAccountDto(
    Guid Id,
    string Code,
    string Name,
    string AccountType,
    string NormalBalance,
    string? ParentCode,
    bool IsPostable,
    bool IsCashBank,
    bool IsActive,
    long Version);

public sealed record SaveLedgerAccountRequest(
    string Code,
    string Name,
    string AccountType,
    string NormalBalance,
    string? ParentCode,
    bool IsPostable,
    bool IsCashBank,
    bool IsActive,
    long? ExpectedVersion,
    string UserName,
    string ComputerName);

public sealed record JournalEntrySummaryDto(
    Guid Id,
    string DocumentNumber,
    DateOnly DocumentDate,
    string Memo,
    string SourceModule,
    string Status,
    decimal DebitTotal,
    decimal CreditTotal,
    DateTimeOffset CreatedAt);

public sealed record JournalEntryLineRequest(
    string AccountCode,
    string? Description,
    decimal Debit,
    decimal Credit);

public sealed record CreateJournalEntryRequest(
    DateOnly DocumentDate,
    string? Memo,
    string UserName,
    string ComputerName,
    IReadOnlyList<JournalEntryLineRequest> Lines);

public sealed record JournalEntryLineDto(
    int LineNumber,
    string AccountCode,
    string AccountName,
    string? Description,
    decimal Debit,
    decimal Credit);

public sealed record JournalEntryDto(
    Guid Id,
    string DocumentNumber,
    DateOnly DocumentDate,
    string Memo,
    string SourceModule,
    string Status,
    decimal DebitTotal,
    decimal CreditTotal,
    DateTimeOffset CreatedAt,
    IReadOnlyList<JournalEntryLineDto> Lines);

public sealed record LedgerTransactionMappingDto(
    string MappingCode,
    string MappingName,
    string? AccountCode,
    string? AccountName,
    long Version);

public sealed record SaveLedgerTransactionMappingRequest(
    string MappingCode,
    string? AccountCode,
    long? ExpectedVersion,
    string UserName,
    string ComputerName);

public sealed record LedgerJournalReportRowDto(
    DateOnly DocumentDate,
    string DocumentNumber,
    string SourceModule,
    string AccountCode,
    string AccountName,
    string? Description,
    decimal Debit,
    decimal Credit);

public sealed record GeneralLedgerReportRowDto(
    DateOnly DocumentDate,
    string DocumentNumber,
    string AccountCode,
    string AccountName,
    string? Description,
    decimal Debit,
    decimal Credit,
    decimal Balance);

public sealed record TrialBalanceReportRowDto(
    string AccountCode,
    string AccountName,
    decimal Debit,
    decimal Credit);

public sealed record IncomeStatementReportRowDto(
    string AccountCode,
    string AccountName,
    string AccountType,
    decimal Revenue,
    decimal Expense,
    decimal NetAmount);

public sealed record BalanceSheetReportRowDto(
    string AccountCode,
    string AccountName,
    string AccountType,
    decimal Debit,
    decimal Credit,
    decimal Balance);

public sealed record LedgerReportTotalsDto(
    decimal Debit,
    decimal Credit,
    decimal Balance,
    int RowCount);

public sealed record LedgerReportDto<T>(IReadOnlyList<T> Rows, LedgerReportTotalsDto Totals);

public sealed record CashAccountDto(
    string Code,
    string Name,
    string AccountCode,
    string AccountName,
    bool IsActive,
    long Version);

public sealed record SaveCashAccountRequest(
    string Code,
    string Name,
    string AccountCode,
    bool IsActive,
    long? ExpectedVersion,
    string UserName,
    string ComputerName);

public sealed record BankAccountDto(
    string Code,
    string BankName,
    string? AccountNumber,
    string? AccountHolder,
    string AccountCode,
    string AccountName,
    bool IsActive,
    long Version);

public sealed record SaveBankAccountRequest(
    string Code,
    string BankName,
    string? AccountNumber,
    string? AccountHolder,
    string AccountCode,
    bool IsActive,
    long? ExpectedVersion,
    string UserName,
    string ComputerName);

public sealed record CashBankCategoryDto(
    string CategoryType,
    string Code,
    string Name,
    string AccountCode,
    string AccountName,
    bool IsDeleted,
    bool IsActive,
    long Version);

public sealed record SaveCashBankCategoryRequest(
    string CategoryType,
    string Code,
    string Name,
    string AccountCode,
    bool IsDeleted,
    bool IsActive,
    long? ExpectedVersion,
    string UserName,
    string ComputerName);

public sealed record CreateCashBankTransactionRequest(
    DateOnly DocumentDate,
    string TransactionKind,
    string Method,
    string? CashAccountCode,
    string? BankAccountCode,
    string? CheckGiroNumber,
    DateOnly? CheckGiroDueDate,
    string? Counterparty,
    string? CategoryType,
    string? CategoryCode,
    string AccountCode,
    decimal Amount,
    string? Notes,
    string UserName,
    string ComputerName);

public sealed record CashBankTransactionDto(
    Guid Id,
    string DocumentNumber,
    DateOnly DocumentDate,
    string TransactionKind,
    string Method,
    string? CashAccountCode,
    string? CashAccountName,
    string? BankAccountCode,
    string? BankAccountName,
    string? CheckGiroNumber,
    DateOnly? CheckGiroDueDate,
    string? Counterparty,
    string? CategoryType,
    string? CategoryCode,
    string AccountCode,
    string AccountName,
    decimal Amount,
    string? Notes,
    string Status,
    string JournalNumber,
    DateTimeOffset CreatedAt);

public sealed record CancelCashBankTransactionRequest(string Reason, string UserName, string ComputerName);
public sealed record RestoreCashBankTransactionRequest(string UserName, string ComputerName);

public sealed record CashBankTransactionLineDto(
    int LineNumber,
    string SettlementMethod,
    string? SettlementCode,
    string SettlementAccountCode,
    string? CounterpartyGroup,
    string? CounterpartyCode,
    string? CounterpartyName,
    string AccountCode,
    string AccountName,
    decimal Amount,
    string? Notes,
    string? CheckGiroNumber,
    DateOnly? CheckGiroDueDate);
