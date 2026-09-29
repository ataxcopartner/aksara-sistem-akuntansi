using Aksara.Contracts;
using Aksara.Desktop.Services;
using Aksara.Desktop.Views;
using Aksara.Infrastructure;
using Aksara.Server;
using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;

var tests = new (string Name, Action Run)[]
{
    ("Konfigurasi lama", ParseLegacyConfiguration),
    ("Konfigurasi profil baru", ParseNewConfiguration),
    ("Profil lokal tidak menyimpan password", SerializeProfileWithoutPassword),
    ("Pemilihan endpoint", SelectEndpoint),
    ("Penolakan instance berbeda", RejectInstanceMismatch),
    ("Penolakan kode berbeda", RejectCompanyCodeMismatch),
    ("Pengikatan identitas valid", BindValidIdentity),
    ("Token lintas perusahaan ditolak", RejectCrossCompanyToken),
    ("Perusahaan lama langsung normal", ExistingCompanyEntersNormally),
    ("Database baru wajib setup", NewCompanyRequiresSetup),
    ("Setup draft tidak dapat ditutup", DraftSetupCannotClose),
    ("API operasional diblokir saat draft", OperationalApiBlockedDuringSetup),
    ("Aktivasi membuka dan reopen mengunci", ActivationAndReopenTransition),
    ("Status setup terpisah per perusahaan", SetupStatusIsPerCompany),
    ("Placeholder nama perusahaan menahan aktivasi", PlaceholderCompanyNameBlocksActivation),
    ("Nama final tersinkron ke identitas perusahaan", FinalCompanyNameSynchronizationContract),
    ("Login existing diarahkan ke perusahaan lama", ExistingLoginRoutesToDefaultTenant),
    ("Login pengguna tenant diarahkan ke tenant baru", TenantUserRoutesToNewTenant),
    ("Username tenant ambigu ditolak", AmbiguousTenantUserIsRejected),
    ("Password salah ditolak oleh hash", WrongPasswordIsRejected),
    ("Template Excel COA dapat dibaca", LedgerAccountExcelCanReadTemplate),
    ("COA bawaan menggunakan kode lima digit", DefaultLedgerAccountsUseFiveDigitCodes),
    ("Migrasi jasa terintegrasi", ServiceMigrationIsIntegrated),
    ("Kontrak transaksi default tetap barang", TransactionContractsDefaultToGoods),
    ("Form penjualan jasa terpisah", ServiceSalesViewIsDedicated),
    ("Master kelompok jasa terpisah", ServiceCategoryMasterIsSeparated)
};

