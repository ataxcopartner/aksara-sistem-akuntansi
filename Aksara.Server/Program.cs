using Aksara.Contracts;
using Aksara.Infrastructure;
using Aksara.Server;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
var cloudRunPort = Environment.GetEnvironmentVariable("PORT");
var configuredUrls = builder.Configuration["Server:Urls"];
builder.WebHost.UseUrls(!string.IsNullOrWhiteSpace(cloudRunPort)
    ? $"http://0.0.0.0:{cloudRunPort}"
    : !string.IsNullOrWhiteSpace(configuredUrls)
        ? configuredUrls
        : "http://0.0.0.0:5188");

var connectionString = ResolveConnectionString(builder.Configuration);
PostgresDatabase? database = string.IsNullOrWhiteSpace(connectionString) ? null : new PostgresDatabase(connectionString);
AuthenticationRepository? authenticationRepository = database is null ? null : new AuthenticationRepository(database);
SalesInventoryRepository? salesRepository = database is null ? null : new SalesInventoryRepository(database);
SalesReturnReportRepository? salesModuleRepository = database is null ? null : new SalesReturnReportRepository(database);
CustomerReceiptRepository? customerReceiptRepository = database is null ? null : new CustomerReceiptRepository(database);
PurchaseInventoryRepository? purchaseRepository = database is null ? null : new PurchaseInventoryRepository(database);
PurchaseReturnReportRepository? purchaseModuleRepository = database is null ? null : new PurchaseReturnReportRepository(database);
SupplierPaymentRepository? supplierPaymentRepository = database is null ? null : new SupplierPaymentRepository(database);
PartnerBalanceRepository? partnerBalanceRepository = database is null ? null : new PartnerBalanceRepository(database);
InventoryMasterRepository? inventoryMasterRepository = database is null ? null : new InventoryMasterRepository(database);
InventoryTransactionRepository? inventoryTransactionRepository = database is null ? null : new InventoryTransactionRepository(database);
InventoryReportRepository? inventoryReportRepository = database is null ? null : new InventoryReportRepository(database);
BusinessPartnerRepository? businessPartnerRepository = database is null ? null : new BusinessPartnerRepository(database);
LedgerRepository? ledgerRepository = database is null ? null : new LedgerRepository(database);
CashBankRepository? cashBankRepository = database is null ? null : new CashBankRepository(database);
ConfigurationRepository? configurationRepository = database is null ? null : new ConfigurationRepository(database);
CompanyIdentityRepository? companyIdentityRepository = database is null ? null : new CompanyIdentityRepository(database);
InitialSetupRepository? initialSetupRepository = database is null ? null : new InitialSetupRepository(database);
PeriodCloseRepository? periodCloseRepository = database is null ? null : new PeriodCloseRepository(database);
var authTokenService = new AuthTokenService(
    Environment.GetEnvironmentVariable("AUTH_TOKEN_KEY") ?? builder.Configuration["Auth:TokenKey"] ?? string.Empty,
    TimeSpan.FromHours(Math.Clamp(builder.Configuration.GetValue("Auth:TokenHours", 12), 1, 168)));
var serverApplicationName = Environment.GetEnvironmentVariable("AKSARA_SERVER_NAME") ?? "AKSARA Server";
var serverProductName = Environment.GetEnvironmentVariable("AKSARA_APP_NAME") ?? "AKSARA";
var serverDeploymentMode = Environment.GetEnvironmentVariable("AKSARA_DEPLOYMENT_MODE") ?? "Server + multi-client LAN";
var tenantLoginDirectory = new TenantLoginDirectory(
    builder.Configuration.GetSection("TenantDirectory:Entries").Get<TenantLoginDirectoryEntry[]>());

var app = builder.Build();

var initializeDatabase = builder.Configuration.GetValue("Database:Initialize", true);
if (database is not null && initializeDatabase)
    await database.InitializeAsync();
CompanyIdentityDto? companyIdentity = companyIdentityRepository is null ? null : await companyIdentityRepository.GetAsync();
if (companyIdentity is not null) authTokenService.SetCompanyIdentity(companyIdentity);

app.Use(async (context, next) =>
{
    try
    {
        await next(context);
    }
    catch (PostgresException exception) when (exception.SqlState is "PCL01" or "PCL02")
    {
        context.Response.StatusCode = StatusCodes.Status409Conflict;
        await context.Response.WriteAsJsonAsync(new ApiError(
            exception.SqlState == "PCL01" ? "PERIOD_CLOSED" : "PERIOD_CLOSE_IMMUTABLE",
            exception.MessageText));
    }
});

app.Use(async (context, next) =>
{
    if (!RequiresAksaraAuth(context.Request.Path))
    {
        await next(context);
        return;
    }

    var authorization = context.Request.Headers.Authorization.ToString();
    const string prefix = "Bearer ";
    if (!authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
        !authTokenService.TryValidate(authorization[prefix.Length..].Trim(), out var principal) ||
        principal is null)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new ApiError("UNAUTHORIZED", "Silakan login AKSARA terlebih dahulu."));
        return;
    }

    if (authenticationRepository is null || !await authenticationRepository.IsSessionActiveAsync(principal.UserName, principal.RoleCode, context.RequestAborted))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new ApiError("SESSION_INACTIVE", "Sesi pengguna sudah tidak aktif. Silakan login kembali."));
        return;
    }

    context.Items["AksaraUser"] = principal;
    if (initialSetupRepository is not null)
    {
        try
        {
            if (await initialSetupRepository.IsRequiredAsync(context.RequestAborted) &&
                !InitialSetupAccessPolicy.IsAllowed(context.Request.Path, context.Request.Method))
            {
                context.Response.StatusCode = StatusCodes.Status423Locked;
                await context.Response.WriteAsJsonAsync(new ApiError(
                    "SETUP_REQUIRED",
                    "Setup Data Awal perusahaan ini wajib diselesaikan sebelum menggunakan transaksi dan laporan."));
                return;
            }
        }
        catch (Exception) when (!context.RequestAborted.IsCancellationRequested)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsJsonAsync(new ApiError(
                "SETUP_STATUS_UNAVAILABLE",
                "Status Setup Data Awal tidak dapat diperiksa. Akses dikunci sementara demi keamanan data."));
            return;
        }
    }

    var requiredPermission = ResolveApiPermission(context.Request.Path, context.Request.Method);
    if (requiredPermission is not null &&
        (authenticationRepository is null || !await authenticationRepository.IsAllowedAsync(
            principal.RoleCode, requiredPermission.Value.Module, requiredPermission.Value.Action, context.RequestAborted)))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new ApiError("FORBIDDEN", "Pengguna ini tidak memiliki hak akses untuk tindakan tersebut."));
        return;
    }
    await next(context);
});

app.MapGet("/api/health", () => new HealthResponse(
    "healthy",
    serverApplicationName,
    typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.1.0",
    DateTimeOffset.Now));

app.MapGet("/api/system/info", () => new SystemInfoResponse(
    serverProductName,
    "Aplikasi Keuangan, Stok, Akuntansi, Rekonsiliasi, dan Administrasi",
    serverDeploymentMode,
    "PostgreSQL",
    companyIdentity));

UpdateDistribution.MapEndpoints(app);

app.MapGet("/api/database/status", async (CancellationToken cancellationToken) => new
{
    configured = database is not null,
    connected = database is not null && await database.CanConnectAsync(cancellationToken)
});

app.MapPost("/api/auth/resolve", async (HttpContext context, ResolveLoginRouteRequest request, CancellationToken cancellationToken) =>
{
    var defaultServerUrl = ResolvePublicServerUrl(context, builder.Configuration);
    if (authenticationRepository is null)
        return DatabaseUnavailable();
    var existsOnDefaultTenant = await authenticationRepository.UserNameExistsAsync(request.UserName, cancellationToken);
    var resolution = tenantLoginDirectory.Resolve(request.UserName, existsOnDefaultTenant, defaultServerUrl);
    if (resolution.IsAmbiguous)
        return Results.Json(new ApiError("TENANT_LOGIN_AMBIGUOUS", resolution.Error ?? "Username tenant ambigu."), statusCode: StatusCodes.Status409Conflict);
    if (resolution.Route is null)
        return Results.BadRequest(new ApiError("TENANT_LOGIN_INVALID", resolution.Error ?? "Username wajib diisi."));
    return Results.Ok(resolution.Route);
});

app.MapPost("/api/auth/login", async (HttpContext context, LoginRequest request, CancellationToken cancellationToken) =>
{
    if (authenticationRepository is null) return DatabaseUnavailable();
    var user = await authenticationRepository.AuthenticateAsync(request, cancellationToken);
    if (user is null)
        return Results.Json(new ApiError("LOGIN_FAILED", "Username atau password tidak sesuai."), statusCode: StatusCodes.Status401Unauthorized);
    return Results.Ok(authTokenService.CreateLoginResponse(user) with { ServerUrl = ResolvePublicServerUrl(context, builder.Configuration) });
});

app.MapGet("/api/auth/session", (HttpContext context) =>
{
    if (context.Items["AksaraUser"] is not AksaraTokenPrincipal principal)
        return Results.Json(new ApiError("UNAUTHORIZED", "Silakan login AKSARA terlebih dahulu."), statusCode: StatusCodes.Status401Unauthorized);

    return Results.Ok(new SessionStatusResponse(true, principal.UserName, principal.ExpiresAt));
});

app.MapGet("/api/configuration/program", async (CancellationToken cancellationToken) =>
    await ExecuteConfigurationAsync(configurationRepository, repository => repository.GetProgramAsync(cancellationToken)));

app.MapPost("/api/configuration/program", async (SaveProgramConfigurationRequest request, CancellationToken cancellationToken) =>
    await ExecuteConfigurationAsync(configurationRepository, repository => repository.SaveProgramAsync(request, cancellationToken)));

app.MapGet("/api/configuration/roles", async (CancellationToken cancellationToken) =>
    await ExecuteConfigurationAsync(configurationRepository, repository => repository.ListRolesAsync(cancellationToken)));

