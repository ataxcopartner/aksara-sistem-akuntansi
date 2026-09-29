namespace Aksara.Contracts;

public static class CompanyIdentityPolicy
{
    public const string PendingCompanyName = "Perusahaan Baru (Belum Setup)";

    public static string BootstrapCompanyName(string? companyName) =>
        string.IsNullOrWhiteSpace(companyName) ? PendingCompanyName : companyName.Trim();

    public static bool IsFinalCompanyName(string? companyName)
    {
        var value = companyName?.Trim();
        return value is { Length: >= 2 and <= 150 } &&
               !value.Equals(PendingCompanyName, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSetupCompanyProfileReady(string? companyName, bool profileConfirmed) =>
        profileConfirmed && IsFinalCompanyName(companyName);
}
