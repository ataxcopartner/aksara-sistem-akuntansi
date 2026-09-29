using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.IO;
using Aksara.Contracts;

namespace Aksara.Desktop.Services;

public sealed class AksaraApiException(string message, string? code = null, HttpStatusCode? statusCode = null)
    : InvalidOperationException(message)
{
    public string? Code { get; } = code;
    public HttpStatusCode? StatusCode { get; } = statusCode;
}

public sealed class AksaraApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private HttpClient _httpClient;
    private readonly Uri _bootstrapAddress;
    private int _sessionExpirationSignaled;

    public static AksaraApiClient Shared { get; } = new();

    public event EventHandler? SessionExpired;
    public event EventHandler? InitialSetupRequired;

    public Uri ServerAddress => _httpClient.BaseAddress!;
    public LoginResponse? CurrentSession { get; private set; }
    public string CurrentUserName => CurrentSession?.UserName ?? Environment.UserName;
    public string CurrentDisplayName => CurrentSession?.DisplayName ?? Environment.UserName;
    public string CurrentRoleCode => CurrentSession?.RoleCode ?? string.Empty;
    public CompanyIdentityDto? CurrentCompany => CurrentSession?.Company;

    private AksaraApiClient()
    {
        _bootstrapAddress = CreateBaseAddress(ResolveConfiguredAddress());
        _httpClient = CreateHttpClient(_bootstrapAddress);
    }

    public void ConfigureServerAddress(string serverAddress) => SwitchServerAddress(serverAddress, enforceConfiguredLock: true);

    public void ConfigureRoutedServerAddress(string serverAddress) => SwitchServerAddress(serverAddress, enforceConfiguredLock: false);

    private void SwitchServerAddress(string serverAddress, bool enforceConfiguredLock)
    {
        var baseAddress = CreateBaseAddress(serverAddress);
        if (enforceConfiguredLock && AppBrand.IsServerAddressLocked)
        {
            var lockedAddress = Environment.GetEnvironmentVariable("AKSARA_SERVER_URL");
            if (string.IsNullOrWhiteSpace(lockedAddress) || baseAddress != CreateBaseAddress(lockedAddress))
                throw new AksaraApiException("Alamat server dikunci oleh profil aplikasi ini.");
        }

        if (_httpClient.BaseAddress == baseAddress)
        {
            Logout();
            return;
        }

        var previousClient = _httpClient;
        _httpClient = CreateHttpClient(baseAddress);
        previousClient.Dispose();
        Logout();
    }

    public async Task<LoginRouteResponse> ResolveLoginRouteAsync(string userName, CancellationToken cancellationToken = default)
    {
        using var client = CreateHttpClient(_bootstrapAddress);
        using var response = await client.PostAsJsonAsync("api/auth/resolve", new ResolveLoginRouteRequest(userName), JsonOptions, cancellationToken);
        return await ReadRequiredAsync<LoginRouteResponse>(response, cancellationToken);
    }

    public async Task<LoginResponse> LoginAsync(string userName, string password, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync("api/auth/login", new LoginRequest(userName, password), JsonOptions, cancellationToken);
        var session = await ReadRequiredAsync<LoginResponse>(response, cancellationToken);
        CurrentSession = session;
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
        Interlocked.Exchange(ref _sessionExpirationSignaled, 0);
        return session;
    }

    public Task<SystemInfoResponse> GetSystemInfoAsync(CancellationToken cancellationToken = default) =>
        GetRequiredAsync<SystemInfoResponse>("api/system/info", cancellationToken);

    public Task<InitialSetupDto> GetInitialSetupAsync(CancellationToken cancellationToken = default) =>
        GetRequiredAsync<InitialSetupDto>("api/configuration/initial-setup", cancellationToken);

    public async Task<InitialSetupDto> SaveInitialSetupAsync(SaveInitialSetupRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync("api/configuration/initial-setup", request, JsonOptions, cancellationToken);
        return await ReadRequiredAsync<InitialSetupDto>(response, cancellationToken);
    }

    public async Task<InitialSetupDto> ActivateInitialSetupAsync(ActivateInitialSetupRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync("api/configuration/initial-setup/activate", request, JsonOptions, cancellationToken);
        return await ReadRequiredAsync<InitialSetupDto>(response, cancellationToken);
    }

    public async Task<InitialSetupDto> ReopenInitialSetupAsync(ReopenInitialSetupRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync("api/configuration/initial-setup/reopen", request, JsonOptions, cancellationToken);
        return await ReadRequiredAsync<InitialSetupDto>(response, cancellationToken);
    }

    public void Logout()
    {
        CurrentSession = null;
        _httpClient.DefaultRequestHeaders.Authorization = null;
    }

    public bool CheckSessionExpiration()
    {
        var sessionExpiry = CurrentSession?.ExpiresAt;
        if (sessionExpiry is null || sessionExpiry > DateTimeOffset.UtcNow) return false;

        SignalSessionExpired();
        return true;
    }

    public async Task<bool> ValidateCurrentSessionAsync(CancellationToken cancellationToken = default)
    {
        if (CheckSessionExpiration()) return false;
        if (CurrentSession is null)
        {
            SignalSessionExpired();
            return false;
        }

        using var response = await _httpClient.GetAsync("api/auth/session", cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            SignalSessionExpired();
            return false;
        }

        var status = await ReadRequiredAsync<SessionStatusResponse>(response, cancellationToken);
        if (status.IsActive) return true;

        SignalSessionExpired();
        return false;
    }

    public bool Can(string module, string action)
    {
        var session = CurrentSession;
        if (session is null) return false;
        if (session.RoleCode.Equals("MASTER", StringComparison.OrdinalIgnoreCase)) return true;
        return session.Permissions?.Any(x => x.Module.Equals(module, StringComparison.OrdinalIgnoreCase) && x.Action.Equals(action, StringComparison.OrdinalIgnoreCase)) == true;
    }

    public Task<ProgramConfigurationDto> GetProgramConfigurationAsync(CancellationToken cancellationToken = default) =>
        GetRequiredAsync<ProgramConfigurationDto>("api/configuration/program", cancellationToken);

    public async Task<ProgramConfigurationDto> SaveProgramConfigurationAsync(SaveProgramConfigurationRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync("api/configuration/program", request, JsonOptions, cancellationToken);
        return await ReadRequiredAsync<ProgramConfigurationDto>(response, cancellationToken);
    }

    public Task<IReadOnlyList<AppRoleDto>> GetRolesAsync(CancellationToken cancellationToken = default) =>
        GetListAsync<AppRoleDto>("api/configuration/roles", cancellationToken);

    public async Task<AppRoleDto> SaveRoleAsync(SaveAppRoleRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync("api/configuration/roles", request, JsonOptions, cancellationToken);
        return await ReadRequiredAsync<AppRoleDto>(response, cancellationToken);
    }

    public Task<IReadOnlyList<AppUserDto>> GetUsersAsync(CancellationToken cancellationToken = default) =>
        GetListAsync<AppUserDto>("api/configuration/users", cancellationToken);

    public async Task<AppUserDto> SaveUserAsync(SaveAppUserRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync("api/configuration/users", request, JsonOptions, cancellationToken);
        return await ReadRequiredAsync<AppUserDto>(response, cancellationToken);
    }

    public Task<IReadOnlyList<RolePermissionDto>> GetRolePermissionsAsync(Guid roleId, CancellationToken cancellationToken = default) =>
        GetListAsync<RolePermissionDto>($"api/configuration/roles/{roleId}/permissions", cancellationToken);

    public async Task<IReadOnlyList<RolePermissionDto>> SaveRolePermissionsAsync(Guid roleId, IReadOnlyList<RolePermissionDto> permissions, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync($"api/configuration/roles/{roleId}/permissions", new SaveRolePermissionsRequest(permissions), JsonOptions, cancellationToken);
        return await ReadRequiredAsync<List<RolePermissionDto>>(response, cancellationToken);
    }

    public async Task<LoginResponse> ChangeCredentialsAsync(ChangeCredentialsRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync("api/configuration/change-credentials", request, JsonOptions, cancellationToken);
        var session = await ReadRequiredAsync<LoginResponse>(response, cancellationToken);
        CurrentSession = session;
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
        return session;
    }

    public Task<IReadOnlyList<PeriodCloseDto>> GetPeriodClosesAsync(CancellationToken cancellationToken = default) =>
        GetListAsync<PeriodCloseDto>("api/maintenance/period-close", cancellationToken);

    public async Task<PeriodClosePreflightDto> GetPeriodClosePreflightAsync(PeriodClosePreflightRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync("api/maintenance/period-close/preflight", request, JsonOptions, cancellationToken);
        return await ReadRequiredAsync<PeriodClosePreflightDto>(response, cancellationToken);
    }

    public async Task<PeriodCloseDto> ClosePeriodAsync(CreatePeriodCloseRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync("api/maintenance/period-close", request, JsonOptions, cancellationToken);
        return await ReadRequiredAsync<PeriodCloseDto>(response, cancellationToken);
    }

    public async Task<PeriodCloseDto> ReopenPeriodCloseAsync(Guid id, ReopenPeriodCloseRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync($"api/maintenance/period-close/{id}/reopen", request, JsonOptions, cancellationToken);
        return await ReadRequiredAsync<PeriodCloseDto>(response, cancellationToken);
    }

    public Task<PeriodCloseExportBundleDto> GetPeriodCloseExportAsync(Guid id, CancellationToken cancellationToken = default) =>
        GetRequiredAsync<PeriodCloseExportBundleDto>($"api/maintenance/period-close/{id}/export", cancellationToken);

    public static string LocalConfigurationPath => Path.Combine(AppBrand.LocalDataDirectory, "aksara-client.json");

    public static void SaveLocalServerAddress(string serverAddress)
    {
        if (AppBrand.IsServerAddressLocked)
            throw new AksaraApiException("Alamat server dikunci oleh profil aplikasi ini.");

        var address = CreateBaseAddress(serverAddress).ToString().TrimEnd('/');
        var directory = Path.GetDirectoryName(LocalConfigurationPath)!;
        Directory.CreateDirectory(directory);
        File.WriteAllText(LocalConfigurationPath, JsonSerializer.Serialize(new { serverUrl = address }, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static async Task<bool> TestServerAddressAsync(string serverAddress, CancellationToken cancellationToken = default)
    {
        using var client = CreateHttpClient(CreateBaseAddress(serverAddress));
        using var response = await client.GetAsync("api/health", cancellationToken);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _httpClient.GetAsync("api/health", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException) { return false; }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
    }

    public async Task<bool> IsDatabaseReadyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _httpClient.GetAsync("api/database/status", cancellationToken);
            if (!response.IsSuccessStatusCode) return false;
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            return document.RootElement.TryGetProperty("connected", out var connected) && connected.GetBoolean();
        }
        catch (HttpRequestException) { return false; }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
    }

    public Task<IReadOnlyList<LedgerAccountDto>> GetLedgerAccountsAsync(string? query, bool includeInactive = true, bool onlyPostable = false, string? type = null, CancellationToken cancellationToken = default) =>
        GetListAsync<LedgerAccountDto>($"api/ledger/accounts?q={Escape(query)}&includeInactive={includeInactive}&onlyPostable={onlyPostable}&type={Escape(type)}", cancellationToken);

    public async Task<LedgerAccountDto> SaveLedgerAccountAsync(SaveLedgerAccountRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync("api/ledger/accounts", request, JsonOptions, cancellationToken);
        return await ReadRequiredAsync<LedgerAccountDto>(response, cancellationToken);
    }

    public Task<IReadOnlyList<JournalEntrySummaryDto>> GetJournalEntriesAsync(string? query, CancellationToken cancellationToken = default) =>
        GetListAsync<JournalEntrySummaryDto>($"api/ledger/journals?q={Escape(query)}&limit=200", cancellationToken);

    public async Task<JournalEntryDto> GetJournalEntryAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync($"api/ledger/journals/{id}", cancellationToken);
        return await ReadRequiredAsync<JournalEntryDto>(response, cancellationToken);
    }

    public async Task<JournalEntryDto> CreateJournalEntryAsync(CreateJournalEntryRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync("api/ledger/journals", request, JsonOptions, cancellationToken);
        return await ReadRequiredAsync<JournalEntryDto>(response, cancellationToken);
    }

    public Task<IReadOnlyList<LedgerTransactionMappingDto>> GetLedgerTransactionMappingsAsync(CancellationToken cancellationToken = default) =>
        GetListAsync<LedgerTransactionMappingDto>("api/ledger/mappings", cancellationToken);

    public async Task<LedgerTransactionMappingDto> SaveLedgerTransactionMappingAsync(SaveLedgerTransactionMappingRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync("api/ledger/mappings", request, JsonOptions, cancellationToken);
        return await ReadRequiredAsync<LedgerTransactionMappingDto>(response, cancellationToken);
    }

    public async Task<LedgerReportDto<LedgerAccountDto>> GetLedgerAccountReportAsync(string? query, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync($"api/ledger/reports/accounts?q={Escape(query)}", cancellationToken);
        return await ReadRequiredAsync<LedgerReportDto<LedgerAccountDto>>(response, cancellationToken);
    }

    public async Task<LedgerReportDto<LedgerJournalReportRowDto>> GetLedgerJournalReportAsync(DateOnly from, DateOnly to, string? query, string? source = null, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync($"api/ledger/reports/journal?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}&q={Escape(query)}&source={Escape(source)}", cancellationToken);
        return await ReadRequiredAsync<LedgerReportDto<LedgerJournalReportRowDto>>(response, cancellationToken);
    }

    public async Task<LedgerReportDto<GeneralLedgerReportRowDto>> GetGeneralLedgerReportAsync(DateOnly from, DateOnly to, string? account, string? query, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync($"api/ledger/reports/general-ledger?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}&account={Escape(account)}&q={Escape(query)}", cancellationToken);
        return await ReadRequiredAsync<LedgerReportDto<GeneralLedgerReportRowDto>>(response, cancellationToken);
    }

    public async Task<LedgerReportDto<TrialBalanceReportRowDto>> GetTrialBalanceReportAsync(DateOnly from, DateOnly to, string? query, bool includeZero = false, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync($"api/ledger/reports/trial-balance?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}&q={Escape(query)}&includeZero={includeZero}", cancellationToken);
        return await ReadRequiredAsync<LedgerReportDto<TrialBalanceReportRowDto>>(response, cancellationToken);
    }

    public async Task<LedgerReportDto<IncomeStatementReportRowDto>> GetIncomeStatementReportAsync(DateOnly from, DateOnly to, string? query, bool includeZero = false, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync($"api/ledger/reports/income-statement?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}&q={Escape(query)}&includeZero={includeZero}", cancellationToken);
        return await ReadRequiredAsync<LedgerReportDto<IncomeStatementReportRowDto>>(response, cancellationToken);
    }

    public async Task<LedgerReportDto<BalanceSheetReportRowDto>> GetBalanceSheetReportAsync(DateOnly from, DateOnly to, string? query, bool includeZero = false, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync($"api/ledger/reports/balance-sheet?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}&q={Escape(query)}&includeZero={includeZero}", cancellationToken);
        return await ReadRequiredAsync<LedgerReportDto<BalanceSheetReportRowDto>>(response, cancellationToken);
    }

    public Task<IReadOnlyList<CashAccountDto>> GetCashAccountsAsync(bool includeInactive = true, CancellationToken cancellationToken = default) =>
        GetListAsync<CashAccountDto>($"api/cash-bank/cash-accounts?includeInactive={includeInactive}", cancellationToken);

    public async Task<CashAccountDto> SaveCashAccountAsync(SaveCashAccountRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync("api/cash-bank/cash-accounts", request, JsonOptions, cancellationToken);
        return await ReadRequiredAsync<CashAccountDto>(response, cancellationToken);
    }

    public Task<IReadOnlyList<BankAccountDto>> GetBankAccountsAsync(bool includeInactive = true, CancellationToken cancellationToken = default) =>
        GetListAsync<BankAccountDto>($"api/cash-bank/bank-accounts?includeInactive={includeInactive}", cancellationToken);

    public async Task<BankAccountDto> SaveBankAccountAsync(SaveBankAccountRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync("api/cash-bank/bank-accounts", request, JsonOptions, cancellationToken);
        return await ReadRequiredAsync<BankAccountDto>(response, cancellationToken);
    }

    public Task<IReadOnlyList<CashBankCategoryDto>> GetCashBankCategoriesAsync(string? type = null, bool includeDeleted = true, CancellationToken cancellationToken = default) =>
        GetListAsync<CashBankCategoryDto>($"api/cash-bank/categories?type={Escape(type)}&includeDeleted={includeDeleted}", cancellationToken);

    public async Task<CashBankCategoryDto> SaveCashBankCategoryAsync(SaveCashBankCategoryRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync("api/cash-bank/categories", request, JsonOptions, cancellationToken);
        return await ReadRequiredAsync<CashBankCategoryDto>(response, cancellationToken);
    }

    public Task<IReadOnlyList<CashBankTransactionDto>> GetCashBankTransactionsAsync(string? query, int limit = 1000, CancellationToken cancellationToken = default) =>
        GetListAsync<CashBankTransactionDto>($"api/cash-bank/transactions?q={Escape(query)}&limit={limit}", cancellationToken);

    public Task<IReadOnlyList<CashBankTransactionLineDto>> GetCashBankTransactionLinesAsync(Guid transactionId, CancellationToken cancellationToken = default) =>
        GetListAsync<CashBankTransactionLineDto>($"api/cash-bank/transactions/{transactionId}/lines", cancellationToken);

    public async Task<CashBankTransactionDto> CreateCashBankTransactionAsync(CreateCashBankTransactionRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync("api/cash-bank/transactions", request, JsonOptions, cancellationToken);
        return await ReadRequiredAsync<CashBankTransactionDto>(response, cancellationToken);
    }

    public async Task<CashBankTransactionDto> CancelCashBankTransactionAsync(Guid id, CancelCashBankTransactionRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync($"api/cash-bank/transactions/{id}/cancel", request, JsonOptions, cancellationToken);
        return await ReadRequiredAsync<CashBankTransactionDto>(response, cancellationToken);
    }

    public async Task<CashBankTransactionDto> RestoreCashBankTransactionAsync(Guid id, RestoreCashBankTransactionRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync($"api/cash-bank/transactions/{id}/restore", request, JsonOptions, cancellationToken);
        return await ReadRequiredAsync<CashBankTransactionDto>(response, cancellationToken);
    }

    public Task<IReadOnlyList<SupplierMasterDto>> GetSuppliersAsync(string? query, bool includeInactive = true, CancellationToken cancellationToken = default) =>
        GetListAsync<SupplierMasterDto>($"api/master/suppliers?q={Escape(query)}&includeInactive={includeInactive}&limit=1000", cancellationToken);

    public async Task<SupplierMasterDto> SaveSupplierAsync(SaveSupplierMasterRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync("api/master/suppliers", request, JsonOptions, cancellationToken);
        return await ReadRequiredAsync<SupplierMasterDto>(response, cancellationToken);
    }

    public Task<IReadOnlyList<CustomerMasterDto>> GetCustomersAsync(string? query, bool includeInactive = true, CancellationToken cancellationToken = default) =>
        GetListAsync<CustomerMasterDto>($"api/master/customers?q={Escape(query)}&includeInactive={includeInactive}&limit=1000", cancellationToken);

    public async Task<CustomerMasterDto> SaveCustomerAsync(SaveCustomerMasterRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync("api/master/customers", request, JsonOptions, cancellationToken);
        return await ReadRequiredAsync<CustomerMasterDto>(response, cancellationToken);
    }

    public Task<IReadOnlyList<SalespersonMasterDto>> GetSalespeopleAsync(string? query, bool includeInactive = true, CancellationToken cancellationToken = default) =>
        GetListAsync<SalespersonMasterDto>($"api/master/salespeople?q={Escape(query)}&includeInactive={includeInactive}&limit=1000", cancellationToken);

    public async Task<SalespersonMasterDto> SaveSalespersonAsync(SaveSalespersonMasterRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync("api/master/salespeople", request, JsonOptions, cancellationToken);
        return await ReadRequiredAsync<SalespersonMasterDto>(response, cancellationToken);
    }

    public Task<IReadOnlyList<SalesCustomerDto>> SearchCustomersAsync(string? query, CancellationToken cancellationToken = default) =>
        GetListAsync<SalesCustomerDto>($"api/sales/customers?q={Escape(query)}&limit=100", cancellationToken);

    public Task<IReadOnlyList<SalespersonDto>> SearchSalespeopleAsync(string? query, CancellationToken cancellationToken = default) =>
        GetListAsync<SalespersonDto>($"api/sales/salespeople?q={Escape(query)}&limit=100", cancellationToken);

    public Task<IReadOnlyList<SalesItemDto>> SearchItemsAsync(string? query, string warehouseCode, CancellationToken cancellationToken = default, string itemType = "GOODS") =>
        GetListAsync<SalesItemDto>($"api/sales/items?q={Escape(query)}&warehouse={Escape(warehouseCode)}&kind={Escape(itemType)}&limit=200", cancellationToken);

    public Task<IReadOnlyList<SaleDto>> SearchSalesAsync(string? query, CancellationToken cancellationToken = default, string documentKind = "GOODS") =>
        GetListAsync<SaleDto>($"api/sales?q={Escape(query)}&kind={Escape(documentKind)}&limit=100", cancellationToken);

    public async Task<int> CountSalesAsync(string? query, CancellationToken cancellationToken = default, string documentKind = "GOODS")
    {
        using var response = await _httpClient.GetAsync($"api/sales/count?q={Escape(query)}&kind={Escape(documentKind)}", cancellationToken);
        return await ReadRequiredAsync<int>(response, cancellationToken);
    }

    public async Task<string> GetNextSaleNumberAsync(DateOnly date, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync($"api/sales/next-number?date={date:yyyy-MM-dd}", cancellationToken);
        return await ReadRequiredAsync<string>(response, cancellationToken);
    }

    public Task<IReadOnlyList<InventoryBalanceDto>> GetInventoryBalancesAsync(string? query, string warehouseCode, CancellationToken cancellationToken = default) =>
        GetListAsync<InventoryBalanceDto>($"api/inventory/balances?q={Escape(query)}&warehouse={Escape(warehouseCode)}", cancellationToken);

    public async Task<SaleDto> CreateSaleAsync(CreateSaleRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync("api/sales", request, JsonOptions, cancellationToken);
        return await ReadRequiredAsync<SaleDto>(response, cancellationToken);
    }

    public async Task<SaleDto> GetSaleAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync($"api/sales/{id}", cancellationToken);
        return await ReadRequiredAsync<SaleDto>(response, cancellationToken);
    }

    public async Task<GunggunganSaleResultDto> CreateGunggunganSaleAsync(
        CreateGunggunganSaleRequest request,
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync("api/sales/gunggungan", request, JsonOptions, cancellationToken);
        return await ReadRequiredAsync<GunggunganSaleResultDto>(response, cancellationToken);
    }

    public async Task<GunggunganSaleReportDto> GetGunggunganSalesAsync(
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(
            $"api/sales/gunggungan?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}", cancellationToken);
        return await ReadRequiredAsync<GunggunganSaleReportDto>(response, cancellationToken);
    }

    public async Task<GunggunganSaleResultDto> GetGunggunganSaleAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync($"api/sales/gunggungan/{id}", cancellationToken);
        return await ReadRequiredAsync<GunggunganSaleResultDto>(response, cancellationToken);
    }

    public async Task<FifoCostPreviewDto> PreviewFifoCostAsync(FifoCostPreviewRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync("api/sales/fifo-preview", request, JsonOptions, cancellationToken);
        return await ReadRequiredAsync<FifoCostPreviewDto>(response, cancellationToken);
    }

    public async Task<SaleDto> CancelSaleAsync(Guid id, CancelSaleRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync($"api/sales/{id}/cancel", request, JsonOptions, cancellationToken);
        return await ReadRequiredAsync<SaleDto>(response, cancellationToken);
    }

    public async Task<SaleDto> RestoreSaleAsync(Guid id, RestoreSaleRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync($"api/sales/{id}/restore", request, JsonOptions, cancellationToken);
        return await ReadRequiredAsync<SaleDto>(response, cancellationToken);
    }

    public async Task<SaleReturnSourceDto> GetSaleReturnSourceAsync(Guid saleId, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync($"api/sales/{saleId}/return-source", cancellationToken);
        return await ReadRequiredAsync<SaleReturnSourceDto>(response, cancellationToken);
    }

    public Task<IReadOnlyList<SaleReturnDto>> SearchSaleReturnsAsync(string? query, CancellationToken cancellationToken = default) =>
        GetListAsync<SaleReturnDto>($"api/sales/returns?q={Escape(query)}&limit=100", cancellationToken);

    public async Task<string> GetNextSaleReturnNumberAsync(DateOnly date, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync($"api/sales/returns/next-number?date={date:yyyy-MM-dd}", cancellationToken);
        return await ReadRequiredAsync<string>(response, cancellationToken);
    }

    public async Task<SaleReturnDto> CreateSaleReturnAsync(CreateSaleReturnRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync("api/sales/returns", request, JsonOptions, cancellationToken);
        return await ReadRequiredAsync<SaleReturnDto>(response, cancellationToken);
    }

    public async Task<SaleReturnDto> CancelSaleReturnAsync(Guid id, CancelSaleReturnRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync($"api/sales/returns/{id}/cancel", request, JsonOptions, cancellationToken);
        return await ReadRequiredAsync<SaleReturnDto>(response, cancellationToken);
    }

    public async Task<SaleReturnDto> RestoreSaleReturnAsync(Guid id, RestoreSaleReturnRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync($"api/sales/returns/{id}/restore", request, JsonOptions, cancellationToken);
        return await ReadRequiredAsync<SaleReturnDto>(response, cancellationToken);
    }

    public async Task<SalesDetailReportDto> GetSalesDetailReportAsync(DateOnly from, DateOnly to, string? query, bool profit, CancellationToken cancellationToken = default)
    {
        var endpoint = profit ? "profit-detail" : "detail";
        using var response = await _httpClient.GetAsync($"api/sales/reports/{endpoint}?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}&q={Escape(query)}", cancellationToken);
        return await ReadRequiredAsync<SalesDetailReportDto>(response, cancellationToken);
    }

    public async Task<SalesHeaderReportDto> GetSalesHeaderReportAsync(DateOnly from, DateOnly to, string? query, bool profit, CancellationToken cancellationToken = default)
    {
        var endpoint = profit ? "profit-header" : "header";
        using var response = await _httpClient.GetAsync($"api/sales/reports/{endpoint}?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}&q={Escape(query)}", cancellationToken);
        return await ReadRequiredAsync<SalesHeaderReportDto>(response, cancellationToken);
    }

    public async Task<SalesReturnDetailReportDto> GetSalesReturnDetailReportAsync(DateOnly from, DateOnly to, string? query, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync($"api/sales/reports/return-detail?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}&q={Escape(query)}", cancellationToken);
        return await ReadRequiredAsync<SalesReturnDetailReportDto>(response, cancellationToken);
    }

    public async Task<SalesReturnHeaderReportDto> GetSalesReturnHeaderReportAsync(DateOnly from, DateOnly to, string? query, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync($"api/sales/reports/return-header?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}&q={Escape(query)}", cancellationToken);
        return await ReadRequiredAsync<SalesReturnHeaderReportDto>(response, cancellationToken);
    }

    public Task<IReadOnlyList<PurchaseSupplierDto>> SearchSuppliersAsync(string? query, CancellationToken cancellationToken = default) =>
        GetListAsync<PurchaseSupplierDto>($"api/purchases/suppliers?q={Escape(query)}&limit=100", cancellationToken);

    public Task<IReadOnlyList<PurchaseItemDto>> SearchPurchaseItemsAsync(string? query, string warehouseCode, CancellationToken cancellationToken = default, string itemType = "GOODS") =>
        GetListAsync<PurchaseItemDto>($"api/purchases/items?q={Escape(query)}&warehouse={Escape(warehouseCode)}&kind={Escape(itemType)}&limit=200", cancellationToken);

    public Task<IReadOnlyList<PurchaseDto>> SearchPurchasesAsync(string? query, CancellationToken cancellationToken = default, string documentKind = "GOODS") =>
        GetListAsync<PurchaseDto>($"api/purchases?q={Escape(query)}&kind={Escape(documentKind)}&limit=100", cancellationToken);

    public async Task<int> CountPurchasesAsync(string? query, CancellationToken cancellationToken = default, string documentKind = "GOODS")
    {
        using var response = await _httpClient.GetAsync($"api/purchases/count?q={Escape(query)}&kind={Escape(documentKind)}", cancellationToken);
        return await ReadRequiredAsync<int>(response, cancellationToken);
    }

    public Task<IReadOnlyList<SupplierPayableDto>> SearchSupplierPayablesAsync(string? supplierCode, string? query, CancellationToken cancellationToken = default) =>
        GetListAsync<SupplierPayableDto>($"api/purchases/payables?supplier={Escape(supplierCode)}&q={Escape(query)}&limit=200", cancellationToken);

    public Task<IReadOnlyList<SupplierPayableDto>> GetSupplierOutstandingReportAsync(string? query, CancellationToken cancellationToken = default) =>
        GetListAsync<SupplierPayableDto>($"api/suppliers/balances/outstanding?q={Escape(query)}&limit=500", cancellationToken);

    public Task<IReadOnlyList<SupplierPayableDto>> SearchSupplierBalanceSourcesAsync(string? supplierCode, CancellationToken cancellationToken = default) =>
        GetListAsync<SupplierPayableDto>($"api/suppliers/balances/outstanding?supplier={Escape(supplierCode)}&limit=200", cancellationToken);

    public Task<IReadOnlyList<SupplierPaymentDto>> SearchSupplierPaymentsAsync(string? query, CancellationToken cancellationToken = default) =>
        GetListAsync<SupplierPaymentDto>($"api/purchases/payments?q={Escape(query)}&limit=500", cancellationToken);

    public async Task<string> GetNextSupplierPaymentNumberAsync(DateOnly date, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync($"api/purchases/payments/next-number?date={date:yyyy-MM-dd}", cancellationToken);
        return await ReadRequiredAsync<string>(response, cancellationToken);
    }

    public async Task<SupplierPaymentDto> CreateSupplierPaymentAsync(CreateSupplierPaymentRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync("api/purchases/payments", request, JsonOptions, cancellationToken);
        return await ReadRequiredAsync<SupplierPaymentDto>(response, cancellationToken);
    }

    public async Task<SupplierPaymentDto> CancelSupplierPaymentAsync(Guid id, CancelSupplierPaymentRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync($"api/purchases/payments/{id}/cancel", request, JsonOptions, cancellationToken);
        return await ReadRequiredAsync<SupplierPaymentDto>(response, cancellationToken);
    }

    public async Task<SupplierPaymentDto> RestoreSupplierPaymentAsync(Guid id, RestoreSupplierPaymentRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync($"api/purchases/payments/{id}/restore", request, JsonOptions, cancellationToken);
        return await ReadRequiredAsync<SupplierPaymentDto>(response, cancellationToken);
    }

    public Task<IReadOnlyList<PartnerBalanceDocumentDto>> SearchPartnerBalancesAsync(string partnerType, string kind, string? query, bool includeCancelled = true, CancellationToken cancellationToken = default)
    {
        var root = partnerType.Equals("SUPPLIER", StringComparison.OrdinalIgnoreCase) ? "suppliers" : "customers";
        return GetListAsync<PartnerBalanceDocumentDto>($"api/{root}/balances?kind={Escape(kind)}&q={Escape(query)}&includeCancelled={includeCancelled.ToString().ToLowerInvariant()}&limit=500", cancellationToken);
    }

    public async Task<string> GetNextPartnerBalanceNumberAsync(string partnerType, string kind, DateOnly date, CancellationToken cancellationToken = default)
    {
        var root = partnerType.Equals("SUPPLIER", StringComparison.OrdinalIgnoreCase) ? "suppliers" : "customers";
        using var response = await _httpClient.GetAsync($"api/{root}/balances/next-number?kind={Escape(kind)}&date={date:yyyy-MM-dd}", cancellationToken);
        return await ReadRequiredAsync<string>(response, cancellationToken);
    }

    public async Task<PartnerBalanceDocumentDto> SavePartnerBalanceAsync(SavePartnerBalanceDocumentRequest request, CancellationToken cancellationToken = default)
    {
        var root = request.PartnerType.Equals("SUPPLIER", StringComparison.OrdinalIgnoreCase) ? "suppliers" : "customers";
        using var response = await _httpClient.PostAsJsonAsync($"api/{root}/balances", request, JsonOptions, cancellationToken);
        return await ReadRequiredAsync<PartnerBalanceDocumentDto>(response, cancellationToken);
    }

    public async Task<PartnerBalanceDocumentDto> CancelPartnerBalanceAsync(string partnerType, Guid id, CancelPartnerBalanceDocumentRequest request, CancellationToken cancellationToken = default)
    {
        var root = partnerType.Equals("SUPPLIER", StringComparison.OrdinalIgnoreCase) ? "suppliers" : "customers";
        using var response = await _httpClient.PostAsJsonAsync($"api/{root}/balances/{id}/cancel", request, JsonOptions, cancellationToken);
        return await ReadRequiredAsync<PartnerBalanceDocumentDto>(response, cancellationToken);
    }

    public async Task<PartnerBalanceDocumentDto> RestorePartnerBalanceAsync(string partnerType, Guid id, RestorePartnerBalanceDocumentRequest request, CancellationToken cancellationToken = default)
    {
        var root = partnerType.Equals("SUPPLIER", StringComparison.OrdinalIgnoreCase) ? "suppliers" : "customers";
        using var response = await _httpClient.PostAsJsonAsync($"api/{root}/balances/{id}/restore", request, JsonOptions, cancellationToken);
        return await ReadRequiredAsync<PartnerBalanceDocumentDto>(response, cancellationToken);
    }

    public Task<IReadOnlyList<CustomerReceivableDto>> SearchCustomerReceivablesAsync(string? customerCode, string? query, CancellationToken cancellationToken = default) =>
        GetListAsync<CustomerReceivableDto>($"api/sales/receivables?customer={Escape(customerCode)}&q={Escape(query)}&limit=200", cancellationToken);

    public Task<IReadOnlyList<CustomerReceivableDto>> GetCustomerOutstandingReportAsync(string? query, CancellationToken cancellationToken = default) =>
        GetListAsync<CustomerReceivableDto>($"api/customers/balances/outstanding?q={Escape(query)}&limit=500", cancellationToken);

    public Task<IReadOnlyList<CustomerReceivableDto>> SearchCustomerBalanceSourcesAsync(string? customerCode, CancellationToken cancellationToken = default) =>
        GetListAsync<CustomerReceivableDto>($"api/customers/balances/outstanding?customer={Escape(customerCode)}&limit=200", cancellationToken);

    public Task<IReadOnlyList<CustomerReceiptDto>> SearchCustomerReceiptsAsync(string? query, CancellationToken cancellationToken = default) =>
        GetListAsync<CustomerReceiptDto>($"api/sales/receipts?q={Escape(query)}&limit=500", cancellationToken);

    public async Task<string> GetNextCustomerReceiptNumberAsync(DateOnly date, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync($"api/sales/receipts/next-number?date={date:yyyy-MM-dd}", cancellationToken);
        return await ReadRequiredAsync<string>(response, cancellationToken);
    }

    public async Task<string> GetNextCustomerCheckGiroNumberAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync("api/sales/check-giros/next-number", cancellationToken);
        return await ReadRequiredAsync<string>(response, cancellationToken);
    }

    public Task<IReadOnlyList<CustomerCheckGiroDto>> SearchCustomerCheckGirosAsync(string? customerCode, string? query, bool openOnly = true, CancellationToken cancellationToken = default) =>
        GetListAsync<CustomerCheckGiroDto>($"api/sales/check-giros?customer={Escape(customerCode)}&q={Escape(query)}&limit=500&openOnly={openOnly.ToString().ToLowerInvariant()}", cancellationToken);

    public async Task<CustomerCheckGiroDto> CreateCustomerCheckGiroAsync(CreateCustomerCheckGiroRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync("api/sales/check-giros", request, JsonOptions, cancellationToken);
        return await ReadRequiredAsync<CustomerCheckGiroDto>(response, cancellationToken);
    }

    public async Task<CustomerCheckGiroDto> CancelCustomerCheckGiroAsync(Guid id, CancelCustomerCheckGiroRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync($"api/sales/check-giros/{id}/cancel", request, JsonOptions, cancellationToken);
        return await ReadRequiredAsync<CustomerCheckGiroDto>(response, cancellationToken);
    }

    public async Task<CustomerCheckGiroDto> RestoreCustomerCheckGiroAsync(Guid id, RestoreCustomerCheckGiroRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync($"api/sales/check-giros/{id}/restore", request, JsonOptions, cancellationToken);
        return await ReadRequiredAsync<CustomerCheckGiroDto>(response, cancellationToken);
    }

    public async Task<CustomerReceiptDto> CreateCustomerReceiptAsync(CreateCustomerReceiptRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync("api/sales/receipts", request, JsonOptions, cancellationToken);
        return await ReadRequiredAsync<CustomerReceiptDto>(response, cancellationToken);
    }

    public async Task<CustomerReceiptDto> CancelCustomerReceiptAsync(Guid id, CancelCustomerReceiptRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync($"api/sales/receipts/{id}/cancel", request, JsonOptions, cancellationToken);
        return await ReadRequiredAsync<CustomerReceiptDto>(response, cancellationToken);
    }

    public async Task<CustomerReceiptDto> RestoreCustomerReceiptAsync(Guid id, RestoreCustomerReceiptRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync($"api/sales/receipts/{id}/restore", request, JsonOptions, cancellationToken);
        return await ReadRequiredAsync<CustomerReceiptDto>(response, cancellationToken);
    }

    public async Task<string> GetNextPurchaseNumberAsync(DateOnly date, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync($"api/purchases/next-number?date={date:yyyy-MM-dd}", cancellationToken);
        return await ReadRequiredAsync<string>(response, cancellationToken);
    }

    public async Task<PurchaseDto> CreatePurchaseAsync(CreatePurchaseRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync("api/purchases", request, JsonOptions, cancellationToken);
        return await ReadRequiredAsync<PurchaseDto>(response, cancellationToken);
    }

    public async Task<PurchaseDto> CancelPurchaseAsync(Guid id, CancelPurchaseRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync($"api/purchases/{id}/cancel", request, JsonOptions, cancellationToken);
        return await ReadRequiredAsync<PurchaseDto>(response, cancellationToken);
    }

    public async Task<PurchaseDto> RestorePurchaseAsync(Guid id, RestorePurchaseRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync($"api/purchases/{id}/restore", request, JsonOptions, cancellationToken);
        return await ReadRequiredAsync<PurchaseDto>(response, cancellationToken);
    }

    public async Task<PurchaseReturnSourceDto> GetPurchaseReturnSourceAsync(Guid purchaseId, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync($"api/purchases/{purchaseId}/return-source", cancellationToken);
        return await ReadRequiredAsync<PurchaseReturnSourceDto>(response, cancellationToken);
    }

    public Task<IReadOnlyList<PurchaseReturnDto>> SearchPurchaseReturnsAsync(string? query, CancellationToken cancellationToken = default) =>
        GetListAsync<PurchaseReturnDto>($"api/purchases/returns?q={Escape(query)}&limit=100", cancellationToken);

    public async Task<string> GetNextPurchaseReturnNumberAsync(DateOnly date, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync($"api/purchases/returns/next-number?date={date:yyyy-MM-dd}", cancellationToken);
        return await ReadRequiredAsync<string>(response, cancellationToken);
    }

    public async Task<PurchaseReturnDto> CreatePurchaseReturnAsync(CreatePurchaseReturnRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync("api/purchases/returns", request, JsonOptions, cancellationToken);
        return await ReadRequiredAsync<PurchaseReturnDto>(response, cancellationToken);
    }

    public async Task<PurchaseReturnDto> CancelPurchaseReturnAsync(Guid id, CancelPurchaseReturnRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync($"api/purchases/returns/{id}/cancel", request, JsonOptions, cancellationToken);
        return await ReadRequiredAsync<PurchaseReturnDto>(response, cancellationToken);
    }

    public async Task<PurchaseReturnDto> RestorePurchaseReturnAsync(Guid id, RestorePurchaseReturnRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync($"api/purchases/returns/{id}/restore", request, JsonOptions, cancellationToken);
        return await ReadRequiredAsync<PurchaseReturnDto>(response, cancellationToken);
    }

    public async Task<PurchaseDetailReportDto> GetPurchaseDetailReportAsync(DateOnly from, DateOnly to, string? query, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync($"api/purchases/reports/detail?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}&q={Escape(query)}", cancellationToken);
        return await ReadRequiredAsync<PurchaseDetailReportDto>(response, cancellationToken);
    }

    public async Task<PurchaseHeaderReportDto> GetPurchaseHeaderReportAsync(DateOnly from, DateOnly to, string? query, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync($"api/purchases/reports/header?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}&q={Escape(query)}", cancellationToken);
        return await ReadRequiredAsync<PurchaseHeaderReportDto>(response, cancellationToken);
    }

    public async Task<PurchaseReturnDetailReportDto> GetPurchaseReturnDetailReportAsync(DateOnly from, DateOnly to, string? query, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync($"api/purchases/reports/return-detail?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}&q={Escape(query)}", cancellationToken);
        return await ReadRequiredAsync<PurchaseReturnDetailReportDto>(response, cancellationToken);
    }

    public async Task<PurchaseReturnHeaderReportDto> GetPurchaseReturnHeaderReportAsync(DateOnly from, DateOnly to, string? query, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync($"api/purchases/reports/return-header?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}&q={Escape(query)}", cancellationToken);
        return await ReadRequiredAsync<PurchaseReturnHeaderReportDto>(response, cancellationToken);
    }

    public Task<IReadOnlyList<InventoryWarehouseDto>> GetInventoryWarehousesAsync(bool includeInactive = true, CancellationToken cancellationToken = default) =>
        GetListAsync<InventoryWarehouseDto>($"api/inventory/warehouses?includeInactive={includeInactive}", cancellationToken);

    public async Task<InventoryWarehouseDto> SaveInventoryWarehouseAsync(SaveInventoryWarehouseRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync("api/inventory/warehouses", request, JsonOptions, cancellationToken);
        return await ReadRequiredAsync<InventoryWarehouseDto>(response, cancellationToken);
    }

    public Task<IReadOnlyList<InventoryAdjustmentTypeDto>> GetInventoryAdjustmentTypesAsync(bool includeInactive = true, CancellationToken cancellationToken = default) =>
        GetListAsync<InventoryAdjustmentTypeDto>($"api/inventory/adjustment-types?includeInactive={includeInactive}", cancellationToken);

    public async Task<InventoryAdjustmentTypeDto> SaveInventoryAdjustmentTypeAsync(SaveInventoryAdjustmentTypeRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync("api/inventory/adjustment-types", request, JsonOptions, cancellationToken);
        return await ReadRequiredAsync<InventoryAdjustmentTypeDto>(response, cancellationToken);
    }

    public Task<IReadOnlyList<InventoryUnitDto>> GetInventoryUnitsAsync(CancellationToken cancellationToken = default) =>
        GetListAsync<InventoryUnitDto>("api/inventory/units", cancellationToken);

    public async Task<InventoryUnitDto> SaveInventoryUnitAsync(SaveInventoryUnitRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync("api/inventory/units", request, JsonOptions, cancellationToken);
        return await ReadRequiredAsync<InventoryUnitDto>(response, cancellationToken);
    }

    public Task<IReadOnlyList<InventoryCategoryDto>> GetInventoryCategoriesAsync(bool includeInactive = true, string itemType = "GOODS", CancellationToken cancellationToken = default) =>
        GetListAsync<InventoryCategoryDto>($"api/inventory/categories?includeInactive={includeInactive}&itemType={Escape(itemType)}", cancellationToken);

    public async Task<InventoryCategoryDto> SaveInventoryCategoryAsync(SaveInventoryCategoryRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync("api/inventory/categories", request, JsonOptions, cancellationToken);
        return await ReadRequiredAsync<InventoryCategoryDto>(response, cancellationToken);
    }

    public Task<IReadOnlyList<InventoryItemSummaryDto>> SearchInventoryItemsAsync(string? query, string? categoryCode = null, bool includeInactive = true, CancellationToken cancellationToken = default, string itemType = "GOODS") =>
        GetListAsync<InventoryItemSummaryDto>($"api/inventory/items?q={Escape(query)}&category={Escape(categoryCode)}&itemType={Escape(itemType)}&includeInactive={includeInactive}&limit=1000", cancellationToken);

    public async Task<InventoryItemDto> GetInventoryItemAsync(string code, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync($"api/inventory/items/{Escape(code)}", cancellationToken);
        return await ReadRequiredAsync<InventoryItemDto>(response, cancellationToken);
    }

    public async Task<InventoryItemDto> SaveInventoryItemAsync(SaveInventoryItemRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsJsonAsync("api/inventory/items", request, JsonOptions, cancellationToken);
        return await ReadRequiredAsync<InventoryItemDto>(response, cancellationToken);
    }

    public Task<IReadOnlyList<InventoryBusinessUnitDto>> GetInventoryBusinessUnitsAsync(CancellationToken cancellationToken = default) =>
        GetListAsync<InventoryBusinessUnitDto>("api/inventory/business-units", cancellationToken);

    public Task<IReadOnlyList<InventoryDocumentSummaryDto>> SearchInventoryDocumentsAsync(string? documentType, string? query, CancellationToken cancellationToken = default) =>
        GetListAsync<InventoryDocumentSummaryDto>($"api/inventory/documents?type={Escape(documentType)}&q={Escape(query)}&limit=250", cancellationToken);

    public async Task<InventoryTransferDto> CreateInventoryTransferAsync(CreateInventoryTransferRequest request, CancellationToken cancellationToken = default) =>
        await PostInventoryAsync<CreateInventoryTransferRequest, InventoryTransferDto>("api/inventory/transfers", request, cancellationToken);
    public async Task<InventoryTransferDto> GetInventoryTransferAsync(Guid id, CancellationToken cancellationToken = default) =>
        await GetInventoryAsync<InventoryTransferDto>($"api/inventory/transfers/{id}", cancellationToken);
    public async Task<InventoryTransferDto> CancelInventoryTransferAsync(Guid id, CancelInventoryDocumentRequest request, CancellationToken cancellationToken = default) =>
        await PostInventoryAsync<CancelInventoryDocumentRequest, InventoryTransferDto>($"api/inventory/transfers/{id}/cancel", request, cancellationToken);

    public async Task<InventoryAdjustmentDto> CreateInventoryAdjustmentAsync(CreateInventoryAdjustmentRequest request, CancellationToken cancellationToken = default) =>
        await PostInventoryAsync<CreateInventoryAdjustmentRequest, InventoryAdjustmentDto>("api/inventory/adjustments", request, cancellationToken);
    public async Task<InventoryAdjustmentDto> GetInventoryAdjustmentAsync(Guid id, CancellationToken cancellationToken = default) =>
        await GetInventoryAsync<InventoryAdjustmentDto>($"api/inventory/adjustments/{id}", cancellationToken);
    public async Task<InventoryAdjustmentDto> CancelInventoryAdjustmentAsync(Guid id, CancelInventoryDocumentRequest request, CancellationToken cancellationToken = default) =>
        await PostInventoryAsync<CancelInventoryDocumentRequest, InventoryAdjustmentDto>($"api/inventory/adjustments/{id}/cancel", request, cancellationToken);

    public async Task<InventoryStockCountDto> CreateInventoryStockCountAsync(CreateInventoryStockCountRequest request, CancellationToken cancellationToken = default) =>
        await PostInventoryAsync<CreateInventoryStockCountRequest, InventoryStockCountDto>("api/inventory/stock-counts", request, cancellationToken);
    public async Task<InventoryStockCountDto> GetInventoryStockCountAsync(Guid id, CancellationToken cancellationToken = default) =>
        await GetInventoryAsync<InventoryStockCountDto>($"api/inventory/stock-counts/{id}", cancellationToken);
    public async Task<InventoryStockCountDto> CancelInventoryStockCountAsync(Guid id, CancelInventoryDocumentRequest request, CancellationToken cancellationToken = default) =>
        await PostInventoryAsync<CancelInventoryDocumentRequest, InventoryStockCountDto>($"api/inventory/stock-counts/{id}/cancel", request, cancellationToken);

    public async Task<InventoryMbapSendDto> CreateInventoryMbapSendAsync(CreateInventoryMbapSendRequest request, CancellationToken cancellationToken = default) =>
        await PostInventoryAsync<CreateInventoryMbapSendRequest, InventoryMbapSendDto>("api/inventory/mbap/send", request, cancellationToken);
    public async Task<InventoryMbapSendDto> GetInventoryMbapSendAsync(Guid id, CancellationToken cancellationToken = default) =>
        await GetInventoryAsync<InventoryMbapSendDto>($"api/inventory/mbap/send/{id}", cancellationToken);
    public async Task<InventoryMbapReceiveSourceDto> GetInventoryMbapReceiveSourceAsync(Guid id, CancellationToken cancellationToken = default) =>
        await GetInventoryAsync<InventoryMbapReceiveSourceDto>($"api/inventory/mbap/send/{id}/receive-source", cancellationToken);
    public async Task<InventoryMbapSendDto> CancelInventoryMbapSendAsync(Guid id, CancelInventoryDocumentRequest request, CancellationToken cancellationToken = default) =>
        await PostInventoryAsync<CancelInventoryDocumentRequest, InventoryMbapSendDto>($"api/inventory/mbap/send/{id}/cancel", request, cancellationToken);

    public async Task<InventoryMbapReceiveDto> CreateInventoryMbapReceiveAsync(CreateInventoryMbapReceiveRequest request, CancellationToken cancellationToken = default) =>
        await PostInventoryAsync<CreateInventoryMbapReceiveRequest, InventoryMbapReceiveDto>("api/inventory/mbap/receive", request, cancellationToken);
    public async Task<InventoryMbapReceiveDto> GetInventoryMbapReceiveAsync(Guid id, CancellationToken cancellationToken = default) =>
        await GetInventoryAsync<InventoryMbapReceiveDto>($"api/inventory/mbap/receive/{id}", cancellationToken);
    public async Task<InventoryMbapReceiveDto> CancelInventoryMbapReceiveAsync(Guid id, CancelInventoryDocumentRequest request, CancellationToken cancellationToken = default) =>
        await PostInventoryAsync<CancelInventoryDocumentRequest, InventoryMbapReceiveDto>($"api/inventory/mbap/receive/{id}/cancel", request, cancellationToken);

    public async Task<InventoryReportDto<InventoryPriceReportRowDto>> GetInventoryPriceReportAsync(string? query, CancellationToken cancellationToken = default) =>
        await GetInventoryAsync<InventoryReportDto<InventoryPriceReportRowDto>>($"api/inventory/reports/prices?q={Escape(query)}", cancellationToken);
    public async Task<InventoryReportDto<InventoryPriceHistoryRowDto>> GetInventoryPriceHistoryAsync(string type, DateOnly from, DateOnly to, string? query, CancellationToken cancellationToken = default) =>
        await GetInventoryAsync<InventoryReportDto<InventoryPriceHistoryRowDto>>($"api/inventory/reports/price-history?type={Escape(type)}&from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}&q={Escape(query)}", cancellationToken);
    public async Task<InventoryReportDto<InventoryStockCardRowDto>> GetInventoryStockCardAsync(DateOnly from, DateOnly to, string? query, string? warehouse, bool withValue, CancellationToken cancellationToken = default) =>
        await GetInventoryAsync<InventoryReportDto<InventoryStockCardRowDto>>($"api/inventory/reports/stock-card?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}&q={Escape(query)}&warehouse={Escape(warehouse)}&withValue={withValue}", cancellationToken);
    public async Task<InventoryReportDto<InventoryValuationRowDto>> GetInventoryValuationAsync(string? query, string? warehouse, string mode, CancellationToken cancellationToken = default) =>
        await GetInventoryAsync<InventoryReportDto<InventoryValuationRowDto>>($"api/inventory/reports/valuation?q={Escape(query)}&warehouse={Escape(warehouse)}&mode={Escape(mode)}", cancellationToken);
    public async Task<InventoryReportDto<InventoryDailyActivityRowDto>> GetInventoryDailyActivityAsync(DateOnly from, DateOnly to, string? query, string? warehouse, CancellationToken cancellationToken = default) =>
        await GetInventoryAsync<InventoryReportDto<InventoryDailyActivityRowDto>>($"api/inventory/reports/daily?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}&q={Escape(query)}&warehouse={Escape(warehouse)}", cancellationToken);
    public async Task<InventoryReportDto<InventoryTurnoverRowDto>> GetInventoryTurnoverAsync(DateOnly from, DateOnly to, string? query, string? warehouse, CancellationToken cancellationToken = default) =>
        await GetInventoryAsync<InventoryReportDto<InventoryTurnoverRowDto>>($"api/inventory/reports/turnover?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}&q={Escape(query)}&warehouse={Escape(warehouse)}", cancellationToken);
    public async Task<InventoryReportDto<InventoryTransactionReportRowDto>> GetInventoryTransactionReportAsync(DateOnly from, DateOnly to, string? documentType, string? query, CancellationToken cancellationToken = default) =>
        await GetInventoryAsync<InventoryReportDto<InventoryTransactionReportRowDto>>($"api/inventory/reports/transactions?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}&type={Escape(documentType)}&q={Escape(query)}", cancellationToken);
    public async Task<PurchasePriceCheckReportDto> GetPurchasePriceCheckAsync(string itemCode, bool useSalesPermission = false, CancellationToken cancellationToken = default)
    {
        try
        {
            return await GetInventoryAsync<PurchasePriceCheckReportDto>($"api/{(useSalesPermission ? "sales" : "inventory")}/reports/purchase-price-check?itemCode={Escape(itemCode)}", cancellationToken);
        }
        catch (AksaraApiException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return await BuildPurchasePriceCompatibilityReportAsync(itemCode, cancellationToken);
        }
    }

    public async Task<IReadOnlyList<InventoryItemSummaryDto>> SearchPurchasePriceCheckItemsAsync(string? query, bool useSalesPermission, CancellationToken cancellationToken = default)
    {
        try
        {
            return await SearchInventoryItemsAsync(query, includeInactive: true, cancellationToken: cancellationToken);
        }
        catch (AksaraApiException exception) when (useSalesPermission && exception.StatusCode == HttpStatusCode.Forbidden)
        {
            var rows = await SearchItemsAsync(query, "UTAMA", cancellationToken);
            return rows.Select(x => new InventoryItemSummaryDto(
                x.Id, x.Code, x.Name, x.Unit, null, null, null, x.AvailableStock, 0, x.RetailPrice, 0,
                0, 0, false, true, 0)).ToArray();
        }
    }

    private async Task<PurchasePriceCheckReportDto> BuildPurchasePriceCompatibilityReportAsync(string itemCode, CancellationToken cancellationToken)
    {
        var from = new DateOnly(1900, 1, 1);
        var to = new DateOnly(2100, 12, 31);
        var purchaseTask = GetInventoryPriceHistoryAsync("PURCHASE", from, to, itemCode, cancellationToken);
        var purchaseReturnTask = GetInventoryPriceHistoryAsync("PURCHASE_RETURN", from, to, itemCode, cancellationToken);
        var saleTask = GetInventoryPriceHistoryAsync("SALE", from, to, itemCode, cancellationToken);
        var saleReturnTask = GetInventoryPriceHistoryAsync("SALE_RETURN", from, to, itemCode, cancellationToken);
        var itemTask = SearchInventoryItemsAsync(itemCode, includeInactive: true, cancellationToken: cancellationToken);
        await Task.WhenAll(purchaseTask, purchaseReturnTask, saleTask, saleReturnTask, itemTask);

        static bool IsItem(InventoryPriceHistoryRowDto row, string code) =>
            string.Equals(row.ItemCode, code, StringComparison.OrdinalIgnoreCase);
        var purchaseRows = purchaseTask.Result.Rows.Where(x => IsItem(x, itemCode)).OrderBy(x => x.DocumentDate).ThenBy(x => x.DocumentNumber).ToArray();
        var returnRows = purchaseReturnTask.Result.Rows.Where(x => IsItem(x, itemCode)).OrderBy(x => x.DocumentDate).ThenBy(x => x.DocumentNumber).ToArray();
        var salesQuantity = saleTask.Result.Rows.Where(x => IsItem(x, itemCode)).Sum(x => x.Quantity);
        var salesReturnQuantity = saleReturnTask.Result.Rows.Where(x => IsItem(x, itemCode)).Sum(x => x.Quantity);
        var netSoldQuantity = Math.Max(0, salesQuantity - salesReturnQuantity);
        var lots = purchaseRows.Select((x, index) => new CompatibilityPurchaseLot(index, x)).ToArray();

        foreach (var returnRow in returnRows)
        {
            var remainingReturn = returnRow.Quantity;
            foreach (var lot in lots.Where(x => x.DocumentDate <= returnRow.DocumentDate && x.AvailableForReturn > 0)
                         .OrderByDescending(x => x.DocumentDate).ThenByDescending(x => x.Index))
            {
                var quantity = Math.Min(lot.AvailableForReturn, remainingReturn);
                lot.ReturnQuantity += quantity;
                remainingReturn -= quantity;
                if (remainingReturn <= 0) break;
            }
            foreach (var lot in lots.Where(x => x.AvailableForReturn > 0).OrderByDescending(x => x.DocumentDate).ThenByDescending(x => x.Index))
            {
                if (remainingReturn <= 0) break;
                var quantity = Math.Min(lot.AvailableForReturn, remainingReturn);
                lot.ReturnQuantity += quantity;
                remainingReturn -= quantity;
            }
        }

        var fifo = AllocateCompatibility(lots, netSoldQuantity, fifo: true);
        var lifoNextIssueUnitCost = GetNextCompatibilityIssueUnitCost(lots, fifo.ConsumedByLot, fifo: false);
        var rows = lots.Select(x => new PurchasePriceCheckRowDto(
            x.DocumentDate, x.DocumentNumber, x.SupplierCode, x.SupplierName, "SEMUA",
            x.PurchaseQuantity, x.ReturnQuantity, fifo.ConsumedByLot[x.Index],
            x.NetQuantity - fifo.ConsumedByLot[x.Index], x.UnitCost,
            (x.NetQuantity - fifo.ConsumedByLot[x.Index]) * x.UnitCost)).ToArray();

        var averageUnitCost = fifo.RemainingQuantity == 0 ? 0 : fifo.RemainingValue / fifo.RemainingQuantity;
        var item = itemTask.Result.FirstOrDefault(x => string.Equals(x.Code, itemCode, StringComparison.OrdinalIgnoreCase));
        var firstHistory = purchaseRows.FirstOrDefault();
        var summary = new PurchasePriceCheckSummaryDto(
            lots.Sum(x => x.PurchaseQuantity), lots.Sum(x => x.ReturnQuantity), netSoldQuantity,
            fifo.RemainingQuantity, fifo.UnallocatedQuantity,
            new(averageUnitCost, fifo.CostOfGoodsSold, fifo.RemainingValue),
            new(fifo.NextIssueUnitCost, fifo.CostOfGoodsSold, fifo.RemainingValue),
            new(lifoNextIssueUnitCost, fifo.CostOfGoodsSold, fifo.RemainingValue));
        return new(
            item?.Code ?? itemCode,
            item?.Name ?? firstHistory?.ItemName ?? itemCode,
            item?.Unit ?? firstHistory?.Unit ?? string.Empty,
            item?.TotalStock ?? fifo.RemainingQuantity,
            rows,
            summary,
            true);
    }

    private static CompatibilityCostFlow AllocateCompatibility(IReadOnlyList<CompatibilityPurchaseLot> lots, decimal requestedQuantity, bool fifo)
    {
        var remainingIssue = requestedQuantity;
        var consumed = lots.ToDictionary(x => x.Index, _ => 0m);
        var ordered = fifo
            ? lots.OrderBy(x => x.DocumentDate).ThenBy(x => x.Index)
            : lots.OrderByDescending(x => x.DocumentDate).ThenByDescending(x => x.Index);
        foreach (var lot in ordered)
        {
            var quantity = Math.Min(lot.NetQuantity, remainingIssue);
            consumed[lot.Index] = quantity;
            remainingIssue -= quantity;
            if (remainingIssue <= 0) break;
        }
        var allocated = consumed.Values.Sum();
        var cogs = lots.Sum(x => consumed[x.Index] * x.UnitCost);
        var remainingQuantity = lots.Sum(x => x.NetQuantity - consumed[x.Index]);
        var remainingValue = lots.Sum(x => (x.NetQuantity - consumed[x.Index]) * x.UnitCost);
        var nextIssueUnitCost = GetNextCompatibilityIssueUnitCost(lots, consumed, fifo);
        return new(consumed, allocated, cogs, remainingQuantity, remainingValue, Math.Max(0, requestedQuantity - allocated), nextIssueUnitCost);
    }

    private static decimal GetNextCompatibilityIssueUnitCost(
        IReadOnlyList<CompatibilityPurchaseLot> lots,
        IReadOnlyDictionary<int, decimal> consumedByLot,
        bool fifo)
    {
        var ordered = fifo
            ? lots.OrderBy(x => x.DocumentDate).ThenBy(x => x.Index)
            : lots.OrderByDescending(x => x.DocumentDate).ThenByDescending(x => x.Index);
        return ordered.FirstOrDefault(x => x.NetQuantity - consumedByLot[x.Index] > 0)?.UnitCost ?? 0;
    }

    private sealed class CompatibilityPurchaseLot(int index, InventoryPriceHistoryRowDto row)
    {
        public int Index { get; } = index;
        public DateOnly DocumentDate { get; } = row.DocumentDate;
        public string DocumentNumber { get; } = row.DocumentNumber;
        public string SupplierCode { get; } = row.PartyCode;
        public string SupplierName { get; } = row.PartyName;
        public decimal PurchaseQuantity { get; } = row.Quantity;
        public decimal UnitCost { get; } = row.UnitPrice * (1m - row.DiscountPercent / 100m);
        public decimal ReturnQuantity { get; set; }
        public decimal AvailableForReturn => Math.Max(0, PurchaseQuantity - ReturnQuantity);
        public decimal NetQuantity => AvailableForReturn;
    }

    private sealed record CompatibilityCostFlow(
        IReadOnlyDictionary<int, decimal> ConsumedByLot,
        decimal AllocatedQuantity,
        decimal CostOfGoodsSold,
        decimal RemainingQuantity,
        decimal RemainingValue,
        decimal UnallocatedQuantity,
        decimal NextIssueUnitCost);

    private async Task<TResponse> GetInventoryAsync<TResponse>(string path, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(path, cancellationToken);
        return await ReadRequiredAsync<TResponse>(response, cancellationToken);
    }

    private async Task<TResponse> PostInventoryAsync<TRequest, TResponse>(string path, TRequest request, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PostAsJsonAsync(path, request, JsonOptions, cancellationToken);
        return await ReadRequiredAsync<TResponse>(response, cancellationToken);
    }

    private async Task<IReadOnlyList<T>> GetListAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(path, cancellationToken);
        return await ReadRequiredAsync<List<T>>(response, cancellationToken);
    }

    private async Task<T> GetRequiredAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(path, cancellationToken);
        return await ReadRequiredAsync<T>(response, cancellationToken);
    }

    private async Task<T> ReadRequiredAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return (await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken))
                ?? throw new AksaraApiException("Server mengembalikan data kosong.", statusCode: response.StatusCode);

        ApiError? error = null;
        try { error = await response.Content.ReadFromJsonAsync<ApiError>(JsonOptions, cancellationToken); }
        catch (JsonException) { }
        if (response.StatusCode == HttpStatusCode.Unauthorized && CurrentSession is not null)
            SignalSessionExpired();
        if (error?.Code.Equals("SETUP_REQUIRED", StringComparison.OrdinalIgnoreCase) == true)
            InitialSetupRequired?.Invoke(this, EventArgs.Empty);
        throw new AksaraApiException(error?.Message ?? $"Permintaan server gagal ({(int)response.StatusCode}).", error?.Code, response.StatusCode);
    }

    private void SignalSessionExpired()
    {
        if (Interlocked.Exchange(ref _sessionExpirationSignaled, 1) != 0) return;

        Logout();
        SessionExpired?.Invoke(this, EventArgs.Empty);
    }

    private static string Escape(string? value) => Uri.EscapeDataString(value?.Trim() ?? string.Empty);

    private static Uri CreateBaseAddress(string serverAddress)
    {
        if (string.IsNullOrWhiteSpace(serverAddress))
            throw new AksaraApiException("Alamat server AKSARA wajib diisi.");

        return new Uri(serverAddress.TrimEnd('/') + "/");
    }

    private static HttpClient CreateHttpClient(Uri baseAddress) => new()
    {
        BaseAddress = baseAddress,
        Timeout = TimeSpan.FromSeconds(15)
    };

    private static string ResolveConfiguredAddress()
    {
        var configuredAddress = Environment.GetEnvironmentVariable("AKSARA_SERVER_URL");
        if (!string.IsNullOrWhiteSpace(configuredAddress))
            return configuredAddress;

        if (File.Exists(LocalConfigurationPath))
        {
            using var localDocument = JsonDocument.Parse(File.ReadAllText(LocalConfigurationPath));
            if (localDocument.RootElement.TryGetProperty("serverUrl", out var localServerUrl) && !string.IsNullOrWhiteSpace(localServerUrl.GetString()))
                return localServerUrl.GetString()!;
        }

        var configPath = Path.Combine(AppContext.BaseDirectory, "aksara-client.json");
        if (File.Exists(configPath))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(configPath));
            if (document.RootElement.TryGetProperty("serverUrl", out var serverUrl) && !string.IsNullOrWhiteSpace(serverUrl.GetString()))
                return serverUrl.GetString()!;
        }

        return "http://localhost:5188";
    }
}