app.MapPost("/api/configuration/roles", async (HttpContext context, SaveAppRoleRequest request, CancellationToken cancellationToken) =>
    await ExecuteConfigurationAsync(configurationRepository, repository => repository.SaveRoleAsync(request, ((AksaraTokenPrincipal)context.Items["AksaraUser"]!).RoleCode, cancellationToken)));

app.MapGet("/api/configuration/users", async (CancellationToken cancellationToken) =>
    await ExecuteConfigurationAsync(configurationRepository, repository => repository.ListUsersAsync(cancellationToken)));

app.MapPost("/api/configuration/users", async (HttpContext context, SaveAppUserRequest request, CancellationToken cancellationToken) =>
    await ExecuteConfigurationAsync(configurationRepository, repository => repository.SaveUserAsync(request, ((AksaraTokenPrincipal)context.Items["AksaraUser"]!).RoleCode, cancellationToken)));

app.MapGet("/api/configuration/roles/{id:guid}/permissions", async (Guid id, CancellationToken cancellationToken) =>
    await ExecuteConfigurationAsync(configurationRepository, repository => repository.GetRolePermissionsAsync(id, cancellationToken)));

app.MapPost("/api/configuration/roles/{id:guid}/permissions", async (HttpContext context, Guid id, SaveRolePermissionsRequest request, CancellationToken cancellationToken) =>
    await ExecuteConfigurationAsync(configurationRepository, repository => repository.SaveRolePermissionsAsync(id, request, ((AksaraTokenPrincipal)context.Items["AksaraUser"]!).RoleCode, cancellationToken)));

app.MapPost("/api/configuration/change-credentials", async (HttpContext context, ChangeCredentialsRequest request, CancellationToken cancellationToken) =>
{
    if (configurationRepository is null) return DatabaseUnavailable();
    if (context.Items["AksaraUser"] is not AksaraTokenPrincipal principal)
        return Results.Json(new ApiError("UNAUTHORIZED", "Silakan login AKSARA terlebih dahulu."), statusCode: StatusCodes.Status401Unauthorized);
    try
    {
        var user = await configurationRepository.ChangeCredentialsAsync(principal.UserName, request, cancellationToken);
        return Results.Ok(authTokenService.CreateLoginResponse(user));
    }
    catch (ConfigurationValidationException exception)
    {
        return Results.BadRequest(new ApiError("CONFIGURATION_INVALID", exception.Message));
    }
    catch (ConfigurationForbiddenException exception)
    {
        return Results.Json(new ApiError("CONFIGURATION_FORBIDDEN", exception.Message), statusCode: StatusCodes.Status403Forbidden);
    }
    catch (ConfigurationConflictException exception)
    {
        return Results.Conflict(new ApiError("CONFIGURATION_CONFLICT", exception.Message));
    }
});

app.MapGet("/api/configuration/initial-setup", async (CancellationToken cancellationToken) =>
    await ExecuteInitialSetupAsync(initialSetupRepository, repository => repository.GetAsync(cancellationToken)));

app.MapPost("/api/configuration/initial-setup", async (SaveInitialSetupRequest request, CancellationToken cancellationToken) =>
    await ExecuteInitialSetupAsync(initialSetupRepository, repository => repository.SaveAsync(request, cancellationToken)));

app.MapPost("/api/configuration/initial-setup/activate", async (ActivateInitialSetupRequest request, CancellationToken cancellationToken) =>
    await ExecuteInitialSetupAsync(initialSetupRepository, repository => repository.ActivateAsync(request, cancellationToken)));

app.MapPost("/api/configuration/initial-setup/reopen", async (ReopenInitialSetupRequest request, CancellationToken cancellationToken) =>
    await ExecuteInitialSetupAsync(initialSetupRepository, repository => repository.ReopenAsync(request, cancellationToken)));

app.MapGet("/api/maintenance/period-close", async (CancellationToken cancellationToken) =>
    await ExecutePeriodCloseAsync(periodCloseRepository, repository => repository.ListAsync(cancellationToken)));

app.MapPost("/api/maintenance/period-close/preflight", async (PeriodClosePreflightRequest request, CancellationToken cancellationToken) =>
    await ExecutePeriodCloseAsync(periodCloseRepository, repository => repository.GetPreflightAsync(request, cancellationToken)));

app.MapPost("/api/maintenance/period-close", async (HttpContext context, CreatePeriodCloseRequest request, CancellationToken cancellationToken) =>
{
    if (context.Items["AksaraUser"] is not AksaraTokenPrincipal principal)
        return Results.Json(new ApiError("UNAUTHORIZED", "Silakan login AKSARA terlebih dahulu."), statusCode: StatusCodes.Status401Unauthorized);
    return await ExecutePeriodCloseAsync(
        periodCloseRepository,
        repository => repository.CloseAsync(request, principal.UserName, cancellationToken),
        created: true);
});

app.MapPost("/api/maintenance/period-close/{id:guid}/reopen", async (
    HttpContext context,
    Guid id,
    ReopenPeriodCloseRequest request,
    CancellationToken cancellationToken) =>
{
    if (context.Items["AksaraUser"] is not AksaraTokenPrincipal principal)
        return Results.Json(new ApiError("UNAUTHORIZED", "Silakan login AKSARA terlebih dahulu."), statusCode: StatusCodes.Status401Unauthorized);
    return await ExecutePeriodCloseAsync(
        periodCloseRepository,
        repository => repository.ReopenAsync(id, request, principal.UserName, cancellationToken));
});

app.MapGet("/api/maintenance/period-close/{id:guid}/export", async (Guid id, CancellationToken cancellationToken) =>
    await ExecutePeriodCloseAsync(periodCloseRepository, repository => repository.GetExportAsync(id, cancellationToken)));

app.MapGet("/api/ledger/accounts", async (string? q, bool? includeInactive, bool? onlyPostable, string? type, CancellationToken cancellationToken) =>
    await ExecuteLedgerAsync(ledgerRepository, repository => repository.ListAccountsAsync(q, includeInactive ?? true, onlyPostable ?? false, type, cancellationToken)));

app.MapPost("/api/ledger/accounts", async (SaveLedgerAccountRequest request, CancellationToken cancellationToken) =>
    await ExecuteLedgerAsync(ledgerRepository, repository => repository.SaveAccountAsync(request, cancellationToken), created: request.ExpectedVersion is null));

app.MapGet("/api/ledger/journals", async (string? q, int? limit, CancellationToken cancellationToken) =>
    await ExecuteLedgerAsync(ledgerRepository, repository => repository.ListJournalEntriesAsync(q, limit ?? 200, cancellationToken)));

app.MapGet("/api/ledger/journals/{id:guid}", async (Guid id, CancellationToken cancellationToken) =>
{
    if (ledgerRepository is null) return DatabaseUnavailable();
    var journal = await ledgerRepository.GetJournalEntryAsync(id, cancellationToken);
    return journal is null
        ? Results.NotFound(new ApiError("JOURNAL_NOT_FOUND", "Jurnal tidak ditemukan."))
        : Results.Ok(journal);
});

app.MapPost("/api/ledger/journals", async (CreateJournalEntryRequest request, CancellationToken cancellationToken) =>
    await ExecuteLedgerAsync(ledgerRepository, repository => repository.CreateJournalEntryAsync(request, cancellationToken), created: true));

app.MapGet("/api/ledger/mappings", async (CancellationToken cancellationToken) =>
    await ExecuteLedgerAsync(ledgerRepository, repository => repository.ListTransactionMappingsAsync(cancellationToken)));

app.MapPost("/api/ledger/mappings", async (SaveLedgerTransactionMappingRequest request, CancellationToken cancellationToken) =>
    await ExecuteLedgerAsync(ledgerRepository, repository => repository.SaveTransactionMappingAsync(request, cancellationToken)));

app.MapGet("/api/ledger/reports/accounts", async (string? q, CancellationToken cancellationToken) =>
    await ExecuteLedgerAsync(ledgerRepository, repository => repository.GetAccountReportAsync(q, cancellationToken)));

app.MapGet("/api/ledger/reports/journal", async (DateOnly from, DateOnly to, string? q, string? source, CancellationToken cancellationToken) =>
    await ExecuteLedgerAsync(ledgerRepository, repository => repository.GetJournalReportAsync(from, to, q, source, cancellationToken)));

app.MapGet("/api/ledger/reports/general-ledger", async (DateOnly from, DateOnly to, string? account, string? q, CancellationToken cancellationToken) =>
    await ExecuteLedgerAsync(ledgerRepository, repository => repository.GetGeneralLedgerReportAsync(from, to, account, q, cancellationToken)));

app.MapGet("/api/ledger/reports/trial-balance", async (DateOnly from, DateOnly to, string? q, bool? includeZero, CancellationToken cancellationToken) =>
    await ExecuteLedgerAsync(ledgerRepository, repository => repository.GetTrialBalanceReportAsync(from, to, q, includeZero ?? false, cancellationToken)));

app.MapGet("/api/ledger/reports/income-statement", async (DateOnly from, DateOnly to, string? q, bool? includeZero, CancellationToken cancellationToken) =>
    await ExecuteLedgerAsync(ledgerRepository, repository => repository.GetIncomeStatementReportAsync(from, to, q, includeZero ?? false, cancellationToken)));

app.MapGet("/api/ledger/reports/balance-sheet", async (DateOnly from, DateOnly to, string? q, bool? includeZero, CancellationToken cancellationToken) =>
    await ExecuteLedgerAsync(ledgerRepository, repository => repository.GetBalanceSheetReportAsync(from, to, q, includeZero ?? false, cancellationToken)));

app.MapGet("/api/cash-bank/cash-accounts", async (bool? includeInactive, CancellationToken cancellationToken) =>
    await ExecuteCashBankAsync(cashBankRepository, repository => repository.ListCashAccountsAsync(includeInactive ?? true, cancellationToken)));

app.MapPost("/api/cash-bank/cash-accounts", async (SaveCashAccountRequest request, CancellationToken cancellationToken) =>
    await ExecuteCashBankAsync(cashBankRepository, repository => repository.SaveCashAccountAsync(request, cancellationToken), created: request.ExpectedVersion is null));

