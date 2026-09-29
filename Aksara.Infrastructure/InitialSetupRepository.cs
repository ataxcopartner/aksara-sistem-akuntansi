using System.Data;
using System.Text.Json;
using Aksara.Contracts;
using Npgsql;
using NpgsqlTypes;

namespace Aksara.Infrastructure;

public sealed class InitialSetupValidationException(string message) : InvalidOperationException(message);
public sealed class InitialSetupConflictException(string message) : InvalidOperationException(message);

public sealed class InitialSetupRepository(PostgresDatabase database)
{
    public async Task<bool> IsRequiredAsync(CancellationToken cancellationToken = default)
    {
        const string sql = "select status,legacy_protected from initial_setup where singleton;";
        await using var command = database.DataSource.CreateCommand(sql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("Status Setup Data Awal belum tersedia.");
        return !reader.GetBoolean(1) && !reader.GetString(0).Equals("ACTIVE", StringComparison.OrdinalIgnoreCase);
    }

    private static readonly string[] RequiredMappings =
    [
        "INVENTORY", "PURCHASE_PAYABLE", "SALES_RECEIVABLE", "OPENING_BALANCE_EQUITY"
    ];

    public async Task<InitialSetupDto> GetAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        var setup = await ReadSetupAsync(connection, null, false, cancellationToken);
        return await BuildDtoAsync(connection, null, setup, cancellationToken);
    }

    public async Task<InitialSetupDto> SaveAsync(SaveInitialSetupRequest request, CancellationToken cancellationToken = default)
    {
        if (request.CutoffDate >= request.StartDate)
            throw new InitialSetupValidationException("Tanggal cutoff harus sebelum tanggal mulai pembukuan.");
        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            var setup = await ReadSetupAsync(connection, transaction, true, cancellationToken);
            EnsureDraft(setup);
            if (setup.Version != request.ExpectedVersion) throw new InitialSetupConflictException("Setup sudah berubah. Muat ulang lalu simpan kembali.");

            var mappings = await ReadMappingsAsync(connection, transaction, cancellationToken);
            var controlCodes = RequiredMappings.Take(3)
                .Select(x => mappings.GetValueOrDefault(x)).Where(x => !string.IsNullOrWhiteSpace(x))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            await ExecuteAsync(connection, transaction, "delete from initial_setup_inventory_draft where setup_id=@setup; delete from initial_setup_partner_draft where setup_id=@setup; delete from initial_setup_ledger_draft where setup_id=@setup;", cancellationToken, ("setup", setup.Id));

            foreach (var row in request.Inventory)
            {
                var warehouse = Code(row.WarehouseCode, "Gudang", 20);
                var item = Code(row.ItemCode, "Barang", 20);
                if (row.Quantity <= 0 || row.UnitCost < 0) throw new InitialSetupValidationException("Quantity stok awal harus lebih dari nol dan HPP tidak boleh negatif.");
                const string sql = """
                    insert into initial_setup_inventory_draft(setup_id,warehouse_code,item_id,quantity,unit_cost)
                    select @setup,@warehouse,id,@quantity,@cost from inventory_item where code=@item and is_active;
                    """;
                var count = await ExecuteAsync(connection, transaction, sql, cancellationToken, ("setup", setup.Id), ("warehouse", warehouse), ("item", item), ("quantity", row.Quantity), ("cost", row.UnitCost));
                if (count != 1) throw new InitialSetupValidationException($"Barang {item} tidak ditemukan atau tidak aktif.");
            }

            foreach (var row in request.Partners)
            {
                var type = Choice(row.PartnerType, "Jenis partner", "SUPPLIER", "CUSTOMER");
                var code = Code(row.PartnerCode, type == "SUPPLIER" ? "Supplier" : "Customer", 20);
                var number = Required(row.DocumentNumber, "Nomor dokumen saldo awal", 30);
                if (row.Amount <= 0) throw new InitialSetupValidationException("Saldo awal utang/piutang harus lebih dari nol.");
                var table = type == "SUPPLIER" ? "supplier" : "customer";
                var idColumn = type == "SUPPLIER" ? "supplier_id" : "customer_id";
                var sql = $"insert into initial_setup_partner_draft(setup_id,partner_type,{idColumn},document_number,due_date,amount,notes) select @setup,@type,id,@number,@due,@amount,@notes from {table} where code=@code and is_active;";
                await using var command = new NpgsqlCommand(sql, connection, transaction);
                command.Parameters.AddWithValue("setup", setup.Id); command.Parameters.AddWithValue("type", type);
                command.Parameters.AddWithValue("number", number); command.Parameters.AddWithValue("amount", row.Amount); command.Parameters.AddWithValue("code", code);
                AddNullable(command, "due", NpgsqlDbType.Date, row.DueDate); AddNullable(command, "notes", NpgsqlDbType.Text, Trim(row.Notes));
                if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) throw new InitialSetupValidationException($"{(type == "SUPPLIER" ? "Supplier" : "Customer")} {code} tidak ditemukan atau tidak aktif.");
            }

