using Aksara.Contracts;

namespace Aksara.Server;

public sealed record TenantLoginDirectoryEntry(
    string UserName,
    string ServerUrl,
    string? CompanyCode,
    Guid? CompanyInstanceId,
    bool IsActive = true);

public sealed record TenantRouteResolution(
    bool IsAmbiguous,
    LoginRouteResponse? Route,
    string? Error);

public sealed class TenantLoginDirectory
{
    private readonly TenantLoginDirectoryEntry[] _entries;

    public TenantLoginDirectory(IEnumerable<TenantLoginDirectoryEntry>? entries)
    {
        _entries = (entries ?? [])
            .Where(entry => entry.IsActive)
            .Select(Validate)
            .ToArray();
    }

    public TenantRouteResolution Resolve(string? userName, bool existsOnDefaultTenant, string defaultServerUrl)
    {
        var normalizedUserName = userName?.Trim() ?? string.Empty;
        if (normalizedUserName.Length == 0)
            return new(false, null, "Username wajib diisi.");

        var matches = _entries
            .Where(entry => entry.UserName.Equals(normalizedUserName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length > 1 || (matches.Length == 1 && existsOnDefaultTenant))
            return new(true, null, "Username terdaftar pada lebih dari satu perusahaan. Login ditolak sampai direktori tenant diperbaiki.");

        if (matches.Length == 1)
        {
            var match = matches[0];
            return new(false, new(match.ServerUrl, "TENANT", match.CompanyCode, match.CompanyInstanceId), null);
        }

        return new(false, new(NormalizeServerUrl(defaultServerUrl), "DEFAULT", null, null), null);
    }

    private static TenantLoginDirectoryEntry Validate(TenantLoginDirectoryEntry entry)
    {
        var userName = entry.UserName?.Trim() ?? string.Empty;
        if (userName.Length is < 3 or > 50 || userName.Any(char.IsWhiteSpace))
            throw new InvalidOperationException("Username pada direktori tenant tidak valid.");

        var serverUrl = NormalizeServerUrl(entry.ServerUrl);
        var companyCode = string.IsNullOrWhiteSpace(entry.CompanyCode) ? null : entry.CompanyCode.Trim().ToUpperInvariant();
        return entry with { UserName = userName, ServerUrl = serverUrl, CompanyCode = companyCode };
    }

    public static string NormalizeServerUrl(string? value)
    {
        if (!Uri.TryCreate(value?.Trim().TrimEnd('/'), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            throw new InvalidOperationException("Alamat server pada direktori tenant wajib berupa URL HTTP(S) absolut.");
        return uri.ToString().TrimEnd('/');
    }
}
