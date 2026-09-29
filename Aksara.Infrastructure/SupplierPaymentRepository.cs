using System.Data;
using System.Globalization;
using System.Text.Json;
using Aksara.Contracts;
using Aksara.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Aksara.Infrastructure;

public sealed class SupplierPaymentRepository(PostgresDatabase database)
{
    public async Task<IReadOnlyList<SupplierPayableDto>> ListPayablesAsync(string? supplierCode, string? query, int limit = 100, CancellationToken cancellationToken = default)
    {
        const string sql = """
            with purchase_paid as (
                select pl.purchase_id source_id, coalesce(sum(pl.amount), 0) paid_amount
                from supplier_payment_line pl
                join supplier_payment sp on sp.id = pl.supplier_payment_id
                where sp.status = 'POSTED' and pl.source_type = 'PURCHASE'
                group by pl.purchase_id
            ), balance_paid as (
                select pl.balance_document_id source_id, coalesce(sum(pl.amount), 0) paid_amount
                from supplier_payment_line pl
                join supplier_payment sp on sp.id = pl.supplier_payment_id
                where sp.status = 'POSTED' and pl.source_type = 'BALANCE_DOCUMENT'
                group by pl.balance_document_id
            ), purchase_reduced as (
                select target_source_id source_id, coalesce(sum(amount),0) reduced_amount
                from partner_balance_document
                where partner_type='SUPPLIER' and direction='DECREASE' and status='POSTED' and target_source_type='PURCHASE'
                group by target_source_id
            ), balance_reduced as (
                select target_source_id source_id, coalesce(sum(amount),0) reduced_amount
                from partner_balance_document
                where partner_type='SUPPLIER' and direction='DECREASE' and status='POSTED' and target_source_type='BALANCE_DOCUMENT'
                group by target_source_id
            ), sources as (
                select p.id, p.document_number, p.document_date, s.code supplier_code, s.company_name supplier_name,
                       p.supplier_invoice_number, p.due_date, p.grand_total, p.down_payment,
                       coalesce(pp.paid_amount,0) paid_amount,
                       p.balance_due-coalesce(pr.reduced_amount,0) balance_due,
                       p.balance_due-coalesce(pr.reduced_amount,0)-coalesce(pp.paid_amount,0) remaining_balance,
                       'PURCHASE' source_type, coalesce(pr.reduced_amount,0) correction_amount, 'Pembelian' source_label
                from purchase p join supplier s on s.id=p.supplier_id
                left join purchase_paid pp on pp.source_id=p.id left join purchase_reduced pr on pr.source_id=p.id
                where p.status='POSTED' and p.transaction_type='CREDIT'
                union all
                select d.id,d.document_number,d.document_date,s.code,s.company_name,null,d.due_date,d.amount,0,
                       coalesce(bp.paid_amount,0),d.amount-coalesce(br.reduced_amount,0),
                       d.amount-coalesce(br.reduced_amount,0)-coalesce(bp.paid_amount,0),
                       'BALANCE_DOCUMENT',coalesce(br.reduced_amount,0),
                       case d.document_kind when 'OPENING' then 'Utang Awal' else 'Koreksi Utang' end
                from partner_balance_document d join supplier s on s.id=d.supplier_id
                left join balance_paid bp on bp.source_id=d.id left join balance_reduced br on br.source_id=d.id
                where d.partner_type='SUPPLIER' and d.direction='INCREASE' and d.status='POSTED'
            )
            select * from sources
            where remaining_balance > 0
              and (@supplier = '' or supplier_code = @supplier)
              and (@query = '' or document_number ilike '%' || @query || '%'
                   or coalesce(supplier_invoice_number, '') ilike '%' || @query || '%'
                   or supplier_code ilike '%' || @query || '%' or supplier_name ilike '%' || @query || '%')
            order by document_date, document_number
            limit @limit;
            """;
        await using var command = database.DataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("supplier", supplierCode?.Trim().ToUpperInvariant() ?? string.Empty);
        command.Parameters.AddWithValue("query", query?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 500));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<SupplierPayableDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetFieldValue<DateOnly>(2),
                reader.GetString(3),
                reader.GetString(4),
                Text(reader, 5),
                reader.IsDBNull(6) ? null : reader.GetFieldValue<DateOnly>(6),
                reader.GetDecimal(7),
                reader.GetDecimal(8),
                reader.GetDecimal(9),
                reader.GetDecimal(10),
                reader.GetDecimal(11),
                reader.GetString(12),
                reader.GetDecimal(13),
                reader.GetString(14)));
        }

        return result;
    }

    public async Task<IReadOnlyList<SupplierPaymentDto>> ListPaymentsAsync(string? query, int limit = 100, CancellationToken cancellationToken = default)
    {
        const string sql = """
            select sp.id
            from supplier_payment sp
            join supplier s on s.id = sp.supplier_id
            where @query = ''
               or sp.document_number ilike '%' || @query || '%'
               or s.code ilike '%' || @query || '%'
               or s.company_name ilike '%' || @query || '%'
               or coalesce(sp.notes, '') ilike '%' || @query || '%'
            order by sp.document_date desc, sp.document_number desc
            limit @limit;
            """;
        var ids = new List<Guid>();
        await using (var command = database.DataSource.CreateCommand(sql))
        {
            command.Parameters.AddWithValue("query", query?.Trim() ?? string.Empty);
            command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 500));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) ids.Add(reader.GetGuid(0));
        }

        var result = new List<SupplierPaymentDto>(ids.Count);
        foreach (var id in ids)
        {
            var item = await GetPaymentAsync(id, cancellationToken);
            if (item is not null) result.Add(item);
        }

        return result;
    }

    public async Task<SupplierPaymentDto?> GetPaymentAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        return await ReadPaymentAsync(connection, null, id, cancellationToken);
    }

    public async Task<string> PreviewNextPaymentNumberAsync(DateOnly documentDate, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        return await PreviewNextPaymentNumberAsync(connection, null, documentDate, cancellationToken);
    }

    public async Task<SupplierPaymentDto> CreatePaymentAsync(CreateSupplierPaymentRequest request, CancellationToken cancellationToken = default)
    {
        var supplierCode = NormalizeCode(request.SupplierCode, "Kode supplier", 20);
        var method = NormalizeMethod(request.Method);
        var paymentAmount = Math.Round(request.PaymentAmount, 2);
        if (paymentAmount <= 0) throw new PurchaseValidationException("Skrg bayar harus lebih dari nol.");
        if (request.Lines.Count == 0) throw new PurchaseValidationException("Minimal satu nota utang harus dipilih.");

        var cashCode = string.IsNullOrWhiteSpace(request.CashAccountCode) ? null : NormalizeCode(request.CashAccountCode, "Kode kas", 20);
        var bankCode = string.IsNullOrWhiteSpace(request.BankAccountCode) ? null : NormalizeCode(request.BankAccountCode, "Kode bank", 20);
        if (method == "CASH" && cashCode is null) throw new PurchaseValidationException("Kas wajib dipilih untuk pembayaran tunai.");
        if (method is "BANK" or "CHECK_GIRO" && bankCode is null) throw new PurchaseValidationException("Bank wajib dipilih untuk pembayaran bank/cek-giro.");

        var requested = request.Lines
            .Select(x => x with { SourceType = NormalizeSourceType(x.SourceType) })
            .GroupBy(x => new { x.SourceType, x.PurchaseId })
            .Select((group, index) => new RequestedPaymentLine(index + 1, group.Key.PurchaseId, group.Key.SourceType, Math.Round(group.Sum(x => x.Amount), 2)))
            .Where(x => x.Amount > 0)
            .ToArray();
        if (requested.Length == 0) throw new PurchaseValidationException("Jumlah bayar pada nota belum diisi.");
        var lineTotal = requested.Sum(x => x.Amount);
        if (lineTotal != paymentAmount) throw new PurchaseValidationException("Total bayar nota harus sama dengan Skrg Bayar.");

        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            var supplier = await ReadSupplierAsync(connection, transaction, supplierCode, cancellationToken)
                ?? throw new PurchaseValidationException($"Supplier {supplierCode} tidak ditemukan atau tidak aktif.");
            var settlement = method == "CASH"
                ? await ReadCashAccountAsync(connection, transaction, cashCode!, cancellationToken)
                : await ReadBankAccountAsync(connection, transaction, bankCode!, cancellationToken);
            var payableAccount = await ReadMappingAccountAsync(connection, transaction, "PURCHASE_PAYABLE", cancellationToken);
            var payableAccountId = await ReadAccountIdAsync(connection, transaction, payableAccount, cancellationToken)
                ?? throw new PurchaseValidationException($"Perkiraan utang {payableAccount} tidak ditemukan.");
            var settlementAccountId = await ReadAccountIdAsync(connection, transaction, settlement.AccountCode, cancellationToken)
                ?? throw new PurchaseValidationException($"Perkiraan kas/bank {settlement.AccountCode} tidak ditemukan.");

            var locked = new List<LockedPayable>();
            foreach (var line in requested)
            {
                var payable = await LockPayableAsync(connection, transaction, supplier.Id, line.PurchaseId, line.SourceType, line.Amount, cancellationToken);
                locked.Add(payable with { LineNumber = line.LineNumber });
            }

            var requestedNumber = NormalizeDocumentNumber(request.DocumentNumber);
            var number = requestedNumber ?? await NextPaymentNumberAsync(connection, transaction, request.DocumentDate, cancellationToken);
            if (requestedNumber is not null && await DocumentNumberExistsAsync(connection, transaction, number, request.DocumentDate.Year, cancellationToken))
                throw new PurchaseConflictException($"No. Bukti {number} sudah dipakai.");

            var paymentId = Guid.NewGuid();
            var journalNumber = await LedgerRepository.NextDocumentNumberAsync(connection, transaction, "SUPPLIER_PAY_JRN", "JUTG", request.DocumentDate, cancellationToken);
            var memo = $"Pembayaran utang {supplier.Code} - {supplier.Name}";
            AccountingRules.ValidateBalancedJournal(new[]
            {
                new JournalLineRule(payableAccount, paymentAmount, 0),
                new JournalLineRule(settlement.AccountCode, 0, paymentAmount)
            });

            var journalId = await InsertJournalAsync(connection, transaction, journalNumber, request.DocumentDate, paymentId, memo, request.UserName, request.ComputerName, cancellationToken);
            await InsertJournalLineAsync(connection, transaction, journalId, 1, payableAccountId, memo, paymentAmount, 0, cancellationToken);
            await InsertJournalLineAsync(connection, transaction, journalId, 2, settlementAccountId, memo, 0, paymentAmount, cancellationToken);

            const string paymentSql = """
                insert into supplier_payment(id, document_number, document_year, document_date, supplier_id, method,
                    cash_account_code, bank_account_code, check_giro_number, check_giro_due_date, payment_amount,
                    notes, journal_entry_id, created_by_name, computer_name)
                values (@id, @number, @year, @date, @supplier, @method, @cash, @bank, @giro, @due, @amount,
                    @notes, @journal, @user, @computer);
                """;
            await LedgerRepository.ExecuteAsync(connection, transaction, paymentSql, cancellationToken,
                ("id", paymentId), ("number", number), ("year", (short)request.DocumentDate.Year), ("date", request.DocumentDate),
                ("supplier", supplier.Id), ("method", method), ("cash", Db(cashCode)), ("bank", Db(bankCode)),
                ("giro", Db(request.CheckGiroNumber)), ("due", request.CheckGiroDueDate is null ? DBNull.Value : request.CheckGiroDueDate.Value),
                ("amount", paymentAmount), ("notes", Db(request.Notes)), ("journal", journalId),
                ("user", Db(request.UserName)), ("computer", Db(request.ComputerName)));

            const string lineSql = """
                insert into supplier_payment_line(supplier_payment_id, purchase_id, balance_document_id, source_type, line_number, original_balance, paid_before, amount, remaining_after)
                values (@payment, @purchase, @balance, @sourceType, @line, @original, @before, @amount, @remaining);
                """;
            foreach (var line in locked)
            {
                await LedgerRepository.ExecuteAsync(connection, transaction, lineSql, cancellationToken,
                    ("payment", paymentId), ("purchase", line.SourceType == "PURCHASE" ? line.SourceId : DBNull.Value),
                    ("balance", line.SourceType == "BALANCE_DOCUMENT" ? line.SourceId : DBNull.Value), ("sourceType", line.SourceType), ("line", line.LineNumber),
                    ("original", line.BalanceDue), ("before", line.PaidBefore), ("amount", line.Amount), ("remaining", line.RemainingAfter));
            }

            await LedgerRepository.InsertAuditAsync(connection, transaction, "SUPPLIER_PAYMENT", paymentId.ToString(), "CREATE", request.UserName, request.ComputerName, request, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return (await GetPaymentAsync(paymentId, cancellationToken))!;
        }
        catch (PostgresException exception) when (exception.SqlState is PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.UniqueViolation)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new PurchaseConflictException("Pembayaran utang berubah saat disimpan. Muat ulang lalu coba kembali.");
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<SupplierPaymentDto> CancelPaymentAsync(Guid id, CancelSupplierPaymentRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Reason)) throw new PurchaseValidationException("Alasan hapus wajib diisi.");
        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            const string lockSql = "select document_number, status from supplier_payment where id = @id for update;";
            string number;
            string status;
            await using (var command = new NpgsqlCommand(lockSql, connection, transaction))
            {
                command.Parameters.AddWithValue("id", id);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken)) throw new PurchaseValidationException("Pembayaran utang tidak ditemukan.");
                number = reader.GetString(0);
                status = reader.GetString(1);
            }

            if (!string.Equals(status, "POSTED", StringComparison.OrdinalIgnoreCase))
                throw new PurchaseConflictException("Pembayaran utang sudah ditandai hapus.");

            await InventoryLedgerPosting.CancelAsync(connection, transaction, id, "SUPPLIER_PAYMENT", request.UserName, request.Reason, cancellationToken);

            const string updateSql = """
                update supplier_payment
                set status = 'CANCELLED', cancelled_at = now(), cancelled_by_name = @user,
                    cancellation_reason = @reason, version = version + 1
                where id = @id;
                """;
            await LedgerRepository.ExecuteAsync(connection, transaction, updateSql, cancellationToken,
                ("id", id), ("user", Db(request.UserName)), ("reason", request.Reason.Trim()));

            await LedgerRepository.InsertAuditAsync(connection, transaction, "SUPPLIER_PAYMENT", id.ToString(), "CANCEL", request.UserName, request.ComputerName, new { documentNumber = number, reason = request.Reason.Trim() }, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return (await GetPaymentAsync(id, cancellationToken))!;
        }
        catch (PostgresException exception) when (exception.SqlState is PostgresErrorCodes.SerializationFailure)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new PurchaseConflictException("Pembayaran utang berubah saat ditandai hapus. Muat ulang lalu coba kembali.");
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<SupplierPaymentDto> RestorePaymentAsync(Guid id, RestoreSupplierPaymentRequest request, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            const string lockSql = "select document_number, status from supplier_payment where id = @id for update;";
            string number;
            string status;
            await using (var command = new NpgsqlCommand(lockSql, connection, transaction))
            {
                command.Parameters.AddWithValue("id", id);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken)) throw new PurchaseValidationException("Pembayaran utang tidak ditemukan.");
                number = reader.GetString(0);
                status = reader.GetString(1);
            }

            if (!string.Equals(status, "CANCELLED", StringComparison.OrdinalIgnoreCase))
                throw new PurchaseConflictException("Pembayaran utang belum ditandai hapus.");

            await EnsurePaymentCanBeRestoredAsync(connection, transaction, id, cancellationToken);
            await InventoryLedgerPosting.RestoreAsync(connection, transaction, id, "SUPPLIER_PAYMENT", cancellationToken);

            const string updateSql = """
                update supplier_payment
                set status = 'POSTED', cancelled_at = null, cancelled_by_name = null,
                    cancellation_reason = null, version = version + 1
                where id = @id;
                """;
            await LedgerRepository.ExecuteAsync(connection, transaction, updateSql, cancellationToken, ("id", id));

            await LedgerRepository.InsertAuditAsync(connection, transaction, "SUPPLIER_PAYMENT", id.ToString(), "RESTORE", request.UserName, request.ComputerName, new { documentNumber = number, status = "POSTED" }, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return (await GetPaymentAsync(id, cancellationToken))!;
        }
        catch (PostgresException exception) when (exception.SqlState is PostgresErrorCodes.SerializationFailure)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new PurchaseConflictException("Pembayaran utang berubah saat batal hapus. Muat ulang lalu coba kembali.");
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static async Task<SupplierPaymentDto?> ReadPaymentAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid id, CancellationToken cancellationToken)
    {
        const string headerSql = """
            select sp.id, sp.document_number, sp.document_date, s.code, s.company_name, sp.method,
                   sp.cash_account_code, ca.name, sp.bank_account_code, ba.bank_name, sp.check_giro_number,
                   sp.check_giro_due_date, sp.payment_amount, sp.status, sp.notes, je.document_number, sp.created_at
            from supplier_payment sp
            join supplier s on s.id = sp.supplier_id
            join journal_entry je on je.id = sp.journal_entry_id
            left join cash_account ca on ca.code = sp.cash_account_code
            left join bank_account ba on ba.code = sp.bank_account_code
            where sp.id = @id;
            """;
        SupplierPaymentHeader? header = null;
        await using (var command = new NpgsqlCommand(headerSql, connection, transaction))
        {
            command.Parameters.AddWithValue("id", id);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                header = new(
                    reader.GetGuid(0),
                    reader.GetString(1),
                    reader.GetFieldValue<DateOnly>(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    Text(reader, 6),
                    Text(reader, 7),
                    Text(reader, 8),
                    Text(reader, 9),
                    Text(reader, 10),
                    reader.IsDBNull(11) ? null : reader.GetFieldValue<DateOnly>(11),
                    reader.GetDecimal(12),
                    reader.GetString(13),
                    Text(reader, 14),
                    reader.GetString(15),
                    reader.GetFieldValue<DateTimeOffset>(16));
            }
        }

        if (header is null) return null;

        const string lineSql = """
            select coalesce(p.id,d.id), coalesce(p.document_number,d.document_number), coalesce(p.document_date,d.document_date),
                   l.original_balance, l.paid_before, l.amount, l.remaining_after, l.source_type
            from supplier_payment_line l
            left join purchase p on l.source_type='PURCHASE' and p.id = l.purchase_id
            left join partner_balance_document d on l.source_type='BALANCE_DOCUMENT' and d.id=l.balance_document_id
            where l.supplier_payment_id = @id
            order by l.line_number;
            """;
        var lines = new List<SupplierPaymentLineDto>();
        await using (var command = new NpgsqlCommand(lineSql, connection, transaction))
        {
            command.Parameters.AddWithValue("id", id);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                lines.Add(new(
                    reader.GetGuid(0),
                    reader.GetString(1),
                    reader.GetFieldValue<DateOnly>(2),
                    reader.GetDecimal(3),
                    reader.GetDecimal(4),
                    reader.GetDecimal(5),
                    reader.GetDecimal(6),
                    reader.GetString(7)));
            }
        }

        return new(
            header.Id,
            header.DocumentNumber,
            header.DocumentDate,
            header.SupplierCode,
            header.SupplierName,
            header.Method,
            header.CashAccountCode,
            header.CashAccountName,
            header.BankAccountCode,
            header.BankAccountName,
            header.CheckGiroNumber,
            header.CheckGiroDueDate,
            header.PaymentAmount,
            header.Status,
            header.Notes,
            header.JournalNumber,
            header.CreatedAt,
            lines);
    }

    private static async Task<(Guid Id, string Code, string Name)?> ReadSupplierAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string code, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("select id, code, company_name from supplier where code = @code and is_active for update;", connection, transaction);
        command.Parameters.AddWithValue("code", code);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? (reader.GetGuid(0), reader.GetString(1), reader.GetString(2)) : null;
    }

    private static async Task<(string Code, string Name, string AccountCode)> ReadCashAccountAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string code, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("select code, name, account_code from cash_account where code = @code and is_active;", connection, transaction);
        command.Parameters.AddWithValue("code", code);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new PurchaseValidationException($"Kas {code} tidak ditemukan atau nonaktif.");
        return (reader.GetString(0), reader.GetString(1), reader.GetString(2));
    }

    private static async Task<(string Code, string Name, string AccountCode)> ReadBankAccountAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string code, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("select code, bank_name, account_code from bank_account where code = @code and is_active;", connection, transaction);
        command.Parameters.AddWithValue("code", code);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new PurchaseValidationException($"Bank {code} tidak ditemukan atau nonaktif.");
        return (reader.GetString(0), reader.GetString(1), reader.GetString(2));
    }

    private static async Task<string> ReadMappingAccountAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string mappingCode, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("select account_code from ledger_transaction_mapping where mapping_code = @code and account_code is not null;", connection, transaction);
        command.Parameters.AddWithValue("code", mappingCode);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is not string accountCode) throw new PurchaseValidationException($"Mapping jurnal {mappingCode} belum dikonfigurasi.");
        return accountCode;
    }

    private static async Task<Guid?> ReadAccountIdAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string accountCode, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("select id from ledger_account where code = @code and is_active and is_postable;", connection, transaction);
        command.Parameters.AddWithValue("code", accountCode);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value as Guid?;
    }

    private static async Task<LockedPayable> LockPayableAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid supplierId, Guid sourceId, string sourceType, decimal amount, CancellationToken cancellationToken)
    {
        sourceType = NormalizeSourceType(sourceType);
        var sql = sourceType == "PURCHASE"
            ? """
                select p.id,p.document_number,p.document_date,p.balance_due,
                       coalesce((select sum(pl.amount) from supplier_payment_line pl join supplier_payment sp on sp.id=pl.supplier_payment_id
                                 where pl.purchase_id=p.id and pl.source_type='PURCHASE' and sp.status='POSTED'),0),
                       coalesce((select sum(d.amount) from partner_balance_document d where d.target_source_type='PURCHASE'
                                 and d.target_source_id=p.id and d.direction='DECREASE' and d.status='POSTED'),0)
                from purchase p where p.id=@id and p.supplier_id=@supplier and p.status='POSTED' and p.transaction_type='CREDIT' for update;
                """
            : """
                select d.id,d.document_number,d.document_date,d.amount,
                       coalesce((select sum(pl.amount) from supplier_payment_line pl join supplier_payment sp on sp.id=pl.supplier_payment_id
                                 where pl.balance_document_id=d.id and pl.source_type='BALANCE_DOCUMENT' and sp.status='POSTED'),0),
                       coalesce((select sum(c.amount) from partner_balance_document c where c.target_source_type='BALANCE_DOCUMENT'
                                 and c.target_source_id=d.id and c.direction='DECREASE' and c.status='POSTED'),0)
                from partner_balance_document d where d.id=@id and d.supplier_id=@supplier and d.partner_type='SUPPLIER'
                  and d.direction='INCREASE' and d.status='POSTED' for update;
                """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("id", sourceId);
        command.Parameters.AddWithValue("supplier", supplierId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new PurchaseValidationException("Nota utang tidak ditemukan untuk supplier ini.");
        var balance = reader.GetDecimal(3);
        var paid = reader.GetDecimal(4);
        var reduced = reader.GetDecimal(5);
        var effectiveBalance = Math.Round(balance - reduced, 2);
        var remaining = PartnerBalanceRules.Remaining(balance, paid, reduced);
        if (amount > remaining) throw new PurchaseValidationException($"Bayar nota {reader.GetString(1)} melebihi sisa utang {remaining:N2}.");
        return new LockedPayable(0, reader.GetGuid(0), sourceType, reader.GetString(1), reader.GetFieldValue<DateOnly>(2), effectiveBalance, paid, amount, remaining - amount);
    }

    private static async Task EnsurePaymentCanBeRestoredAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid paymentId, CancellationToken cancellationToken)
    {
        const string sql = """
            select sp.supplier_id,l.source_type,coalesce(l.purchase_id,l.balance_document_id),l.amount
            from supplier_payment_line l join supplier_payment sp on sp.id=l.supplier_payment_id
            where l.supplier_payment_id=@payment order by l.line_number;
            """;
        var lines = new List<(Guid SupplierId, string SourceType, Guid SourceId, decimal Amount)>();
        await using (var command = new NpgsqlCommand(sql, connection, transaction))
        {
            command.Parameters.AddWithValue("payment", paymentId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) lines.Add((reader.GetGuid(0), reader.GetString(1), reader.GetGuid(2), reader.GetDecimal(3)));
        }
        if (lines.Count == 0) throw new PurchaseConflictException("Nota utang sumber sudah tidak aktif, pembayaran tidak dapat dipulihkan.");
        foreach (var line in lines)
            try { await LockPayableAsync(connection, transaction, line.SupplierId, line.SourceId, line.SourceType, line.Amount, cancellationToken); }
            catch (PurchaseValidationException exception) { throw new PurchaseConflictException($"Batal hapus pembayaran tidak dapat dilakukan. {exception.Message}"); }
    }

    private static async Task<Guid> InsertJournalAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string number, DateOnly date, Guid sourceId, string memo, string? userName, string? computerName, CancellationToken cancellationToken)
    {
        const string sql = """
            insert into journal_entry(document_number, document_year, document_date, memo, source_module, source_id, created_by_name, computer_name)
            values (@number, @year, @date, @memo, 'SUPPLIER_PAYMENT', @sourceId, @user, @computer)
            returning id;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("number", number);
        command.Parameters.AddWithValue("year", (short)date.Year);
        command.Parameters.AddWithValue("date", date);
        command.Parameters.AddWithValue("memo", memo);
        command.Parameters.AddWithValue("sourceId", sourceId);
        command.Parameters.AddWithValue("user", Db(userName));
        command.Parameters.AddWithValue("computer", Db(computerName));
        return (Guid)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task InsertJournalLineAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid journalId, int lineNumber, Guid accountId, string description, decimal debit, decimal credit, CancellationToken cancellationToken)
    {
        const string sql = """
            insert into journal_entry_line(journal_entry_id, line_number, account_id, description, debit, credit)
            values (@journal, @line, @account, @description, @debit, @credit);
            """;
        await LedgerRepository.ExecuteAsync(connection, transaction, sql, cancellationToken,
            ("journal", journalId), ("line", lineNumber), ("account", accountId), ("description", description), ("debit", debit), ("credit", credit));
    }

    private static async Task<bool> DocumentNumberExistsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string number, int year, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("select exists(select 1 from supplier_payment where document_number = @number and document_year = @year);", connection, transaction);
        command.Parameters.AddWithValue("number", number);
        command.Parameters.AddWithValue("year", (short)year);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task<string> NextPaymentNumberAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, DateOnly date, CancellationToken cancellationToken)
    {
        var period = date.ToString("yyyyMM", CultureInfo.InvariantCulture);
        var seed = await ReadPaymentSequenceSeedAsync(connection, transaction, date, cancellationToken);
        const string sql = """
            insert into document_sequence(document_type, period, last_number)
            values ('SUPPLIER_PAY', @period, @seed + 1)
            on conflict (document_type, period)
            do update set last_number = greatest(document_sequence.last_number, @seed) + 1
            returning last_number;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("period", period);
        command.Parameters.AddWithValue("seed", seed);
        var sequence = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
        var configuredPrefix = await ConfigurationRepository.GetDocumentPrefixAsync(connection, transaction, "SUPPLIER_PAY", "BUT", cancellationToken);
        return $"{configuredPrefix}{date:yyMM}{sequence:000}";
    }

    private static async Task<string> PreviewNextPaymentNumberAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, DateOnly date, CancellationToken cancellationToken)
    {
        var seed = await ReadPaymentSequenceSeedAsync(connection, transaction, date, cancellationToken);
        var configuredPrefix = await ConfigurationRepository.GetDocumentPrefixAsync(connection, transaction, "SUPPLIER_PAY", "BUT", cancellationToken);
        return $"{configuredPrefix}{date:yyMM}{seed + 1:000}";
    }

    private static async Task<int> ReadPaymentSequenceSeedAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, DateOnly date, CancellationToken cancellationToken)
    {
        const string sql = """
            select greatest(
                coalesce((select last_number from document_sequence where document_type = 'SUPPLIER_PAY' and period = @period), 0),
                coalesce((
                    select max(nullif(regexp_replace(substring(document_number from char_length(@prefix) + 1), '\D', '', 'g'), '')::integer)
                    from supplier_payment
                    where document_year = @year and document_number like @prefix || '%'
                ), 0)
            );
            """;
        var configuredPrefix = await ConfigurationRepository.GetDocumentPrefixAsync(connection, transaction, "SUPPLIER_PAY", "BUT", cancellationToken);
        var prefix = $"{configuredPrefix}{date:yyMM}";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("period", date.ToString("yyyyMM", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("year", (short)date.Year);
        command.Parameters.AddWithValue("prefix", prefix);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static string? NormalizeDocumentNumber(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrWhiteSpace(text) || text.Equals("OTOMATIS", StringComparison.OrdinalIgnoreCase)) return null;
        if (text.Length > 30) throw new PurchaseValidationException("No. Bukti maksimal 30 karakter.");
        return text.ToUpperInvariant();
    }

    private static string NormalizeMethod(string value)
    {
        var method = NormalizeCode(value, "Cara bayar", 12);
        if (method is not ("CASH" or "BANK" or "CHECK_GIRO")) throw new PurchaseValidationException("Cara bayar harus CASH, BANK, atau CHECK_GIRO.");
        return method;
    }

    private static string NormalizeSourceType(string? value)
    {
        var sourceType = value?.Trim().ToUpperInvariant();
        return sourceType is "PURCHASE" or "BALANCE_DOCUMENT"
            ? sourceType
            : throw new PurchaseValidationException("Jenis sumber utang tidak valid.");
    }

    private static string NormalizeCode(string value, string label, int maxLength)
    {
        var text = value.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(text)) throw new PurchaseValidationException($"{label} wajib diisi.");
        if (text.Length > maxLength) throw new PurchaseValidationException($"{label} maksimal {maxLength} karakter.");
        return text;
    }

    private static object Db(string? value) => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();
    private static string? Text(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private sealed record RequestedPaymentLine(int LineNumber, Guid PurchaseId, string SourceType, decimal Amount);
    private sealed record LockedPayable(int LineNumber, Guid SourceId, string SourceType, string DocumentNumber, DateOnly DocumentDate, decimal BalanceDue, decimal PaidBefore, decimal Amount, decimal RemainingAfter);
    private sealed record SupplierPaymentHeader(
        Guid Id,
        string DocumentNumber,
        DateOnly DocumentDate,
        string SupplierCode,
        string SupplierName,
        string Method,
        string? CashAccountCode,
        string? CashAccountName,
        string? BankAccountCode,
        string? BankAccountName,
        string? CheckGiroNumber,
        DateOnly? CheckGiroDueDate,
        decimal PaymentAmount,
        string Status,
        string? Notes,
        string JournalNumber,
        DateTimeOffset CreatedAt);
}
