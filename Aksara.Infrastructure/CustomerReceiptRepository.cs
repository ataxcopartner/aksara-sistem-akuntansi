using System.Data;
using System.Globalization;
using System.Text.Json;
using Aksara.Contracts;
using Aksara.Domain;
using Npgsql;

namespace Aksara.Infrastructure;

public sealed class CustomerReceiptRepository(PostgresDatabase database)
{
    public async Task<IReadOnlyList<CustomerReceivableDto>> ListReceivablesAsync(string? customerCode, string? query, int limit = 100, CancellationToken cancellationToken = default)
    {
        const string sql = """
            with sale_paid as (
                select rl.sale_id source_id, coalesce(sum(rl.amount), 0) paid_amount
                from customer_receipt_line rl
                join customer_receipt cr on cr.id = rl.customer_receipt_id
                where cr.status = 'POSTED' and rl.source_type='SALE'
                group by rl.sale_id
            ), balance_paid as (
                select rl.balance_document_id source_id, coalesce(sum(rl.amount),0) paid_amount
                from customer_receipt_line rl join customer_receipt cr on cr.id=rl.customer_receipt_id
                where cr.status='POSTED' and rl.source_type='BALANCE_DOCUMENT' group by rl.balance_document_id
            ), sale_reduced as (
                select target_source_id source_id,coalesce(sum(amount),0) reduced_amount from partner_balance_document
                where partner_type='CUSTOMER' and direction='DECREASE' and status='POSTED' and target_source_type='SALE'
                group by target_source_id
            ), balance_reduced as (
                select target_source_id source_id,coalesce(sum(amount),0) reduced_amount from partner_balance_document
                where partner_type='CUSTOMER' and direction='DECREASE' and status='POSTED' and target_source_type='BALANCE_DOCUMENT'
                group by target_source_id
            ), sources as (
                select s.id,s.document_number,s.document_date,c.code customer_code,c.company_name customer_name,s.sales_code,s.due_date,
                       s.grand_total,s.down_payment,coalesce(sp.paid_amount,0) paid_amount,
                       s.balance_due-coalesce(sr.reduced_amount,0) balance_due,
                       s.balance_due-coalesce(sr.reduced_amount,0)-coalesce(sp.paid_amount,0) remaining_balance,
                       'SALE' source_type,coalesce(sr.reduced_amount,0) correction_amount,'Penjualan' source_label
                from sale s join customer c on c.id=s.customer_id
                left join sale_paid sp on sp.source_id=s.id left join sale_reduced sr on sr.source_id=s.id
                where s.status='POSTED' and s.transaction_type='CREDIT'
                union all
                select d.id,d.document_number,d.document_date,c.code,c.company_name,null,d.due_date,d.amount,0,
                       coalesce(bp.paid_amount,0),d.amount-coalesce(br.reduced_amount,0),
                       d.amount-coalesce(br.reduced_amount,0)-coalesce(bp.paid_amount,0),
                       'BALANCE_DOCUMENT',coalesce(br.reduced_amount,0),
                       case d.document_kind when 'OPENING' then 'Piutang Awal' else 'Koreksi Piutang' end
                from partner_balance_document d join customer c on c.id=d.customer_id
                left join balance_paid bp on bp.source_id=d.id left join balance_reduced br on br.source_id=d.id
                where d.partner_type='CUSTOMER' and d.direction='INCREASE' and d.status='POSTED'
            )
            select * from sources where remaining_balance > 0
              and (@customer='' or customer_code=@customer)
              and (@query='' or document_number ilike '%' || @query || '%' or coalesce(sales_code,'') ilike '%' || @query || '%'
                   or customer_code ilike '%' || @query || '%' or customer_name ilike '%' || @query || '%')
            order by document_date,document_number
            limit @limit;
            """;
        await using var command = database.DataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("customer", customerCode?.Trim().ToUpperInvariant() ?? string.Empty);
        command.Parameters.AddWithValue("query", query?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 500));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<CustomerReceivableDto>();
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