app.MapGet("/api/cash-bank/bank-accounts", async (bool? includeInactive, CancellationToken cancellationToken) =>
    await ExecuteCashBankAsync(cashBankRepository, repository => repository.ListBankAccountsAsync(includeInactive ?? true, cancellationToken)));

app.MapPost("/api/cash-bank/bank-accounts", async (SaveBankAccountRequest request, CancellationToken cancellationToken) =>
    await ExecuteCashBankAsync(cashBankRepository, repository => repository.SaveBankAccountAsync(request, cancellationToken), created: request.ExpectedVersion is null));

app.MapGet("/api/cash-bank/categories", async (string? type, bool? includeDeleted, CancellationToken cancellationToken) =>
    await ExecuteCashBankAsync(cashBankRepository, repository => repository.ListCategoriesAsync(type, includeDeleted ?? true, cancellationToken)));

app.MapPost("/api/cash-bank/categories", async (SaveCashBankCategoryRequest request, CancellationToken cancellationToken) =>
    await ExecuteCashBankAsync(cashBankRepository, repository => repository.SaveCategoryAsync(request, cancellationToken), created: request.ExpectedVersion is null));

app.MapGet("/api/cash-bank/transactions", async (string? q, int? limit, CancellationToken cancellationToken) =>
    await ExecuteCashBankAsync(cashBankRepository, repository => repository.ListTransactionsAsync(q, limit ?? 200, cancellationToken)));

app.MapGet("/api/cash-bank/transactions/{id:guid}", async (Guid id, CancellationToken cancellationToken) =>
{
    if (cashBankRepository is null) return DatabaseUnavailable();
    var transaction = await cashBankRepository.ReadTransactionAsync(id, cancellationToken);
    return transaction is null
        ? Results.NotFound(new ApiError("CASH_BANK_TRANSACTION_NOT_FOUND", "Transaksi kas-bank tidak ditemukan."))
        : Results.Ok(transaction);
});

app.MapGet("/api/cash-bank/transactions/{id:guid}/lines", async (Guid id, CancellationToken cancellationToken) =>
    await ExecuteCashBankAsync(cashBankRepository, repository => repository.ListTransactionLinesAsync(id, cancellationToken)));

app.MapPost("/api/cash-bank/transactions", async (CreateCashBankTransactionRequest request, CancellationToken cancellationToken) =>
    await ExecuteCashBankAsync(cashBankRepository, repository => repository.CreateTransactionAsync(request, cancellationToken), created: true));

app.MapPost("/api/cash-bank/transactions/{id:guid}/cancel", async (Guid id, CancelCashBankTransactionRequest request, CancellationToken cancellationToken) =>
    await ExecuteCashBankAsync(cashBankRepository, repository => repository.CancelTransactionAsync(id, request, cancellationToken)));

app.MapPost("/api/cash-bank/transactions/{id:guid}/restore", async (Guid id, RestoreCashBankTransactionRequest request, CancellationToken cancellationToken) =>
    await ExecuteCashBankAsync(cashBankRepository, repository => repository.RestoreTransactionAsync(id, request, cancellationToken)));

app.MapGet("/api/master/suppliers", async (string? q, bool? includeInactive, int? limit, CancellationToken cancellationToken) =>
    await ExecutePartnerAsync(businessPartnerRepository, repository => repository.ListSuppliersAsync(q, includeInactive ?? true, limit ?? 1000, cancellationToken)));

app.MapPost("/api/master/suppliers", async (SaveSupplierMasterRequest request, CancellationToken cancellationToken) =>
    await ExecutePartnerAsync(businessPartnerRepository, repository => repository.SaveSupplierAsync(request, cancellationToken), created: request.ExpectedVersion is null));

app.MapGet("/api/master/customers", async (string? q, bool? includeInactive, int? limit, CancellationToken cancellationToken) =>
    await ExecutePartnerAsync(businessPartnerRepository, repository => repository.ListCustomersAsync(q, includeInactive ?? true, limit ?? 1000, cancellationToken)));

app.MapPost("/api/master/customers", async (SaveCustomerMasterRequest request, CancellationToken cancellationToken) =>
    await ExecutePartnerAsync(businessPartnerRepository, repository => repository.SaveCustomerAsync(request, cancellationToken), created: request.ExpectedVersion is null));

app.MapGet("/api/master/salespeople", async (string? q, bool? includeInactive, int? limit, CancellationToken cancellationToken) =>
    await ExecutePartnerAsync(businessPartnerRepository, repository => repository.ListSalespeopleAsync(q, includeInactive ?? true, limit ?? 1000, cancellationToken)));

app.MapPost("/api/master/salespeople", async (SaveSalespersonMasterRequest request, CancellationToken cancellationToken) =>
    await ExecutePartnerAsync(businessPartnerRepository, repository => repository.SaveSalespersonAsync(request, cancellationToken), created: request.ExpectedVersion is null));

app.MapGet("/api/sales/customers", async (string? q, int? limit, CancellationToken cancellationToken) =>
    await ExecuteSalesAsync(salesRepository, repository => repository.SearchCustomersAsync(q, limit ?? 50, cancellationToken)));

app.MapGet("/api/sales/salespeople", async (string? q, int? limit, CancellationToken cancellationToken) =>
    await ExecuteSalesAsync(salesRepository, repository => repository.SearchSalespeopleAsync(q, limit ?? 100, cancellationToken)));

app.MapGet("/api/sales/items", async (string? q, string? warehouse, string? kind, int? limit, CancellationToken cancellationToken) =>
    await ExecuteSalesAsync(salesRepository, repository => repository.SearchItemsAsync(q, warehouse, limit ?? 100, cancellationToken, kind ?? "GOODS")));

app.MapGet("/api/sales", async (string? q, string? kind, int? limit, CancellationToken cancellationToken) =>
    await ExecuteSalesAsync(salesRepository, repository => repository.ListSalesAsync(q, limit ?? 100, cancellationToken, kind ?? "GOODS")));

app.MapGet("/api/sales/count", async (string? q, string? kind, CancellationToken cancellationToken) =>
    await ExecuteSalesAsync(salesRepository, repository => repository.CountSalesAsync(q, cancellationToken, kind ?? "GOODS")));

app.MapGet("/api/sales/next-number", async (DateOnly date, CancellationToken cancellationToken) =>
    await ExecuteSalesAsync(salesRepository, repository => repository.PreviewNextDocumentNumberAsync(date, cancellationToken)));

app.MapGet("/api/sales/gunggungan", async (DateOnly from, DateOnly to, CancellationToken cancellationToken) =>
    await ExecuteSalesAsync(salesRepository, repository => repository.GetGunggunganReportAsync(from, to, cancellationToken)));

app.MapPost("/api/sales/gunggungan", async (CreateGunggunganSaleRequest request, CancellationToken cancellationToken) =>
    await ExecuteSalesAsync(salesRepository, repository => repository.CreateGunggunganSaleAsync(request, cancellationToken), created: true));

app.MapGet("/api/sales/gunggungan/{id:guid}", async (Guid id, CancellationToken cancellationToken) =>
{
    if (salesRepository is null) return DatabaseUnavailable();
    var sale = await salesRepository.GetGunggunganSaleAsync(id, cancellationToken);
    return sale is null
        ? Results.NotFound(new ApiError("GUNGGUNGAN_SALE_NOT_FOUND", "Penjualan Gunggungan tidak ditemukan."))
        : Results.Ok(sale);
});

app.MapGet("/api/sales/receivables", async (string? customer, string? q, int? limit, CancellationToken cancellationToken) =>
    await ExecuteCustomerReceiptAsync(customerReceiptRepository, repository => repository.ListReceivablesAsync(customer, q, limit ?? 100, cancellationToken)));

app.MapGet("/api/sales/receipts", async (string? q, int? limit, CancellationToken cancellationToken) =>
    await ExecuteCustomerReceiptAsync(customerReceiptRepository, repository => repository.ListReceiptsAsync(q, limit ?? 100, cancellationToken)));

app.MapGet("/api/sales/receipts/next-number", async (DateOnly date, CancellationToken cancellationToken) =>
    await ExecuteCustomerReceiptAsync(customerReceiptRepository, repository => repository.PreviewNextReceiptNumberAsync(date, cancellationToken)));

app.MapGet("/api/sales/check-giros/next-number", async (CancellationToken cancellationToken) =>
    await ExecuteCustomerReceiptAsync(customerReceiptRepository, repository => repository.PreviewNextCheckGiroNumberAsync(cancellationToken)));

app.MapGet("/api/sales/check-giros", async (string? customer, string? q, int? limit, bool? openOnly, CancellationToken cancellationToken) =>
    await ExecuteCustomerReceiptAsync(customerReceiptRepository, repository => repository.ListCheckGirosAsync(customer, q, limit ?? 100, openOnly ?? true, cancellationToken)));

app.MapPost("/api/sales/check-giros", async (CreateCustomerCheckGiroRequest request, CancellationToken cancellationToken) =>
    await ExecuteCustomerReceiptAsync(customerReceiptRepository, repository => repository.CreateCheckGiroAsync(request, cancellationToken), created: true));

app.MapPost("/api/sales/check-giros/{id:guid}/cancel", async (Guid id, CancelCustomerCheckGiroRequest request, CancellationToken cancellationToken) =>
    await ExecuteCustomerReceiptAsync(customerReceiptRepository, repository => repository.CancelCheckGiroAsync(id, request, cancellationToken)));

app.MapPost("/api/sales/check-giros/{id:guid}/restore", async (Guid id, RestoreCustomerCheckGiroRequest request, CancellationToken cancellationToken) =>
    await ExecuteCustomerReceiptAsync(customerReceiptRepository, repository => repository.RestoreCheckGiroAsync(id, request, cancellationToken)));

