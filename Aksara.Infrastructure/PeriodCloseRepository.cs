using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aksara.Contracts;
using Aksara.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Aksara.Infrastructure;

public sealed class PeriodCloseValidationException(string message) : InvalidOperationException(message);
public sealed class PeriodCloseConflictException(string message) : InvalidOperationException(message);

public sealed class PeriodCloseRepository(PostgresDatabase database)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<PeriodCloseDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            select id,period_name,cutoff_date,opening_date,status,created_by_name,computer_name,created_at,
                   backup_location,backup_file_name,snapshot_sha256,
                   archive_location,archive_file_name,archive_sha256,summary_json::text,
                   status='POSTED' and id=(
                       select id from accounting_period_close
                       where status='POSTED'
                       order by cutoff_date desc,created_at desc,id desc limit 1
                   ) can_reopen,
                   reopened_by_name,reopen_computer_name,reopened_at,reopen_reason
            from accounting_period_close
            where status in ('POSTED','REOPENED')
            order by cutoff_date desc,created_at desc;
            """;
        await using var command = database.DataSource.CreateCommand(sql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<PeriodCloseDto>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadClose(reader));
        return result;
    }

    public async Task<PeriodClosePreflightDto> GetPreflightAsync(PeriodClosePreflightRequest request, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        var prepared = await PrepareAsync(connection, null, request, cancellationToken);
        return prepared.Preflight;
    }

    public async Task<PeriodCloseDto> CloseAsync(
        CreatePeriodCloseRequest request,
        string userName,
        CancellationToken cancellationToken = default)
    {
        var periodName = ValidateRequest(request.CutoffDate, request.OpeningDate, request.PeriodName);
        var backupLocation = Required(request.BackupLocation, "Lokasi backup total", 500);
        var archiveLocation = Required(request.ArchiveLocation, "Lokasi arsip sampai cutoff", 500);
        var idempotencyKey = Required(request.IdempotencyKey, "Identitas permintaan", 80);
        var actor = Required(userName, "Pengguna", 80);

        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            await ExecuteAsync(connection, transaction, "select pg_advisory_xact_lock(hashtext('AKSARA_PERIOD_CLOSE'));", cancellationToken);
            await LockMutationTablesAsync(connection, transaction, cancellationToken);
            var existing = await ReadByIdempotencyAsync(connection, transaction, idempotencyKey, cancellationToken);
            if (existing is not null)
            {
                if (existing.CutoffDate != request.CutoffDate || existing.OpeningDate != request.OpeningDate ||
                    !existing.PeriodName.Equals(periodName, StringComparison.OrdinalIgnoreCase))
                    throw new PeriodCloseConflictException("Identitas permintaan sudah digunakan untuk periode yang berbeda.");
                await transaction.CommitAsync(cancellationToken);
                return existing;
            }

            var prepared = await PrepareAsync(connection, transaction, new(request.CutoffDate, request.OpeningDate, periodName), cancellationToken);
            if (!prepared.Preflight.CanClose)
                throw new PeriodCloseValidationException(string.Join(Environment.NewLine, prepared.Preflight.Blockers));

            var closeId = Guid.NewGuid();
            const string insertClose = """
                insert into accounting_period_close(
                    id,period_name,cutoff_date,opening_date,status,created_by_name,computer_name,
                    backup_location,archive_location,idempotency_key)
                values(@id,@name,@cutoff,@opening,'PROCESSING',@user,@computer,@location,@archiveLocation,@key);
                """;
            await using (var command = new NpgsqlCommand(insertClose, connection, transaction))
            {
                command.Parameters.AddWithValue("id", closeId);
                command.Parameters.AddWithValue("name", periodName);
                command.Parameters.AddWithValue("cutoff", request.CutoffDate);
                command.Parameters.AddWithValue("opening", request.OpeningDate);
                command.Parameters.AddWithValue("user", actor);
                command.Parameters.AddWithValue("computer", Db(request.ComputerName));
                command.Parameters.AddWithValue("location", backupLocation);
                command.Parameters.AddWithValue("archiveLocation", archiveLocation);
                command.Parameters.AddWithValue("key", idempotencyKey);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await InsertLedgerBalancesAsync(connection, transaction, closeId, request.CutoffDate, cancellationToken);
            await InsertCashBankBalancesAsync(connection, transaction, closeId, cancellationToken);
            await InsertPartnerBalancesAsync(connection, transaction, closeId, request.CutoffDate, cancellationToken);
            await InsertInventorySnapshotAsync(connection, transaction, closeId, prepared.Inventory, cancellationToken);

            var storedSummary = new StoredSummary(prepared.Preflight.Documents, prepared.Preflight.CarryForward);
            var summaryJson = JsonSerializer.Serialize(storedSummary, JsonOptions);
            var backupFileName = BuildBackupFileName(request.CutoffDate, periodName, closeId);
            var archiveFileName = BuildArchiveFileName(request.CutoffDate, periodName, closeId);
            var archivePayload = await BuildArchivePayloadAsync(
                connection, transaction, closeId, request.CutoffDate, archiveFileName, actor, request.ComputerName, cancellationToken);
            var archiveHash = Hash(archivePayload);
            var exportPayload = await BuildExportPayloadAsync(
                connection, transaction, closeId, summaryJson, backupFileName, archiveFileName,
                archivePayload, actor, request.ComputerName, cancellationToken);
            var snapshotHash = Hash(exportPayload);

            const string finish = """
                update accounting_period_close set
                    status='POSTED',backup_file_name=@file,snapshot_sha256=@hash,
                    archive_file_name=@archiveFile,archive_sha256=@archiveHash,
                    summary_json=@summary,export_payload=@payload,archive_payload=@archivePayload
                where id=@id and status='PROCESSING';
                """;
            await using (var command = new NpgsqlCommand(finish, connection, transaction))
            {
                command.Parameters.AddWithValue("id", closeId);
                command.Parameters.AddWithValue("file", backupFileName);
                command.Parameters.AddWithValue("hash", snapshotHash);
                command.Parameters.AddWithValue("archiveFile", archiveFileName);
                command.Parameters.AddWithValue("archiveHash", archiveHash);
                command.Parameters.Add(new NpgsqlParameter("summary", NpgsqlDbType.Jsonb) { Value = summaryJson });
                command.Parameters.AddWithValue("payload", exportPayload);
                command.Parameters.AddWithValue("archivePayload", archivePayload);
                if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
                    throw new PeriodCloseConflictException("Proses Tutup Buku berubah sebelum selesai.");
            }

            const string audit = """
                insert into audit_log(computer_name,module,action,entity_type,entity_id,after_data)
                values(@computer,'MAINTENANCE','PERIOD_CLOSE','ACCOUNTING_PERIOD_CLOSE',@id,@data);
                """;
            await using (var command = new NpgsqlCommand(audit, connection, transaction))
            {
                command.Parameters.AddWithValue("computer", Db(request.ComputerName));
                command.Parameters.AddWithValue("id", closeId.ToString());
                command.Parameters.Add(new NpgsqlParameter("data", NpgsqlDbType.Jsonb)
                {
                    Value = JsonSerializer.Serialize(new
                    {
                        userName = actor,
                        periodName,
                        request.CutoffDate,
                        request.OpeningDate,
                        backupLocation,
                        archiveLocation,
                        backupFileName,
                        archiveFileName,
                        snapshotHash,
                        archiveHash,
                        summary = storedSummary
                    }, JsonOptions)
                });
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return (await GetAsync(closeId, cancellationToken))!;
        }
        catch (PostgresException exception) when (exception.SqlState is PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.SerializationFailure)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new PeriodCloseConflictException("Periode sudah ditutup atau berubah saat diproses. Muat ulang histori Tutup Buku.");
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<PeriodCloseDto> ReopenAsync(
        Guid id,
        ReopenPeriodCloseRequest request,
        string userName,
        CancellationToken cancellationToken = default)
    {
        var actor = Required(userName, "Pengguna", 80);
        var computerName = Optional(request.ComputerName, 100);
        var reason = Optional(request.Reason, 300) ?? "Dibatalkan melalui form Tutup Buku.";
        var reopenedAt = DateTimeOffset.UtcNow;

        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            await ExecuteAsync(connection, transaction, "select pg_advisory_xact_lock(hashtext('AKSARA_PERIOD_CLOSE'));", cancellationToken);

            string? selectedStatus;
            await using (var selectedCommand = new NpgsqlCommand(
                "select status from accounting_period_close where id=@id for update;", connection, transaction))
            {
                selectedCommand.Parameters.AddWithValue("id", id);
                selectedStatus = (await selectedCommand.ExecuteScalarAsync(cancellationToken))?.ToString();
            }
            if (selectedStatus is null)
                throw new PeriodCloseValidationException("Histori Tutup Buku tidak ditemukan.");

            Guid? latestPostedId = null;
            await using (var latestCommand = new NpgsqlCommand("""
                select id from accounting_period_close
                where status='POSTED'
                order by cutoff_date desc,created_at desc,id desc
                limit 1 for update;
                """, connection, transaction))
            {
                var value = await latestCommand.ExecuteScalarAsync(cancellationToken);
                if (value is Guid latestId) latestPostedId = latestId;
            }

            try { PeriodCloseRules.ValidateReopenSelection(id, selectedStatus, latestPostedId); }
            catch (InvalidOperationException exception) { throw new PeriodCloseConflictException(exception.Message); }

            await using (var bypassCommand = new NpgsqlCommand(
                "select set_config('aksara.period_close_reopen_id',@id,true);", connection, transaction))
            {
                bypassCommand.Parameters.AddWithValue("id", id.ToString());
                await bypassCommand.ExecuteScalarAsync(cancellationToken);
            }

            const string reopenSql = """
                update accounting_period_close set
                    status='REOPENED',reopened_by_name=@user,reopen_computer_name=@computer,
                    reopened_at=@reopenedAt,reopen_reason=@reason
                where id=@id and status='POSTED';
                """;
            await using (var command = new NpgsqlCommand(reopenSql, connection, transaction))
            {
                command.Parameters.AddWithValue("id", id);
                command.Parameters.AddWithValue("user", actor);
                command.Parameters.AddWithValue("computer", Db(computerName));
                command.Parameters.AddWithValue("reopenedAt", reopenedAt);
                command.Parameters.AddWithValue("reason", reason);
                if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
                    throw new PeriodCloseConflictException("Histori Tutup Buku berubah sebelum pembatalan selesai.");
            }

            DateOnly? activeCutoff = null;
            await using (var cutoffCommand = new NpgsqlCommand(
                "select max(cutoff_date) from accounting_period_close where status='POSTED';", connection, transaction))
            {
                var value = await cutoffCommand.ExecuteScalarAsync(cancellationToken);
                if (value is DateOnly cutoff) activeCutoff = cutoff;
                else if (value is DateTime date) activeCutoff = DateOnly.FromDateTime(date);
            }

            const string audit = """
                insert into audit_log(computer_name,module,action,entity_type,entity_id,after_data)
                values(@computer,'MAINTENANCE','REOPEN_PERIOD_CLOSE','ACCOUNTING_PERIOD_CLOSE',@id,@data);
                """;
            await using (var command = new NpgsqlCommand(audit, connection, transaction))
            {
                command.Parameters.AddWithValue("computer", Db(computerName));
                command.Parameters.AddWithValue("id", id.ToString());
                command.Parameters.Add(new NpgsqlParameter("data", NpgsqlDbType.Jsonb)
                {
                    Value = JsonSerializer.Serialize(new
                    {
                        userName = actor,
                        reason,
                        reopenedAt,
                        activeCutoff
                    }, JsonOptions)
                });
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return (await GetAsync(id, cancellationToken))!;
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.SerializationFailure)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new PeriodCloseConflictException("Histori Tutup Buku berubah saat diproses. Muat ulang lalu coba kembali.");
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<PeriodCloseExportBundleDto> GetExportAsync(Guid id, CancellationToken cancellationToken = default)
    {
        const string sql = """
            select backup_file_name,snapshot_sha256,export_payload,
                   archive_file_name,archive_sha256,archive_payload
            from accounting_period_close
            where id=@id and status in ('POSTED','REOPENED');
            """;
        await using var command = database.DataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new PeriodCloseValidationException("Histori Tutup Buku tidak ditemukan.");
        var fileName = reader.GetString(0);
        var expectedHash = reader.GetString(1).Trim();
        var content = reader.GetString(2);
        var actualHash = Hash(content);
        if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new PeriodCloseConflictException("Hash snapshot Tutup Buku tidak cocok. Ekspor dihentikan untuk menjaga audit.");
        var archiveFileName = reader.GetString(3);
        var expectedArchiveHash = reader.GetString(4).Trim();
        var archiveContent = reader.GetString(5);
        var actualArchiveHash = Hash(archiveContent);
        if (!actualArchiveHash.Equals(expectedArchiveHash, StringComparison.OrdinalIgnoreCase))
            throw new PeriodCloseConflictException("Hash arsip transaksi Tutup Buku tidak cocok. Ekspor dihentikan untuk menjaga audit.");
        return new(new(fileName, actualHash, content), new(archiveFileName, actualArchiveHash, archiveContent));
    }

    private async Task<PeriodCloseDto?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        const string sql = """
            select id,period_name,cutoff_date,opening_date,status,created_by_name,computer_name,created_at,
                   backup_location,backup_file_name,snapshot_sha256,
                   archive_location,archive_file_name,archive_sha256,summary_json::text,
                   status='POSTED' and id=(
                       select id from accounting_period_close
                       where status='POSTED'
                       order by cutoff_date desc,created_at desc,id desc limit 1
                   ) can_reopen,
                   reopened_by_name,reopen_computer_name,reopened_at,reopen_reason
            from accounting_period_close where id=@id;
            """;
        await using var command = database.DataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadClose(reader) : null;
    }

    private static async Task<PeriodCloseDto?> ReadByIdempotencyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string key,
        CancellationToken cancellationToken)
    {
        const string sql = """
            select id,period_name,cutoff_date,opening_date,status,created_by_name,computer_name,created_at,
                   backup_location,backup_file_name,snapshot_sha256,
                   archive_location,archive_file_name,archive_sha256,summary_json::text,
                   status='POSTED' and id=(
                       select id from accounting_period_close
                       where status='POSTED'
                       order by cutoff_date desc,created_at desc,id desc limit 1
                   ) can_reopen,
                   reopened_by_name,reopen_computer_name,reopened_at,reopen_reason
            from accounting_period_close where idempotency_key=@key and status='POSTED';
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("key", key);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadClose(reader) : null;
    }

    private static PeriodCloseDto ReadClose(NpgsqlDataReader reader)
    {
        var stored = JsonSerializer.Deserialize<StoredSummary>(reader.GetString(14), JsonOptions)
            ?? new(EmptyDocuments(), EmptyCarryForward());
        return new(
            reader.GetGuid(0), reader.GetString(1), reader.GetFieldValue<DateOnly>(2), reader.GetFieldValue<DateOnly>(3),
            reader.GetString(4), reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.GetFieldValue<DateTimeOffset>(7), reader.GetString(8), reader.GetString(9), reader.GetString(10).Trim(),
            reader.GetString(11), reader.GetString(12), reader.GetString(13).Trim(),
            stored.Documents, stored.CarryForward, reader.GetBoolean(15),
            reader.IsDBNull(16) ? null : reader.GetString(16),
            reader.IsDBNull(17) ? null : reader.GetString(17),
            reader.IsDBNull(18) ? null : reader.GetFieldValue<DateTimeOffset>(18),
            reader.IsDBNull(19) ? null : reader.GetString(19));
    }

    private static async Task<PreparedClose> PrepareAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        PeriodClosePreflightRequest request,
        CancellationToken cancellationToken)
    {
        var periodName = ValidateRequest(request.CutoffDate, request.OpeningDate, request.PeriodName);
        var blockers = new List<string>();

        DateOnly? latestCutoff = null;
        await using (var command = new NpgsqlCommand("select max(cutoff_date) from accounting_period_close where status='POSTED';", connection, transaction))
        {
            var value = await command.ExecuteScalarAsync(cancellationToken);
            if (value is DateTime date) latestCutoff = DateOnly.FromDateTime(date);
            else if (value is DateOnly dateOnly) latestCutoff = dateOnly;
        }
        if (latestCutoff is not null && request.CutoffDate <= latestCutoff)
            blockers.Add($"Periode sudah ditutup sampai {latestCutoff:dd/MM/yyyy}. Pilih tanggal setelah periode tersebut.");

        await using (var command = new NpgsqlCommand("select exists(select 1 from accounting_period_close where lower(period_name)=lower(@name) and status in ('PROCESSING','POSTED'));", connection, transaction))
        {
            command.Parameters.AddWithValue("name", periodName);
            if (Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken)))
                blockers.Add($"Nama periode '{periodName}' sudah digunakan.");
        }

        var unbalanced = await ScalarIntAsync(connection, transaction, """
            select count(*) from (
                select e.id
                from journal_entry e join journal_entry_line l on l.journal_entry_id=e.id
                where e.status='POSTED' and e.document_date<=@cutoff
                group by e.id having round(sum(l.debit)-sum(l.credit),2)<>0
            ) q;
            """, request.CutoffDate, cancellationToken);
        if (unbalanced > 0) blockers.Add($"Terdapat {unbalanced:N0} jurnal tidak seimbang sampai tanggal tutup buku.");

        var draftCount = await ScalarIntAsync(connection, transaction, BuildDraftCountSql(), request.CutoffDate, cancellationToken);
        if (draftCount > 0) blockers.Add($"Terdapat {draftCount:N0} transaksi berstatus draft/invalid sampai tanggal tutup buku.");

        var documents = await ReadDocumentCountsAsync(connection, transaction, request.CutoffDate, cancellationToken);
        var inventory = await ReadInventorySnapshotAsync(connection, transaction, request.CutoffDate, cancellationToken);
        blockers.AddRange(inventory.Errors);
        var carryForward = await ReadCarryForwardAsync(connection, transaction, request.CutoffDate, inventory, cancellationToken);
        return new(new(
            request.CutoffDate, request.OpeningDate, periodName, blockers.Count == 0, latestCutoff,
            blockers.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), documents, carryForward), inventory);
    }

    private static async Task<PeriodCloseDocumentCountsDto> ReadDocumentCountsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        DateOnly cutoff,
        CancellationToken cancellationToken)
    {
        const string sql = """
            select
                (select count(*) from journal_entry where status='POSTED' and document_date<=@cutoff),
                (select count(*) from cash_bank_transaction where status='POSTED' and document_date<=@cutoff),
                (select count(*) from purchase where status='POSTED' and document_date<=@cutoff),
                (select count(*) from purchase_return where status='POSTED' and document_date<=@cutoff),
                (select count(*) from sale where status='POSTED' and document_date<=@cutoff),
                (select count(*) from sale_return where status='POSTED' and document_date<=@cutoff),
                (select count(*) from supplier_payment where status='POSTED' and document_date<=@cutoff),
                (select count(*) from customer_receipt where status='POSTED' and document_date<=@cutoff),
                (select count(*) from customer_check_giro where status<>'CANCELLED' and document_date<=@cutoff),
                (select count(*) from partner_balance_document where status='POSTED' and document_date<=@cutoff),
                (select count(*) from inventory_adjustment where status='POSTED' and document_date<=@cutoff)
                +(select count(*) from inventory_transfer where status='POSTED' and document_date<=@cutoff)
                +(select count(*) from inventory_stock_count where status='POSTED' and document_date<=@cutoff)
                +(select count(*) from inventory_mbap_send where status='POSTED' and document_date<=@cutoff)
                +(select count(*) from inventory_mbap_receive where status='POSTED' and document_date<=@cutoff);
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("cutoff", cutoff);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        var values = Enumerable.Range(0, 11).Select(index => Convert.ToInt32(reader.GetInt64(index))).ToArray();
        return new(values[0], values[1], values[2], values[3], values[4], values[5], values[6], values[7], values[8], values[9], values[10], values.Sum());
    }

    private static async Task<PeriodCloseCarryForwardDto> ReadCarryForwardAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        DateOnly cutoff,
        PeriodCloseInventorySnapshot inventory,
        CancellationToken cancellationToken)
    {
        const string ledgerSql = """
            select count(*),coalesce(sum(debit),0),coalesce(sum(credit),0)
            from (
                select a.id,coalesce(sum(l.debit),0) debit,coalesce(sum(l.credit),0) credit
                from ledger_account a
                join journal_entry_line l on l.account_id=a.id
                join journal_entry e on e.id=l.journal_entry_id
                where e.status='POSTED' and e.document_date<=@cutoff and a.is_postable
                group by a.id having coalesce(sum(l.debit),0)<>0 or coalesce(sum(l.credit),0)<>0
            ) q;
            """;
        int ledgerAccounts;
        decimal ledgerDebit;
        decimal ledgerCredit;
        await using (var command = new NpgsqlCommand(ledgerSql, connection, transaction))
        {
            command.Parameters.AddWithValue("cutoff", cutoff);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            ledgerAccounts = Convert.ToInt32(reader.GetInt64(0));
            ledgerDebit = reader.GetDecimal(1);
            ledgerCredit = reader.GetDecimal(2);
        }

        const string cashBankSql = """
            with balances as (
                select a.code,a.normal_balance,
                       coalesce(sum(case when e.id is not null then l.debit else 0 end),0) debit,
                       coalesce(sum(case when e.id is not null then l.credit else 0 end),0) credit
                from ledger_account a
                left join journal_entry_line l on l.account_id=a.id
                left join journal_entry e on e.id=l.journal_entry_id and e.status='POSTED' and e.document_date<=@cutoff
                where a.code in (
                    select account_code from cash_account where is_active
                    union select account_code from bank_account where is_active)
                group by a.code,a.normal_balance
            )
            select count(*),coalesce(sum(case when normal_balance='D' then debit-credit else credit-debit end),0) from balances;
            """;
        int cashBankAccounts;
        decimal cashBankBalance;
        await using (var command = new NpgsqlCommand(cashBankSql, connection, transaction))
        {
            command.Parameters.AddWithValue("cutoff", cutoff);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            cashBankAccounts = Convert.ToInt32(reader.GetInt64(0));
            cashBankBalance = reader.GetDecimal(1);
        }

        int supplierCount;
        decimal supplierTotal;
        int customerCount;
        decimal customerTotal;
        await using (var command = new NpgsqlCommand(PartnerSnapshotCte + """
            select
                count(*) filter(where partner_type='SUPPLIER'),
                coalesce(sum(balance) filter(where partner_type='SUPPLIER'),0),
                count(*) filter(where partner_type='CUSTOMER'),
                coalesce(sum(balance) filter(where partner_type='CUSTOMER'),0)
            from partner_sources where balance>0;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("cutoff", cutoff);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            supplierCount = Convert.ToInt32(reader.GetInt64(0));
            supplierTotal = reader.GetDecimal(1);
            customerCount = Convert.ToInt32(reader.GetInt64(2));
            customerTotal = reader.GetDecimal(3);
        }

        return new(
            ledgerAccounts, ledgerDebit, ledgerCredit, cashBankAccounts, cashBankBalance,
            supplierCount, supplierTotal, customerCount, customerTotal,
            inventory.Balances.Count, inventory.Balances.Sum(x => x.Quantity),
            inventory.Balances.Sum(x => x.Value), inventory.Layers.Count);
    }

    private static async Task<PeriodCloseInventorySnapshot> ReadInventorySnapshotAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        DateOnly cutoff,
        CancellationToken cancellationToken)
    {
        const string sql = """
            select m.id,m.occurred_at,i.code,i.name,m.warehouse_code,m.source_type,m.source_id,m.source_number,
                   m.quantity_in,m.quantity_out,m.unit_cost
            from inventory_movement m
            join inventory_item i on i.id=m.item_id
            where m.occurred_at::date<=@cutoff
            order by m.occurred_at,m.id;
            """;
        var rows = new List<PeriodCloseInventoryMovement>();
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("cutoff", cutoff);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(new(
                reader.GetGuid(0), reader.GetFieldValue<DateTimeOffset>(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetGuid(6), reader.GetString(7),
                reader.GetDecimal(8), reader.GetDecimal(9), reader.GetDecimal(10)));
        return PeriodCloseRules.BuildInventorySnapshot(rows);
    }

    private static async Task InsertLedgerBalancesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid closeId, DateOnly cutoff, CancellationToken cancellationToken)
    {
        const string sql = """
            insert into period_close_ledger_balance(
                period_close_id,account_code,account_name,normal_balance,debit_total,credit_total,balance)
            select @close,a.code,a.name,a.normal_balance,coalesce(sum(l.debit),0),coalesce(sum(l.credit),0),
                   case when a.normal_balance='D' then coalesce(sum(l.debit-l.credit),0) else coalesce(sum(l.credit-l.debit),0) end
            from ledger_account a
            join journal_entry_line l on l.account_id=a.id
            join journal_entry e on e.id=l.journal_entry_id
            where e.status='POSTED' and e.document_date<=@cutoff and a.is_postable
            group by a.code,a.name,a.normal_balance
            having coalesce(sum(l.debit),0)<>0 or coalesce(sum(l.credit),0)<>0;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("close", closeId);
        command.Parameters.AddWithValue("cutoff", cutoff);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertCashBankBalancesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid closeId, CancellationToken cancellationToken)
    {
        const string sql = """
            insert into period_close_cash_bank_balance(period_close_id,resource_type,resource_code,resource_name,account_code,balance)
            select @close,'CASH',c.code,coalesce(nullif(trim(c.name),''),c.code),c.account_code,coalesce(l.balance,0)
            from cash_account c left join period_close_ledger_balance l on l.period_close_id=@close and l.account_code=c.account_code
            where c.is_active
            union all
            select @close,'BANK',b.code,
                   coalesce(nullif(trim(b.bank_name),''),b.code) ||
                   case when coalesce(trim(b.account_number),'')='' then '' else ' - ' || trim(b.account_number) end,
                   b.account_code,coalesce(l.balance,0)
            from bank_account b left join period_close_ledger_balance l on l.period_close_id=@close and l.account_code=b.account_code
            where b.is_active;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("close", closeId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertPartnerBalancesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid closeId, DateOnly cutoff, CancellationToken cancellationToken)
    {
        var sql = PartnerSnapshotCte + """
            insert into period_close_partner_balance(
                period_close_id,partner_type,partner_code,partner_name,source_type,source_id,
                document_number,document_date,due_date,balance)
            select @close,partner_type,partner_code,partner_name,source_type,source_id,
                   document_number,document_date,due_date,balance
            from partner_sources where balance>0;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("close", closeId);
        command.Parameters.AddWithValue("cutoff", cutoff);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertInventorySnapshotAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid closeId,
        PeriodCloseInventorySnapshot inventory,
        CancellationToken cancellationToken)
    {
        const string balanceSql = """
            insert into period_close_inventory_balance(
                period_close_id,item_code,item_name,warehouse_code,quantity,unit_cost,inventory_value)
            values(@close,@item,@name,@warehouse,@quantity,@cost,@value);
            """;
        foreach (var row in inventory.Balances)
        {
            await using var command = new NpgsqlCommand(balanceSql, connection, transaction);
            command.Parameters.AddWithValue("close", closeId);
            command.Parameters.AddWithValue("item", row.ItemCode);
            command.Parameters.AddWithValue("name", row.ItemName);
            command.Parameters.AddWithValue("warehouse", row.WarehouseCode);
            command.Parameters.AddWithValue("quantity", row.Quantity);
            command.Parameters.AddWithValue("cost", row.UnitCost);
            command.Parameters.AddWithValue("value", decimal.Round(row.Value, 2));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        const string layerSql = """
            insert into period_close_inventory_cost_layer(
                period_close_id,item_code,item_name,warehouse_code,received_at,source_type,source_id,
                source_number,original_quantity,remaining_quantity,unit_cost)
            values(@close,@item,@name,@warehouse,@received,@type,@source,@number,@original,@remaining,@cost);
            """;
        foreach (var row in inventory.Layers)
        {
            await using var command = new NpgsqlCommand(layerSql, connection, transaction);
            command.Parameters.AddWithValue("close", closeId);
            command.Parameters.AddWithValue("item", row.ItemCode);
            command.Parameters.AddWithValue("name", row.ItemName);
            command.Parameters.AddWithValue("warehouse", row.WarehouseCode);
            command.Parameters.AddWithValue("received", row.ReceivedAt);
            command.Parameters.AddWithValue("type", row.SourceType);
            command.Parameters.AddWithValue("source", row.SourceId);
            command.Parameters.AddWithValue("number", row.SourceNumber);
            command.Parameters.AddWithValue("original", row.OriginalQuantity);
            command.Parameters.AddWithValue("remaining", row.RemainingQuantity);
            command.Parameters.AddWithValue("cost", row.UnitCost);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<string> BuildExportPayloadAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid closeId,
        string summaryJson,
        string backupFileName,
        string archiveFileName,
        string archivePayload,
        string userName,
        string? computerName,
        CancellationToken cancellationToken)
    {
        const string sql = """
            select jsonb_build_object(
                'schemaVersion',1,
                'product','AKSARA',
                'exportType','PERIOD_CLOSE_TOTAL_BACKUP',
                'period',jsonb_build_object(
                    'id',c.id,'periodName',c.period_name,'cutoffDate',c.cutoff_date,'openingDate',c.opening_date,
                    'createdBy',@user,'computerName',@computer,'createdAt',c.created_at,
                    'backupFileName',@file,'throughCutoffArchiveFileName',@archiveFile),
                'summary',@summary::jsonb,
                'coveredTransactions',@archive::jsonb,
                'ledgerBalances',coalesce((select jsonb_agg(jsonb_build_object(
                    'accountCode',x.account_code,'accountName',x.account_name,'normalBalance',x.normal_balance,
                    'debitTotal',x.debit_total,'creditTotal',x.credit_total,'balance',x.balance) order by x.account_code)
                    from period_close_ledger_balance x where x.period_close_id=c.id),'[]'::jsonb),
                'cashBankBalances',coalesce((select jsonb_agg(jsonb_build_object(
                    'resourceType',x.resource_type,'resourceCode',x.resource_code,'resourceName',x.resource_name,
                    'accountCode',x.account_code,'balance',x.balance) order by x.resource_type,x.resource_code)
                    from period_close_cash_bank_balance x where x.period_close_id=c.id),'[]'::jsonb),
                'partnerBalances',coalesce((select jsonb_agg(jsonb_build_object(
                    'partnerType',x.partner_type,'partnerCode',x.partner_code,'partnerName',x.partner_name,
                    'sourceType',x.source_type,'sourceId',x.source_id,'documentNumber',x.document_number,
                    'documentDate',x.document_date,'dueDate',x.due_date,'balance',x.balance)
                    order by x.partner_type,x.partner_code,x.document_date,x.document_number)
                    from period_close_partner_balance x where x.period_close_id=c.id),'[]'::jsonb),
                'inventoryBalances',coalesce((select jsonb_agg(jsonb_build_object(
                    'itemCode',x.item_code,'itemName',x.item_name,'warehouseCode',x.warehouse_code,
                    'quantity',x.quantity,'unitCost',x.unit_cost,'inventoryValue',x.inventory_value)
                    order by x.item_code,x.warehouse_code)
                    from period_close_inventory_balance x where x.period_close_id=c.id),'[]'::jsonb),
                'inventoryCostLayers',coalesce((select jsonb_agg(jsonb_build_object(
                    'itemCode',x.item_code,'itemName',x.item_name,'warehouseCode',x.warehouse_code,
                    'receivedAt',x.received_at,'sourceType',x.source_type,'sourceId',x.source_id,
                    'sourceNumber',x.source_number,'originalQuantity',x.original_quantity,
                    'remainingQuantity',x.remaining_quantity,'unitCost',x.unit_cost)
                    order by x.item_code,x.warehouse_code,x.received_at,x.id)
                    from period_close_inventory_cost_layer x where x.period_close_id=c.id),'[]'::jsonb)
            )::text
            from accounting_period_close c where c.id=@close;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("close", closeId);
        command.Parameters.AddWithValue("user", userName);
        command.Parameters.AddWithValue("computer", Db(computerName));
        command.Parameters.AddWithValue("file", backupFileName);
        command.Parameters.AddWithValue("archiveFile", archiveFileName);
        command.Parameters.Add(new NpgsqlParameter("archive", NpgsqlDbType.Jsonb) { Value = archivePayload });
        command.Parameters.Add(new NpgsqlParameter("summary", NpgsqlDbType.Jsonb) { Value = summaryJson });
        return (string)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task<string> BuildArchivePayloadAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid closeId,
        DateOnly cutoff,
        string archiveFileName,
        string userName,
        string? computerName,
        CancellationToken cancellationToken)
    {
        const string sql = """
            select jsonb_build_object(
                'schemaVersion',1,
                'product','AKSARA',
                'exportType','TRANSACTIONS_THROUGH_CUTOFF',
                'periodCloseId',@close,
                'cutoffDate',@cutoff,
                'createdBy',@user,
                'computerName',@computer,
                'archiveFileName',@file,
                'journals',coalesce((select jsonb_agg(to_jsonb(x) order by x.document_date,x.id) from journal_entry x where x.document_date<=@cutoff),'[]'::jsonb),
                'journalLines',coalesce((select jsonb_agg(to_jsonb(x) order by h.document_date,x.id) from journal_entry_line x join journal_entry h on h.id=x.journal_entry_id where h.document_date<=@cutoff),'[]'::jsonb),
                'cashBankTransactions',coalesce((select jsonb_agg(to_jsonb(x) order by x.document_date,x.id) from cash_bank_transaction x where x.document_date<=@cutoff),'[]'::jsonb),
                'cashBankLines',coalesce((select jsonb_agg(to_jsonb(x) order by h.document_date,x.id) from cash_bank_transaction_line x join cash_bank_transaction h on h.id=x.transaction_id where h.document_date<=@cutoff),'[]'::jsonb),
                'purchases',coalesce((select jsonb_agg(to_jsonb(x) order by x.document_date,x.id) from purchase x where x.document_date<=@cutoff),'[]'::jsonb),
                'purchaseLines',coalesce((select jsonb_agg(to_jsonb(x) order by h.document_date,x.id) from purchase_line x join purchase h on h.id=x.purchase_id where h.document_date<=@cutoff),'[]'::jsonb),
                'purchaseReturns',coalesce((select jsonb_agg(to_jsonb(x) order by x.document_date,x.id) from purchase_return x where x.document_date<=@cutoff),'[]'::jsonb),
                'purchaseReturnLines',coalesce((select jsonb_agg(to_jsonb(x) order by h.document_date,x.id) from purchase_return_line x join purchase_return h on h.id=x.purchase_return_id where h.document_date<=@cutoff),'[]'::jsonb),
                'sales',coalesce((select jsonb_agg(to_jsonb(x) order by x.document_date,x.id) from sale x where x.document_date<=@cutoff),'[]'::jsonb),
                'saleLines',coalesce((select jsonb_agg(to_jsonb(x) order by h.document_date,x.id) from sale_line x join sale h on h.id=x.sale_id where h.document_date<=@cutoff),'[]'::jsonb),
                'saleReturns',coalesce((select jsonb_agg(to_jsonb(x) order by x.document_date,x.id) from sale_return x where x.document_date<=@cutoff),'[]'::jsonb),
                'saleReturnLines',coalesce((select jsonb_agg(to_jsonb(x) order by h.document_date,x.id) from sale_return_line x join sale_return h on h.id=x.sale_return_id where h.document_date<=@cutoff),'[]'::jsonb),
                'supplierPayments',coalesce((select jsonb_agg(to_jsonb(x) order by x.document_date,x.id) from supplier_payment x where x.document_date<=@cutoff),'[]'::jsonb),
                'supplierPaymentLines',coalesce((select jsonb_agg(to_jsonb(x) order by h.document_date,x.id) from supplier_payment_line x join supplier_payment h on h.id=x.supplier_payment_id where h.document_date<=@cutoff),'[]'::jsonb),
                'customerReceipts',coalesce((select jsonb_agg(to_jsonb(x) order by x.document_date,x.id) from customer_receipt x where x.document_date<=@cutoff),'[]'::jsonb),
                'customerReceiptLines',coalesce((select jsonb_agg(to_jsonb(x) order by h.document_date,x.id) from customer_receipt_line x join customer_receipt h on h.id=x.customer_receipt_id where h.document_date<=@cutoff),'[]'::jsonb),
                'customerCheckGiros',coalesce((select jsonb_agg(to_jsonb(x) order by x.document_date,x.id) from customer_check_giro x where x.document_date<=@cutoff),'[]'::jsonb),
                'partnerBalanceDocuments',coalesce((select jsonb_agg(to_jsonb(x) order by x.document_date,x.id) from partner_balance_document x where x.document_date<=@cutoff),'[]'::jsonb),
                'inventoryAdjustments',coalesce((select jsonb_agg(to_jsonb(x) order by x.document_date,x.id) from inventory_adjustment x where x.document_date<=@cutoff),'[]'::jsonb),
                'inventoryAdjustmentLines',coalesce((select jsonb_agg(to_jsonb(x) order by h.document_date,x.id) from inventory_adjustment_line x join inventory_adjustment h on h.id=x.adjustment_id where h.document_date<=@cutoff),'[]'::jsonb),
                'inventoryTransfers',coalesce((select jsonb_agg(to_jsonb(x) order by x.document_date,x.id) from inventory_transfer x where x.document_date<=@cutoff),'[]'::jsonb),
                'inventoryTransferLines',coalesce((select jsonb_agg(to_jsonb(x) order by h.document_date,x.id) from inventory_transfer_line x join inventory_transfer h on h.id=x.transfer_id where h.document_date<=@cutoff),'[]'::jsonb),
                'inventoryStockCounts',coalesce((select jsonb_agg(to_jsonb(x) order by x.document_date,x.id) from inventory_stock_count x where x.document_date<=@cutoff),'[]'::jsonb),
                'inventoryStockCountLines',coalesce((select jsonb_agg(to_jsonb(x) order by h.document_date,x.id) from inventory_stock_count_line x join inventory_stock_count h on h.id=x.stock_count_id where h.document_date<=@cutoff),'[]'::jsonb),
                'inventoryMbapSends',coalesce((select jsonb_agg(to_jsonb(x) order by x.document_date,x.id) from inventory_mbap_send x where x.document_date<=@cutoff),'[]'::jsonb),
                'inventoryMbapSendLines',coalesce((select jsonb_agg(to_jsonb(x) order by h.document_date,x.id) from inventory_mbap_send_line x join inventory_mbap_send h on h.id=x.send_id where h.document_date<=@cutoff),'[]'::jsonb),
                'inventoryMbapReceives',coalesce((select jsonb_agg(to_jsonb(x) order by x.document_date,x.id) from inventory_mbap_receive x where x.document_date<=@cutoff),'[]'::jsonb),
                'inventoryMbapReceiveLines',coalesce((select jsonb_agg(to_jsonb(x) order by h.document_date,x.id) from inventory_mbap_receive_line x join inventory_mbap_receive h on h.id=x.receive_id where h.document_date<=@cutoff),'[]'::jsonb),
                'inventoryMovements',coalesce((select jsonb_agg(to_jsonb(x) order by x.occurred_at,x.id) from inventory_movement x where x.occurred_at::date<=@cutoff),'[]'::jsonb)
            )::text;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("close", closeId);
        command.Parameters.AddWithValue("cutoff", cutoff);
        command.Parameters.AddWithValue("user", userName);
        command.Parameters.AddWithValue("computer", Db(computerName));
        command.Parameters.AddWithValue("file", archiveFileName);
        return (string)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task<int> ScalarIntAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string sql,
        DateOnly cutoff,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("cutoff", cutoff);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static Task LockMutationTablesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken) => ExecuteAsync(connection, transaction, """
            lock table
                journal_entry,journal_entry_line,
                cash_bank_transaction,cash_bank_transaction_line,
                purchase,purchase_line,purchase_return,purchase_return_line,
                sale,sale_line,sale_return,sale_return_line,
                supplier_payment,supplier_payment_line,
                customer_receipt,customer_receipt_line,customer_check_giro,
                partner_balance_document,
                inventory_adjustment,inventory_adjustment_line,
                inventory_transfer,inventory_transfer_line,
                inventory_stock_count,inventory_stock_count_line,
                inventory_mbap_send,inventory_mbap_send_line,
                inventory_mbap_receive,inventory_mbap_receive_line,
                inventory_movement,inventory_standard_cost_history
            in share mode;
            """, cancellationToken);

    private static string ValidateRequest(DateOnly cutoff, DateOnly opening, string? periodName)
    {
        try { return PeriodCloseRules.ValidateRequest(cutoff, opening, periodName); }
        catch (InvalidOperationException exception) { throw new PeriodCloseValidationException(exception.Message); }
    }

    private static string Required(string? value, string label, int maxLength)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0) throw new PeriodCloseValidationException($"{label} wajib diisi.");
        if (text.Length > maxLength) throw new PeriodCloseValidationException($"{label} maksimal {maxLength} karakter.");
        return text;
    }

    private static string? Optional(string? value, int maxLength)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0) return null;
        if (text.Length > maxLength) throw new PeriodCloseValidationException($"Teks maksimal {maxLength} karakter.");
        return text;
    }

    private static string BuildBackupFileName(DateOnly cutoff, string periodName, Guid id)
    {
        var slug = Slug(periodName);
        return $"_tpbuku_{cutoff:yyyyMMdd}_{slug}_{id.ToString("N")[..8]}.json";
    }

    private static string BuildArchiveFileName(DateOnly cutoff, string periodName, Guid id) =>
        $"_data_sd_{cutoff:yyyyMMdd}_{Slug(periodName)}_{id.ToString("N")[..8]}.json";

    private static string Slug(string periodName)
    {
        var slug = Regex.Replace(periodName.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (slug.Length == 0) slug = "periode";
        if (slug.Length > 50) slug = slug[..50].TrimEnd('-');
        return slug;
    }

    private static string Hash(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    private static object Db(string? value) => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();

    private static string BuildDraftCountSql() => """
        select count(*) from (
            select status from journal_entry where document_date<=@cutoff and status not in ('POSTED','CANCELLED')
            union all select status from cash_bank_transaction where document_date<=@cutoff and status not in ('POSTED','CANCELLED')
            union all select status from purchase where document_date<=@cutoff and status not in ('POSTED','CANCELLED')
            union all select status from purchase_return where document_date<=@cutoff and status not in ('POSTED','CANCELLED')
            union all select status from sale where document_date<=@cutoff and status not in ('POSTED','CANCELLED')
            union all select status from sale_return where document_date<=@cutoff and status not in ('POSTED','CANCELLED')
            union all select status from supplier_payment where document_date<=@cutoff and status not in ('POSTED','CANCELLED')
            union all select status from customer_receipt where document_date<=@cutoff and status not in ('POSTED','CANCELLED')
            union all select status from partner_balance_document where document_date<=@cutoff and status not in ('POSTED','CANCELLED')
            union all select status from inventory_adjustment where document_date<=@cutoff and status not in ('POSTED','CANCELLED')
            union all select status from inventory_transfer where document_date<=@cutoff and status not in ('POSTED','CANCELLED')
            union all select status from inventory_stock_count where document_date<=@cutoff and status not in ('POSTED','CANCELLED')
            union all select status from inventory_mbap_send where document_date<=@cutoff and status not in ('POSTED','CANCELLED')
            union all select status from inventory_mbap_receive where document_date<=@cutoff and status not in ('POSTED','CANCELLED')
        ) q;
        """;

    private const string PartnerSnapshotCte = """
        with supplier_purchase_paid as (
            select l.purchase_id source_id,coalesce(sum(l.amount),0) amount
            from supplier_payment_line l join supplier_payment p on p.id=l.supplier_payment_id
            where p.status='POSTED' and p.document_date<=@cutoff and l.source_type='PURCHASE'
            group by l.purchase_id
        ), supplier_balance_paid as (
            select l.balance_document_id source_id,coalesce(sum(l.amount),0) amount
            from supplier_payment_line l join supplier_payment p on p.id=l.supplier_payment_id
            where p.status='POSTED' and p.document_date<=@cutoff and l.source_type='BALANCE_DOCUMENT'
            group by l.balance_document_id
        ), supplier_purchase_reduced as (
            select target_source_id source_id,coalesce(sum(amount),0) amount
            from partner_balance_document
            where partner_type='SUPPLIER' and direction='DECREASE' and status='POSTED'
              and target_source_type='PURCHASE' and document_date<=@cutoff
            group by target_source_id
        ), supplier_balance_reduced as (
            select target_source_id source_id,coalesce(sum(amount),0) amount
            from partner_balance_document
            where partner_type='SUPPLIER' and direction='DECREASE' and status='POSTED'
              and target_source_type='BALANCE_DOCUMENT' and document_date<=@cutoff
            group by target_source_id
        ), customer_sale_paid as (
            select l.sale_id source_id,coalesce(sum(l.amount),0) amount
            from customer_receipt_line l join customer_receipt r on r.id=l.customer_receipt_id
            where r.status='POSTED' and r.document_date<=@cutoff and l.source_type='SALE'
            group by l.sale_id
        ), customer_balance_paid as (
            select l.balance_document_id source_id,coalesce(sum(l.amount),0) amount
            from customer_receipt_line l join customer_receipt r on r.id=l.customer_receipt_id
            where r.status='POSTED' and r.document_date<=@cutoff and l.source_type='BALANCE_DOCUMENT'
            group by l.balance_document_id
        ), customer_sale_reduced as (
            select target_source_id source_id,coalesce(sum(amount),0) amount
            from partner_balance_document
            where partner_type='CUSTOMER' and direction='DECREASE' and status='POSTED'
              and target_source_type='SALE' and document_date<=@cutoff
            group by target_source_id
        ), customer_balance_reduced as (
            select target_source_id source_id,coalesce(sum(amount),0) amount
            from partner_balance_document
            where partner_type='CUSTOMER' and direction='DECREASE' and status='POSTED'
              and target_source_type='BALANCE_DOCUMENT' and document_date<=@cutoff
            group by target_source_id
        ), partner_sources as (
            select 'SUPPLIER'::varchar partner_type,s.code partner_code,s.company_name partner_name,
                   'PURCHASE'::varchar source_type,p.id source_id,p.document_number,p.document_date,p.due_date,
                   p.balance_due-coalesce(r.amount,0)-coalesce(x.amount,0) balance
            from purchase p join supplier s on s.id=p.supplier_id
            left join supplier_purchase_paid x on x.source_id=p.id
            left join supplier_purchase_reduced r on r.source_id=p.id
            where p.status='POSTED' and p.transaction_type='CREDIT' and p.document_date<=@cutoff
            union all
            select 'SUPPLIER',s.code,s.company_name,'BALANCE_DOCUMENT',d.id,d.document_number,d.document_date,d.due_date,
                   d.amount-coalesce(r.amount,0)-coalesce(x.amount,0)
            from partner_balance_document d join supplier s on s.id=d.supplier_id
            left join supplier_balance_paid x on x.source_id=d.id
            left join supplier_balance_reduced r on r.source_id=d.id
            where d.partner_type='SUPPLIER' and d.direction='INCREASE' and d.status='POSTED' and d.document_date<=@cutoff
            union all
            select 'CUSTOMER',c.code,c.company_name,'SALE',s.id,s.document_number,s.document_date,s.due_date,
                   s.balance_due-coalesce(r.amount,0)-coalesce(x.amount,0)
            from sale s join customer c on c.id=s.customer_id
            left join customer_sale_paid x on x.source_id=s.id
            left join customer_sale_reduced r on r.source_id=s.id
            where s.status='POSTED' and s.transaction_type='CREDIT' and s.document_date<=@cutoff
            union all
            select 'CUSTOMER',c.code,c.company_name,'BALANCE_DOCUMENT',d.id,d.document_number,d.document_date,d.due_date,
                   d.amount-coalesce(r.amount,0)-coalesce(x.amount,0)
            from partner_balance_document d join customer c on c.id=d.customer_id
            left join customer_balance_paid x on x.source_id=d.id
            left join customer_balance_reduced r on r.source_id=d.id
            where d.partner_type='CUSTOMER' and d.direction='INCREASE' and d.status='POSTED' and d.document_date<=@cutoff
        )
        """;

    private static PeriodCloseDocumentCountsDto EmptyDocuments() => new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
    private static PeriodCloseCarryForwardDto EmptyCarryForward() => new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    private sealed record StoredSummary(PeriodCloseDocumentCountsDto Documents, PeriodCloseCarryForwardDto CarryForward);
    private sealed record PreparedClose(PeriodClosePreflightDto Preflight, PeriodCloseInventorySnapshot Inventory);
}