            foreach (var row in request.Ledger)
            {
                var code = Code(row.AccountCode, "Kode perkiraan", 20);
                if (controlCodes.Contains(code))
                    throw new InitialSetupValidationException($"Akun kontrol {code} tidak boleh dimasukkan ke jurnal awal manual karena dibentuk otomatis dari subledger.");
                if ((row.Debit <= 0 && row.Credit <= 0) || (row.Debit > 0 && row.Credit > 0))
                    throw new InitialSetupValidationException($"Baris akun {code} harus berisi debit atau kredit, bukan keduanya.");
                const string sql = """
                    insert into initial_setup_ledger_draft(setup_id,account_id,description,debit,credit)
                    select @setup,id,@description,@debit,@credit from ledger_account where code=@code and is_active and is_postable;
                    """;
                await using var command = new NpgsqlCommand(sql, connection, transaction);
                command.Parameters.AddWithValue("setup", setup.Id); command.Parameters.AddWithValue("code", code);
                AddNullable(command, "description", NpgsqlDbType.Text, Trim(row.Description));
                command.Parameters.AddWithValue("debit", Math.Round(row.Debit, 2)); command.Parameters.AddWithValue("credit", Math.Round(row.Credit, 2));
                if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) throw new InitialSetupValidationException($"Perkiraan {code} tidak ditemukan atau tidak dapat diposting.");
            }

            const string update = "update initial_setup set start_date=@start,cutoff_date=@cutoff,version=version+1 where singleton and version=@version;";
            if (await ExecuteAsync(connection, transaction, update, cancellationToken, ("start", request.StartDate), ("cutoff", request.CutoffDate), ("version", request.ExpectedVersion)) != 1)
                throw new InitialSetupConflictException("Setup sudah berubah. Muat ulang lalu simpan kembali.");
            await InsertAuditAsync(connection, transaction, setup.Id, "SAVE_DRAFT", request.UserName, request.ComputerName, null,
                new { request.StartDate, request.CutoffDate, Inventory = request.Inventory.Count, Partners = request.Partners.Count, Ledger = request.Ledger.Count }, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return await GetAsync(cancellationToken);
        }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.ForeignKeyViolation or PostgresErrorCodes.SerializationFailure)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new InitialSetupConflictException("Draft setup mengandung kode atau dokumen ganda, atau berubah bersamaan.");
        }
        catch { await transaction.RollbackAsync(cancellationToken); throw; }
    }

    public async Task<InitialSetupDto> ActivateAsync(ActivateInitialSetupRequest request, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            var setup = await ReadSetupAsync(connection, transaction, true, cancellationToken);
            EnsureDraft(setup);
            if (setup.Version != request.ExpectedVersion) throw new InitialSetupConflictException("Setup sudah berubah. Muat ulang sebelum aktivasi.");
            var errors = await ValidateAsync(connection, transaction, setup, cancellationToken);
            if (errors.Count > 0) throw new InitialSetupValidationException(string.Join(Environment.NewLine, errors));
            var mappings = await ReadMappingsAsync(connection, transaction, cancellationToken);
            var date = setup.StartDate!.Value;

            var inventory = await ReadInventoryAsync(connection, transaction, setup.Id, cancellationToken);
            foreach (var group in inventory.GroupBy(x => x.WarehouseCode, StringComparer.OrdinalIgnoreCase))
            {
                var adjustmentId = Guid.NewGuid();
                var number = await NextNumberAsync(connection, transaction, "SETUP_INVENTORY", "SAW", date, cancellationToken);
                const string header = "insert into inventory_adjustment(id,document_number,document_year,document_date,adjustment_type_code,warehouse_code,notes,created_by_name,computer_name) values(@id,@number,@year,@date,'SALDO_AWAL',@warehouse,'Aktivasi Setup Data Awal',@user,@computer);";
                await ExecuteAsync(connection, transaction, header, cancellationToken, ("id", adjustmentId), ("number", number), ("year", (short)date.Year), ("date", date), ("warehouse", group.Key), ("user", Db(request.UserName)), ("computer", Db(request.ComputerName)));
                var line = 0;
                foreach (var row in group)
                {
                    var lineId = Guid.NewGuid();
                    const string lineSql = "insert into inventory_adjustment_line(id,adjustment_id,line_number,item_id,quantity,unit,unit_cost) select @lineId,@id,@line,i.id,@quantity,i.unit,@cost from inventory_item i where i.code=@item;";
                    await ExecuteAsync(connection, transaction, lineSql, cancellationToken, ("lineId", lineId), ("id", adjustmentId), ("line", ++line), ("quantity", row.Quantity), ("cost", row.UnitCost), ("item", row.ItemCode));
                    const string balance = "insert into inventory_balance(item_id,warehouse_code,quantity) select id,@warehouse,@quantity from inventory_item where code=@item on conflict(item_id,warehouse_code) do update set quantity=inventory_balance.quantity+excluded.quantity,version=inventory_balance.version+1,updated_at=now();";
                    await ExecuteAsync(connection, transaction, balance, cancellationToken, ("warehouse", group.Key), ("quantity", row.Quantity), ("item", row.ItemCode));
                    const string movement = "insert into inventory_movement(occurred_at,item_id,warehouse_code,source_type,source_id,source_number,quantity_in,quantity_out,unit_cost) select @at,id,@warehouse,'INVENTORY_ADJUSTMENT',@source,@number,@quantity,0,@cost from inventory_item where code=@item;";
                    await ExecuteAsync(connection, transaction, movement, cancellationToken, ("at", new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)), ("warehouse", group.Key), ("source", adjustmentId), ("number", number), ("quantity", row.Quantity), ("cost", row.UnitCost), ("item", row.ItemCode));
                    const string layer = "insert into inventory_cost_layer(item_id,warehouse_code,received_at,source_type,source_id,source_line_id,source_number,original_quantity,remaining_quantity,unit_cost) select id,@warehouse,@at,'INITIAL_SETUP',@source,@lineId,@number,@quantity,@quantity,@cost from inventory_item where code=@item;";
                    await ExecuteAsync(connection, transaction, layer, cancellationToken, ("warehouse", group.Key), ("at", new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)), ("source", adjustmentId), ("lineId", lineId), ("number", number), ("quantity", row.Quantity), ("cost", row.UnitCost), ("item", row.ItemCode));
                }
                await TrackPostingAsync(connection, transaction, setup.Id, "INVENTORY_ADJUSTMENT", adjustmentId, number, cancellationToken);
            }
            await ExecuteAsync(connection, transaction, "update inventory_item i set average_cost=x.cost,version=i.version+1,updated_at=now() from (select item_id,sum(remaining_quantity*unit_cost)/nullif(sum(remaining_quantity),0) cost from inventory_cost_layer where remaining_quantity>0 group by item_id)x where i.id=x.item_id;", cancellationToken);

            var inventoryTotal = inventory.Sum(x => x.Quantity * x.UnitCost);
            if (inventoryTotal > 0)
                await InsertBalancedJournalAsync(connection, transaction, setup.Id, "INITIAL_SETUP_INVENTORY", "JSI", date, "Saldo awal persediaan", request.UserName, request.ComputerName,
                    [(mappings["INVENTORY"]!, inventoryTotal, 0m), (mappings["OPENING_BALANCE_EQUITY"]!, 0m, inventoryTotal)], cancellationToken);

            foreach (var row in await ReadPartnersAsync(connection, transaction, setup.Id, cancellationToken))
                await PostPartnerAsync(connection, transaction, setup, row, date, mappings, request, cancellationToken);

            var ledger = await ReadLedgerAsync(connection, transaction, setup.Id, cancellationToken);
            if (ledger.Count > 0)
                await InsertBalancedJournalAsync(connection, transaction, setup.Id, "INITIAL_SETUP_LEDGER", "JSA", date, "Jurnal awal akun lainnya", request.UserName, request.ComputerName,
                    ledger.Select(x => (x.AccountCode, x.Debit, x.Credit)).ToArray(), cancellationToken);

            const string activate = "update initial_setup set status='ACTIVE',activated_at=now(),activated_by_name=@user,activated_computer_name=@computer,version=version+1 where singleton and version=@version;";
            if (await ExecuteAsync(connection, transaction, activate, cancellationToken, ("user", Db(request.UserName)), ("computer", Db(request.ComputerName)), ("version", request.ExpectedVersion)) != 1)
                throw new InitialSetupConflictException("Setup berubah saat diaktifkan.");
            await InsertAuditAsync(connection, transaction, setup.Id, "ACTIVATE", request.UserName, request.ComputerName, null,
                new { Inventory = inventory.Count, Partners = (await ReadPartnersAsync(connection, transaction, setup.Id, cancellationToken)).Count, Ledger = ledger.Count }, cancellationToken);
            await LedgerRepository.InsertAuditAsync(connection, transaction, "INITIAL_SETUP", setup.Id.ToString(), "ACTIVATE", request.UserName, request.ComputerName, new { setup.StartDate, setup.CutoffDate }, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return await GetAsync(cancellationToken);
        }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.UniqueViolation)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new InitialSetupConflictException("Aktivasi berubah karena proses lain. Tidak ada saldo awal yang disimpan sebagian.");
        }
        catch { await transaction.RollbackAsync(cancellationToken); throw; }
    }

    public async Task<InitialSetupDto> ReopenAsync(ReopenInitialSetupRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Reason)) throw new InitialSetupValidationException("Alasan reopen wajib diisi.");
        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            var setup = await ReadSetupAsync(connection, transaction, true, cancellationToken);
            if (setup.Status != "ACTIVE") throw new InitialSetupConflictException("Setup belum aktif.");
            if (setup.LegacyProtected) throw new InitialSetupValidationException("Setup hasil migrasi perusahaan lama tidak dapat dibuka ulang. Gunakan transaksi koreksi yang diaudit.");
            if (setup.Version != request.ExpectedVersion) throw new InitialSetupConflictException("Setup sudah berubah. Muat ulang sebelum reopen.");
            if (await HasActivityAfterActivationAsync(connection, transaction, setup, cancellationToken))
                throw new InitialSetupValidationException("Setup tidak dapat dibuka ulang karena sudah ada transaksi setelah aktivasi. Gunakan transaksi koreksi.");

            var postings = await ReadPostingsAsync(connection, transaction, setup.Id, cancellationToken);
            foreach (var posting in postings.Where(x => x.Type == "INVENTORY_ADJUSTMENT"))
            {
                await ExecuteAsync(connection, transaction, "update inventory_balance b set quantity=b.quantity-m.quantity,version=b.version+1,updated_at=now() from (select item_id,warehouse_code,sum(quantity_in-quantity_out) quantity from inventory_movement where source_type='INVENTORY_ADJUSTMENT' and source_id=@id group by item_id,warehouse_code)m where b.item_id=m.item_id and b.warehouse_code=m.warehouse_code;", cancellationToken, ("id", posting.Id));
                await ExecuteAsync(connection, transaction, "delete from inventory_cost_layer where source_type='INITIAL_SETUP' and source_id=@id; delete from inventory_movement where source_type='INVENTORY_ADJUSTMENT' and source_id=@id; delete from inventory_adjustment where id=@id;", cancellationToken, ("id", posting.Id));
            }
            foreach (var posting in postings.Where(x => x.Type == "PARTNER_BALANCE"))
                await ExecuteAsync(connection, transaction, "delete from partner_balance_document where id=@id;", cancellationToken, ("id", posting.Id));
            foreach (var posting in postings.Where(x => x.Type == "JOURNAL_ENTRY"))
                await ExecuteAsync(connection, transaction, "delete from journal_entry where id=@id;", cancellationToken, ("id", posting.Id));
            await ExecuteAsync(connection, transaction, "update inventory_item i set average_cost=coalesce(x.cost,0),version=i.version+1,updated_at=now() from (select i2.id item_id,sum(l.remaining_quantity*l.unit_cost)/nullif(sum(l.remaining_quantity),0) cost from inventory_item i2 left join inventory_cost_layer l on l.item_id=i2.id and l.remaining_quantity>0 group by i2.id)x where i.id=x.item_id; delete from initial_setup_posting where setup_id=@setup;", cancellationToken, ("setup", setup.Id));
            const string reopen = "update initial_setup set status='DRAFT',activated_at=null,activated_by_name=null,activated_computer_name=null,version=version+1 where singleton and version=@version;";
            if (await ExecuteAsync(connection, transaction, reopen, cancellationToken, ("version", request.ExpectedVersion)) != 1) throw new InitialSetupConflictException("Setup berubah saat reopen.");
            await InsertAuditAsync(connection, transaction, setup.Id, "REOPEN", request.UserName, request.ComputerName, request.Reason.Trim(), null, cancellationToken);
            await LedgerRepository.InsertAuditAsync(connection, transaction, "INITIAL_SETUP", setup.Id.ToString(), "REOPEN", request.UserName, request.ComputerName, new { request.Reason }, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return await GetAsync(cancellationToken);
        }
        catch { await transaction.RollbackAsync(cancellationToken); throw; }
    }

    private async Task<InitialSetupDto> BuildDtoAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, SetupRow setup, CancellationToken cancellationToken)
    {
        var company = await CompanyIdentityRepository.ReadAsync(connection, transaction, cancellationToken);
        var inventory = await ReadInventoryAsync(connection, transaction, setup.Id, cancellationToken);
        var partners = await ReadPartnersAsync(connection, transaction, setup.Id, cancellationToken);
        var ledger = await ReadLedgerAsync(connection, transaction, setup.Id, cancellationToken);
        var errors = setup.Status == "ACTIVE" ? [] : await ValidateAsync(connection, transaction, setup, cancellationToken);
        var counts = await ReadMasterCountsAsync(connection, transaction, cancellationToken);
        var mappings = await ReadMappingsAsync(connection, transaction, cancellationToken);
        var profileValid = setup.StartDate is not null && setup.CutoffDate is not null && setup.CutoffDate < setup.StartDate && CompanyIdentityPolicy.IsSetupCompanyProfileReady(company.CompanyName, setup.ProfileConfirmed);
        var accountsValid = RequiredMappings.All(x => !string.IsNullOrWhiteSpace(mappings.GetValueOrDefault(x))) && counts.Accounts > 0;
        var mastersValid = counts.Warehouses > 0 && counts.Units > 0 && counts.Categories > 0 && counts.Items > 0 && counts.CashBanks > 0;
        string State(bool valid) => setup.Status == "ACTIVE" ? "Selesai" : valid ? "Valid" : setup.StartDate is null ? "Draft" : "Perlu dilengkapi";
        var stages = new List<InitialSetupStageDto>
        {
            new("PROFILE","Profil & tanggal mulai",State(profileValid),profileValid ? $"Mulai {setup.StartDate:dd/MM/yyyy}; cutoff {setup.CutoffDate:dd/MM/yyyy}." : "Buka Profil Perusahaan, isi atau konfirmasi nama final, lalu lengkapi tanggal pembukuan.","PROGRAM_CONFIGURATION"),
            new("ACCOUNTS","Perkiraan & pemetaan jurnal",State(accountsValid),$"{counts.Accounts} perkiraan; {mappings.Count(x=>!string.IsNullOrWhiteSpace(x.Value))}/{RequiredMappings.Length} mapping inti terisi.","LEDGER_MAPPING"),
            new("MASTERS","Master data",State(mastersValid),$"{counts.Warehouses} gudang, {counts.Units} satuan, {counts.Categories} kelompok, {counts.Items} barang, {counts.Partners} partner.","MASTER_DATA"),
            new("OPENING","Saldo awal",State(errors.Count==0 && profileValid),$"{inventory.Count} stok, {partners.Count} utang/piutang, {ledger.Count} akun jurnal awal.",null),
            new("PREFLIGHT","Rekonsiliasi / preflight",State(errors.Count==0),errors.Count==0 ? "Tidak ditemukan masalah rekonsiliasi." : $"{errors.Count} masalah harus diselesaikan.",null),
            new("ACTIVATE","Aktivasi setup",setup.Status=="ACTIVE"?"Selesai":errors.Count==0?"Valid":"Perlu dilengkapi",setup.Status=="ACTIVE" ? $"Aktif {setup.ActivatedAt:dd/MM/yyyy HH:mm}." : "Aktivasi memposting seluruh saldo awal secara atomik.",null)
        };
        return new(setup.Id, setup.Status, setup.StartDate, setup.CutoffDate, setup.LegacyProtected, setup.ActivatedAt, setup.ActivatedByName, setup.Version, company, stages, errors, inventory, partners, ledger);
    }

    private async Task<List<string>> ValidateAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, SetupRow setup, CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        var company = await CompanyIdentityRepository.ReadAsync(connection, transaction, cancellationToken);
        if (!CompanyIdentityPolicy.IsFinalCompanyName(company.CompanyName))
            errors.Add("Nama perusahaan final wajib diisi pada Profil Perusahaan sebelum setup dapat diaktifkan.");
        else if (!setup.ProfileConfirmed)
            errors.Add("Nama perusahaan final wajib dikonfirmasi dengan menyimpan Profil Perusahaan sebelum setup dapat diaktifkan.");
        if (setup.StartDate is null || setup.CutoffDate is null) errors.Add("Tanggal mulai dan cutoff wajib diisi.");
        else if (setup.CutoffDate >= setup.StartDate) errors.Add("Tanggal cutoff harus sebelum tanggal mulai.");
        var mappings = await ReadMappingsAsync(connection, transaction, cancellationToken);
        foreach (var key in RequiredMappings) if (string.IsNullOrWhiteSpace(mappings.GetValueOrDefault(key))) errors.Add($"Mapping jurnal {key} belum diisi.");
        var counts = await ReadMasterCountsAsync(connection, transaction, cancellationToken);
        if (counts.Warehouses == 0) errors.Add("Minimal satu gudang aktif wajib tersedia.");
        if (counts.Units == 0) errors.Add("Minimal satu satuan aktif wajib tersedia.");
        if (counts.Categories == 0) errors.Add("Minimal satu kelompok barang aktif wajib tersedia.");
        if (counts.Items == 0) errors.Add("Minimal satu barang aktif wajib tersedia.");
        if (counts.CashBanks == 0) errors.Add("Minimal satu kas atau bank aktif wajib tersedia.");
        var ledger = await ReadLedgerAsync(connection, transaction, setup.Id, cancellationToken);
        if (Math.Round(ledger.Sum(x => x.Debit),2) != Math.Round(ledger.Sum(x => x.Credit),2)) errors.Add("Jurnal awal akun lainnya belum seimbang.");
        if (setup.Status == "DRAFT" && await HasExistingOperationalDataAsync(connection, transaction, cancellationToken)) errors.Add("Database sudah memiliki transaksi operasional. Setup tidak boleh diaktifkan untuk mencegah saldo ganda.");
        if (await HasNonZeroDraftStockAsync(connection, transaction, setup.Id, cancellationToken)) errors.Add("Terdapat barang/gudang draft yang sudah memiliki stok. Gunakan penyesuaian persediaan, bukan saldo awal.");
        if (await HasDuplicatePartnerDocumentAsync(connection, transaction, setup.Id, setup.StartDate, cancellationToken)) errors.Add("Nomor saldo awal utang/piutang sudah digunakan.");
        return errors.Distinct().ToList();
    }

    private static async Task PostPartnerAsync(NpgsqlConnection c, NpgsqlTransaction t, SetupRow setup, InitialSetupPartnerDraftDto row, DateOnly date, Dictionary<string,string?> mappings, ActivateInitialSetupRequest request, CancellationToken ct)
    {
        var id = Guid.NewGuid(); var supplier = row.PartnerType == "SUPPLIER";
        var journalNumber = await NextNumberAsync(c,t,supplier?"SUPPLIER_OPENING_JRN":"CUSTOMER_OPENING_JRN",supplier?"JUAW":"JPAW",date,ct);
        var module = supplier ? "SUPPLIER_OPENING" : "CUSTOMER_OPENING";
        var memo = $"{(supplier?"Utang":"Piutang")} awal {row.PartnerCode} - {row.PartnerName}";
        var control = mappings[supplier?"PURCHASE_PAYABLE":"SALES_RECEIVABLE"]!; var equity=mappings["OPENING_BALANCE_EQUITY"]!;
        var journal = await InsertJournalHeaderAsync(c,t,journalNumber,date,memo,module,id,request.UserName,request.ComputerName,ct);
        var lines = supplier ? new[] { (equity,row.Amount,0m),(control,0m,row.Amount) } : new[] { (control,row.Amount,0m),(equity,0m,row.Amount) };
        await InsertJournalLinesAsync(c,t,journal,memo,lines,ct);
        const string sql="""
            insert into partner_balance_document(id,partner_type,document_kind,direction,document_number,document_year,document_date,supplier_id,customer_id,due_date,amount,notes,journal_entry_id,created_by_name,computer_name)
            select @id,@type,'OPENING','INCREASE',@number,@year,@date,
                   case when @type='SUPPLIER' then p.id end,case when @type='CUSTOMER' then p.id end,@due,@amount,@notes,@journal,@user,@computer
            from (select id from supplier where @type='SUPPLIER' and code=@code union all select id from customer where @type='CUSTOMER' and code=@code)p;
            """;
        await using var command=new NpgsqlCommand(sql,c,t);command.Parameters.AddWithValue("id",id);command.Parameters.AddWithValue("type",row.PartnerType);command.Parameters.AddWithValue("number",row.DocumentNumber);command.Parameters.AddWithValue("year",(short)date.Year);command.Parameters.AddWithValue("date",date);command.Parameters.AddWithValue("amount",row.Amount);command.Parameters.AddWithValue("journal",journal);command.Parameters.AddWithValue("user",Db(request.UserName));command.Parameters.AddWithValue("computer",Db(request.ComputerName));command.Parameters.AddWithValue("code",row.PartnerCode);AddNullable(command,"due",NpgsqlDbType.Date,row.DueDate);AddNullable(command,"notes",NpgsqlDbType.Text,Trim(row.Notes));await command.ExecuteNonQueryAsync(ct);
        await TrackPostingAsync(c,t,setup.Id,"PARTNER_BALANCE",id,row.DocumentNumber,ct);await TrackPostingAsync(c,t,setup.Id,"JOURNAL_ENTRY",journal,journalNumber,ct);
    }

    private static async Task<Guid> InsertBalancedJournalAsync(NpgsqlConnection c,NpgsqlTransaction t,Guid setupId,string module,string prefix,DateOnly date,string memo,string user,string computer,IReadOnlyList<(string Account,decimal Debit,decimal Credit)> lines,CancellationToken ct)
    {var number=await NextNumberAsync(c,t,module,prefix,date,ct);var id=await InsertJournalHeaderAsync(c,t,number,date,memo,module,setupId,user,computer,ct);await InsertJournalLinesAsync(c,t,id,memo,lines,ct);await TrackPostingAsync(c,t,setupId,"JOURNAL_ENTRY",id,number,ct);return id;}
    private static async Task<Guid> InsertJournalHeaderAsync(NpgsqlConnection c,NpgsqlTransaction t,string number,DateOnly date,string memo,string module,Guid source,string user,string computer,CancellationToken ct)
    {const string sql="insert into journal_entry(document_number,document_year,document_date,memo,source_module,source_id,created_by_name,computer_name) values(@number,@year,@date,@memo,@module,@source,@user,@computer) returning id;";await using var cmd=new NpgsqlCommand(sql,c,t);cmd.Parameters.AddWithValue("number",number);cmd.Parameters.AddWithValue("year",(short)date.Year);cmd.Parameters.AddWithValue("date",date);cmd.Parameters.AddWithValue("memo",memo);cmd.Parameters.AddWithValue("module",module);cmd.Parameters.AddWithValue("source",source);cmd.Parameters.AddWithValue("user",Db(user));cmd.Parameters.AddWithValue("computer",Db(computer));return (Guid)(await cmd.ExecuteScalarAsync(ct))!;}
    private static async Task InsertJournalLinesAsync(NpgsqlConnection c,NpgsqlTransaction t,Guid journal,string memo,IReadOnlyList<(string Account,decimal Debit,decimal Credit)> lines,CancellationToken ct)
    {if(Math.Round(lines.Sum(x=>x.Debit),2)!=Math.Round(lines.Sum(x=>x.Credit),2))throw new InitialSetupValidationException("Jurnal setup tidak seimbang.");var n=0;foreach(var x in lines){const string sql="insert into journal_entry_line(journal_entry_id,line_number,account_id,description,debit,credit) select @journal,@line,id,@memo,@debit,@credit from ledger_account where code=@account and is_active and is_postable;";if(await ExecuteAsync(c,t,sql,ct,("journal",journal),("line",++n),("memo",memo),("debit",x.Debit),("credit",x.Credit),("account",x.Account))!=1)throw new InitialSetupValidationException($"Perkiraan {x.Account} tidak tersedia.");}}
    private static async Task<string> NextNumberAsync(NpgsqlConnection c,NpgsqlTransaction t,string type,string prefix,DateOnly date,CancellationToken ct){const string sql="insert into document_sequence(document_type,period,last_number) values(@type,@period,1) on conflict(document_type,period) do update set last_number=document_sequence.last_number+1 returning last_number;";await using var cmd=new NpgsqlCommand(sql,c,t);cmd.Parameters.AddWithValue("type",type);cmd.Parameters.AddWithValue("period",date.ToString("yyyyMM"));var n=Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));return $"{prefix}{date:yyMM}{n:0000}";}

    private static async Task<SetupRow> ReadSetupAsync(NpgsqlConnection c,NpgsqlTransaction? t,bool locked,CancellationToken ct){var sql="select id,status,start_date,cutoff_date,legacy_protected,activated_at,activated_by_name,profile_confirmed,version from initial_setup where singleton"+(locked?" for update":"")+";";await using var cmd=new NpgsqlCommand(sql,c,t);await using var r=await cmd.ExecuteReaderAsync(ct);if(!await r.ReadAsync(ct))throw new InvalidOperationException("Setup Data Awal belum tersedia.");return new(r.GetGuid(0),r.GetString(1),r.IsDBNull(2)?null:r.GetFieldValue<DateOnly>(2),r.IsDBNull(3)?null:r.GetFieldValue<DateOnly>(3),r.GetBoolean(4),r.IsDBNull(5)?null:r.GetFieldValue<DateTimeOffset>(5),r.IsDBNull(6)?null:r.GetString(6),r.GetBoolean(7),r.GetInt64(8));}
    private static async Task<List<InitialSetupInventoryDraftDto>> ReadInventoryAsync(NpgsqlConnection c,NpgsqlTransaction? t,Guid setup,CancellationToken ct){const string sql="select d.id,d.warehouse_code,i.code,i.name,i.unit,d.quantity,d.unit_cost from initial_setup_inventory_draft d join inventory_item i on i.id=d.item_id where d.setup_id=@setup order by d.warehouse_code,i.code;";var rows=new List<InitialSetupInventoryDraftDto>();await using var cmd=new NpgsqlCommand(sql,c,t);cmd.Parameters.AddWithValue("setup",setup);await using var r=await cmd.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct))rows.Add(new(r.GetGuid(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),r.GetDecimal(5),r.GetDecimal(6)));return rows;}
    private static async Task<List<InitialSetupPartnerDraftDto>> ReadPartnersAsync(NpgsqlConnection c,NpgsqlTransaction? t,Guid setup,CancellationToken ct){const string sql="select d.id,d.partner_type,coalesce(s.code,c.code),coalesce(s.company_name,c.company_name),d.document_number,d.due_date,d.amount,d.notes from initial_setup_partner_draft d left join supplier s on s.id=d.supplier_id left join customer c on c.id=d.customer_id where d.setup_id=@setup order by d.partner_type,d.document_number;";var rows=new List<InitialSetupPartnerDraftDto>();await using var cmd=new NpgsqlCommand(sql,c,t);cmd.Parameters.AddWithValue("setup",setup);await using var r=await cmd.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct))rows.Add(new(r.GetGuid(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),r.IsDBNull(5)?null:r.GetFieldValue<DateOnly>(5),r.GetDecimal(6),r.IsDBNull(7)?null:r.GetString(7)));return rows;}
    private static async Task<List<InitialSetupLedgerDraftDto>> ReadLedgerAsync(NpgsqlConnection c,NpgsqlTransaction? t,Guid setup,CancellationToken ct){const string sql="select d.id,a.code,a.name,d.description,d.debit,d.credit from initial_setup_ledger_draft d join ledger_account a on a.id=d.account_id where d.setup_id=@setup order by a.code;";var rows=new List<InitialSetupLedgerDraftDto>();await using var cmd=new NpgsqlCommand(sql,c,t);cmd.Parameters.AddWithValue("setup",setup);await using var r=await cmd.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct))rows.Add(new(r.GetGuid(0),r.GetString(1),r.GetString(2),r.IsDBNull(3)?null:r.GetString(3),r.GetDecimal(4),r.GetDecimal(5)));return rows;}
    private static async Task<Dictionary<string,string?>> ReadMappingsAsync(NpgsqlConnection c,NpgsqlTransaction? t,CancellationToken ct){var result=new Dictionary<string,string?>(StringComparer.OrdinalIgnoreCase);await using var cmd=new NpgsqlCommand("select mapping_code,account_code from ledger_transaction_mapping where mapping_code=any(@codes);",c,t);cmd.Parameters.AddWithValue("codes",RequiredMappings);await using var r=await cmd.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct))result[r.GetString(0)]=r.IsDBNull(1)?null:r.GetString(1);return result;}
    private static async Task<MasterCounts> ReadMasterCountsAsync(NpgsqlConnection c,NpgsqlTransaction? t,CancellationToken ct){const string sql="select (select count(*) from warehouse where is_active),(select count(*) from inventory_unit),(select count(*) from inventory_category where is_active),(select count(*) from inventory_item where is_active),(select count(*) from supplier where is_active)+(select count(*) from customer where is_active),(select count(*) from cash_account where is_active)+(select count(*) from bank_account where is_active),(select count(*) from ledger_account where is_active and is_postable);";await using var cmd=new NpgsqlCommand(sql,c,t);await using var r=await cmd.ExecuteReaderAsync(ct);await r.ReadAsync(ct);return new(r.GetInt64(0),r.GetInt64(1),r.GetInt64(2),r.GetInt64(3),r.GetInt64(4),r.GetInt64(5),r.GetInt64(6));}
    private static async Task<bool> HasExistingOperationalDataAsync(NpgsqlConnection c,NpgsqlTransaction? t,CancellationToken ct){const string sql="select exists(select 1 from journal_entry where source_module not like 'INITIAL_SETUP%') or exists(select 1 from inventory_movement where source_type<>'INITIAL_SETUP') or exists(select 1 from purchase) or exists(select 1 from sale) or exists(select 1 from partner_balance_document) or exists(select 1 from cash_bank_transaction);";await using var cmd=new NpgsqlCommand(sql,c,t);return (bool)(await cmd.ExecuteScalarAsync(ct))!;}
    private static async Task<bool> HasNonZeroDraftStockAsync(NpgsqlConnection c,NpgsqlTransaction? t,Guid setup,CancellationToken ct){const string sql="select exists(select 1 from initial_setup_inventory_draft d join inventory_balance b on b.item_id=d.item_id and b.warehouse_code=d.warehouse_code where d.setup_id=@setup and b.quantity<>0);";await using var cmd=new NpgsqlCommand(sql,c,t);cmd.Parameters.AddWithValue("setup",setup);return (bool)(await cmd.ExecuteScalarAsync(ct))!;}
    private static async Task<bool> HasDuplicatePartnerDocumentAsync(NpgsqlConnection c,NpgsqlTransaction? t,Guid setup,DateOnly? date,CancellationToken ct){if(date is null)return false;const string sql="select exists(select 1 from initial_setup_partner_draft d join partner_balance_document p on p.document_number=d.document_number and p.document_year=@year where d.setup_id=@setup);";await using var cmd=new NpgsqlCommand(sql,c,t);cmd.Parameters.AddWithValue("setup",setup);cmd.Parameters.AddWithValue("year",(short)date.Value.Year);return (bool)(await cmd.ExecuteScalarAsync(ct))!;}
    private static async Task<bool> HasActivityAfterActivationAsync(NpgsqlConnection c,NpgsqlTransaction t,SetupRow setup,CancellationToken ct){const string sql="select exists(select 1 from audit_log where occurred_at>@at and module<>'INITIAL_SETUP') or exists(select 1 from journal_entry where created_at>@at and source_module not like 'INITIAL_SETUP%');";await using var cmd=new NpgsqlCommand(sql,c,t);cmd.Parameters.AddWithValue("at",setup.ActivatedAt??DateTimeOffset.MinValue);return (bool)(await cmd.ExecuteScalarAsync(ct))!;}
    private static async Task<List<PostingRow>> ReadPostingsAsync(NpgsqlConnection c,NpgsqlTransaction t,Guid setup,CancellationToken ct){var rows=new List<PostingRow>();await using var cmd=new NpgsqlCommand("select entity_type,entity_id from initial_setup_posting where setup_id=@setup order by id desc;",c,t);cmd.Parameters.AddWithValue("setup",setup);await using var r=await cmd.ExecuteReaderAsync(ct);while(await r.ReadAsync(ct))rows.Add(new(r.GetString(0),r.GetGuid(1)));return rows;}
    private static async Task TrackPostingAsync(NpgsqlConnection c,NpgsqlTransaction t,Guid setup,string type,Guid id,string? number,CancellationToken ct)=>await ExecuteAsync(c,t,"insert into initial_setup_posting(setup_id,entity_type,entity_id,entity_number) values(@setup,@type,@id,@number);",ct,("setup",setup),("type",type),("id",id),("number",Db(number)));
    private static async Task InsertAuditAsync(NpgsqlConnection c,NpgsqlTransaction t,Guid setup,string action,string user,string computer,string? reason,object? snapshot,CancellationToken ct){const string sql="insert into initial_setup_audit(setup_id,action,user_name,computer_name,reason,snapshot) values(@setup,@action,@user,@computer,@reason,@snapshot);";await using var cmd=new NpgsqlCommand(sql,c,t);cmd.Parameters.AddWithValue("setup",setup);cmd.Parameters.AddWithValue("action",action);cmd.Parameters.AddWithValue("user",Db(user));cmd.Parameters.AddWithValue("computer",Db(computer));cmd.Parameters.AddWithValue("reason",Db(reason));cmd.Parameters.Add(new NpgsqlParameter("snapshot",NpgsqlDbType.Jsonb){Value=snapshot is null?DBNull.Value:JsonSerializer.Serialize(snapshot)});await cmd.ExecuteNonQueryAsync(ct);}
    private static async Task<int> ExecuteAsync(NpgsqlConnection c,NpgsqlTransaction? t,string sql,CancellationToken ct,params (string Name,object? Value)[] values){await using var cmd=new NpgsqlCommand(sql,c,t);foreach(var x in values)cmd.Parameters.AddWithValue(x.Name,x.Value??DBNull.Value);return await cmd.ExecuteNonQueryAsync(ct);}
    private static void AddNullable(NpgsqlCommand c,string name,NpgsqlDbType type,object? value)=>c.Parameters.Add(new NpgsqlParameter(name,type){Value=value??DBNull.Value});
    private static string Choice(string? value,string label,params string[] choices){var v=value?.Trim().ToUpperInvariant()??"";return choices.Contains(v)?v:throw new InitialSetupValidationException($"{label} tidak valid.");}
    private static string Code(string? value,string label,int max)=>Required(value,label,max).ToUpperInvariant();
    private static string Required(string? value,string label,int max){var v=value?.Trim()??"";if(v.Length==0)throw new InitialSetupValidationException($"{label} wajib diisi.");if(v.Length>max)throw new InitialSetupValidationException($"{label} maksimal {max} karakter.");return v;}
    private static string? Trim(string? value)=>string.IsNullOrWhiteSpace(value)?null:value.Trim();
    private static object Db(string? value)=>string.IsNullOrWhiteSpace(value)?DBNull.Value:value.Trim();
    private static void EnsureDraft(SetupRow setup){if(setup.LegacyProtected)throw new InitialSetupValidationException("Data perusahaan lama dilindungi. Gunakan transaksi koreksi, bukan Setup Data Awal.");if(setup.Status!="DRAFT")throw new InitialSetupConflictException("Setup Data Awal sudah aktif dan draft dikunci.");}
    private sealed record SetupRow(Guid Id,string Status,DateOnly? StartDate,DateOnly? CutoffDate,bool LegacyProtected,DateTimeOffset? ActivatedAt,string? ActivatedByName,bool ProfileConfirmed,long Version);
    private sealed record MasterCounts(long Warehouses,long Units,long Categories,long Items,long Partners,long CashBanks,long Accounts);
    private sealed record PostingRow(string Type,Guid Id);
}
