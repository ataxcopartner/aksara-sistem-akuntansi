using Aksara.Contracts;

namespace Aksara.Desktop.Services;

internal static class ProgramConfigurationCache
{
    public const string PrintCompanyName = "CV. SEJAHTERA JAYA MANDIRI";

    public static ProgramConfigurationDto Current { get; private set; } = Defaults();

    public static async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            Current = await AksaraApiClient.Shared.GetProgramConfigurationAsync(cancellationToken);
        }
        catch
        {
            Current = Defaults();
        }
    }

    public static void Set(ProgramConfigurationDto value) => Current = value;

    public static string ResolveTitle(string title)
    {
        var normalized = title.Trim().ToUpperInvariant();
        return normalized switch
        {
            "NOTA PENJUALAN" => Current.SaleTitle,
            "NOTA PEMBELIAN" or "FAKTUR PEMBELIAN" => Current.PurchaseTitle,
            "RETUR PENJUALAN" => Current.SaleReturnTitle,
            "RETUR PEMBELIAN" => Current.PurchaseReturnTitle,
            "SURAT JALAN" => Current.DeliveryTitle,
            "BUKTI KAS / BANK" or "BUKTI KAS BANK" => Current.CashBankTitle,
            _ => title
        };
    }

    private static ProgramConfigurationDto Defaults() => new(
        PrintCompanyName, "", "", "", "", "", "", true, 0, 20, 20,
        "PJL", "PBM", "RJL", "RPB", "KB", "BPT", "BUT", "CG",
        false, true, false, "TEXT", "NOTA PENJUALAN", "NOTA PEMBELIAN",
        "RETUR PENJUALAN", "RETUR PEMBELIAN", "SURAT JALAN", "BUKTI KAS / BANK",
        "Dibuat Oleh", "Diperiksa Oleh", "Diterima Oleh", "", DateTimeOffset.MinValue, 0);
}