    public async Task<IReadOnlyList<CustomerReceiptDto>> ListReceiptsAsync(string? query, int limit = 100, CancellationToken cancellationToken = default)
    {
        const string sql = """
            select cr.id
            from customer_receipt cr
            join customer c on c.id = cr.customer_id
            where @query = ''
               or cr.document_number ilike '%' || @query || '%'
               or c.code ilike '%' || @query || '%'
               or c.company_name ilike '%' || @query || '%'
               or coalesce(cr.notes, '') ilike '%' || @query || '%'
            order by cr.document_date desc, cr.document_number desc
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

        var result = new List<CustomerReceiptDto>(ids.Count);
        foreach (var id in ids)
        {
            var item = await GetReceiptAsync(id, cancellationToken);
            if (item is not null) result.Add(item);
        }

        return result;
    }

    public async Task<CustomerReceiptDto?> GetReceiptAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        return await ReadReceiptAsync(connection, null, id, cancellationToken);
    }

    public async Task<string> PreviewNextReceiptNumberAsync(DateOnly documentDate, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        return await PreviewNextReceiptNumberAsync(connection, null, documentDate, cancellationToken);
    }

    public async Task<string> PreviewNextCheckGiroNumberAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        var seed = await ReadCheckGiroSequenceSeedAsync(connection, null, cancellationToken);
        var prefix = await ConfigurationRepository.GetDocumentPrefixAsync(connection, null, "CUSTOMER_CHECK_GIRO", "CG", cancellationToken);
        return $"{prefix}{seed + 1:00000000}";
    }

    public async Task<IReadOnlyList<CustomerCheckGiroDto>> ListCheckGirosAsync(string? customerCode, string? query, int limit = 100, bool openOnly = true, CancellationToken cancellationToken = default)
    {
        var code = string.IsNullOrWhiteSpace(customerCode) ? string.Empty : NormalizeCode(customerCode, "Kode customer", 20);
        const string sql = """
            select cg.id, cg.sequence_number, coalesce(cg.document_date, cg.created_at::date), cg.bank_name, cg.check_giro_number,
                   cg.check_giro_type, cg.due_date, coalesce(cg.face_amount, cg.amount - cg.additional_amount, cg.amount),
                   cg.additional_amount, cg.amount, c.code, c.company_name, c.address, cg.origin_area, cg.initial_balance,
                   cg.is_rejected, cg.rejected_date, cg.settlement_date, cg.status, cg.notes
            from customer_check_giro cg
            join customer c on c.id = cg.customer_id
            where (@customer = '' or c.code = @customer)
              and (not @openOnly or cg.status = 'OPEN')
              and (@query = '' or cg.sequence_number ilike '%' || @query || '%'
                   or cg.bank_name ilike '%' || @query || '%'
                   or cg.check_giro_number ilike '%' || @query || '%'
                   or c.company_name ilike '%' || @query || '%')
            order by coalesce(cg.document_date, cg.created_at::date), cg.sequence_number
            limit @limit;
            """;
        await using var command = database.DataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("customer", code);
        command.Parameters.AddWithValue("openOnly", openOnly);
        command.Parameters.AddWithValue("query", query?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 500));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<CustomerCheckGiroDto>();
        while (await reader.ReadAsync(cancellationToken))
            result.Add(ReadCheckGiro(reader));
        return result;
    }

    public async Task<CustomerCheckGiroDto> CreateCheckGiroAsync(CreateCustomerCheckGiroRequest request, CancellationToken cancellationToken = default)
    {
        var customerCode = NormalizeCode(request.CustomerCode, "Kode customer", 20);
        var bankName = NormalizeText(request.BankName, "Bank", 80);
        var giroNumber = NormalizeText(request.CheckGiroNumber, "No. C/G", 50);
        var faceAmount = Math.Round(request.FaceAmount > 0 ? request.FaceAmount : request.Amount - request.AdditionalAmount, 2);
        var additionalAmount = Math.Round(request.AdditionalAmount, 2);
        if (additionalAmount < 0) throw new SalesValidationException("Tambahan cek/giro tidak boleh minus.");
        var amount = Math.Round(request.Amount > 0 ? request.Amount : faceAmount + additionalAmount, 2);
        if (amount <= 0) throw new SalesValidationException("Nilai cek/giro harus lebih dari nol.");
        if (faceAmount <= 0) faceAmount = amount - additionalAmount;
        if (faceAmount <= 0) throw new SalesValidationException("Senilai cek/giro harus lebih dari nol.");
        var documentDate = request.DocumentDate == default ? DateOnly.FromDateTime(DateTime.Today) : request.DocumentDate;
        var originArea = NormalizeOriginArea(request.OriginArea);
        var status = request.IsRejected ? "CANCELLED" : "OPEN";

        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            var customer = await ReadCustomerAsync(connection, transaction, customerCode, cancellationToken)
                ?? throw new SalesValidationException($"Customer {customerCode} tidak ditemukan atau tidak aktif.");
            var sequenceNumber = NormalizeDocumentNumber(request.SequenceNumber)
                ?? await NextCheckGiroNumberAsync(connection, transaction, cancellationToken);
            if (await CheckGiroNumberExistsAsync(connection, transaction, sequenceNumber, cancellationToken))
                throw new SalesConflictException($"No. urut {sequenceNumber} sudah dipakai.");

            var id = Guid.NewGuid();
            const string sql = """
                insert into customer_check_giro(id, sequence_number, customer_id, bank_name, check_giro_number, check_giro_type,
                    due_date, amount, status, source, notes, created_by_name, computer_name, document_date, origin_area,
                    initial_balance, is_rejected, rejected_date, additional_amount, settlement_date, face_amount)
                values (@id, @sequence, @customer, @bank, @number, @type, @due, @amount, @status, 'MANUAL',
                    @notes, @user, @computer, @documentDate, @originArea, @initialBalance, @isRejected, @rejectedDate,
                    @additionalAmount, @settlementDate, @faceAmount);
                """;
            await LedgerRepository.ExecuteAsync(connection, transaction, sql, cancellationToken,
                ("id", id), ("sequence", sequenceNumber), ("customer", customer.Id), ("bank", bankName), ("number", giroNumber),
                ("type", Db(request.CheckGiroType)), ("due", request.DueDate is null ? DBNull.Value : request.DueDate.Value),
                ("amount", amount), ("status", status), ("notes", Db(request.Notes)), ("user", Db(request.UserName)), ("computer", Db(request.ComputerName)),
                ("documentDate", documentDate), ("originArea", originArea), ("initialBalance", request.InitialBalance), ("isRejected", request.IsRejected),
                ("rejectedDate", request.RejectedDate is null ? DBNull.Value : request.RejectedDate.Value), ("additionalAmount", additionalAmount),
                ("settlementDate", request.SettlementDate is null ? DBNull.Value : request.SettlementDate.Value), ("faceAmount", faceAmount));

            await LedgerRepository.InsertAuditAsync(connection, transaction, "CUSTOMER_CHECK_GIRO", id.ToString(), "CREATE", request.UserName, request.ComputerName, request, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return await ReadCheckGiroByIdAsync(id, cancellationToken)
                ?? throw new SalesConflictException("Cek/giro tersimpan tetapi gagal dibuka ulang.");
        }
        catch (PostgresException exception) when (exception.SqlState is PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.UniqueViolation)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new SalesConflictException("Data cek/giro berubah saat disimpan. Muat ulang lalu coba kembali.");
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<CustomerReceiptDto> CreateReceiptAsync(CreateCustomerReceiptRequest request, CancellationToken cancellationToken = default)
    {
        var customerCode = NormalizeCode(request.CustomerCode, "Kode customer", 20);
        var method = NormalizeMethod(request.Method);
        var receiptAmount = Math.Round(request.ReceiptAmount, 2);
        if (receiptAmount <= 0) throw new SalesValidationException("Penerimaan harus lebih dari nol.");
        if (request.Lines.Count == 0) throw new SalesValidationException("Minimal satu nota piutang harus dipilih.");

        var cashCode = string.IsNullOrWhiteSpace(request.CashAccountCode) ? null : NormalizeCode(request.CashAccountCode, "Kode kas", 20);
        var bankCode = string.IsNullOrWhiteSpace(request.BankAccountCode) ? null : NormalizeCode(request.BankAccountCode, "Kode bank", 20);
        if (method == "CASH" && cashCode is null) throw new SalesValidationException("Kas wajib dipilih untuk penerimaan tunai.");
        if (method == "BANK" && bankCode is null) throw new SalesValidationException("Bank wajib dipilih untuk penerimaan bank.");
        if (method == "CHECK_GIRO" && request.CheckGiroId is null) throw new SalesValidationException("No. urut cek/giro wajib dipilih untuk penerimaan C/G.");

        var requested = request.Lines
            .Select(x => x with { SourceType = NormalizeSourceType(x.SourceType) })
            .GroupBy(x => new { x.SourceType, x.SaleId })
            .Select((group, index) => new RequestedReceiptLine(index + 1, group.Key.SaleId, group.Key.SourceType, Math.Round(group.Sum(x => x.Amount), 2)))
            .Where(x => x.Amount > 0)
            .ToArray();
        if (requested.Length == 0) throw new SalesValidationException("Jumlah bayar pada piutang belum diisi.");
        var lineTotal = requested.Sum(x => x.Amount);
        if (lineTotal != receiptAmount) throw new SalesValidationException("Total piutang dibayar harus sama dengan penerimaan.");

        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            var customer = await ReadCustomerAsync(connection, transaction, customerCode, cancellationToken)
                ?? throw new SalesValidationException($"Customer {customerCode} tidak ditemukan atau tidak aktif.");
            var settlement = method switch
            {
                "CASH" => await ReadCashAccountAsync(connection, transaction, cashCode!, cancellationToken),
                "BANK" => await ReadBankAccountAsync(connection, transaction, bankCode!, cancellationToken),
                _ => await ReadCheckGiroSettlementAsync(connection, transaction, request.CheckGiroId!.Value, customer.Id, receiptAmount, cancellationToken)
            };
            var receivableAccount = await ReadMappingAccountAsync(connection, transaction, "SALES_RECEIVABLE", cancellationToken);
            var receivableAccountId = await ReadAccountIdAsync(connection, transaction, receivableAccount, cancellationToken)
                ?? throw new SalesValidationException($"Perkiraan piutang {receivableAccount} tidak ditemukan.");
            var settlementAccountId = await ReadAccountIdAsync(connection, transaction, settlement.AccountCode, cancellationToken)
                ?? throw new SalesValidationException($"Perkiraan penerimaan {settlement.AccountCode} tidak ditemukan.");

            var locked = new List<LockedReceivable>();
            foreach (var line in requested)
                locked.Add((await LockReceivableAsync(connection, transaction, customer.Id, line.SaleId, line.SourceType, line.Amount, cancellationToken)) with { LineNumber = line.LineNumber });

            var requestedNumber = NormalizeDocumentNumber(request.DocumentNumber);
            var number = requestedNumber ?? await NextReceiptNumberAsync(connection, transaction, request.DocumentDate, cancellationToken);
            if (requestedNumber is not null && await DocumentNumberExistsAsync(connection, transaction, number, request.DocumentDate.Year, cancellationToken))
                throw new SalesConflictException($"No. Bukti {number} sudah dipakai.");

            var receiptId = Guid.NewGuid();
            var journalNumber = await LedgerRepository.NextDocumentNumberAsync(connection, transaction, "CUSTOMER_RECEIPT_JRN", "JPIU", request.DocumentDate, cancellationToken);
            var memo = $"Penerimaan piutang {customer.Code} - {customer.Name}";
            AccountingRules.ValidateBalancedJournal(new[]
            {
                new JournalLineRule(settlement.AccountCode, receiptAmount, 0),
                new JournalLineRule(receivableAccount, 0, receiptAmount)
            });

            var journalId = await InsertJournalAsync(connection, transaction, journalNumber, request.DocumentDate, receiptId, memo, request.UserName, request.ComputerName, cancellationToken);
            await InsertJournalLineAsync(connection, transaction, journalId, 1, settlementAccountId, memo, receiptAmount, 0, cancellationToken);
            await InsertJournalLineAsync(connection, transaction, journalId, 2, receivableAccountId, memo, 0, receiptAmount, cancellationToken);

            const string receiptSql = """
                insert into customer_receipt(id, document_number, document_year, document_date, customer_id, collector_code, method,
                    cash_account_code, bank_account_code, check_giro_id, receipt_amount, notes, journal_entry_id, created_by_name, computer_name)
                values (@id, @number, @year, @date, @customer, @collector, @method, @cash, @bank, @giro,
                    @amount, @notes, @journal, @user, @computer);
                """;
            await LedgerRepository.ExecuteAsync(connection, transaction, receiptSql, cancellationToken,
                ("id", receiptId), ("number", number), ("year", (short)request.DocumentDate.Year), ("date", request.DocumentDate),
                ("customer", customer.Id), ("collector", Db(request.CollectorCode)), ("method", method),
                ("cash", Db(cashCode)), ("bank", Db(bankCode)), ("giro", request.CheckGiroId is null ? DBNull.Value : request.CheckGiroId.Value),
                ("amount", receiptAmount), ("notes", Db(request.Notes)), ("journal", journalId),
                ("user", Db(request.UserName)), ("computer", Db(request.ComputerName)));

            const string lineSql = """
                insert into customer_receipt_line(customer_receipt_id, sale_id, balance_document_id, source_type, line_number, original_balance, paid_before, amount, remaining_after)
                values (@receipt, @sale, @balance, @sourceType, @line, @original, @before, @amount, @remaining);
                """;
            foreach (var line in locked)
            {
                await LedgerRepository.ExecuteAsync(connection, transaction, lineSql, cancellationToken,
                    ("receipt", receiptId), ("sale", line.SourceType == "SALE" ? line.SourceId : DBNull.Value),
                    ("balance", line.SourceType == "BALANCE_DOCUMENT" ? line.SourceId : DBNull.Value), ("sourceType", line.SourceType), ("line", line.LineNumber),
                    ("original", line.BalanceDue), ("before", line.PaidBefore), ("amount", line.Amount), ("remaining", line.RemainingAfter));
            }

            if (method == "CHECK_GIRO")
            {
                const string usedSql = """
                    update customer_check_giro
                    set status = 'USED', used_receipt_id = @receipt, used_at = now(), updated_at = now(), version = version + 1
                    where id = @giro and status = 'OPEN';
                    """;
                await LedgerRepository.ExecuteAsync(connection, transaction, usedSql, cancellationToken,
                    ("receipt", receiptId), ("giro", request.CheckGiroId!.Value));
            }

            await LedgerRepository.InsertAuditAsync(connection, transaction, "CUSTOMER_RECEIPT", receiptId.ToString(), "CREATE", request.UserName, request.ComputerName, request, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return (await GetReceiptAsync(receiptId, cancellationToken))!;
        }
        catch (PostgresException exception) when (exception.SqlState is PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.UniqueViolation)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new SalesConflictException("Penerimaan piutang berubah saat disimpan. Muat ulang lalu coba kembali.");
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<CustomerCheckGiroDto> CancelCheckGiroAsync(Guid id, CancelCustomerCheckGiroRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Reason)) throw new SalesValidationException("Alasan hapus wajib diisi.");
        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            const string lockSql = "select sequence_number, status, is_rejected from customer_check_giro where id = @id for update;";
            string number;
            string status;
            bool isRejected;
            await using (var command = new NpgsqlCommand(lockSql, connection, transaction))
            {
                command.Parameters.AddWithValue("id", id);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken)) throw new SalesValidationException("Data C/G tidak ditemukan.");
                number = reader.GetString(0);
                status = reader.GetString(1);
                isRejected = reader.GetBoolean(2);
            }

            if (string.Equals(status, "USED", StringComparison.OrdinalIgnoreCase))
                throw new SalesConflictException("C/G sudah dipakai pada penerimaan piutang, sehingga tidak dapat ditandai hapus dari form C/G.");
            if (string.Equals(status, "CANCELLED", StringComparison.OrdinalIgnoreCase))
                throw new SalesConflictException(isRejected ? "C/G sudah berstatus ditolak." : "C/G sudah ditandai hapus.");

            const string updateSql = """
                update customer_check_giro
                set status = 'CANCELLED', cancelled_at = now(), cancelled_by_name = @user,
                    cancellation_reason = @reason, updated_at = now(), version = version + 1
                where id = @id;
                """;
            await LedgerRepository.ExecuteAsync(connection, transaction, updateSql, cancellationToken,
                ("id", id), ("user", Db(request.UserName)), ("reason", request.Reason.Trim()));

            await LedgerRepository.InsertAuditAsync(connection, transaction, "CUSTOMER_CHECK_GIRO", id.ToString(), "CANCEL", request.UserName, request.ComputerName, new { sequenceNumber = number, reason = request.Reason.Trim() }, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return (await ReadCheckGiroByIdAsync(id, cancellationToken))!;
        }
        catch (PostgresException exception) when (exception.SqlState is PostgresErrorCodes.SerializationFailure)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new SalesConflictException("Data C/G berubah saat ditandai hapus. Muat ulang lalu coba kembali.");
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<CustomerCheckGiroDto> RestoreCheckGiroAsync(Guid id, RestoreCustomerCheckGiroRequest request, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            const string lockSql = "select sequence_number, status, is_rejected from customer_check_giro where id = @id for update;";
            string number;
            string status;
            bool isRejected;
            await using (var command = new NpgsqlCommand(lockSql, connection, transaction))
            {
                command.Parameters.AddWithValue("id", id);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken)) throw new SalesValidationException("Data C/G tidak ditemukan.");
                number = reader.GetString(0);
                status = reader.GetString(1);
                isRejected = reader.GetBoolean(2);
            }

            if (!string.Equals(status, "CANCELLED", StringComparison.OrdinalIgnoreCase))
                throw new SalesConflictException("C/G belum ditandai hapus.");
            if (isRejected)
                throw new SalesConflictException("C/G yang batal karena ditolak tidak dipulihkan lewat tombol Btl.Hapus.");

            const string updateSql = """
                update customer_check_giro
                set status = 'OPEN', cancelled_at = null, cancelled_by_name = null,
                    cancellation_reason = null, updated_at = now(), version = version + 1
                where id = @id;
                """;
            await LedgerRepository.ExecuteAsync(connection, transaction, updateSql, cancellationToken, ("id", id));

            await LedgerRepository.InsertAuditAsync(connection, transaction, "CUSTOMER_CHECK_GIRO", id.ToString(), "RESTORE", request.UserName, request.ComputerName, new { sequenceNumber = number, status = "OPEN" }, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return (await ReadCheckGiroByIdAsync(id, cancellationToken))!;
        }
        catch (PostgresException exception) when (exception.SqlState is PostgresErrorCodes.SerializationFailure)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new SalesConflictException("Data C/G berubah saat batal hapus. Muat ulang lalu coba kembali.");
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<CustomerReceiptDto> CancelReceiptAsync(Guid id, CancelCustomerReceiptRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Reason)) throw new SalesValidationException("Alasan hapus wajib diisi.");
        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            const string lockSql = "select document_number, status, method, check_giro_id from customer_receipt where id = @id for update;";
            string number;
            string status;
            string method;
            Guid? checkGiroId;
            await using (var command = new NpgsqlCommand(lockSql, connection, transaction))
            {
                command.Parameters.AddWithValue("id", id);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken)) throw new SalesValidationException("Penerimaan piutang tidak ditemukan.");
                number = reader.GetString(0);
                status = reader.GetString(1);
                method = reader.GetString(2);
                checkGiroId = reader.IsDBNull(3) ? null : reader.GetGuid(3);
            }

            if (!string.Equals(status, "POSTED", StringComparison.OrdinalIgnoreCase))
                throw new SalesConflictException("Penerimaan piutang sudah ditandai hapus.");

            if (string.Equals(method, "CHECK_GIRO", StringComparison.OrdinalIgnoreCase) && checkGiroId is not null)
            {
                const string releaseSql = """
                    update customer_check_giro
                    set status = 'OPEN', used_receipt_id = null, used_at = null, updated_at = now(), version = version + 1
                    where id = @giro and used_receipt_id = @receipt;
                    """;
                await LedgerRepository.ExecuteAsync(connection, transaction, releaseSql, cancellationToken,
                    ("giro", checkGiroId.Value), ("receipt", id));
            }

            await InventoryLedgerPosting.CancelAsync(connection, transaction, id, "CUSTOMER_RECEIPT", request.UserName, request.Reason, cancellationToken);

            const string updateSql = """
                update customer_receipt
                set status = 'CANCELLED', cancelled_at = now(), cancelled_by_name = @user,
                    cancellation_reason = @reason, version = version + 1
                where id = @id;
                """;
            await LedgerRepository.ExecuteAsync(connection, transaction, updateSql, cancellationToken,
                ("id", id), ("user", Db(request.UserName)), ("reason", request.Reason.Trim()));

            await LedgerRepository.InsertAuditAsync(connection, transaction, "CUSTOMER_RECEIPT", id.ToString(), "CANCEL", request.UserName, request.ComputerName, new { documentNumber = number, reason = request.Reason.Trim() }, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return (await GetReceiptAsync(id, cancellationToken))!;
        }
        catch (PostgresException exception) when (exception.SqlState is PostgresErrorCodes.SerializationFailure)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new SalesConflictException("Penerimaan piutang berubah saat ditandai hapus. Muat ulang lalu coba kembali.");
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<CustomerReceiptDto> RestoreReceiptAsync(Guid id, RestoreCustomerReceiptRequest request, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            const string lockSql = "select document_number, status, method, check_giro_id, receipt_amount from customer_receipt where id = @id for update;";
            string number;
            string status;
            string method;
            Guid? checkGiroId;
            decimal receiptAmount;
            await using (var command = new NpgsqlCommand(lockSql, connection, transaction))
            {
                command.Parameters.AddWithValue("id", id);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken)) throw new SalesValidationException("Penerimaan piutang tidak ditemukan.");
                number = reader.GetString(0);
                status = reader.GetString(1);
                method = reader.GetString(2);
                checkGiroId = reader.IsDBNull(3) ? null : reader.GetGuid(3);
                receiptAmount = reader.GetDecimal(4);
            }

            if (!string.Equals(status, "CANCELLED", StringComparison.OrdinalIgnoreCase))
                throw new SalesConflictException("Penerimaan piutang belum ditandai hapus.");

            await EnsureReceiptCanBeRestoredAsync(connection, transaction, id, cancellationToken);

            if (string.Equals(method, "CHECK_GIRO", StringComparison.OrdinalIgnoreCase) && checkGiroId is not null)
            {
                const string giroSql = "select sequence_number, status, amount, is_rejected from customer_check_giro where id = @id for update;";
                string giroNumber;
                string giroStatus;
                decimal giroAmount;
                bool isRejected;
                await using (var command = new NpgsqlCommand(giroSql, connection, transaction))
                {
                    command.Parameters.AddWithValue("id", checkGiroId.Value);
                    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                    if (!await reader.ReadAsync(cancellationToken)) throw new SalesValidationException("C/G penerimaan tidak ditemukan.");
                    giroNumber = reader.GetString(0);
                    giroStatus = reader.GetString(1);
                    giroAmount = reader.GetDecimal(2);
                    isRejected = reader.GetBoolean(3);
                }

                if (isRejected || !string.Equals(giroStatus, "OPEN", StringComparison.OrdinalIgnoreCase))
                    throw new SalesConflictException($"C/G {giroNumber} sudah tidak tersedia untuk dipakai kembali.");
                if (giroAmount != receiptAmount)
                    throw new SalesConflictException($"Nilai C/G {giroNumber} tidak sama dengan nilai penerimaan.");

                const string useSql = """
                    update customer_check_giro
                    set status = 'USED', used_receipt_id = @receipt, used_at = now(), updated_at = now(), version = version + 1
                    where id = @giro;
                    """;
                await LedgerRepository.ExecuteAsync(connection, transaction, useSql, cancellationToken,
                    ("receipt", id), ("giro", checkGiroId.Value));
            }

            await InventoryLedgerPosting.RestoreAsync(connection, transaction, id, "CUSTOMER_RECEIPT", cancellationToken);

            const string updateSql = """
                update customer_receipt
                set status = 'POSTED', cancelled_at = null, cancelled_by_name = null,
                    cancellation_reason = null, version = version + 1
                where id = @id;
                """;
            await LedgerRepository.ExecuteAsync(connection, transaction, updateSql, cancellationToken, ("id", id));

            await LedgerRepository.InsertAuditAsync(connection, transaction, "CUSTOMER_RECEIPT", id.ToString(), "RESTORE", request.UserName, request.ComputerName, new { documentNumber = number, status = "POSTED" }, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return (await GetReceiptAsync(id, cancellationToken))!;
        }
        catch (PostgresException exception) when (exception.SqlState is PostgresErrorCodes.SerializationFailure)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new SalesConflictException("Penerimaan piutang berubah saat batal hapus. Muat ulang lalu coba kembali.");
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private async Task<CustomerCheckGiroDto?> ReadCheckGiroByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        const string sql = """
            select cg.id, cg.sequence_number, coalesce(cg.document_date, cg.created_at::date), cg.bank_name, cg.check_giro_number,
                   cg.check_giro_type, cg.due_date, coalesce(cg.face_amount, cg.amount - cg.additional_amount, cg.amount),
                   cg.additional_amount, cg.amount, c.code, c.company_name, c.address, cg.origin_area, cg.initial_balance,
                   cg.is_rejected, cg.rejected_date, cg.settlement_date, cg.status, cg.notes
            from customer_check_giro cg
            join customer c on c.id = cg.customer_id
            where cg.id = @id;
            """;
        await using var command = database.DataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadCheckGiro(reader) : null;
    }

    private static async Task<CustomerReceiptDto?> ReadReceiptAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid id, CancellationToken cancellationToken)
    {
        const string headerSql = """
            select cr.id, cr.document_number, cr.document_date, c.code, c.company_name, cr.collector_code, cr.method,
                   cr.cash_account_code, ca.name, cr.bank_account_code, ba.bank_name, cr.check_giro_id,
                   cg.sequence_number, coalesce(cg.document_date, cg.created_at::date), cg.bank_name, cg.check_giro_number,
                   cg.check_giro_type, cg.due_date, coalesce(cg.face_amount, cg.amount - cg.additional_amount, cg.amount),
                   cg.additional_amount, cg.amount, cg.origin_area, cg.initial_balance, cg.is_rejected,
                   cg.rejected_date, cg.settlement_date, cg.status, cg.notes, cr.receipt_amount, cr.status,
                   cr.notes, je.document_number, cr.created_at, c.address
            from customer_receipt cr
            join customer c on c.id = cr.customer_id
            join journal_entry je on je.id = cr.journal_entry_id
            left join cash_account ca on ca.code = cr.cash_account_code
            left join bank_account ba on ba.code = cr.bank_account_code
            left join customer_check_giro cg on cg.id = cr.check_giro_id
            where cr.id = @id;
            """;
        CustomerReceiptHeader? header = null;
        await using (var command = new NpgsqlCommand(headerSql, connection, transaction))
        {
            command.Parameters.AddWithValue("id", id);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                CustomerCheckGiroDto? giro = null;
                if (!reader.IsDBNull(11))
                {
                    giro = new CustomerCheckGiroDto(
                        reader.GetGuid(11),
                        reader.GetString(12),
                        reader.GetFieldValue<DateOnly>(13),
                        reader.GetString(14),
                        reader.GetString(15),
                        Text(reader, 16),
                        reader.IsDBNull(17) ? null : reader.GetFieldValue<DateOnly>(17),
                        reader.GetDecimal(18),
                        reader.GetDecimal(19),
                        reader.GetDecimal(20),
                        reader.GetString(3),
                        reader.GetString(4),
                        Text(reader, 33),
                        reader.GetString(21),
                        reader.GetBoolean(22),
                        reader.GetBoolean(23),
                        reader.IsDBNull(24) ? null : reader.GetFieldValue<DateOnly>(24),
                        reader.IsDBNull(25) ? null : reader.GetFieldValue<DateOnly>(25),
                        reader.GetString(26),
                        Text(reader, 27));
                }

                header = new(
                    reader.GetGuid(0),
                    reader.GetString(1),
                    reader.GetFieldValue<DateOnly>(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    Text(reader, 5),
                    reader.GetString(6),
                    Text(reader, 7),
                    Text(reader, 8),
                    Text(reader, 9),
                    Text(reader, 10),
                    giro,
                    reader.GetDecimal(28),
                    reader.GetString(29),
                    Text(reader, 30),
                    reader.GetString(31),
                    reader.GetFieldValue<DateTimeOffset>(32));
            }
        }

        if (header is null) return null;

        const string lineSql = """
            select coalesce(s.id,d.id),coalesce(s.document_number,d.document_number),coalesce(s.document_date,d.document_date),s.sales_code,
                   l.original_balance,l.paid_before,l.amount,l.remaining_after,l.source_type
            from customer_receipt_line l
            left join sale s on l.source_type='SALE' and s.id=l.sale_id
            left join partner_balance_document d on l.source_type='BALANCE_DOCUMENT' and d.id=l.balance_document_id
            where l.customer_receipt_id = @id
            order by l.line_number;
            """;
        var lines = new List<CustomerReceiptLineDto>();
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
                    Text(reader, 3),
                    reader.GetDecimal(4),
                    reader.GetDecimal(5),
                    reader.GetDecimal(6),
                    reader.GetDecimal(7),
                    reader.GetString(8)));
            }
        }

        return new(
            header.Id,
            header.DocumentNumber,
            header.DocumentDate,
            header.CustomerCode,
            header.CustomerName,
            header.CollectorCode,
            header.Method,
            header.CashAccountCode,
            header.CashAccountName,
            header.BankAccountCode,
            header.BankAccountName,
            header.CheckGiro,
            header.ReceiptAmount,
            header.Status,
            header.Notes,
            header.JournalNumber,
            header.CreatedAt,
            lines);
    }

    private static async Task<(Guid Id, string Code, string Name)?> ReadCustomerAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string code, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("select id, code, company_name from customer where code = @code and is_active for share;", connection, transaction);
        command.Parameters.AddWithValue("code", code);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? (reader.GetGuid(0), reader.GetString(1), reader.GetString(2)) : null;
    }

    private static async Task<(string Code, string Name, string AccountCode)> ReadCashAccountAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string code, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("select code, name, account_code from cash_account where code = @code and is_active;", connection, transaction);
        command.Parameters.AddWithValue("code", code);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new SalesValidationException($"Kas {code} tidak ditemukan atau nonaktif.");
        return (reader.GetString(0), reader.GetString(1), reader.GetString(2));
    }

    private static async Task<(string Code, string Name, string AccountCode)> ReadBankAccountAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string code, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("select code, bank_name, account_code from bank_account where code = @code and is_active;", connection, transaction);
        command.Parameters.AddWithValue("code", code);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new SalesValidationException($"Bank {code} tidak ditemukan atau nonaktif.");
        return (reader.GetString(0), reader.GetString(1), reader.GetString(2));
    }

    private static async Task<(string Code, string Name, string AccountCode)> ReadCheckGiroSettlementAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, Guid customerId, decimal receiptAmount, CancellationToken cancellationToken)
    {
        const string sql = """
            select cg.sequence_number, cg.bank_name, cg.amount
            from customer_check_giro cg
            where cg.id = @id and cg.customer_id = @customer and cg.status = 'OPEN'
            for update;
            """;
        string sequence;
        string bank;
        decimal amount;
        await using (var command = new NpgsqlCommand(sql, connection, transaction))
        {
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("customer", customerId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) throw new SalesValidationException("Cek/giro tidak ditemukan untuk customer ini atau sudah dipakai.");
            sequence = reader.GetString(0);
            bank = reader.GetString(1);
            amount = reader.GetDecimal(2);
        }

        if (receiptAmount != amount) throw new SalesValidationException($"Nilai penerimaan C/G harus sama dengan cek/giro {sequence} ({amount:N2}).");
        var account = await ReadMappingAccountAsync(connection, transaction, "CHECK_GIRO_RECEIVABLE", cancellationToken);
        return (sequence, bank, account);
    }

    private static async Task<LockedReceivable> LockReceivableAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid customerId, Guid sourceId, string sourceType, decimal amount, CancellationToken cancellationToken)
    {
        sourceType = NormalizeSourceType(sourceType);
        var sql = sourceType == "SALE"
            ? """
                select s.id,s.document_number,s.document_date,s.sales_code,s.balance_due,
                       coalesce((select sum(rl.amount) from customer_receipt_line rl join customer_receipt cr on cr.id=rl.customer_receipt_id
                                 where rl.sale_id=s.id and rl.source_type='SALE' and cr.status='POSTED'),0),
                       coalesce((select sum(d.amount) from partner_balance_document d where d.target_source_type='SALE'
                                 and d.target_source_id=s.id and d.direction='DECREASE' and d.status='POSTED'),0)
                from sale s where s.id=@id and s.customer_id=@customer and s.status='POSTED' and s.transaction_type='CREDIT' for update;
                """
            : """
                select d.id,d.document_number,d.document_date,null,d.amount,
                       coalesce((select sum(rl.amount) from customer_receipt_line rl join customer_receipt cr on cr.id=rl.customer_receipt_id
                                 where rl.balance_document_id=d.id and rl.source_type='BALANCE_DOCUMENT' and cr.status='POSTED'),0),
                       coalesce((select sum(c.amount) from partner_balance_document c where c.target_source_type='BALANCE_DOCUMENT'
                                 and c.target_source_id=d.id and c.direction='DECREASE' and c.status='POSTED'),0)
                from partner_balance_document d where d.id=@id and d.customer_id=@customer and d.partner_type='CUSTOMER'
                  and d.direction='INCREASE' and d.status='POSTED' for update;
                """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("id", sourceId);
        command.Parameters.AddWithValue("customer", customerId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new SalesValidationException("Nota piutang tidak ditemukan untuk customer ini.");
        var balance = reader.GetDecimal(4);
        var paid = reader.GetDecimal(5);
        var reduced = reader.GetDecimal(6);
        var effectiveBalance = Math.Round(balance - reduced, 2);
        var remaining = PartnerBalanceRules.Remaining(balance, paid, reduced);
        if (amount > remaining) throw new SalesValidationException($"Bayar nota {reader.GetString(1)} melebihi sisa piutang {remaining:N2}.");
        return new LockedReceivable(0, reader.GetGuid(0), sourceType, reader.GetString(1), reader.GetFieldValue<DateOnly>(2), Text(reader, 3), effectiveBalance, paid, amount, remaining - amount);
    }

    private static async Task EnsureReceiptCanBeRestoredAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid receiptId, CancellationToken cancellationToken)
    {
        const string sql = """
            select cr.customer_id,l.source_type,coalesce(l.sale_id,l.balance_document_id),l.amount
            from customer_receipt_line l join customer_receipt cr on cr.id=l.customer_receipt_id
            where l.customer_receipt_id=@receipt order by l.line_number;
            """;
        var lines = new List<(Guid CustomerId, string SourceType, Guid SourceId, decimal Amount)>();
        await using (var command = new NpgsqlCommand(sql, connection, transaction))
        {
            command.Parameters.AddWithValue("receipt", receiptId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) lines.Add((reader.GetGuid(0), reader.GetString(1), reader.GetGuid(2), reader.GetDecimal(3)));
        }
        if (lines.Count == 0) throw new SalesConflictException("Nota piutang sumber sudah tidak aktif, penerimaan tidak dapat dipulihkan.");
        foreach (var line in lines)
            try { await LockReceivableAsync(connection, transaction, line.CustomerId, line.SourceId, line.SourceType, line.Amount, cancellationToken); }
            catch (SalesValidationException exception) { throw new SalesConflictException($"Batal hapus penerimaan tidak dapat dilakukan. {exception.Message}"); }
    }

    private static async Task<string> ReadMappingAccountAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string mappingCode, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("select account_code from ledger_transaction_mapping where mapping_code = @code and account_code is not null;", connection, transaction);
        command.Parameters.AddWithValue("code", mappingCode);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is not string accountCode) throw new SalesValidationException($"Mapping jurnal {mappingCode} belum dikonfigurasi.");
        return accountCode;
    }

    private static async Task<Guid?> ReadAccountIdAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string accountCode, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("select id from ledger_account where code = @code and is_active and is_postable;", connection, transaction);
        command.Parameters.AddWithValue("code", accountCode);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value as Guid?;
    }

    private static async Task<Guid> InsertJournalAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string number, DateOnly date, Guid sourceId, string memo, string? userName, string? computerName, CancellationToken cancellationToken)
    {
        const string sql = """
            insert into journal_entry(document_number, document_year, document_date, memo, source_module, source_id, created_by_name, computer_name)
            values (@number, @year, @date, @memo, 'CUSTOMER_RECEIPT', @sourceId, @user, @computer)
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
        await using var command = new NpgsqlCommand("select exists(select 1 from customer_receipt where document_number = @number and document_year = @year);", connection, transaction);
        command.Parameters.AddWithValue("number", number);
        command.Parameters.AddWithValue("year", (short)year);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task<bool> CheckGiroNumberExistsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string number, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("select exists(select 1 from customer_check_giro where sequence_number = @number);", connection, transaction);
        command.Parameters.AddWithValue("number", number);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task<string> NextReceiptNumberAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, DateOnly date, CancellationToken cancellationToken)
    {
        var period = date.ToString("yyyyMM", CultureInfo.InvariantCulture);
        var seed = await ReadReceiptSequenceSeedAsync(connection, transaction, date, cancellationToken);
        const string sql = """
            insert into document_sequence(document_type, period, last_number)
            values ('CUSTOMER_RECEIPT', @period, @seed + 1)
            on conflict (document_type, period)
            do update set last_number = greatest(document_sequence.last_number, @seed) + 1
            returning last_number;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("period", period);
        command.Parameters.AddWithValue("seed", seed);
        var sequence = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
        var configuredPrefix = await ConfigurationRepository.GetDocumentPrefixAsync(connection, transaction, "CUSTOMER_RECEIPT", "BPT", cancellationToken);
        return $"{configuredPrefix}{date:yyMM}{sequence:000}";
    }

    private static async Task<string> PreviewNextReceiptNumberAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, DateOnly date, CancellationToken cancellationToken)
    {
        var seed = await ReadReceiptSequenceSeedAsync(connection, transaction, date, cancellationToken);
        var configuredPrefix = await ConfigurationRepository.GetDocumentPrefixAsync(connection, transaction, "CUSTOMER_RECEIPT", "BPT", cancellationToken);
        return $"{configuredPrefix}{date:yyMM}{seed + 1:000}";
    }

    private static async Task<int> ReadReceiptSequenceSeedAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, DateOnly date, CancellationToken cancellationToken)
    {
        const string sql = """
            select greatest(
                coalesce((select last_number from document_sequence where document_type = 'CUSTOMER_RECEIPT' and period = @period), 0),
                coalesce((
                    select max(nullif(regexp_replace(substring(document_number from char_length(@prefix) + 1), '\D', '', 'g'), '')::integer)
                    from customer_receipt
                    where document_year = @year and document_number like @prefix || '%'
                ), 0)
            );
            """;
        var configuredPrefix = await ConfigurationRepository.GetDocumentPrefixAsync(connection, transaction, "CUSTOMER_RECEIPT", "BPT", cancellationToken);
        var prefix = $"{configuredPrefix}{date:yyMM}";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("period", date.ToString("yyyyMM", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("year", (short)date.Year);
        command.Parameters.AddWithValue("prefix", prefix);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<string> NextCheckGiroNumberAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        var seed = await ReadCheckGiroSequenceSeedAsync(connection, transaction, cancellationToken);

        const string sql = """
            insert into document_sequence(document_type, period, last_number)
            values ('CUSTOMER_CHECK_GIRO', 'GLOBAL', @seed + 1)
            on conflict (document_type, period)
            do update set last_number = greatest(document_sequence.last_number, @seed) + 1
            returning last_number;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("seed", seed);
        var sequence = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
        var prefix = await ConfigurationRepository.GetDocumentPrefixAsync(connection, transaction, "CUSTOMER_CHECK_GIRO", "CG", cancellationToken);
        return $"{prefix}{sequence:00000000}";
    }

