namespace Aksara.Contracts;

public sealed record ProgramConfigurationDto(
    string CompanyName,
    string OwnerName,
    string Address,
    string City,
    string Phone,
    string Email,
    string TaxNumber,
    bool TaxEnabled,
    decimal DefaultTaxPercent,
    int TransactionDetailRows,
    int MinimumPrintRows,
    string SaleNumberPrefix,
    string PurchaseNumberPrefix,
    string SaleReturnNumberPrefix,
    string PurchaseReturnNumberPrefix,
    string CashBankNumberPrefix,
    string CustomerReceiptNumberPrefix,
    string SupplierPaymentNumberPrefix,
    string CheckGiroNumberPrefix,
    bool ShowPrintDateTime,
    bool ShowItemCode,
    bool ShowPrintedBy,
    string PrintHeaderMode,
    string SaleTitle,
    string PurchaseTitle,
    string SaleReturnTitle,
    string PurchaseReturnTitle,
    string DeliveryTitle,
    string CashBankTitle,
    string Signature1,
    string Signature2,
    string Signature3,
    string PrintFooterNote,
    DateTimeOffset UpdatedAt,
    long Version);

public sealed record SaveProgramConfigurationRequest(
    string CompanyName,
    string? OwnerName,
    string? Address,
    string? City,
    string? Phone,
    string? Email,
    string? TaxNumber,
    bool TaxEnabled,
    decimal DefaultTaxPercent,
    int TransactionDetailRows,
    int MinimumPrintRows,
    string SaleNumberPrefix,
    string PurchaseNumberPrefix,
    string SaleReturnNumberPrefix,
    string PurchaseReturnNumberPrefix,
    string CashBankNumberPrefix,
    string CustomerReceiptNumberPrefix,
    string SupplierPaymentNumberPrefix,
    string CheckGiroNumberPrefix,
    bool ShowPrintDateTime,
    bool ShowItemCode,
    bool ShowPrintedBy,
    string PrintHeaderMode,
    string SaleTitle,
    string PurchaseTitle,
    string SaleReturnTitle,
    string PurchaseReturnTitle,
    string DeliveryTitle,
    string CashBankTitle,
    string? Signature1,
    string? Signature2,
    string? Signature3,
    string? PrintFooterNote,
    long ExpectedVersion,
    string? UserName,
    string? ComputerName);

public sealed record PermissionGrantDto(string Module, string Action);

public sealed record AppRoleDto(
    Guid Id,
    string Code,
    string Name,
    bool IsSystem,
    bool IsActive,
    long Version);

public sealed record AppUserDto(
    Guid Id,
    string UserName,
    string DisplayName,
    Guid RoleId,
    string RoleCode,
    string RoleName,
    bool IsActive,
    DateTimeOffset? LastLoginAt,
    long Version);

public sealed record RolePermissionDto(
    string Module,
    string ModuleName,
    bool CanView,
    bool CanManage,
    bool CanDelete);

public sealed record SaveAppRoleRequest(
    Guid? Id,
    string Code,
    string Name,
    bool IsActive,
    long? ExpectedVersion);

public sealed record SaveAppUserRequest(
    Guid? Id,
    string UserName,
    string DisplayName,
    Guid RoleId,
    string? Password,
    bool IsActive,
    long? ExpectedVersion);

public sealed record SaveRolePermissionsRequest(IReadOnlyList<RolePermissionDto> Permissions);

public sealed record ChangeCredentialsRequest(
    string UserName,
    string DisplayName,
    string CurrentPassword,
    string? NewPassword);