static void ServiceCategoryMasterIsSeparated()
{
    Exception? failure = null;
    var thread = new Thread(() =>
    {
        try
        {
            var list = new ItemListView(serviceMode: true);
            var grid = (DataGrid?)list.FindName("ItemGrid");
            var categories = new InventoryMasterView(InventoryMasterKind.ServiceCategories);

            if (grid is null || grid.Columns[3].Visibility != Visibility.Collapsed || grid.Columns[8].Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("Satuan atau HPP Standar masih tampil pada daftar jasa.");
            Equal("Daftar Kelompok Jasa", categories.ViewTitle);
            Contains(new InventoryCategoryDto("KONSUL", "Konsultasi", null, true, 1, CategoryType: "SERVICE").Analysis, "Kode Jasa");
        }
        catch (Exception exception)
        {
            failure = exception;
        }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();
    if (failure is not null) throw failure;
}

static void ServiceSalesViewIsDedicated()
{
    Exception? failure = null;
    var thread = new Thread(() =>
    {
        try
        {
            var goods = new SalesView();
            var service = new ServiceSalesView();
            var goodsHeader = (FrameworkElement?)goods.FindName("StandardSalesHeader");
            var serviceHeader = (FrameworkElement?)service.FindName("ServiceSalesHeader");
            var standardServiceHeader = (FrameworkElement?)service.FindName("StandardSalesHeader");
            var detailTabs = (FrameworkElement?)service.FindName("DetailTabs");
            var serviceDetailsHeading = (FrameworkElement?)service.FindName("ServiceDetailsHeading");
            var lines = (DataGrid?)service.FindName("LinesGrid");

            if (goodsHeader?.Visibility != Visibility.Visible)
                throw new InvalidOperationException("Form barang tidak lagi memakai header standarnya.");
            if (serviceHeader?.Visibility != Visibility.Visible || standardServiceHeader?.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("Form jasa belum memakai tampilan khusus jasa.");
            if (detailTabs?.Visibility != Visibility.Collapsed || serviceDetailsHeading?.Visibility != Visibility.Visible)
                throw new InvalidOperationException("Tab biaya bahan/uraian kerja masih tampil pada form jasa.");
            if (lines is null || lines.Columns[0].Visibility != Visibility.Collapsed || lines.Columns[3].Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("Kolom gudang/koli masih tampil pada form jasa.");
            if (lines.Columns[5].Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("Kolom satuan masih tampil pada penjualan jasa.");
            Equal("Tarif", lines.Columns[6].Header?.ToString());
            Equal("Nilai Jasa", lines.Columns[8].Header?.ToString());
        }
        catch (Exception exception)
        {
            failure = exception;
        }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();
    if (failure is not null) throw failure;
}

static void ServiceMigrationIsIntegrated()
{
    var path = Path.Combine(AppContext.BaseDirectory, "Database", "037_service_module.sql");
    if (!File.Exists(path)) throw new InvalidOperationException("Migrasi modul jasa tidak ditemukan pada output QA.");
    var sql = File.ReadAllText(path);
    foreach (var expected in new[]
             {
                 "item_type", "document_kind", "service_sale_material_usage",
                 "SERVICE_REVENUE", "SERVICE_COGS", "SERVICE_PURCHASE_EXPENSE",
                 "PENDAPATAN JASA", "HPP JASA", "BEBAN PEMBELIAN JASA"
             })
        Contains(sql, expected);

    var categoryPath = Path.Combine(AppContext.BaseDirectory, "Database", "038_service_categories.sql");
    if (!File.Exists(categoryPath)) throw new InvalidOperationException("Migrasi kelompok jasa tidak ditemukan pada output QA.");
    var categorySql = File.ReadAllText(categoryPath);
    Contains(categorySql, "category_type");
    Contains(categorySql, "SERVICE");
}

static void TransactionContractsDefaultToGoods()
{
    var sale = new CreateSaleRequest(DateOnly.FromDateTime(DateTime.Today), null, "CUST", "CREDIT", null,
        DateOnly.FromDateTime(DateTime.Today), null, "UTAMA", 0, 0, 0, 0, null, "qa", "qa", []);
    var purchase = new CreatePurchaseRequest(DateOnly.FromDateTime(DateTime.Today), null, "SUPP", null, "CREDIT",
        DateOnly.FromDateTime(DateTime.Today), "UTAMA", 0, 0, 0, 0, null, "qa", "qa", []);
    Equal("GOODS", sale.DocumentKind);
    Equal("GOODS", purchase.DocumentKind);
}

static void DefaultLedgerAccountsUseFiveDigitCodes()
{
    var databaseDirectory = Path.Combine(AppContext.BaseDirectory, "Database");
    var ledgerSeed = File.ReadAllText(Path.Combine(databaseDirectory, "007_ledger_cash_bank.sql"));
    var giroSeed = File.ReadAllText(Path.Combine(databaseDirectory, "012_customer_receipts.sql"));
    var correctionSeed = File.ReadAllText(Path.Combine(databaseDirectory, "028_partner_correction_accounts.sql"));
    var migration = File.ReadAllText(Path.Combine(databaseDirectory, "036_default_coa_five_digit_codes.sql"));

    foreach (var code in new[]
             {
                 "10000", "11000", "11100", "11101", "11102", "11201", "11202", "11301", "11401",
                 "20000", "21101", "21201", "30000", "31101", "31201", "40000", "41101", "41201",
                 "50000", "51101", "60000", "61101", "61201", "61301"
             })
        Contains(ledgerSeed, $"('{code}'");

    Contains(giroSeed, "('11302'");
    Contains(correctionSeed, "('80000'");
    Contains(correctionSeed, "('82000'");
    Contains(correctionSeed, "('83000'");
    Contains(correctionSeed, "('82101'");
    Contains(correctionSeed, "('83101'");
    Contains(migration, "('100.02.09.00', '11302'");
    Contains(migration, "('8', '80000'");

    if (giroSeed.Contains("100.02.09.00", StringComparison.Ordinal)
        || correctionSeed.Contains("800.00.00.00", StringComparison.Ordinal))
        throw new InvalidOperationException("Seed COA masih memuat nomor akun bawaan berkode panjang.");
}

var failed = 0;
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine("PASS " + test.Name);
    }
    catch (Exception exception)
    {
        failed++;
        Console.Error.WriteLine($"FAIL {test.Name}: {exception.Message}");
    }
}
return failed == 0 ? 0 : 1;

static void LedgerAccountExcelCanReadTemplate()
{
    var path = Path.Combine(Path.GetTempPath(), $"aksara-coa-{Guid.NewGuid():N}.xlsx");
    try
    {
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "xl/workbook.xml", """
                <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                  <sheets><sheet name="Template COA" sheetId="1" r:id="rId1" /></sheets>
                </workbook>
                """);
            WriteEntry(archive, "xl/_rels/workbook.xml.rels", """
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml" />
                </Relationships>
                """);
            WriteEntry(archive, "xl/worksheets/sheet1.xml", """
                <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>
                  <row r="1"><c r="A1" t="inlineStr"><is><t>Kode Perkiraan</t></is></c><c r="B1" t="inlineStr"><is><t>Nama Perkiraan</t></is></c><c r="C1" t="inlineStr"><is><t>Jenis</t></is></c><c r="D1" t="inlineStr"><is><t>Saldo Normal</t></is></c><c r="E1" t="inlineStr"><is><t>Kode Induk</t></is></c><c r="F1" t="inlineStr"><is><t>Dapat Diposting</t></is></c><c r="G1" t="inlineStr"><is><t>Kas Bank</t></is></c><c r="H1" t="inlineStr"><is><t>Aktif</t></is></c></row>
                  <row r="2"><c r="A2" t="inlineStr"><is><t>1000</t></is></c><c r="B2" t="inlineStr"><is><t>AKTIVA</t></is></c><c r="C2" t="inlineStr"><is><t>AKTIVA</t></is></c><c r="F2" t="inlineStr"><is><t>TIDAK</t></is></c><c r="H2" t="inlineStr"><is><t>YA</t></is></c></row>
                  <row r="3"><c r="A3" t="inlineStr"><is><t>1110</t></is></c><c r="B3" t="inlineStr"><is><t>KAS</t></is></c><c r="C3" t="inlineStr"><is><t>ASSET</t></is></c><c r="D3" t="inlineStr"><is><t>D</t></is></c><c r="E3" t="inlineStr"><is><t>1000</t></is></c><c r="F3" t="inlineStr"><is><t>YA</t></is></c><c r="G3" t="inlineStr"><is><t>YA</t></is></c><c r="H3" t="inlineStr"><is><t>YA</t></is></c></row>
                </sheetData></worksheet>
                """);
        }
        var rows = LedgerAccountExcel.Read(path);
        Equal(2, rows.Count);
        Equal("ASSET", rows[0].AccountType);
        Equal("D", rows[0].NormalBalance);
        Equal(false, rows[0].IsPostable);
        Equal("1000", rows[1].ParentCode);
        Equal(true, rows[1].IsCashBank);
    }
    finally
    {
        if (File.Exists(path)) File.Delete(path);
    }
}

static void WriteEntry(ZipArchive archive, string name, string content)
{
    var entry = archive.CreateEntry(name);
    using var writer = new StreamWriter(entry.Open());
    writer.Write(content);
}

static void ParseLegacyConfiguration()
{
    const string json = """
        {"serverUrl":"https://lama.example.com","updateManifestUrl":"https://updates.example.com/manifest.json"}
        """;
    var result = CompanyProfileStore.Parse(json, "http://fallback.invalid");
    Equal(1, result.Profiles.Count);
    Equal("https://lama.example.com", result.Profiles[0].ServerAddress);
    Equal("https://updates.example.com/manifest.json", result.Profiles[0].UpdateManifestUrl);
    Equal(string.Empty, result.Profiles[0].CompanyCode);
}

static void ParseNewConfiguration()
{
    const string json = """
        {
          "schemaVersion":2,
          "lastSelectedCompanyCode":"BETA",
          "profiles":[
            {"id":"11111111-1111-1111-1111-111111111111","companyCode":"ALFA","companyName":"PT Alfa","serverUrl":"https://alfa.example.com","updateManifestUrl":"https://updates.example.com/manifest.json"},
            {"id":"22222222-2222-2222-2222-222222222222","companyCode":"BETA","companyName":"PT Beta","serverAddress":"https://beta.example.com","updateManifestUrl":"https://updates.example.com/manifest.json"}
          ]
        }
        """;
    var result = CompanyProfileStore.Parse(json, "http://fallback.invalid");
    Equal(2, result.Profiles.Count);
    Equal("BETA", result.LastSelectedCompanyCode);
    Equal("https://beta.example.com", result.Profiles[1].ServerAddress);
}

static void SerializeProfileWithoutPassword()
{
    var json = CompanyProfileStore.Serialize([Profile("ALFA", null)], "ALFA");
    if (!json.Contains("\"serverUrl\"", StringComparison.Ordinal) || json.Contains("password", StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Format profil lokal tidak aman atau tidak sesuai schema.");
}

static void SelectEndpoint()
{
    AksaraApiClient.Shared.ConfigureServerAddress("https://perusahaan-b.example.com");
    Equal("https://perusahaan-b.example.com/", AksaraApiClient.Shared.ServerAddress.AbsoluteUri);
    AksaraApiClient.Shared.ConfigureServerAddress("https://perusahaan-a.example.com");
    Equal("https://perusahaan-a.example.com/", AksaraApiClient.Shared.ServerAddress.AbsoluteUri);
}

static void RejectInstanceMismatch()
{
    var profile = Profile("ALFA", Guid.Parse("11111111-1111-1111-1111-111111111111"));
    var company = Company("ALFA", Guid.Parse("22222222-2222-2222-2222-222222222222"));
    Throws(() => CompanyProfileStore.BindVerifiedIdentity(profile, company));
}

static void RejectCompanyCodeMismatch()
{
    var instance = Guid.Parse("11111111-1111-1111-1111-111111111111");
    Throws(() => CompanyProfileStore.BindVerifiedIdentity(Profile("ALFA", instance), Company("BETA", instance)));
}

static void BindValidIdentity()
{
    var instance = Guid.Parse("11111111-1111-1111-1111-111111111111");
    var bound = CompanyProfileStore.BindVerifiedIdentity(Profile(string.Empty, null), Company("ALFA", instance));
    Equal("ALFA", bound.CompanyCode);
    Equal(instance, bound.ExpectedInstanceId);
}

static void RejectCrossCompanyToken()
{
    const string sharedKey = "shared-key-used-only-for-isolation-test";
    var companyA = Company("ALFA", Guid.Parse("11111111-1111-1111-1111-111111111111"));
    var companyB = Company("BETA", Guid.Parse("22222222-2222-2222-2222-222222222222"));
    var serverA = new AuthTokenService(sharedKey, TimeSpan.FromMinutes(5));
    var serverB = new AuthTokenService(sharedKey, TimeSpan.FromMinutes(5));
    serverA.SetCompanyIdentity(companyA);
    serverB.SetCompanyIdentity(companyB);
    var user = new AuthenticatedUserDto(Guid.NewGuid(), "user-sama", "User Sama", "STAFF", "Staff", []);
    var token = serverA.CreateLoginResponse(user).Token;
    if (!serverA.TryValidate(token, out _)) throw new InvalidOperationException("Token perusahaan asal seharusnya valid.");
    if (serverB.TryValidate(token, out _)) throw new InvalidOperationException("Token perusahaan lain tidak boleh diterima.");
}

static void ExistingCompanyEntersNormally()
{
    if (InitialSetupFlowPolicy.RequiresSetup(Setup("ACTIVE", false, "ALFA")))
        throw new InvalidOperationException("Setup ACTIVE tidak boleh mengunci perusahaan lama.");
    if (InitialSetupFlowPolicy.RequiresSetup(Setup("DRAFT", true, "LEGACY")))
        throw new InvalidOperationException("Database legacy_protected tidak boleh dipaksa setup ulang.");
}

static void NewCompanyRequiresSetup()
{
    if (!InitialSetupFlowPolicy.RequiresSetup(Setup("DRAFT", false, "BARU")))
        throw new InvalidOperationException("Database baru harus masuk setup.");
}

static void DraftSetupCannotClose()
{
    if (InitialSetupFlowPolicy.CanCloseSetup(Setup("DRAFT", false, "BARU")) || InitialSetupFlowPolicy.CanCloseSetup(null))
        throw new InvalidOperationException("Setup draft atau status tidak diketahui tidak boleh ditutup menuju aplikasi normal.");
}

static void OperationalApiBlockedDuringSetup()
{
    if (InitialSetupAccessPolicy.IsAllowed("/api/purchases", "POST") ||
        InitialSetupAccessPolicy.IsAllowed("/api/sales", "GET") ||
        InitialSetupAccessPolicy.IsAllowed("/api/maintenance/period-close", "POST") ||
        InitialSetupAccessPolicy.IsAllowed("/api/ledger/reports/trial-balance", "GET"))
        throw new InvalidOperationException("API operasional/laporan tidak boleh lolos saat setup.");
    if (!InitialSetupAccessPolicy.IsAllowed("/api/configuration/initial-setup/activate", "POST") ||
        !InitialSetupAccessPolicy.IsAllowed("/api/master/suppliers", "POST") ||
        !InitialSetupAccessPolicy.IsAllowed("/api/ledger/mappings", "POST"))
        throw new InvalidOperationException("API yang diperlukan setup harus tetap tersedia.");
}

static void ActivationAndReopenTransition()
{
    if (InitialSetupFlowPolicy.RequiresSetup(Setup("ACTIVE", false, "BARU")))
        throw new InvalidOperationException("Aktivasi harus membuka aplikasi normal.");
    if (!InitialSetupFlowPolicy.RequiresSetup(Setup("DRAFT", false, "BARU")))
        throw new InvalidOperationException("Buka ulang harus mengunci aplikasi kembali.");
}

static void SetupStatusIsPerCompany()
{
    var active = Setup("ACTIVE", false, "ALFA");
    var draft = Setup("DRAFT", false, "BETA");
    if (active.Company.InstanceId == draft.Company.InstanceId ||
        InitialSetupFlowPolicy.RequiresSetup(active) ||
        !InitialSetupFlowPolicy.RequiresSetup(draft))
        throw new InvalidOperationException("Status perusahaan tidak terisolasi.");
}

static void PlaceholderCompanyNameBlocksActivation()
{
    Equal(CompanyIdentityPolicy.PendingCompanyName, CompanyIdentityPolicy.BootstrapCompanyName(null));
    if (CompanyIdentityPolicy.IsSetupCompanyProfileReady(CompanyIdentityPolicy.PendingCompanyName, profileConfirmed: true))
        throw new InvalidOperationException("Nama bootstrap sementara tidak boleh lolos preflight/aktivasi.");
    if (CompanyIdentityPolicy.IsSetupCompanyProfileReady("PT Perusahaan Final", profileConfirmed: false))
        throw new InvalidOperationException("Nama final yang belum dikonfirmasi tidak boleh lolos preflight/aktivasi.");
    if (!CompanyIdentityPolicy.IsSetupCompanyProfileReady("PT Perusahaan Final", profileConfirmed: true))
        throw new InvalidOperationException("Nama perusahaan final yang sudah dikonfirmasi seharusnya diterima.");
}

static void FinalCompanyNameSynchronizationContract()
{
    var migration = Path.Combine(AppContext.BaseDirectory, "Database", "033_initial_setup_profile_confirmation.sql");
    if (!File.Exists(migration)) throw new InvalidOperationException("Migrasi identitas perusahaan tidak ditemukan pada output QA.");
    var sql = File.ReadAllText(migration);
    if (!sql.Contains("after update of company_name on program_configuration", StringComparison.OrdinalIgnoreCase) ||
        !sql.Contains("set company_name=new.company_name", StringComparison.OrdinalIgnoreCase) ||
        !sql.Contains("set profile_confirmed=true", StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Kontrak sinkronisasi nama final ke company_instance tidak tersedia.");
}

static void ExistingLoginRoutesToDefaultTenant()
{
    var directory = Directory();
    var result = directory.Resolve("admin", existsOnDefaultTenant: true, "https://old.example.com");
    if (result.IsAmbiguous || result.Route is null) throw new InvalidOperationException("Akun existing gagal diarahkan ke tenant default.");
    Equal("DEFAULT", result.Route.RouteKind);
    Equal("https://old.example.com", result.Route.ServerUrl);
}

static void TenantUserRoutesToNewTenant()
{
    var directory = Directory();
    var result = directory.Resolve("tenant-test-user", existsOnDefaultTenant: false, "https://old.example.com");
    if (result.IsAmbiguous || result.Route is null) throw new InvalidOperationException("Pengguna uji gagal diarahkan ke tenant kedua.");
    Equal("TENANT", result.Route.RouteKind);
    Equal("https://new.example.com", result.Route.ServerUrl);
    Equal("COMPANY-2", result.Route.CompanyCode);
}

static void AmbiguousTenantUserIsRejected()
{
    var directory = Directory();
    var result = directory.Resolve("tenant-test-user", existsOnDefaultTenant: true, "https://old.example.com");
    if (!result.IsAmbiguous || result.Route is not null)
        throw new InvalidOperationException("Username yang ada di tenant default dan direktori harus ditolak sebagai ambigu.");
}

static void WrongPasswordIsRejected()
{
    const string testPassword = "Only-For-QA-123!";
    var hash = PasswordHasher.Hash(testPassword);
    if (!PasswordHasher.Verify(hash, testPassword) || PasswordHasher.Verify(hash, "password-salah"))
        throw new InvalidOperationException("Verifikasi password hash tenant tidak konsisten.");
    if (hash.Contains(testPassword, StringComparison.Ordinal))
        throw new InvalidOperationException("Password tidak boleh tersimpan sebagai plaintext.");
}

static TenantLoginDirectory Directory() => new(
[
    new("tenant-test-user", "https://new.example.com", "COMPANY-2", Guid.Parse("22222222-2222-2222-2222-222222222222"))
]);

static InitialSetupDto Setup(string status, bool legacy, string code) => new(
    Guid.NewGuid(), status, null, null, legacy, null, null, 1,
    new CompanyIdentityDto(Guid.NewGuid(), code, "PT " + code, "DATABASE"), [], [], [], [], []);

static CompanyConnectionProfile Profile(string code, Guid? instance) =>
    new(Guid.NewGuid(), code, "PT Alfa", "https://alfa.example.com", "https://updates.example.com/manifest.json", instance);

static CompanyIdentityDto Company(string code, Guid instance) => new(instance, code, "PT " + code, "DATABASE");

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Diharapkan {expected}, ditemukan {actual}.");
}

static void Contains(string text, string expected)
{
    if (!text.Contains(expected, StringComparison.Ordinal))
        throw new InvalidOperationException($"Teks yang diharapkan tidak ditemukan: {expected}");
}

static void Throws(Action action)
{
    try { action(); }
    catch (AksaraApiException) { return; }
    throw new InvalidOperationException("Operasi seharusnya ditolak.");
}