    private static async Task<int> ReadCheckGiroSequenceSeedAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken cancellationToken)
    {
        const string sql = """
            select greatest(
                coalesce((select last_number from document_sequence where document_type = 'CUSTOMER_CHECK_GIRO' and period = 'GLOBAL'), 0),
                coalesce((
                    select max(nullif(regexp_replace(substring(sequence_number from char_length(@prefix) + 1), '\D', '', 'g'), '')::integer)
                    from customer_check_giro
                    where sequence_number like @prefix || '%'
                ), 0)
            );
            """;
        var prefix = await ConfigurationRepository.GetDocumentPrefixAsync(connection, transaction, "CUSTOMER_CHECK_GIRO", "CG", cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("prefix", prefix);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static CustomerCheckGiroDto ReadCheckGiro(NpgsqlDataReader reader) => new(
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
        reader.GetString(10),
        reader.GetString(11),
        Text(reader, 12),
        reader.GetString(13),
        reader.GetBoolean(14),
        reader.GetBoolean(15),
        reader.IsDBNull(16) ? null : reader.GetFieldValue<DateOnly>(16),
        reader.IsDBNull(17) ? null : reader.GetFieldValue<DateOnly>(17),
        reader.GetString(18),
        Text(reader, 19));

    private static string? NormalizeDocumentNumber(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrWhiteSpace(text) || text.Equals("OTOMATIS", StringComparison.OrdinalIgnoreCase)) return null;
        if (text.Length > 30) throw new SalesValidationException("No. Bukti/No. Urut maksimal 30 karakter.");
        return text.ToUpperInvariant();
    }

    private static string NormalizeMethod(string value)
    {
        var method = NormalizeCode(value, "Cara terima", 12);
        if (method is not ("CASH" or "BANK" or "CHECK_GIRO")) throw new SalesValidationException("Cara terima harus CASH, BANK, atau CHECK_GIRO.");
        return method;
    }

    private static string NormalizeOriginArea(string? value)
    {
        var text = (value ?? "LOCAL").Trim().ToUpperInvariant();
        return text is "OUT_OF_TOWN" or "LUAR_KOTA" or "LUAR KOTA" ? "OUT_OF_TOWN" : "LOCAL";
    }

    private static string NormalizeCode(string value, string label, int maxLength)
    {
        var text = value.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(text)) throw new SalesValidationException($"{label} wajib diisi.");
        if (text.Length > maxLength) throw new SalesValidationException($"{label} maksimal {maxLength} karakter.");
        return text;
    }

    private static string NormalizeSourceType(string? value)
    {
        var sourceType = value?.Trim().ToUpperInvariant();
        return sourceType is "SALE" or "BALANCE_DOCUMENT"
            ? sourceType
            : throw new SalesValidationException("Jenis sumber piutang tidak valid.");
    }

    private static string NormalizeText(string value, string label, int maxLength)
    {
        var text = value.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(text)) throw new SalesValidationException($"{label} wajib diisi.");
        if (text.Length > maxLength) throw new SalesValidationException($"{label} maksimal {maxLength} karakter.");
        return text;
    }

    private static object Db(string? value) => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();
    private static string? Text(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private sealed record RequestedReceiptLine(int LineNumber, Guid SaleId, string SourceType, decimal Amount);
    private sealed record LockedReceivable(int LineNumber, Guid SourceId, string SourceType, string DocumentNumber, DateOnly DocumentDate, string? SalesCode, decimal BalanceDue, decimal PaidBefore, decimal Amount, decimal RemainingAfter);
    private sealed record CustomerReceiptHeader(
        Guid Id,
        string DocumentNumber,
        DateOnly DocumentDate,
        string CustomerCode,
        string CustomerName,
        string? CollectorCode,
        string Method,
        string? CashAccountCode,
        string? CashAccountName,
        string? BankAccountCode,
        string? BankAccountName,
        CustomerCheckGiroDto? CheckGiro,
        decimal ReceiptAmount,
        string Status,
        string? Notes,
        string JournalNumber,
        DateTimeOffset CreatedAt);
}
