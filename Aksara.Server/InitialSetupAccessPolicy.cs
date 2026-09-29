using Microsoft.AspNetCore.Http;

namespace Aksara.Server;

public static class InitialSetupAccessPolicy
{
    public static bool RequiresSetup(string status, bool legacyProtected) =>
        !legacyProtected && !status.Equals("ACTIVE", StringComparison.OrdinalIgnoreCase);

    public static bool IsAllowed(PathString path, string method)
    {
        var value = path.Value ?? string.Empty;
        if (value.Equals("/api/auth/session", StringComparison.OrdinalIgnoreCase)) return HttpMethods.IsGet(method);
        if (value.StartsWith("/api/configuration/initial-setup", StringComparison.OrdinalIgnoreCase)) return true;
        if (value.Equals("/api/configuration/program", StringComparison.OrdinalIgnoreCase)) return HttpMethods.IsGet(method) || HttpMethods.IsPost(method);

        if (value.Equals("/api/ledger/accounts", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("/api/ledger/mappings", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("/api/master/suppliers", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("/api/master/customers", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("/api/master/salespeople", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("/api/inventory/warehouses", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("/api/inventory/units", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("/api/inventory/categories", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("/api/inventory/items", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("/api/cash-bank/cash-accounts", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("/api/cash-bank/bank-accounts", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("/api/cash-bank/categories", StringComparison.OrdinalIgnoreCase))
            return HttpMethods.IsGet(method) || HttpMethods.IsPost(method);

        if (value.StartsWith("/api/inventory/items/", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("/api/inventory/balances", StringComparison.OrdinalIgnoreCase))
            return HttpMethods.IsGet(method);

        return false;
    }
}