app.MapGet("/api/sales/receipts/{id:guid}", async (Guid id, CancellationToken cancellationToken) =>
{
    if (customerReceiptRepository is null) return DatabaseUnavailable();
    var receipt = await customerReceiptRepository.GetReceiptAsync(id, cancellationToken);
    return receipt is null
        ? Results.NotFound(new ApiError("CUSTOMER_RECEIPT_NOT_FOUND", "Penerimaan piutang tidak ditemukan."))
        : Results.Ok(receipt);
});

app.MapPost("/api/sales/receipts", async (CreateCustomerReceiptRequest request, CancellationToken cancellationToken) =>
    await ExecuteCustomerReceiptAsync(customerReceiptRepository, repository => repository.CreateReceiptAsync(request, cancellationToken), created: true));

app.MapPost("/api/sales/receipts/{id:guid}/cancel", async (Guid id, CancelCustomerReceiptRequest request, CancellationToken cancellationToken) =>
    await ExecuteCustomerReceiptAsync(customerReceiptRepository, repository => repository.CancelReceiptAsync(id, request, cancellationToken)));

app.MapPost("/api/sales/receipts/{id:guid}/restore", async (Guid id, RestoreCustomerReceiptRequest request, CancellationToken cancellationToken) =>
    await ExecuteCustomerReceiptAsync(customerReceiptRepository, repository => repository.RestoreReceiptAsync(id, request, cancellationToken)));

app.MapGet("/api/sales/{id:guid}", async (Guid id, CancellationToken cancellationToken) =>
{
    if (salesRepository is null) return DatabaseUnavailable();
    var sale = await salesRepository.GetSaleAsync(id, cancellationToken);
    return sale is null
        ? Results.NotFound(new ApiError("SALE_NOT_FOUND", "Nota penjualan tidak ditemukan."))
        : Results.Ok(sale);
});

app.MapPost("/api/sales", async (CreateSaleRequest request, CancellationToken cancellationToken) =>
    await ExecuteSalesAsync(salesRepository, repository => repository.CreateSaleAsync(request, cancellationToken), created: true));

app.MapPost("/api/sales/fifo-preview", async (FifoCostPreviewRequest request, CancellationToken cancellationToken) =>
    await ExecuteSalesAsync(salesRepository, repository => repository.PreviewFifoCostAsync(request, cancellationToken)));

app.MapPost("/api/sales/{id:guid}/cancel", async (Guid id, CancelSaleRequest request, CancellationToken cancellationToken) =>
    await ExecuteSalesAsync(salesRepository, repository => repository.CancelSaleAsync(id, request, cancellationToken)));

app.MapPost("/api/sales/{id:guid}/restore", async (Guid id, RestoreSaleRequest request, CancellationToken cancellationToken) =>
    await ExecuteSalesAsync(salesRepository, repository => repository.RestoreSaleAsync(id, request, cancellationToken)));

app.MapGet("/api/sales/{id:guid}/return-source", async (Guid id, CancellationToken cancellationToken) =>
{
    if (salesModuleRepository is null) return DatabaseUnavailable();
    var source = await salesModuleRepository.GetReturnSourceAsync(id, cancellationToken);
    return source is null
        ? Results.NotFound(new ApiError("RETURN_SOURCE_NOT_FOUND", "Nota tidak ditemukan, sudah dibatalkan, atau tidak dapat diretur."))
        : Results.Ok(source);
});

app.MapGet("/api/sales/returns", async (string? q, int? limit, CancellationToken cancellationToken) =>
    await ExecuteSalesModuleAsync(salesModuleRepository, repository => repository.ListReturnsAsync(q, limit ?? 100, cancellationToken)));

app.MapGet("/api/sales/returns/next-number", async (DateOnly date, CancellationToken cancellationToken) =>
    await ExecuteSalesModuleAsync(salesModuleRepository, repository => repository.PreviewNextReturnNumberAsync(date, cancellationToken)));

app.MapGet("/api/sales/returns/{id:guid}", async (Guid id, CancellationToken cancellationToken) =>
{
    if (salesModuleRepository is null) return DatabaseUnavailable();
    var item = await salesModuleRepository.GetReturnAsync(id, cancellationToken);
    return item is null
        ? Results.NotFound(new ApiError("SALE_RETURN_NOT_FOUND", "Retur penjualan tidak ditemukan."))
        : Results.Ok(item);
});

app.MapPost("/api/sales/returns", async (CreateSaleReturnRequest request, CancellationToken cancellationToken) =>
    await ExecuteSalesModuleAsync(salesModuleRepository, repository => repository.CreateReturnAsync(request, cancellationToken), created: true));

app.MapPost("/api/sales/returns/{id:guid}/cancel", async (Guid id, CancelSaleReturnRequest request, CancellationToken cancellationToken) =>
    await ExecuteSalesModuleAsync(salesModuleRepository, repository => repository.CancelReturnAsync(id, request, cancellationToken)));

app.MapPost("/api/sales/returns/{id:guid}/restore", async (Guid id, RestoreSaleReturnRequest request, CancellationToken cancellationToken) =>
    await ExecuteSalesModuleAsync(salesModuleRepository, repository => repository.RestoreReturnAsync(id, request, cancellationToken)));

app.MapGet("/api/sales/reports/detail", async (DateOnly from, DateOnly to, string? q, CancellationToken cancellationToken) =>
    await ExecuteSalesModuleAsync(salesModuleRepository, repository => repository.GetDetailReportAsync(from, to, q, cancellationToken)));

app.MapGet("/api/sales/reports/header", async (DateOnly from, DateOnly to, string? q, CancellationToken cancellationToken) =>
    await ExecuteSalesModuleAsync(salesModuleRepository, repository => repository.GetHeaderReportAsync(from, to, q, cancellationToken)));

app.MapGet("/api/sales/reports/return-detail", async (DateOnly from, DateOnly to, string? q, CancellationToken cancellationToken) =>
    await ExecuteSalesModuleAsync(salesModuleRepository, repository => repository.GetReturnDetailReportAsync(from, to, q, cancellationToken)));

app.MapGet("/api/sales/reports/return-header", async (DateOnly from, DateOnly to, string? q, CancellationToken cancellationToken) =>
    await ExecuteSalesModuleAsync(salesModuleRepository, repository => repository.GetReturnHeaderReportAsync(from, to, q, cancellationToken)));

app.MapGet("/api/sales/reports/profit-detail", async (DateOnly from, DateOnly to, string? q, CancellationToken cancellationToken) =>
    await ExecuteSalesModuleAsync(salesModuleRepository, repository => repository.GetDetailReportAsync(from, to, q, cancellationToken)));

app.MapGet("/api/sales/reports/profit-header", async (DateOnly from, DateOnly to, string? q, CancellationToken cancellationToken) =>
    await ExecuteSalesModuleAsync(salesModuleRepository, repository => repository.GetHeaderReportAsync(from, to, q, cancellationToken)));

app.MapGet("/api/purchases/suppliers", async (string? q, int? limit, CancellationToken cancellationToken) =>
    await ExecutePurchaseAsync(purchaseRepository, repository => repository.SearchSuppliersAsync(q, limit ?? 50, cancellationToken)));

app.MapGet("/api/purchases/items", async (string? q, string? warehouse, string? kind, int? limit, CancellationToken cancellationToken) =>
    await ExecutePurchaseAsync(purchaseRepository, repository => repository.SearchItemsAsync(q, warehouse, limit ?? 100, cancellationToken, kind ?? "GOODS")));

app.MapGet("/api/purchases", async (string? q, string? kind, int? limit, CancellationToken cancellationToken) =>
    await ExecutePurchaseAsync(purchaseRepository, repository => repository.ListPurchasesAsync(q, limit ?? 100, cancellationToken, kind ?? "GOODS")));

app.MapGet("/api/purchases/count", async (string? q, string? kind, CancellationToken cancellationToken) =>
    await ExecutePurchaseAsync(purchaseRepository, repository => repository.CountPurchasesAsync(q, cancellationToken, kind ?? "GOODS")));

app.MapGet("/api/purchases/payables", async (string? supplier, string? q, int? limit, CancellationToken cancellationToken) =>
    await ExecuteSupplierPaymentAsync(supplierPaymentRepository, repository => repository.ListPayablesAsync(supplier, q, limit ?? 100, cancellationToken)));

app.MapGet("/api/purchases/payments", async (string? q, int? limit, CancellationToken cancellationToken) =>
    await ExecuteSupplierPaymentAsync(supplierPaymentRepository, repository => repository.ListPaymentsAsync(q, limit ?? 100, cancellationToken)));

app.MapGet("/api/purchases/payments/next-number", async (DateOnly date, CancellationToken cancellationToken) =>
    await ExecuteSupplierPaymentAsync(supplierPaymentRepository, repository => repository.PreviewNextPaymentNumberAsync(date, cancellationToken)));

app.MapGet("/api/purchases/payments/{id:guid}", async (Guid id, CancellationToken cancellationToken) =>
{
    if (supplierPaymentRepository is null) return DatabaseUnavailable();
    var payment = await supplierPaymentRepository.GetPaymentAsync(id, cancellationToken);
    return payment is null
        ? Results.NotFound(new ApiError("SUPPLIER_PAYMENT_NOT_FOUND", "Pembayaran utang tidak ditemukan."))
        : Results.Ok(payment);
});

app.MapPost("/api/purchases/payments", async (CreateSupplierPaymentRequest request, CancellationToken cancellationToken) =>
    await ExecuteSupplierPaymentAsync(supplierPaymentRepository, repository => repository.CreatePaymentAsync(request, cancellationToken), created: true));

app.MapPost("/api/purchases/payments/{id:guid}/cancel", async (Guid id, CancelSupplierPaymentRequest request, CancellationToken cancellationToken) =>
    await ExecuteSupplierPaymentAsync(supplierPaymentRepository, repository => repository.CancelPaymentAsync(id, request, cancellationToken)));

app.MapPost("/api/purchases/payments/{id:guid}/restore", async (Guid id, RestoreSupplierPaymentRequest request, CancellationToken cancellationToken) =>
    await ExecuteSupplierPaymentAsync(supplierPaymentRepository, repository => repository.RestorePaymentAsync(id, request, cancellationToken)));

