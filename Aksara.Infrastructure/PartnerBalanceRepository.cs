using System.Data;
using System.Globalization;
using Aksara.Contracts;
using Aksara.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Aksara.Infrastructure;

public sealed class PartnerBalanceValidationException(string message) : InvalidOperationException(message);
public sealed class PartnerBalanceConflictException(string message) : InvalidOperationException(message);

public sealed class PartnerBalanceRepository(PostgresDatabase database)
{
    public async Task<IReadOnlyList<PartnerBalanceDocumentDto>> ListAsync(
        string partnerType,
        string? kind,
        string? query,
        bool includeCancelled = true,
        int limit = 500,
        CancellationToken cancellationToken = default)
    {
        partnerType = NormalizePartnerType(partnerType);
        var normalizedKind = string.IsNullOrWhiteSpace(kind) ? string.Empty : NormalizeKind(kind);
        const string sql = """
            select d.id
            from partner_balance_document d
            left join supplier s on s.id = d.supplier_id
            left join customer c on c.id = d.customer_id
            where d.partner_type = @partnerType
              and (@kind = '' or d.document_kind = @kind)
              and (@includeCancelled or d.status = 'POSTED')
              and (@query = '' or d.document_number ilike '%' || @query || '%'
                   or coalesce(s.code, c.code, '') ilike '%' || @query || '%'
                   or coalesce(s.company_name, c.company_name, '') ilike '%' || @query || '%'
                   or coalesce(d.notes, '') ilike '%' || @query || '%')
            order by d.document_date desc, d.document_number desc
            limit @limit;
            """;
        var ids = new List<Guid>();
        await using (var command = database.DataSource.CreateCommand(sql))
        {
            command.Parameters.AddWithValue("partnerType", partnerType);
            command.Parameters.AddWithValue("kind", normalizedKind);
            command.Parameters.AddWithValue("includeCancelled", includeCancelled);
            command.Parameters.AddWithValue("query", query?.Trim() ?? string.Empty);
            command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 1000));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) ids.Add(reader.GetGuid(0));
        }

        var result = new List<PartnerBalanceDocumentDto>(ids.Count);
        foreach (var id in ids)
        {
            var row = await GetAsync(id, cancellationToken);
            if (row is not null) result.Add(row);
        }
        return result;
    }

    public async Task<PartnerBalanceDocumentDto?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        return await ReadAsync(connection, null, id, cancellationToken);
    }

    public async Task<string> PreviewNextNumberAsync(string partnerType, string kind, DateOnly date, CancellationToken cancellationToken = default)
    {
        partnerType = NormalizePartnerType(partnerType);
        kind = NormalizeKind(kind);
        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        var (sequenceType, prefix) = Numbering(partnerType, kind);
        var seed = await ReadSequenceSeedAsync(connection, null, sequenceType, prefix, date, cancellationToken);
        return $"{prefix}{date:yyMM}{seed + 1:000}";
    }

    public async Task<PartnerBalanceDocumentDto> SaveAsync(SavePartnerBalanceDocumentRequest request, CancellationToken cancellationToken = default)
    {
        var partnerType = NormalizePartnerType(request.PartnerType);
        var kind = NormalizeKind(request.Kind);
        var direction = NormalizeDirection(request.Direction);
        var amount = Math.Round(request.Amount, 2);
        var targetSourceType = string.IsNullOrWhiteSpace(request.TargetSourceType) ? null : NormalizeSourceType(request.TargetSourceType, partnerType);
        try
        {
            PartnerBalanceRules.ValidateDocument(kind, direction, amount, targetSourceType, request.TargetSourceId);
        }
        catch (InvalidOperationException exception)
        {
            throw new PartnerBalanceValidationException(exception.Message);
        }

        var partnerCode = NormalizeCode(request.PartnerCode, partnerType == "SUPPLIER" ? "Kode supplier" : "Kode customer", 20);
        var requestedNumber = NormalizeDocumentNumber(request.DocumentNumber);
        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            ExistingDocument? existing = null;
            if (request.Id is Guid existingId)
            {
                existing = await LockExistingAsync(connection, transaction, existingId, cancellationToken)
                    ?? throw new PartnerBalanceValidationException("Dokumen saldo tidak ditemukan.");
                try { PartnerBalanceRules.ValidateVersion(request.ExpectedVersion ?? 0, existing.Version); }
                catch (InvalidOperationException) { throw new PartnerBalanceConflictException("Dokumen sudah berubah. Muat ulang lalu simpan kembali."); }
                if (existing.Status != "POSTED")
                    throw new PartnerBalanceConflictException("Dokumen yang ditandai hapus tidak dapat diedit.");
                if (existing.PartnerType != partnerType || existing.Kind != kind)
                    throw new PartnerBalanceValidationException("Jenis partner dan jenis dokumen tidak dapat diubah.");
                await EnsureEditableAsync(connection, transaction, existing, cancellationToken);
            }
            else if (request.ExpectedVersion is not null)
            {
                throw new PartnerBalanceValidationException("Versi hanya diisi saat mengubah dokumen.");
            }

            var partner = await ReadPartnerAsync(connection, transaction, partnerType, partnerCode, cancellationToken)
                ?? throw new PartnerBalanceValidationException($"{PartnerLabel(partnerType)} {partnerCode} tidak ditemukan atau nonaktif.");

            if (kind == PartnerBalanceRules.Correction && direction == PartnerBalanceRules.Decrease)
            {
                if (existing?.Id == request.TargetSourceId)
                    throw new PartnerBalanceValidationException("Dokumen koreksi tidak boleh mengoreksi dirinya sendiri.");
                var source = await LockSourceAsync(
                    connection,
                    transaction,
                    partnerType,
                    partner.Id,
                    targetSourceType!,
                    request.TargetSourceId!.Value,
                    existing?.Id,
                    cancellationToken);
                if (source is null)
                    throw new PartnerBalanceValidationException("Bukti yang dikoreksi tidak ditemukan, tidak aktif, atau bukan milik partner ini.");
                try { PartnerBalanceRules.ValidateDecrease(amount, source.Remaining); }
                catch (InvalidOperationException exception) { throw new PartnerBalanceValidationException($"{exception.Message} Sisa {source.DocumentNumber}: {source.Remaining:N2}."); }
            }

            var controlMapping = partnerType == "SUPPLIER" ? "PURCHASE_PAYABLE" : "SALES_RECEIVABLE";
            var offsetMapping = kind == PartnerBalanceRules.Opening
                ? "OPENING_BALANCE_EQUITY"
                : partnerType == "SUPPLIER" ? "PAYABLE_CORRECTION" : "RECEIVABLE_CORRECTION";
            var controlAccount = await ReadMappingAccountAsync(connection, transaction, controlMapping, cancellationToken);
            var offsetAccount = await ReadMappingAccountAsync(connection, transaction, offsetMapping, cancellationToken);
            var controlAccountId = await ReadAccountIdAsync(connection, transaction, controlAccount, cancellationToken)
                ?? throw new PartnerBalanceValidationException($"Perkiraan {controlAccount} tidak ditemukan atau tidak dapat diposting.");
            var offsetAccountId = await ReadAccountIdAsync(connection, transaction, offsetAccount, cancellationToken)
                ?? throw new PartnerBalanceValidationException($"Perkiraan {offsetAccount} tidak ditemukan atau tidak dapat diposting.");

            var (sequenceType, prefix) = Numbering(partnerType, kind);
            var number = requestedNumber ?? await NextNumberAsync(connection, transaction, sequenceType, prefix, request.DocumentDate, cancellationToken);
            if (await NumberExistsAsync(connection, transaction, number, request.DocumentDate.Year, existing?.Id, cancellationToken))
                throw new PartnerBalanceConflictException($"No. Bukti {number} sudah dipakai.");

            var id = existing?.Id ?? Guid.NewGuid();
            var module = SourceModule(partnerType, kind);
            var label = DocumentLabel(partnerType, kind);
            var memo = $"{label} {partner.Code} - {partner.Name}";
            var journalRules = PartnerBalanceRules.BuildJournal(partnerType, direction, amount, controlAccount, offsetAccount);

            Guid journalId;
            string journalNumber;
            if (existing is null)
            {
                journalNumber = await LedgerRepository.NextDocumentNumberAsync(connection, transaction, JournalSequenceType(partnerType, kind), JournalPrefix(partnerType, kind), request.DocumentDate, cancellationToken);
                journalId = await InsertJournalAsync(connection, transaction, journalNumber, request.DocumentDate, module, id, memo, request.UserName, request.ComputerName, cancellationToken);
            }
            else
            {
                journalId = existing.JournalId;
                journalNumber = existing.JournalNumber;
                await UpdateJournalAsync(connection, transaction, journalId, request.DocumentDate, memo, request.UserName, request.ComputerName, cancellationToken);
            }
            await ReplaceJournalLinesAsync(connection, transaction, journalId, controlAccountId, offsetAccountId, memo, journalRules, controlAccount, cancellationToken);

            if (existing is null)
            {
                const string insert = """
                    insert into partner_balance_document(
                        id, partner_type, document_kind, direction, document_number, document_year, document_date,
                        supplier_id, customer_id, due_date, amount, target_source_type, target_source_id, notes,
                        journal_entry_id, created_by_name, computer_name)
                    values (@id,@partnerType,@kind,@direction,@number,@year,@date,@supplier,@customer,@due,@amount,
                        @targetType,@targetId,@notes,@journal,@user,@computer);
                    """;
                await using var command = new NpgsqlCommand(insert, connection, transaction);
                command.Parameters.AddWithValue("id", id);
                command.Parameters.AddWithValue("partnerType", partnerType);
                command.Parameters.AddWithValue("kind", kind);
                command.Parameters.AddWithValue("direction", direction);
                command.Parameters.AddWithValue("number", number);
                command.Parameters.AddWithValue("year", (short)request.DocumentDate.Year);
                command.Parameters.AddWithValue("date", request.DocumentDate);
                AddNullable(command, "supplier", NpgsqlDbType.Uuid, partnerType == "SUPPLIER" ? partner.Id : null);
                AddNullable(command, "customer", NpgsqlDbType.Uuid, partnerType == "CUSTOMER" ? partner.Id : null);
                AddNullable(command, "due", NpgsqlDbType.Date, request.DueDate);
                command.Parameters.AddWithValue("amount", amount);
                AddNullable(command, "targetType", NpgsqlDbType.Varchar, targetSourceType);
                AddNullable(command, "targetId", NpgsqlDbType.Uuid, request.TargetSourceId);
                AddNullable(command, "notes", NpgsqlDbType.Text, Trimmed(request.Notes));
                command.Parameters.AddWithValue("journal", journalId);
                AddNullable(command, "user", NpgsqlDbType.Varchar, Trimmed(request.UserName));
                AddNullable(command, "computer", NpgsqlDbType.Varchar, Trimmed(request.ComputerName));
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            else
            {
                const string update = """
                    update partner_balance_document set
                        direction=@direction, document_number=@number, document_year=@year, document_date=@date,
                        supplier_id=@supplier, customer_id=@customer, due_date=@due, amount=@amount,
                        target_source_type=@targetType, target_source_id=@targetId, notes=@notes,
                        updated_at=now(), updated_by_name=@user, computer_name=@computer, version=version+1
                    where id=@id and version=@version and status='POSTED';
                    """;
                await using var command = new NpgsqlCommand(update, connection, transaction);
                command.Parameters.AddWithValue("direction", direction);
                command.Parameters.AddWithValue("number", number);
                command.Parameters.AddWithValue("year", (short)request.DocumentDate.Year);
                command.Parameters.AddWithValue("date", request.DocumentDate);
                AddNullable(command, "supplier", NpgsqlDbType.Uuid, partnerType == "SUPPLIER" ? partner.Id : null);
                AddNullable(command, "customer", NpgsqlDbType.Uuid, partnerType == "CUSTOMER" ? partner.Id : null);
                AddNullable(command, "due", NpgsqlDbType.Date, request.DueDate);
                command.Parameters.AddWithValue("amount", amount);
                AddNullable(command, "targetType", NpgsqlDbType.Varchar, targetSourceType);
                AddNullable(command, "targetId", NpgsqlDbType.Uuid, request.TargetSourceId);
                AddNullable(command, "notes", NpgsqlDbType.Text, Trimmed(request.Notes));
                AddNullable(command, "user", NpgsqlDbType.Varchar, Trimmed(request.UserName));
                AddNullable(command, "computer", NpgsqlDbType.Varchar, Trimmed(request.ComputerName));
                command.Parameters.AddWithValue("id", existing.Id);
                command.Parameters.AddWithValue("version", existing.Version);
                if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
                    throw new PartnerBalanceConflictException("Dokumen berubah saat disimpan. Muat ulang lalu coba kembali.");
            }

            await LedgerRepository.InsertAuditAsync(connection, transaction, "PARTNER_BALANCE", id.ToString(), existing is null ? "CREATE" : "UPDATE", request.UserName, request.ComputerName, request, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return (await GetAsync(id, cancellationToken))!;
        }
        catch (PostgresException exception) when (exception.SqlState is PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.UniqueViolation)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new PartnerBalanceConflictException("Dokumen saldo berubah saat disimpan. Muat ulang lalu coba kembali.");
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<PartnerBalanceDocumentDto> CancelAsync(Guid id, CancelPartnerBalanceDocumentRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Reason)) throw new PartnerBalanceValidationException("Alasan hapus wajib diisi.");
        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        await using (var protectedCommand = new NpgsqlCommand("select exists(select 1 from initial_setup_posting where entity_type='PARTNER_BALANCE' and entity_id=@id);", connection))
        {
            protectedCommand.Parameters.AddWithValue("id", id);
            if ((bool)(await protectedCommand.ExecuteScalarAsync(cancellationToken))!)
                throw new PartnerBalanceValidationException("Saldo awal dari Setup Data Awal hanya dapat diubah melalui reopen setup atau transaksi koreksi.");
        }
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            var existing = await LockExistingAsync(connection, transaction, id, cancellationToken)
                ?? throw new PartnerBalanceValidationException("Dokumen saldo tidak ditemukan.");
            try { PartnerBalanceRules.ValidateVersion(request.ExpectedVersion, existing.Version); }
            catch (InvalidOperationException) { throw new PartnerBalanceConflictException("Dokumen sudah berubah. Muat ulang lalu coba kembali."); }
            if (existing.Status != "POSTED") throw new PartnerBalanceConflictException("Dokumen sudah ditandai hapus.");
            if (existing.Direction == PartnerBalanceRules.Increase)
            {
                var usage = await ReadPositiveUsageAsync(connection, transaction, existing.PartnerType, existing.Id, cancellationToken);
                if (usage > 0) throw new PartnerBalanceConflictException($"Dokumen sudah digunakan sebesar {usage:N2}, sehingga tidak dapat ditandai hapus.");
            }

            await InventoryLedgerPosting.CancelAsync(connection, transaction, id, SourceModule(existing.PartnerType, existing.Kind), request.UserName, request.Reason, cancellationToken);
            const string sql = """
                update partner_balance_document set status='CANCELLED', cancelled_at=now(), cancelled_by_name=@user,
                    cancellation_reason=@reason, version=version+1 where id=@id and version=@version and status='POSTED';
                """;
            await using (var command = new NpgsqlCommand(sql, connection, transaction))
            {
                command.Parameters.AddWithValue("id", id);
                command.Parameters.AddWithValue("version", request.ExpectedVersion);
                command.Parameters.AddWithValue("user", Db(request.UserName));
                command.Parameters.AddWithValue("reason", request.Reason.Trim());
                if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) throw new PartnerBalanceConflictException("Dokumen berubah saat ditandai hapus.");
            }
            await LedgerRepository.InsertAuditAsync(connection, transaction, "PARTNER_BALANCE", id.ToString(), "CANCEL", request.UserName, request.ComputerName, new { request.Reason, request.ExpectedVersion }, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return (await GetAsync(id, cancellationToken))!;
        }
        catch { await transaction.RollbackAsync(cancellationToken); throw; }
    }

    public async Task<PartnerBalanceDocumentDto> RestoreAsync(Guid id, RestorePartnerBalanceDocumentRequest request, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            var existing = await LockExistingAsync(connection, transaction, id, cancellationToken)
                ?? throw new PartnerBalanceValidationException("Dokumen saldo tidak ditemukan.");
            try { PartnerBalanceRules.ValidateVersion(request.ExpectedVersion, existing.Version); }
            catch (InvalidOperationException) { throw new PartnerBalanceConflictException("Dokumen sudah berubah. Muat ulang lalu coba kembali."); }
            if (existing.Status != "CANCELLED") throw new PartnerBalanceConflictException("Dokumen belum ditandai hapus.");
            if (existing.Direction == PartnerBalanceRules.Decrease)
            {
                var partnerId = existing.PartnerType == "SUPPLIER" ? existing.SupplierId : existing.CustomerId;
                var source = partnerId is null || existing.TargetSourceType is null || existing.TargetSourceId is null
                    ? null
                    : await LockSourceAsync(connection, transaction, existing.PartnerType, partnerId.Value, existing.TargetSourceType, existing.TargetSourceId.Value, existing.Id, cancellationToken);
                if (source is null) throw new PartnerBalanceConflictException("Bukti sumber koreksi sudah tidak aktif.");
                try { PartnerBalanceRules.ValidateDecrease(existing.Amount, source.Remaining); }
                catch (InvalidOperationException exception) { throw new PartnerBalanceConflictException($"{exception.Message} Sisa {source.DocumentNumber}: {source.Remaining:N2}."); }
            }

            await InventoryLedgerPosting.RestoreAsync(connection, transaction, id, SourceModule(existing.PartnerType, existing.Kind), cancellationToken);
            const string sql = """
                update partner_balance_document set status='POSTED', cancelled_at=null, cancelled_by_name=null,
                    cancellation_reason=null, version=version+1 where id=@id and version=@version and status='CANCELLED';
                """;
            await using (var command = new NpgsqlCommand(sql, connection, transaction))
            {
                command.Parameters.AddWithValue("id", id);
                command.Parameters.AddWithValue("version", request.ExpectedVersion);
                if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) throw new PartnerBalanceConflictException("Dokumen berubah saat batal hapus.");
            }
            await LedgerRepository.InsertAuditAsync(connection, transaction, "PARTNER_BALANCE", id.ToString(), "RESTORE", request.UserName, request.ComputerName, new { request.ExpectedVersion }, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return (await GetAsync(id, cancellationToken))!;
        }
        catch { await transaction.RollbackAsync(cancellationToken); throw; }
    }

    private static async Task<PartnerBalanceDocumentDto?> ReadAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid id, CancellationToken cancellationToken)
    {
        const string sql = """
            with settlement as (
                select balance_document_id id, sum(amount) amount from supplier_payment_line l
                join supplier_payment h on h.id=l.supplier_payment_id
                where h.status='POSTED' and l.balance_document_id is not null group by balance_document_id
                union all
                select balance_document_id id, sum(amount) amount from customer_receipt_line l
                join customer_receipt h on h.id=l.customer_receipt_id
                where h.status='POSTED' and l.balance_document_id is not null group by balance_document_id
            ), settled as (select id,sum(amount) amount from settlement group by id),
            reduced as (
                select target_source_id id, sum(amount) amount from partner_balance_document
                where status='POSTED' and direction='DECREASE' and target_source_type='BALANCE_DOCUMENT'
                group by target_source_id
            )
            select d.id,d.partner_type,d.document_kind,d.direction,d.document_number,d.document_date,
                   coalesce(s.code,c.code),coalesce(s.company_name,c.company_name),coalesce(s.address,c.address),d.due_date,
                   d.amount,coalesce(settled.amount,0),coalesce(reduced.amount,0),
                   case when d.direction='INCREASE' then d.amount-coalesce(settled.amount,0)-coalesce(reduced.amount,0) else 0 end,
                   d.target_source_type,d.target_source_id,
                   coalesce(tp.document_number,ts.document_number,tb.document_number),d.notes,d.status,je.document_number,d.created_at,d.version
            from partner_balance_document d
            left join supplier s on s.id=d.supplier_id left join customer c on c.id=d.customer_id
            left join settled on settled.id=d.id left join reduced on reduced.id=d.id
            left join purchase tp on d.target_source_type='PURCHASE' and tp.id=d.target_source_id
            left join sale ts on d.target_source_type='SALE' and ts.id=d.target_source_id
            left join partner_balance_document tb on d.target_source_type='BALANCE_DOCUMENT' and tb.id=d.target_source_id
            join journal_entry je on je.id=d.journal_entry_id
            where d.id=@id;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new(
            reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetFieldValue<DateOnly>(5),
            reader.GetString(6), reader.GetString(7), Text(reader, 8), reader.IsDBNull(9) ? null : reader.GetFieldValue<DateOnly>(9),
            reader.GetDecimal(10), reader.GetDecimal(11), reader.GetDecimal(12), reader.GetDecimal(13), Text(reader, 14),
            reader.IsDBNull(15) ? null : reader.GetGuid(15), Text(reader, 16), Text(reader, 17), reader.GetString(18), reader.GetString(19),
            reader.GetFieldValue<DateTimeOffset>(20), reader.GetInt64(21));
    }

    private static async Task<ExistingDocument?> LockExistingAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, CancellationToken cancellationToken)
    {
        const string sql = """
            select d.id,d.partner_type,d.document_kind,d.direction,d.amount,d.status,d.version,d.journal_entry_id,je.document_number,
                   d.supplier_id,d.customer_id,d.target_source_type,d.target_source_id
            from partner_balance_document d join journal_entry je on je.id=d.journal_entry_id
            where d.id=@id for update of d,je;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? new(
            reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetDecimal(4), reader.GetString(5),
            reader.GetInt64(6), reader.GetGuid(7), reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetGuid(9),
            reader.IsDBNull(10) ? null : reader.GetGuid(10), Text(reader, 11), reader.IsDBNull(12) ? null : reader.GetGuid(12)) : null;
    }

    private static async Task EnsureEditableAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, ExistingDocument document, CancellationToken cancellationToken)
    {
        if (document.Direction == PartnerBalanceRules.Increase)
        {
            var usage = await ReadPositiveUsageAsync(connection, transaction, document.PartnerType, document.Id, cancellationToken);
            if (usage > 0) throw new PartnerBalanceConflictException($"Dokumen sudah digunakan sebesar {usage:N2}. Buat koreksi baru agar riwayat tetap utuh.");
        }
    }

    private static async Task<decimal> ReadPositiveUsageAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string partnerType, Guid id, CancellationToken cancellationToken)
    {
        var settlementTable = partnerType == "SUPPLIER" ? "supplier_payment_line" : "customer_receipt_line";
        var headerTable = partnerType == "SUPPLIER" ? "supplier_payment" : "customer_receipt";
        var headerForeignKey = partnerType == "SUPPLIER" ? "supplier_payment_id" : "customer_receipt_id";
        var sql = $"""
            select
              coalesce((select sum(l.amount) from {settlementTable} l join {headerTable} h on h.id=l.{headerForeignKey}
                        where l.balance_document_id=@id and h.status='POSTED'),0)
              + coalesce((select sum(amount) from partner_balance_document
                          where target_source_type='BALANCE_DOCUMENT' and target_source_id=@id and status='POSTED' and direction='DECREASE'),0);
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("id", id);
        return Convert.ToDecimal(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<SourceBalance?> LockSourceAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string partnerType, Guid partnerId,
        string sourceType, Guid sourceId, Guid? excludingCorrectionId, CancellationToken cancellationToken)
    {
        var sourceSql = (partnerType, sourceType) switch
        {
            ("SUPPLIER", "PURCHASE") => "select document_number,balance_due from purchase where id=@id and supplier_id=@partner and status='POSTED' and transaction_type='CREDIT' for update",
            ("CUSTOMER", "SALE") => "select document_number,balance_due from sale where id=@id and customer_id=@partner and status='POSTED' and transaction_type='CREDIT' for update",
            ("SUPPLIER", "BALANCE_DOCUMENT") => "select document_number,amount from partner_balance_document where id=@id and supplier_id=@partner and partner_type='SUPPLIER' and direction='INCREASE' and status='POSTED' for update",
            ("CUSTOMER", "BALANCE_DOCUMENT") => "select document_number,amount from partner_balance_document where id=@id and customer_id=@partner and partner_type='CUSTOMER' and direction='INCREASE' and status='POSTED' for update",
            _ => throw new PartnerBalanceValidationException("Jenis bukti koreksi tidak sesuai dengan partner.")
        };
        string number;
        decimal original;
        await using (var command = new NpgsqlCommand(sourceSql, connection, transaction))
        {
            command.Parameters.AddWithValue("id", sourceId);
            command.Parameters.AddWithValue("partner", partnerId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return null;
            number = reader.GetString(0);
            original = reader.GetDecimal(1);
        }

        var (lineTable, headerTable, headerKey, sourceColumn) = partnerType == "SUPPLIER"
            ? ("supplier_payment_line", "supplier_payment", "supplier_payment_id", sourceType == "PURCHASE" ? "purchase_id" : "balance_document_id")
            : ("customer_receipt_line", "customer_receipt", "customer_receipt_id", sourceType == "SALE" ? "sale_id" : "balance_document_id");
        var excludeSql = excludingCorrectionId is null ? string.Empty : " and d.id<>@exclude";
        var usageSql = $"""
            select
              coalesce((select sum(l.amount) from {lineTable} l join {headerTable} h on h.id=l.{headerKey}
                        where l.{sourceColumn}=@id and h.status='POSTED'),0),
              coalesce((select sum(d.amount) from partner_balance_document d
                        where d.target_source_type=@sourceType and d.target_source_id=@id and d.status='POSTED'
                          and d.direction='DECREASE'{excludeSql}),0);
            """;
        decimal settled;
        decimal reduced;
        await using (var command = new NpgsqlCommand(usageSql, connection, transaction))
        {
            command.Parameters.AddWithValue("id", sourceId);
            command.Parameters.AddWithValue("sourceType", sourceType);
            if (excludingCorrectionId is Guid excludeId) command.Parameters.AddWithValue("exclude", excludeId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            settled = reader.GetDecimal(0);
            reduced = reader.GetDecimal(1);
        }
        return new(number, original, settled, reduced, PartnerBalanceRules.Remaining(original, settled, reduced));
    }

    private static async Task<(Guid Id, string Code, string Name)?> ReadPartnerAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string partnerType, string code, CancellationToken cancellationToken)
    {
        var table = partnerType == "SUPPLIER" ? "supplier" : "customer";
        await using var command = new NpgsqlCommand($"select id,code,company_name from {table} where code=@code and is_active for share;", connection, transaction);
        command.Parameters.AddWithValue("code", code);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? (reader.GetGuid(0), reader.GetString(1), reader.GetString(2)) : null;
    }

    private static async Task<string> ReadMappingAccountAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string mappingCode, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("select account_code from ledger_transaction_mapping where mapping_code=@code and account_code is not null;", connection, transaction);
        command.Parameters.AddWithValue("code", mappingCode);
        var value = await command.ExecuteScalarAsync(cancellationToken) as string;
        return !string.IsNullOrWhiteSpace(value) ? value : throw new PartnerBalanceValidationException($"Mapping jurnal {mappingCode} belum dikonfigurasi di Ledger > Penetapan Kode Perkiraan Jurnal Transaksi.");
    }

    private static async Task<Guid?> ReadAccountIdAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string code, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("select id from ledger_account where code=@code and is_active and is_postable;", connection, transaction);
        command.Parameters.AddWithValue("code", code);
        return await command.ExecuteScalarAsync(cancellationToken) as Guid?;
    }

    private static async Task<Guid> InsertJournalAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string number, DateOnly date, string module, Guid sourceId, string memo, string? user, string? computer, CancellationToken cancellationToken)
    {
        const string sql = """
            insert into journal_entry(document_number,document_year,document_date,memo,source_module,source_id,created_by_name,computer_name)
            values(@number,@year,@date,@memo,@module,@source,@user,@computer) returning id;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("number", number); command.Parameters.AddWithValue("year", (short)date.Year);
        command.Parameters.AddWithValue("date", date); command.Parameters.AddWithValue("memo", memo); command.Parameters.AddWithValue("module", module);
        command.Parameters.AddWithValue("source", sourceId); command.Parameters.AddWithValue("user", Db(user)); command.Parameters.AddWithValue("computer", Db(computer));
        return (Guid)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task UpdateJournalAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid journalId, DateOnly date, string memo, string? user, string? computer, CancellationToken cancellationToken)
    {
        const string sql = "update journal_entry set document_date=@date,document_year=@year,memo=@memo,created_by_name=@user,computer_name=@computer,version=version+1 where id=@id and status='POSTED';";
        await LedgerRepository.ExecuteAsync(connection, transaction, sql, cancellationToken,
            ("date", date), ("year", (short)date.Year), ("memo", memo), ("user", Db(user)), ("computer", Db(computer)), ("id", journalId));
    }

    private static async Task ReplaceJournalLinesAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid journalId, Guid controlId, Guid offsetId,
        string memo, IReadOnlyList<JournalLineRule> rules, string controlCode, CancellationToken cancellationToken)
    {
        await LedgerRepository.ExecuteAsync(connection, transaction, "delete from journal_entry_line where journal_entry_id=@id;", cancellationToken, ("id", journalId));
        for (var index = 0; index < rules.Count; index++)
        {
            var rule = rules[index];
            var accountId = string.Equals(rule.AccountCode, controlCode, StringComparison.OrdinalIgnoreCase) ? controlId : offsetId;
            const string sql = "insert into journal_entry_line(journal_entry_id,line_number,account_id,description,debit,credit) values(@journal,@line,@account,@memo,@debit,@credit);";
            await LedgerRepository.ExecuteAsync(connection, transaction, sql, cancellationToken,
                ("journal", journalId), ("line", index + 1), ("account", accountId), ("memo", memo), ("debit", rule.Debit), ("credit", rule.Credit));
        }
    }

    private static async Task<bool> NumberExistsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string number, int year, Guid? excludingId, CancellationToken cancellationToken)
    {
        var excludeSql = excludingId is null ? string.Empty : " and id<>@id";
        await using var command = new NpgsqlCommand($"select exists(select 1 from partner_balance_document where document_number=@number and document_year=@year{excludeSql});", connection, transaction);
        command.Parameters.AddWithValue("number", number); command.Parameters.AddWithValue("year", (short)year);
        if (excludingId is Guid id) command.Parameters.AddWithValue("id", id);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task<string> NextNumberAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string type, string prefix, DateOnly date, CancellationToken cancellationToken)
    {
        var seed = await ReadSequenceSeedAsync(connection, transaction, type, prefix, date, cancellationToken);
        const string sql = """
            insert into document_sequence(document_type,period,last_number) values(@type,@period,@seed+1)
            on conflict(document_type,period) do update set last_number=greatest(document_sequence.last_number,@seed)+1 returning last_number;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("type", type); command.Parameters.AddWithValue("period", date.ToString("yyyyMM", CultureInfo.InvariantCulture)); command.Parameters.AddWithValue("seed", seed);
        var sequence = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
        return $"{prefix}{date:yyMM}{sequence:000}";
    }

    private static async Task<int> ReadSequenceSeedAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string type, string prefix, DateOnly date, CancellationToken cancellationToken)
    {
        const string sql = """
            select greatest(
                coalesce((select last_number from document_sequence where document_type=@type and period=@period),0),
                coalesce((select max(nullif(regexp_replace(substring(document_number from char_length(@fullPrefix)+1),'\D','','g'),'')::integer)
                          from partner_balance_document where document_year=@year and document_number like @fullPrefix || '%'),0));
            """;
        var fullPrefix = $"{prefix}{date:yyMM}";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("type", type); command.Parameters.AddWithValue("period", date.ToString("yyyyMM", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("fullPrefix", fullPrefix); command.Parameters.AddWithValue("year", (short)date.Year);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static (string Type, string Prefix) Numbering(string partnerType, string kind) => (partnerType, kind) switch
    {
        ("SUPPLIER", "OPENING") => ("SUPPLIER_OPENING", "UAW"),
        ("CUSTOMER", "OPENING") => ("CUSTOMER_OPENING", "PAW"),
        ("SUPPLIER", "CORRECTION") => ("SUPPLIER_CORRECTION", "KRU"),
        _ => ("CUSTOMER_CORRECTION", "KRP")
    };

    private static string SourceModule(string partnerType, string kind) => $"{partnerType}_{kind}";
    private static string JournalSequenceType(string partnerType, string kind) => (partnerType, kind) switch
    {
        ("SUPPLIER", "OPENING") => "SUPPLIER_OPENING_JRN",
        ("CUSTOMER", "OPENING") => "CUSTOMER_OPENING_JRN",
        ("SUPPLIER", "CORRECTION") => "SUPPLIER_CORR_JRN",
        _ => "CUSTOMER_CORR_JRN"
    };
    private static string JournalPrefix(string partnerType, string kind) => (partnerType, kind) switch
    {
        ("SUPPLIER", "OPENING") => "JUAW", ("CUSTOMER", "OPENING") => "JPAW",
        ("SUPPLIER", "CORRECTION") => "JKRU", _ => "JKRP"
    };
    private static string DocumentLabel(string partnerType, string kind) => (partnerType, kind) switch
    {
        ("SUPPLIER", "OPENING") => "Utang awal", ("CUSTOMER", "OPENING") => "Piutang awal",
        ("SUPPLIER", "CORRECTION") => "Koreksi utang", _ => "Koreksi piutang"
    };
    private static string PartnerLabel(string partnerType) => partnerType == "SUPPLIER" ? "Supplier" : "Customer";
    private static string NormalizePartnerType(string value) => NormalizeChoice(value, "Jenis partner", "SUPPLIER", "CUSTOMER");
    private static string NormalizeKind(string value) => NormalizeChoice(value, "Jenis dokumen", "OPENING", "CORRECTION");
    private static string NormalizeDirection(string value) => NormalizeChoice(value, "Arah koreksi", "INCREASE", "DECREASE");
    private static string NormalizeSourceType(string value, string partnerType) => partnerType == "SUPPLIER"
        ? NormalizeChoice(value, "Jenis bukti", "PURCHASE", "BALANCE_DOCUMENT")
        : NormalizeChoice(value, "Jenis bukti", "SALE", "BALANCE_DOCUMENT");
    private static string NormalizeChoice(string value, string label, params string[] allowed)
    {
        var text = value?.Trim().ToUpperInvariant() ?? string.Empty;
        return allowed.Contains(text) ? text : throw new PartnerBalanceValidationException($"{label} tidak valid.");
    }
    private static string NormalizeCode(string value, string label, int max)
    {
        var text = value?.Trim().ToUpperInvariant() ?? string.Empty;
        if (text.Length == 0) throw new PartnerBalanceValidationException($"{label} wajib diisi.");
        if (text.Length > max) throw new PartnerBalanceValidationException($"{label} maksimal {max} karakter.");
        return text;
    }
    private static string? NormalizeDocumentNumber(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrWhiteSpace(text) || text.Equals("OTOMATIS", StringComparison.OrdinalIgnoreCase)) return null;
        if (text.Length > 30) throw new PartnerBalanceValidationException("No. Bukti maksimal 30 karakter.");
        return text.ToUpperInvariant();
    }
    private static object Db(string? value) => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();
    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static void AddNullable(NpgsqlCommand command, string name, NpgsqlDbType type, object? value) =>
        command.Parameters.Add(new NpgsqlParameter(name, type) { Value = value ?? DBNull.Value });
    private static string? Text(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private sealed record ExistingDocument(Guid Id, string PartnerType, string Kind, string Direction, decimal Amount, string Status, long Version, Guid JournalId, string JournalNumber, Guid? SupplierId, Guid? CustomerId, string? TargetSourceType, Guid? TargetSourceId);
    private sealed record SourceBalance(string DocumentNumber, decimal Original, decimal Settled, decimal Reduced, decimal Remaining);
}
