namespace Aksara.Contracts;

public sealed record LoginRequest(string UserName, string Password);

public sealed record ResolveLoginRouteRequest(string UserName);

public sealed record LoginRouteResponse(
    string ServerUrl,
    string RouteKind,
    string? CompanyCode,
    Guid? ExpectedCompanyInstanceId);

public sealed record AuthenticatedUserDto(
    Guid Id,
    string UserName,
    string DisplayName,
    string RoleCode,
    string RoleName,
    IReadOnlyList<PermissionGrantDto> Permissions);

public sealed record LoginResponse(
    string UserName,
    string DisplayName,
    string RoleCode,
    string RoleName,
    IReadOnlyList<PermissionGrantDto> Permissions,
    CompanyIdentityDto Company,
    string Token,
    DateTimeOffset ExpiresAt,
    string? ServerUrl = null);

public sealed record SessionStatusResponse(
    bool IsActive,
    string UserName,
    DateTimeOffset ExpiresAt);