app.MapGet("/api/suppliers/balances", async (string? kind, string? q, bool? includeCancelled, int? limit, CancellationToken cancellationToken) =>
    await ExecutePartnerBalanceAsync(partnerBalanceRepository, repository => repository.ListAsync("SUPPLIER", kind, q, includeCancelled ?? true, limit ?? 500, cancellationToken)));

app.MapGet("/api/suppliers/balances/next-number", async (string kind, DateOnly date, CancellationToken cancellationToken) =>
    await ExecutePartnerBalanceAsync(partnerBalanceRepository, repository => repository.PreviewNextNumberAsync("SUPPLIER", kind, date, cancellationToken)));

app.MapGet("/api/suppliers/balances/outstanding", async (string? supplier, string? q, int? limit, CancellationToken cancellationToken) =>
    await ExecuteSupplierPaymentAsync(supplierPaymentRepository, repository => repository.ListPayablesAsync(supplier, q, limit ?? 100, cancellationToken)));

app.MapGet("/api/suppliers/balances/{id:guid}", async (Guid id, CancellationToken cancellationToken) =>
{
    if (partnerBalanceRepository is null) return DatabaseUnavailable();
    var row = await partnerBalanceRepository.GetAsync(id, cancellationToken);
    return row is null || row.PartnerType != "SUPPLIER"
        ? Results.NotFound(new ApiError("PARTNER_BALANCE_NOT_FOUND", "Dokumen saldo supplier tidak ditemukan."))
        : Results.Ok(row);
});

app.MapPost("/api/suppliers/balances", async (SavePartnerBalanceDocumentRequest request, CancellationToken cancellationToken) =>
    await ExecutePartnerBalanceAsync(partnerBalanceRepository, repository => repository.SaveAsync(request with { PartnerType = "SUPPLIER" }, cancellationToken), created: request.Id is null));

app.MapPost("/api/suppliers/balances/{id:guid}/cancel", async (Guid id, CancelPartnerBalanceDocumentRequest request, CancellationToken cancellationToken) =>
    await ExecutePartnerBalanceAsync(partnerBalanceRepository, repository => repository.CancelAsync(id, request, cancellationToken)));

app.MapPost("/api/suppliers/balances/{id:guid}/restore", async (Guid id, RestorePartnerBalanceDocumentRequest request, CancellationToken cancellationToken) =>
    await ExecutePartnerBalanceAsync(partnerBalanceRepository, repository => repository.RestoreAsync(id, request, cancellationToken)));

app.MapGet("/api/customers/balances", async (string? kind, string? q, bool? includeCancelled, int? limit, CancellationToken cancellationToken) =>
    await ExecutePartnerBalanceAsync(partnerBalanceRepository, repository => repository.ListAsync("CUSTOMER", kind, q, includeCancelled ?? true, limit ?? 500, cancellationToken)));

app.MapGet("/api/customers/balances/next-number", async (string kind, DateOnly date, CancellationToken cancellationToken) =>
    await ExecutePartnerBalanceAsync(partnerBalanceRepository, repository => repository.PreviewNextNumberAsync("CUSTOMER", kind, date, cancellationToken)));

app.MapGet("/api/customers/balances/outstanding", async (string? customer, string? q, int? limit, CancellationToken cancellationToken) =>
    await ExecuteCustomerReceiptAsync(customerReceiptRepository, repository => repository.ListReceivablesAsync(customer, q, limit ?? 100, cancellationToken)));

app.MapGet("/api/customers/balances/{id:guid}", async (Guid id, CancellationToken cancellationToken) =>
{
    if (partnerBalanceRepository is null) return DatabaseUnavailable();
    var row = await partnerBalanceRepository.GetAsync(id, cancellationToken);
    return row is null || row.PartnerType != "CUSTOMER"
        ? Results.NotFound(new ApiError("PARTNER_BALANCE_NOT_FOUND", "Dokumen saldo customer tidak ditemukan."))
        : Results.Ok(row);
});

app.MapPost("/api/customers/balances", async (SavePartnerBalanceDocumentRequest request, CancellationToken cancellationToken) =>
    await ExecutePartnerBalanceAsync(partnerBalanceRepository, repository => repository.SaveAsync(request with { PartnerType = "CUSTOMER" }, cancellationToken), created: request.Id is null));

app.MapPost("/api/customers/balances/{id:guid}/cancel", async (Guid id, CancelPartnerBalanceDocumentRequest request, CancellationToken cancellationToken) =>
    await ExecutePartnerBalanceAsync(partnerBalanceRepository, repository => repository.CancelAsync(id, request, cancellationToken)));

app.MapPost("/api/customers/balances/{id:guid}/restore", async (Guid id, RestorePartnerBalanceDocumentRequest request, CancellationToken cancellationToken) =>
    await ExecutePartnerBalanceAsync(partnerBalanceRepository, repository => repository.RestoreAsync(id, request, cancellationToken)));

app.MapGet("/api/purchases/next-number", async (DateOnly date, CancellationToken cancellationToken) =>
    await ExecutePurchaseAsync(purchaseRepository, repository => repository.PreviewNextDocumentNumberAsync(date, cancellationToken)));

app.MapGet("/api/purchases/{id:guid}", async (Guid id, CancellationToken cancellationToken) =>
{
    if (purchaseRepository is null) return DatabaseUnavailable();
    var purchase = await purchaseRepository.GetPurchaseAsync(id, cancellationToken);
    return purchase is null
        ? Results.NotFound(new ApiError("PURCHASE_NOT_FOUND", "Faktur pembelian tidak ditemukan."))
        : Results.Ok(purchase);
});

app.MapPost("/api/purchases", async (CreatePurchaseRequest request, CancellationToken cancellationToken) =>
    await ExecutePurchaseAsync(purchaseRepository, repository => repository.CreatePurchaseAsync(request, cancellationToken), created: true));

app.MapPost("/api/purchases/{id:guid}/cancel", async (Guid id, CancelPurchaseRequest request, CancellationToken cancellationToken) =>
    await ExecutePurchaseAsync(purchaseRepository, repository => repository.CancelPurchaseAsync(id, request, cancellationToken)));

app.MapPost("/api/purchases/{id:guid}/restore", async (Guid id, RestorePurchaseRequest request, CancellationToken cancellationToken) =>
    await ExecutePurchaseAsync(purchaseRepository, repository => repository.RestorePurchaseAsync(id, request, cancellationToken)));

app.MapGet("/api/purchases/{id:guid}/return-source", async (Guid id, CancellationToken cancellationToken) =>
{
    if (purchaseModuleRepository is null) return DatabaseUnavailable();
    var source = await purchaseModuleRepository.GetReturnSourceAsync(id, cancellationToken);
    return source is null
        ? Results.NotFound(new ApiError("PURCHASE_RETURN_SOURCE_NOT_FOUND", "Faktur tidak ditemukan, sudah dibatalkan, atau tidak dapat diretur."))
        : Results.Ok(source);
});

app.MapGet("/api/purchases/returns", async (string? q, int? limit, CancellationToken cancellationToken) =>
    await ExecutePurchaseModuleAsync(purchaseModuleRepository, repository => repository.ListReturnsAsync(q, limit ?? 100, cancellationToken)));

app.MapGet("/api/purchases/returns/next-number", async (DateOnly date, CancellationToken cancellationToken) =>
    await ExecutePurchaseModuleAsync(purchaseModuleRepository, repository => repository.PreviewNextReturnNumberAsync(date, cancellationToken)));

app.MapGet("/api/purchases/returns/{id:guid}", async (Guid id, CancellationToken cancellationToken) =>
{
    if (purchaseModuleRepository is null) return DatabaseUnavailable();
    var item = await purchaseModuleRepository.GetReturnAsync(id, cancellationToken);
    return item is null
        ? Results.NotFound(new ApiError("PURCHASE_RETURN_NOT_FOUND", "Retur pembelian tidak ditemukan."))
        : Results.Ok(item);
});

app.MapPost("/api/purchases/returns", async (CreatePurchaseReturnRequest request, CancellationToken cancellationToken) =>
    await ExecutePurchaseModuleAsync(purchaseModuleRepository, repository => repository.CreateReturnAsync(request, cancellationToken), created: true));

app.MapPost("/api/purchases/returns/{id:guid}/cancel", async (Guid id, CancelPurchaseReturnRequest request, CancellationToken cancellationToken) =>
    await ExecutePurchaseModuleAsync(purchaseModuleRepository, repository => repository.CancelReturnAsync(id, request, cancellationToken)));

app.MapPost("/api/purchases/returns/{id:guid}/restore", async (Guid id, RestorePurchaseReturnRequest request, CancellationToken cancellationToken) =>
    await ExecutePurchaseModuleAsync(purchaseModuleRepository, repository => repository.RestoreReturnAsync(id, request, cancellationToken)));

app.MapGet("/api/purchases/reports/detail", async (DateOnly from, DateOnly to, string? q, CancellationToken cancellationToken) =>
    await ExecutePurchaseModuleAsync(purchaseModuleRepository, repository => repository.GetDetailReportAsync(from, to, q, cancellationToken)));

app.MapGet("/api/purchases/reports/header", async (DateOnly from, DateOnly to, string? q, CancellationToken cancellationToken) =>
    await ExecutePurchaseModuleAsync(purchaseModuleRepository, repository => repository.GetHeaderReportAsync(from, to, q, cancellationToken)));

app.MapGet("/api/purchases/reports/return-detail", async (DateOnly from, DateOnly to, string? q, CancellationToken cancellationToken) =>
    await ExecutePurchaseModuleAsync(purchaseModuleRepository, repository => repository.GetReturnDetailReportAsync(from, to, q, cancellationToken)));

app.MapGet("/api/purchases/reports/return-header", async (DateOnly from, DateOnly to, string? q, CancellationToken cancellationToken) =>
    await ExecutePurchaseModuleAsync(purchaseModuleRepository, repository => repository.GetReturnHeaderReportAsync(from, to, q, cancellationToken)));

app.MapGet("/api/inventory/balances", async (string? q, string? warehouse, CancellationToken cancellationToken) =>
    await ExecuteSalesAsync(salesRepository, repository => repository.GetInventoryBalancesAsync(q, warehouse, cancellationToken)));

app.MapGet("/api/inventory/warehouses", async (bool? includeInactive, CancellationToken cancellationToken) =>
    await ExecuteInventoryAsync(inventoryMasterRepository, repository => repository.ListWarehousesAsync(includeInactive ?? true, cancellationToken)));

app.MapPost("/api/inventory/warehouses", async (SaveInventoryWarehouseRequest request, CancellationToken cancellationToken) =>
    await ExecuteInventoryAsync(inventoryMasterRepository, repository => repository.SaveWarehouseAsync(request, cancellationToken), created: request.ExpectedVersion is null));

app.MapGet("/api/inventory/adjustment-types", async (bool? includeInactive, CancellationToken cancellationToken) =>
    await ExecuteInventoryAsync(inventoryMasterRepository, repository => repository.ListAdjustmentTypesAsync(includeInactive ?? true, cancellationToken)));

app.MapPost("/api/inventory/adjustment-types", async (SaveInventoryAdjustmentTypeRequest request, CancellationToken cancellationToken) =>
    await ExecuteInventoryAsync(inventoryMasterRepository, repository => repository.SaveAdjustmentTypeAsync(request, cancellationToken), created: request.ExpectedVersion is null));

app.MapGet("/api/inventory/units", async (CancellationToken cancellationToken) =>
    await ExecuteInventoryAsync(inventoryMasterRepository, repository => repository.ListUnitsAsync(cancellationToken)));

app.MapPost("/api/inventory/units", async (SaveInventoryUnitRequest request, CancellationToken cancellationToken) =>
    await ExecuteInventoryAsync(inventoryMasterRepository, repository => repository.SaveUnitAsync(request, cancellationToken), created: true));

app.MapGet("/api/inventory/categories", async (bool? includeInactive, string? itemType, CancellationToken cancellationToken) =>
    await ExecuteInventoryAsync(inventoryMasterRepository, repository => repository.ListCategoriesAsync(includeInactive ?? true, itemType ?? "GOODS", cancellationToken)));

app.MapPost("/api/inventory/categories", async (SaveInventoryCategoryRequest request, CancellationToken cancellationToken) =>
    await ExecuteInventoryAsync(inventoryMasterRepository, repository => repository.SaveCategoryAsync(request, cancellationToken), created: request.ExpectedVersion is null));

app.MapGet("/api/inventory/items", async (string? q, string? category, string? itemType, bool? includeInactive, int? limit, CancellationToken cancellationToken) =>
    await ExecuteInventoryAsync(inventoryMasterRepository, repository => repository.ListItemsAsync(q, category, itemType, includeInactive ?? true, limit ?? 1000, cancellationToken)));

app.MapGet("/api/inventory/items/{code}", async (string code, CancellationToken cancellationToken) =>
{
    if (inventoryMasterRepository is null) return DatabaseUnavailable();
    var item = await inventoryMasterRepository.GetItemAsync(code, cancellationToken);
    return item is null
        ? Results.NotFound(new ApiError("INVENTORY_ITEM_NOT_FOUND", "Barang tidak ditemukan."))
        : Results.Ok(item);
});

app.MapPost("/api/inventory/items", async (SaveInventoryItemRequest request, CancellationToken cancellationToken) =>
    await ExecuteInventoryAsync(inventoryMasterRepository, repository => repository.SaveItemAsync(request, cancellationToken), created: request.ExpectedVersion is null));

app.MapGet("/api/inventory/business-units", async (CancellationToken cancellationToken) =>
    await ExecuteInventoryAsync(inventoryMasterRepository, repository => repository.ListBusinessUnitsAsync(cancellationToken)));

app.MapGet("/api/inventory/documents", async (string? type, string? q, int? limit, CancellationToken cancellationToken) =>
    await ExecuteInventoryAsync(inventoryTransactionRepository, repository => repository.ListDocumentsAsync(type, q, limit ?? 250, cancellationToken)));

app.MapPost("/api/inventory/transfers", async (CreateInventoryTransferRequest request, CancellationToken cancellationToken) =>
    await ExecuteInventoryAsync(inventoryTransactionRepository, repository => repository.CreateTransferAsync(request, cancellationToken), created: true));
app.MapGet("/api/inventory/transfers/{id:guid}", async (Guid id, CancellationToken cancellationToken) =>
    await ExecuteInventoryDocumentAsync(inventoryTransactionRepository, repository => repository.GetTransferAsync(id, cancellationToken), "INVENTORY_TRANSFER_NOT_FOUND", "Mutasi barang tidak ditemukan."));
app.MapPost("/api/inventory/transfers/{id:guid}/cancel", async (Guid id, CancelInventoryDocumentRequest request, CancellationToken cancellationToken) =>
    await ExecuteInventoryAsync(inventoryTransactionRepository, repository => repository.CancelTransferAsync(id, request, cancellationToken)));

app.MapPost("/api/inventory/adjustments", async (CreateInventoryAdjustmentRequest request, CancellationToken cancellationToken) =>
    await ExecuteInventoryAsync(inventoryTransactionRepository, repository => repository.CreateAdjustmentAsync(request, cancellationToken), created: true));
app.MapGet("/api/inventory/adjustments/{id:guid}", async (Guid id, CancellationToken cancellationToken) =>
    await ExecuteInventoryDocumentAsync(inventoryTransactionRepository, repository => repository.GetAdjustmentAsync(id, cancellationToken), "INVENTORY_ADJUSTMENT_NOT_FOUND", "Penyesuaian persediaan tidak ditemukan."));
app.MapPost("/api/inventory/adjustments/{id:guid}/cancel", async (Guid id, CancelInventoryDocumentRequest request, CancellationToken cancellationToken) =>
    await ExecuteInventoryAsync(inventoryTransactionRepository, repository => repository.CancelAdjustmentAsync(id, request, cancellationToken)));

app.MapPost("/api/inventory/stock-counts", async (CreateInventoryStockCountRequest request, CancellationToken cancellationToken) =>
    await ExecuteInventoryAsync(inventoryTransactionRepository, repository => repository.CreateStockCountAsync(request, cancellationToken), created: true));
app.MapGet("/api/inventory/stock-counts/{id:guid}", async (Guid id, CancellationToken cancellationToken) =>
    await ExecuteInventoryDocumentAsync(inventoryTransactionRepository, repository => repository.GetStockCountAsync(id, cancellationToken), "INVENTORY_STOCK_COUNT_NOT_FOUND", "Stok fisik tidak ditemukan."));
app.MapPost("/api/inventory/stock-counts/{id:guid}/cancel", async (Guid id, CancelInventoryDocumentRequest request, CancellationToken cancellationToken) =>
    await ExecuteInventoryAsync(inventoryTransactionRepository, repository => repository.CancelStockCountAsync(id, request, cancellationToken)));

app.MapPost("/api/inventory/mbap/send", async (CreateInventoryMbapSendRequest request, CancellationToken cancellationToken) =>
    await ExecuteInventoryAsync(inventoryTransactionRepository, repository => repository.CreateMbapSendAsync(request, cancellationToken), created: true));
app.MapGet("/api/inventory/mbap/send/{id:guid}", async (Guid id, CancellationToken cancellationToken) =>
    await ExecuteInventoryDocumentAsync(inventoryTransactionRepository, repository => repository.GetMbapSendAsync(id, cancellationToken), "INVENTORY_MBAP_SEND_NOT_FOUND", "MBAP Kirim tidak ditemukan."));
app.MapGet("/api/inventory/mbap/send/{id:guid}/receive-source", async (Guid id, CancellationToken cancellationToken) =>
    await ExecuteInventoryDocumentAsync(inventoryTransactionRepository, repository => repository.GetMbapReceiveSourceAsync(id, cancellationToken), "INVENTORY_MBAP_SOURCE_NOT_FOUND", "MBAP Kirim tidak ditemukan atau sudah diterima seluruhnya."));
app.MapPost("/api/inventory/mbap/send/{id:guid}/cancel", async (Guid id, CancelInventoryDocumentRequest request, CancellationToken cancellationToken) =>
    await ExecuteInventoryAsync(inventoryTransactionRepository, repository => repository.CancelMbapSendAsync(id, request, cancellationToken)));

app.MapPost("/api/inventory/mbap/receive", async (CreateInventoryMbapReceiveRequest request, CancellationToken cancellationToken) =>
    await ExecuteInventoryAsync(inventoryTransactionRepository, repository => repository.CreateMbapReceiveAsync(request, cancellationToken), created: true));
app.MapGet("/api/inventory/mbap/receive/{id:guid}", async (Guid id, CancellationToken cancellationToken) =>
    await ExecuteInventoryDocumentAsync(inventoryTransactionRepository, repository => repository.GetMbapReceiveAsync(id, cancellationToken), "INVENTORY_MBAP_RECEIVE_NOT_FOUND", "MBAP Terima tidak ditemukan."));
app.MapPost("/api/inventory/mbap/receive/{id:guid}/cancel", async (Guid id, CancelInventoryDocumentRequest request, CancellationToken cancellationToken) =>
    await ExecuteInventoryAsync(inventoryTransactionRepository, repository => repository.CancelMbapReceiveAsync(id, request, cancellationToken)));

app.MapGet("/api/inventory/reports/prices", async (string? q, CancellationToken cancellationToken) =>
    await ExecuteInventoryAsync(inventoryReportRepository, repository => repository.GetPriceReportAsync(q, cancellationToken)));
app.MapGet("/api/inventory/reports/price-history", async (string type, DateOnly from, DateOnly to, string? q, CancellationToken cancellationToken) =>
    await ExecuteInventoryAsync(inventoryReportRepository, repository => repository.GetPriceHistoryAsync(type, from, to, q, cancellationToken)));
app.MapGet("/api/inventory/reports/stock-card", async (DateOnly from, DateOnly to, string? q, string? warehouse, bool? withValue, CancellationToken cancellationToken) =>
    await ExecuteInventoryAsync(inventoryReportRepository, repository => repository.GetStockCardAsync(from, to, q, warehouse, withValue ?? false, cancellationToken)));
app.MapGet("/api/inventory/reports/valuation", async (string? q, string? warehouse, string? mode, CancellationToken cancellationToken) =>
    await ExecuteInventoryAsync(inventoryReportRepository, repository => repository.GetValuationAsync(q, warehouse, mode ?? "ALL", cancellationToken)));
app.MapGet("/api/inventory/reports/daily", async (DateOnly from, DateOnly to, string? q, string? warehouse, CancellationToken cancellationToken) =>
    await ExecuteInventoryAsync(inventoryReportRepository, repository => repository.GetDailyActivityAsync(from, to, q, warehouse, cancellationToken)));
app.MapGet("/api/inventory/reports/turnover", async (DateOnly from, DateOnly to, string? q, string? warehouse, CancellationToken cancellationToken) =>
    await ExecuteInventoryAsync(inventoryReportRepository, repository => repository.GetTurnoverAsync(from, to, q, warehouse, cancellationToken)));
app.MapGet("/api/inventory/reports/transactions", async (DateOnly from, DateOnly to, string? type, string? q, CancellationToken cancellationToken) =>
    await ExecuteInventoryAsync(inventoryReportRepository, repository => repository.GetTransactionReportAsync(from, to, type, q, cancellationToken)));
app.MapGet("/api/inventory/reports/purchase-price-check", async (string itemCode, CancellationToken cancellationToken) =>
    await ExecuteInventoryAsync(inventoryReportRepository, repository => repository.GetPurchasePriceCheckAsync(itemCode, cancellationToken)));
app.MapGet("/api/sales/reports/purchase-price-check", async (string itemCode, CancellationToken cancellationToken) =>
    await ExecuteInventoryAsync(inventoryReportRepository, repository => repository.GetPurchasePriceCheckAsync(itemCode, cancellationToken)));
app.MapGet("/api/sales/reports/purchase-price-check/items", async (string? q, CancellationToken cancellationToken) =>
    await ExecuteInventoryAsync(inventoryMasterRepository, repository => repository.ListItemsAsync(q, null, "GOODS", true, 1000, cancellationToken)));

app.Run();

static IResult DatabaseUnavailable() => Results.Json(
    new ApiError("DATABASE_NOT_CONFIGURED", "Database AKSARA belum dikonfigurasi pada server."),
    statusCode: StatusCodes.Status503ServiceUnavailable);

static async Task<IResult> ExecuteConfigurationAsync<T>(
    ConfigurationRepository? repository,
    Func<ConfigurationRepository, Task<T>> operation)
{
    if (repository is null) return DatabaseUnavailable();
    try
    {
        return Results.Ok(await operation(repository));
    }
    catch (ConfigurationValidationException exception)
    {
        return Results.BadRequest(new ApiError("CONFIGURATION_INVALID", exception.Message));
    }
    catch (ConfigurationForbiddenException exception)
    {
        return Results.Json(new ApiError("CONFIGURATION_FORBIDDEN", exception.Message), statusCode: StatusCodes.Status403Forbidden);
    }
    catch (ConfigurationConflictException exception)
    {
        return Results.Conflict(new ApiError("CONFIGURATION_CONFLICT", exception.Message));
    }
}

static async Task<IResult> ExecuteInitialSetupAsync<T>(
    InitialSetupRepository? repository,
    Func<InitialSetupRepository, Task<T>> operation)
{
    if (repository is null) return DatabaseUnavailable();
    try { return Results.Ok(await operation(repository)); }
    catch (InitialSetupValidationException exception)
    {
        return Results.BadRequest(new ApiError("INITIAL_SETUP_INVALID", exception.Message));
    }
    catch (InitialSetupConflictException exception)
    {
        return Results.Conflict(new ApiError("INITIAL_SETUP_CONFLICT", exception.Message));
    }
}

static async Task<IResult> ExecutePeriodCloseAsync<T>(
    PeriodCloseRepository? repository,
    Func<PeriodCloseRepository, Task<T>> operation,
    bool created = false)
{
    if (repository is null) return DatabaseUnavailable();
    try
    {
        var result = await operation(repository);
        return created ? Results.Json(result, statusCode: StatusCodes.Status201Created) : Results.Ok(result);
    }
    catch (PeriodCloseValidationException exception)
    {
        return Results.BadRequest(new ApiError("PERIOD_CLOSE_INVALID", exception.Message));
    }
    catch (PeriodCloseConflictException exception)
    {
        return Results.Conflict(new ApiError("PERIOD_CLOSE_CONFLICT", exception.Message));
    }
}

static bool RequiresAksaraAuth(PathString path)
{
    if (!path.StartsWithSegments("/api"))
        return false;

    return !path.StartsWithSegments("/api/health") &&
           !path.StartsWithSegments("/api/system/info") &&
           !path.StartsWithSegments("/api/database/status") &&
           !path.StartsWithSegments("/api/auth/login") &&
           !path.StartsWithSegments("/api/auth/resolve") &&
           !path.StartsWithSegments("/api/update");
}

static string ResolvePublicServerUrl(HttpContext context, IConfiguration configuration)
{
    var configured = Environment.GetEnvironmentVariable("AKSARA_PUBLIC_URL") ?? configuration["Server:PublicUrl"];
    if (!string.IsNullOrWhiteSpace(configured)) return TenantLoginDirectory.NormalizeServerUrl(configured);
    return TenantLoginDirectory.NormalizeServerUrl($"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}");
}

static (string Module, string Action)? ResolveApiPermission(PathString path, string method)
{
    var value = path.Value ?? string.Empty;
    if (value.Equals("/api/configuration/program", StringComparison.OrdinalIgnoreCase) && HttpMethods.IsGet(method)) return null;
    if (value.Equals("/api/configuration/change-credentials", StringComparison.OrdinalIgnoreCase)) return null;

    var module = value.StartsWith("/api/maintenance", StringComparison.OrdinalIgnoreCase) ? "MAINTENANCE"
        : value.StartsWith("/api/ledger", StringComparison.OrdinalIgnoreCase) ? "LEDGER"
        : value.StartsWith("/api/cash-bank", StringComparison.OrdinalIgnoreCase) ? "CASH_BANK"
        : value.StartsWith("/api/suppliers", StringComparison.OrdinalIgnoreCase) ? "SUPPLIER"
        : value.StartsWith("/api/customers", StringComparison.OrdinalIgnoreCase) ? "CUSTOMER"
        : value.StartsWith("/api/purchases", StringComparison.OrdinalIgnoreCase) ? "PURCHASE"
        : value.StartsWith("/api/sales", StringComparison.OrdinalIgnoreCase) ? "SALES"
        : value.StartsWith("/api/inventory", StringComparison.OrdinalIgnoreCase) ? "INVENTORY"
        : value.StartsWith("/api/master/suppliers", StringComparison.OrdinalIgnoreCase) ? "SUPPLIER"
        : value.StartsWith("/api/master/customers", StringComparison.OrdinalIgnoreCase) ? "CUSTOMER"
        : value.StartsWith("/api/master/salespeople", StringComparison.OrdinalIgnoreCase) ? "SALES"
        : value.StartsWith("/api/configuration", StringComparison.OrdinalIgnoreCase) ? "CONFIGURATION"
        : null;
    if (module is null) return null;

    var action = HttpMethods.IsGet(method) || value.EndsWith("/preflight", StringComparison.OrdinalIgnoreCase) ? "VIEW"
        : value.EndsWith("/cancel", StringComparison.OrdinalIgnoreCase) || value.EndsWith("/restore", StringComparison.OrdinalIgnoreCase)
            ? "DELETE"
            : "MANAGE";
    return (module, action);
}

static string? ResolveConnectionString(IConfiguration configuration)
{
    var configured = configuration.GetConnectionString("Aksara");
    if (!string.IsNullOrWhiteSpace(configured))
        return configured;

    var instanceConnectionName = configuration["INSTANCE_CONNECTION_NAME"];
    var databaseName = configuration["DB_NAME"];
    var userName = configuration["DB_USER"];
    var password = configuration["DB_PASSWORD"];
    if (string.IsNullOrWhiteSpace(instanceConnectionName) ||
        string.IsNullOrWhiteSpace(databaseName) ||
        string.IsNullOrWhiteSpace(userName) ||
        string.IsNullOrWhiteSpace(password))
    {
        return null;
    }

    return new NpgsqlConnectionStringBuilder
    {
        Host = $"/cloudsql/{instanceConnectionName}",
        Port = int.TryParse(configuration["DB_PORT"], out var port) ? port : 5432,
        Database = databaseName,
        Username = userName,
        Password = password,
        Pooling = true
    }.ConnectionString;
}

static async Task<IResult> ExecuteSalesAsync<T>(
    SalesInventoryRepository? repository,
    Func<SalesInventoryRepository, Task<T>> operation,
    bool created = false)
{
    if (repository is null) return DatabaseUnavailable();
    try
    {
        var result = await operation(repository);
        return created ? Results.Json(result, statusCode: StatusCodes.Status201Created) : Results.Ok(result);
    }
    catch (StockUnavailableException exception)
    {
        return Results.Conflict(new ApiError(
            "INSUFFICIENT_STOCK",
            exception.Message,
            new Dictionary<string, string[]> { [exception.ItemCode] = [$"Gudang {exception.WarehouseCode}: diminta {exception.Requested:N3}, tersedia {exception.Available:N3}"] }));
    }
    catch (SalesConflictException exception)
    {
        return Results.Conflict(new ApiError("SALE_CONFLICT", exception.Message));
    }
    catch (SalesValidationException exception)
    {
        return Results.BadRequest(new ApiError("VALIDATION_ERROR", exception.Message));
    }
    catch (InvalidOperationException exception)
    {
        return Results.BadRequest(new ApiError("BUSINESS_RULE_ERROR", exception.Message));
    }
}

static async Task<IResult> ExecuteSalesModuleAsync<T>(
    SalesReturnReportRepository? repository,
    Func<SalesReturnReportRepository, Task<T>> operation,
    bool created = false)
{
    if (repository is null) return DatabaseUnavailable();
    try
    {
        var result = await operation(repository);
        return created ? Results.Json(result, statusCode: StatusCodes.Status201Created) : Results.Ok(result);
    }
    catch (SalesConflictException exception)
    {
        return Results.Conflict(new ApiError("SALE_MODULE_CONFLICT", exception.Message));
    }
    catch (SalesValidationException exception)
    {
        return Results.BadRequest(new ApiError("VALIDATION_ERROR", exception.Message));
    }
    catch (InvalidOperationException exception)
    {
        return Results.BadRequest(new ApiError("BUSINESS_RULE_ERROR", exception.Message));
    }
}

static async Task<IResult> ExecuteCustomerReceiptAsync<T>(
    CustomerReceiptRepository? repository,
    Func<CustomerReceiptRepository, Task<T>> operation,
    bool created = false)
{
    if (repository is null) return DatabaseUnavailable();
    try
    {
        var result = await operation(repository);
        return created ? Results.Json(result, statusCode: StatusCodes.Status201Created) : Results.Ok(result);
    }
    catch (SalesConflictException exception)
    {
        return Results.Conflict(new ApiError("CUSTOMER_RECEIPT_CONFLICT", exception.Message));
    }
    catch (SalesValidationException exception)
    {
        return Results.BadRequest(new ApiError("VALIDATION_ERROR", exception.Message));
    }
    catch (LedgerConflictException exception)
    {
        return Results.Conflict(new ApiError("LEDGER_CONFLICT", exception.Message));
    }
    catch (LedgerValidationException exception)
    {
        return Results.BadRequest(new ApiError("VALIDATION_ERROR", exception.Message));
    }
    catch (InvalidOperationException exception)
    {
        return Results.BadRequest(new ApiError("BUSINESS_RULE_ERROR", exception.Message));
    }
}

static async Task<IResult> ExecutePurchaseAsync<T>(
    PurchaseInventoryRepository? repository,
    Func<PurchaseInventoryRepository, Task<T>> operation,
    bool created = false)
{
    if (repository is null) return DatabaseUnavailable();
    try
    {
        var result = await operation(repository);
        return created ? Results.Json(result, statusCode: StatusCodes.Status201Created) : Results.Ok(result);
    }
    catch (PurchaseConflictException exception)
    {
        return Results.Conflict(new ApiError("PURCHASE_CONFLICT", exception.Message));
    }
    catch (PurchaseValidationException exception)
    {
        return Results.BadRequest(new ApiError("VALIDATION_ERROR", exception.Message));
    }
    catch (InvalidOperationException exception)
    {
        return Results.BadRequest(new ApiError("BUSINESS_RULE_ERROR", exception.Message));
    }
}

static async Task<IResult> ExecutePurchaseModuleAsync<T>(
    PurchaseReturnReportRepository? repository,
    Func<PurchaseReturnReportRepository, Task<T>> operation,
    bool created = false)
{
    if (repository is null) return DatabaseUnavailable();
    try
    {
        var result = await operation(repository);
        return created ? Results.Json(result, statusCode: StatusCodes.Status201Created) : Results.Ok(result);
    }
    catch (PurchaseConflictException exception)
    {
        return Results.Conflict(new ApiError("PURCHASE_MODULE_CONFLICT", exception.Message));
    }
    catch (PurchaseValidationException exception)
    {
        return Results.BadRequest(new ApiError("VALIDATION_ERROR", exception.Message));
    }
    catch (InvalidOperationException exception)
    {
        return Results.BadRequest(new ApiError("BUSINESS_RULE_ERROR", exception.Message));
    }
}

static async Task<IResult> ExecuteSupplierPaymentAsync<T>(
    SupplierPaymentRepository? repository,
    Func<SupplierPaymentRepository, Task<T>> operation,
    bool created = false)
{
    if (repository is null) return DatabaseUnavailable();
    try
    {
        var result = await operation(repository);
        return created ? Results.Json(result, statusCode: StatusCodes.Status201Created) : Results.Ok(result);
    }
    catch (PurchaseConflictException exception)
    {
        return Results.Conflict(new ApiError("SUPPLIER_PAYMENT_CONFLICT", exception.Message));
    }
    catch (PurchaseValidationException exception)
    {
        return Results.BadRequest(new ApiError("VALIDATION_ERROR", exception.Message));
    }
    catch (LedgerConflictException exception)
    {
        return Results.Conflict(new ApiError("LEDGER_CONFLICT", exception.Message));
    }
    catch (LedgerValidationException exception)
    {
        return Results.BadRequest(new ApiError("VALIDATION_ERROR", exception.Message));
    }
    catch (InvalidOperationException exception)
    {
        return Results.BadRequest(new ApiError("BUSINESS_RULE_ERROR", exception.Message));
    }
}

static async Task<IResult> ExecutePartnerBalanceAsync<T>(
    PartnerBalanceRepository? repository,
    Func<PartnerBalanceRepository, Task<T>> operation,
    bool created = false)
{
    if (repository is null) return DatabaseUnavailable();
    try
    {
        var result = await operation(repository);
        return created ? Results.Json(result, statusCode: StatusCodes.Status201Created) : Results.Ok(result);
    }
    catch (PartnerBalanceConflictException exception)
    {
        return Results.Conflict(new ApiError("PARTNER_BALANCE_CONFLICT", exception.Message));
    }
    catch (PartnerBalanceValidationException exception)
    {
        return Results.BadRequest(new ApiError("VALIDATION_ERROR", exception.Message));
    }
    catch (InvalidOperationException exception)
    {
        return Results.BadRequest(new ApiError("BUSINESS_RULE_ERROR", exception.Message));
    }
}

static async Task<IResult> ExecuteInventoryAsync<TRepository, TResult>(
    TRepository? repository,
    Func<TRepository, Task<TResult>> operation,
    bool created = false)
    where TRepository : class
{
    if (repository is null) return DatabaseUnavailable();
    try
    {
        var result = await operation(repository);
        return created ? Results.Json(result, statusCode: StatusCodes.Status201Created) : Results.Ok(result);
    }
    catch (InventoryConflictException exception)
    {
        return Results.Conflict(new ApiError("INVENTORY_CONFLICT", exception.Message));
    }
    catch (InventoryValidationException exception)
    {
        return Results.BadRequest(new ApiError("VALIDATION_ERROR", exception.Message));
    }
    catch (InvalidOperationException exception)
    {
        return Results.BadRequest(new ApiError("BUSINESS_RULE_ERROR", exception.Message));
    }
}

static async Task<IResult> ExecutePartnerAsync<TResult>(
    BusinessPartnerRepository? repository,
    Func<BusinessPartnerRepository, Task<TResult>> operation,
    bool created = false)
{
    if (repository is null) return DatabaseUnavailable();
    try
    {
        var result = await operation(repository);
        return created ? Results.Json(result, statusCode: StatusCodes.Status201Created) : Results.Ok(result);
    }
    catch (PartnerConflictException exception)
    {
        return Results.Conflict(new ApiError("MASTER_DATA_CONFLICT", exception.Message));
    }
    catch (PartnerValidationException exception)
    {
        return Results.BadRequest(new ApiError("VALIDATION_ERROR", exception.Message));
    }
}

static async Task<IResult> ExecuteLedgerAsync<TResult>(
    LedgerRepository? repository,
    Func<LedgerRepository, Task<TResult>> operation,
    bool created = false)
{
    if (repository is null) return DatabaseUnavailable();
    try
    {
        var result = await operation(repository);
        return created ? Results.Json(result, statusCode: StatusCodes.Status201Created) : Results.Ok(result);
    }
    catch (LedgerConflictException exception)
    {
        return Results.Conflict(new ApiError("LEDGER_CONFLICT", exception.Message));
    }
    catch (LedgerValidationException exception)
    {
        return Results.BadRequest(new ApiError("VALIDATION_ERROR", exception.Message));
    }
    catch (InvalidOperationException exception)
    {
        return Results.BadRequest(new ApiError("BUSINESS_RULE_ERROR", exception.Message));
    }
}

static async Task<IResult> ExecuteCashBankAsync<TResult>(
    CashBankRepository? repository,
    Func<CashBankRepository, Task<TResult>> operation,
    bool created = false)
{
    if (repository is null) return DatabaseUnavailable();
    try
    {
        var result = await operation(repository);
        return created ? Results.Json(result, statusCode: StatusCodes.Status201Created) : Results.Ok(result);
    }
    catch (CashBankConflictException exception)
    {
        return Results.Conflict(new ApiError("CASH_BANK_CONFLICT", exception.Message));
    }
    catch (CashBankValidationException exception)
    {
        return Results.BadRequest(new ApiError("VALIDATION_ERROR", exception.Message));
    }
    catch (LedgerConflictException exception)
    {
        return Results.Conflict(new ApiError("LEDGER_CONFLICT", exception.Message));
    }
    catch (LedgerValidationException exception)
    {
        return Results.BadRequest(new ApiError("VALIDATION_ERROR", exception.Message));
    }
    catch (InvalidOperationException exception)
    {
        return Results.BadRequest(new ApiError("BUSINESS_RULE_ERROR", exception.Message));
    }
}

static async Task<IResult> ExecuteInventoryDocumentAsync<T>(
    InventoryTransactionRepository? repository,
    Func<InventoryTransactionRepository, Task<T?>> operation,
    string errorCode,
    string errorMessage)
    where T : class
{
    if (repository is null) return DatabaseUnavailable();
    try
    {
        var result = await operation(repository);
        return result is null ? Results.NotFound(new ApiError(errorCode, errorMessage)) : Results.Ok(result);
    }
    catch (InventoryValidationException exception)
    {
        return Results.BadRequest(new ApiError("VALIDATION_ERROR", exception.Message));
    }
}
