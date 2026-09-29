using System.Data;
using System.Text.Json;
using Aksara.Contracts;
using Aksara.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Aksara.Infrastructure;

public sealed class CashBankValidationException(string message) : InvalidOperationException(message);
public sealed class CashBankConflictException(string message) : InvalidOperationException(message);

public sealed class CashBankRepository(PostgresDatabase database)
{
    public async Task<IReadOnlyList<CashAccountDto>> ListCashAccountsAsync(bool includeInactive = true, CancellationToken cancellationToken = default)
    {
        const string sql = """
            select c.code, c.name, c.account_code, a.name, c.is_active, c.version
            from cash_account c
            join ledger_account a on a.code = c.account_code
            where @includeInactive or c.is_active
            order by c.code;
            """;
        await using var command = database.DataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("includeInactive", includeInactive);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<CashAccountDto>();
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetBoolean(4), reader.GetInt64(5)));
        return result;
    }

    public async Task<CashAccountDto> SaveCashAccountAsync(SaveCashAccountRequest request, CancellationToken cancellationToken = default)
    {
        var code = NormalizeCode(request.Code, "Kode kas", 20);
        var name = Required(request.Name, "Nama kas", 100);
        var account = NormalizeCode(request.AccountCode, "Kode perkiraan", 20);
        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            var existingVersion = await LedgerRepository.ReadVersionAsync(connection, transaction, "select version from cash_account where code = @code for update;", cancellationToken, ("code", code));
            LedgerRepository.ValidateExpectedVersion(existingVersion, request.ExpectedVersion, $"Kas {code}");
            if (!await IsCashBankLedgerAccountAsync(connection, transaction, account, cancellationToken))
                throw new CashBankValidationException($"Perkiraan {account} bukan akun kas/bank aktif.");
            const string sql = """
                insert into cash_account(code, name, account_code, is_active, updated_at, version)
                values (@code, @name, @account, @active, now(), 1)
                on conflict (code) do update set
                    name = excluded.name,
                    account_code = excluded.account_code,
                    is_active = excluded.is_active,
                    updated_at = now(),
                    version = cash_account.version + 1
                returning code, name, account_code, (select name from ledger_account where code = cash_account.account_code), is_active, version;
                """;
            CashAccountDto saved;
            await using (var command = new NpgsqlCommand(sql, connection, transaction))
            {
                command.Parameters.AddWithValue("code", code);
                command.Parameters.AddWithValue("name", name);
                command.Parameters.AddWithValue("account", account);
                command.Parameters.AddWithValue("active", request.IsActive);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                await reader.ReadAsync(cancellationToken);
                saved = new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetBoolean(4), reader.GetInt64(5));
            }
            await InsertAuditAsync(connection, transaction, "CASH_ACCOUNT", code, existingVersion is null ? "CREATE" : "UPDATE", request.UserName, request.ComputerName, request, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return saved;
        }
        catch (PostgresException exception) when (exception.SqlState is PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.UniqueViolation)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new CashBankConflictException($"Kas {code} berubah atau kodenya sudah digunakan. Muat ulang lalu coba kembali.");
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<IReadOnlyList<BankAccountDto>> ListBankAccountsAsync(bool includeInactive = true, CancellationToken cancellationToken = default)
    {
        const string sql = """
            select b.code, b.bank_name, b.account_number, b.account_holder, b.account_code, a.name, b.is_active, b.version
            from bank_account b
            join ledger_account a on a.code = b.account_code
            where @includeInactive or b.is_active
            order by b.code;
            """;
        await using var command = database.DataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("includeInactive", includeInactive);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<BankAccountDto>();
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new(reader.GetString(0), reader.GetString(1), Text(reader, 2), Text(reader, 3), reader.GetString(4), reader.GetString(5), reader.GetBoolean(6), reader.GetInt64(7)));
        return result;
    }

    public async Task<BankAccountDto> SaveBankAccountAsync(SaveBankAccountRequest request, CancellationToken cancellationToken = default)
    {
        var code = NormalizeCode(request.Code, "Kode bank", 20);
        var name = Required(request.BankName, "Nama bank", 100);
        var account = NormalizeCode(request.AccountCode, "Kode perkiraan", 20);
        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            var existingVersion = await LedgerRepository.ReadVersionAsync(connection, transaction, "select version from bank_account where code = @code for update;", cancellationToken, ("code", code));
            LedgerRepository.ValidateExpectedVersion(existingVersion, request.ExpectedVersion, $"Bank {code}");
            if (!await IsCashBankLedgerAccountAsync(connection, transaction, account, cancellationToken))
                throw new CashBankValidationException($"Perkiraan {account} bukan akun kas/bank aktif.");
            const string sql = """
                insert into bank_account(code, bank_name, account_number, account_holder, account_code, is_active, updated_at, version)
                values (@code, @name, @number, @holder, @account, @active, now(), 1)
                on conflict (code) do update set
                    bank_name = excluded.bank_name,
                    account_number = excluded.account_number,
                    account_holder = excluded.account_holder,
                    account_code = excluded.account_code,
                    is_active = excluded.is_active,
                    updated_at = now(),
                    version = bank_account.version + 1
                returning code, bank_name, account_number, account_holder, account_code, (select name from ledger_account where code = bank_account.account_code), is_active, version;
                """;
            BankAccountDto saved;
            await using (var command = new NpgsqlCommand(sql, connection, transaction))
            {
                command.Parameters.AddWithValue("code", code);
                command.Parameters.AddWithValue("name", name);
                command.Parameters.AddWithValue("number", Db(request.AccountNumber));
                command.Parameters.AddWithValue("holder", Db(request.AccountHolder));
                command.Parameters.AddWithValue("account", account);
                command.Parameters.AddWithValue("active", request.IsActive);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                await reader.ReadAsync(cancellationToken);
                saved = new(reader.GetString(0), reader.GetString(1), Text(reader, 2), Text(reader, 3), reader.GetString(4), reader.GetString(5), reader.GetBoolean(6), reader.GetInt64(7));
            }
            await InsertAuditAsync(connection, transaction, "BANK_ACCOUNT", code, existingVersion is null ? "CREATE" : "UPDATE", request.UserName, request.ComputerName, request, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return saved;
        }
        catch (PostgresException exception) when (exception.SqlState is PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.UniqueViolation)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new CashBankConflictException($"Bank {code} berubah atau kodenya sudah digunakan. Muat ulang lalu coba kembali.");
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<IReadOnlyList<CashBankCategoryDto>> ListCategoriesAsync(string? categoryType, bool includeDeleted = true, CancellationToken cancellationToken = default)
    {
        const string sql = """
            select c.category_type, c.code, c.name, c.account_code, a.name, c.is_deleted, c.is_active, c.version
            from cash_bank_category c
            join ledger_account a on a.code = c.account_code
            where (@type = '' or c.category_type = @type)
              and (@includeDeleted or not c.is_deleted)
            order by c.category_type, c.code;
            """;
        await using var command = database.DataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("type", categoryType?.Trim().ToUpperInvariant() ?? string.Empty);
        command.Parameters.AddWithValue("includeDeleted", includeDeleted);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<CashBankCategoryDto>();
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetBoolean(5), reader.GetBoolean(6), reader.GetInt64(7)));
        return result;
    }

    public async Task<CashBankCategoryDto> SaveCategoryAsync(SaveCashBankCategoryRequest request, CancellationToken cancellationToken = default)
    {
        var type = NormalizeCategoryType(request.CategoryType);
        var code = NormalizeCode(request.Code, "Kode daftar", 20);
        var name = Required(request.Name, type == "EXPENSE" ? "Nama biaya" : "Nama penghasilan", 120);
        var account = NormalizeCode(request.AccountCode, "Kode perkiraan", 20);
        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            var existingVersion = await LedgerRepository.ReadVersionAsync(connection, transaction, "select version from cash_bank_category where category_type = @type and code = @code for update;", cancellationToken, ("type", type), ("code", code));
            LedgerRepository.ValidateExpectedVersion(existingVersion, request.ExpectedVersion, $"{(type == "EXPENSE" ? "Biaya" : "Penghasilan")} {code}");
            if (!await LedgerRepository.IsPostableAccountAsync(connection, transaction, account, cancellationToken))
                throw new CashBankValidationException($"Perkiraan {account} tidak ditemukan atau tidak dapat diposting.");
            const string sql = """
                insert into cash_bank_category(category_type, code, name, account_code, is_deleted, is_active, updated_at, version)
                values (@type, @code, @name, @account, @deleted, @active, now(), 1)
                on conflict (category_type, code) do update set
                    name = excluded.name,
                    account_code = excluded.account_code,
                    is_deleted = excluded.is_deleted,
                    is_active = excluded.is_active,
                    updated_at = now(),
                    version = cash_bank_category.version + 1
                returning category_type, code, name, account_code, (select name from ledger_account where code = cash_bank_category.account_code), is_deleted, is_active, version;
                """;
            CashBankCategoryDto saved;
            await using (var command = new NpgsqlCommand(sql, connection, transaction))
            {
                command.Parameters.AddWithValue("type", type);
                command.Parameters.AddWithValue("code", code);
                command.Parameters.AddWithValue("name", name);
                command.Parameters.AddWithValue("account", account);
                command.Parameters.AddWithValue("deleted", request.IsDeleted);
                command.Parameters.AddWithValue("active", request.IsActive);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                await reader.ReadAsync(cancellationToken);
                saved = new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetBoolean(5), reader.GetBoolean(6), reader.GetInt64(7));
            }
            await InsertAuditAsync(connection, transaction, "CASH_BANK_CATEGORY", $"{type}:{code}", existingVersion is null ? "CREATE" : "UPDATE", request.UserName, request.ComputerName, request, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return saved;
        }
        catch (PostgresException exception) when (exception.SqlState is PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.UniqueViolation)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new CashBankConflictException($"Daftar {code} berubah atau kodenya sudah digunakan. Muat ulang lalu coba kembali.");
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<IReadOnlyList<CashBankTransactionDto>> ListTransactionsAsync(string? query, int limit = 200, CancellationToken cancellationToken = default)
    {
        const string sql = """
            select t.id, t.document_number, t.document_date, t.transaction_kind, t.method,
                   t.cash_account_code, c.name, t.bank_account_code, b.bank_name,
                   t.check_giro_number, t.check_giro_due_date, t.counterparty, t.category_type, t.category_code,
                   t.account_code, a.name, t.amount, t.notes, t.status, j.document_number, t.created_at
            from cash_bank_transaction t
            join ledger_account a on a.code = t.account_code
            join journal_entry j on j.id = t.journal_entry_id
            left join cash_account c on c.code = t.cash_account_code
            left join bank_account b on b.code = t.bank_account_code
            where @query = ''
               or t.document_number ilike '%' || @query || '%'
               or coalesce(t.counterparty, '') ilike '%' || @query || '%'
               or coalesce(t.notes, '') ilike '%' || @query || '%'
               or a.code ilike '%' || @query || '%'
               or a.name ilike '%' || @query || '%'
            order by t.document_date desc, t.document_number desc
            limit @limit;
            """;
        await using var command = database.DataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("query", query?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 1000));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<CashBankTransactionDto>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadTransaction(reader));
        return result;
    }

    public async Task<CashBankTransactionDto> CreateTransactionAsync(CreateCashBankTransactionRequest request, CancellationToken cancellationToken = default)
    {
        var kind = NormalizeKind(request.TransactionKind);
        var method = NormalizeMethod(request.Method);
        var amount = Math.Round(request.Amount, 2);
        if (amount <= 0) throw new CashBankValidationException("Jumlah transaksi harus lebih dari nol.");
        var cashCode = string.IsNullOrWhiteSpace(request.CashAccountCode) ? null : NormalizeCode(request.CashAccountCode, "Kode kas", 20);
        var bankCode = string.IsNullOrWhiteSpace(request.BankAccountCode) ? null : NormalizeCode(request.BankAccountCode, "Kode bank", 20);
        if (method == "CASH" && cashCode is null) throw new CashBankValidationException("Kas wajib dipilih untuk transaksi kas.");
        if (method is "BANK" or "CHECK_GIRO" && bankCode is null) throw new CashBankValidationException("Bank wajib dipilih untuk transaksi bank/cek-giro.");

        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            var settlement = method == "CASH"
                ? await ReadCashLedgerAccountAsync(connection, transaction, cashCode!, cancellationToken)
                : await ReadBankLedgerAccountAsync(connection, transaction, bankCode!, cancellationToken);
            var category = await ResolveCategoryAsync(connection, transaction, request.CategoryType, request.CategoryCode, cancellationToken);
            var postingAccount = category?.AccountCode ?? NormalizeCode(request.AccountCode, "Kode perkiraan", 20);
            var postingAccountId = await ReadAccountIdAsync(connection, transaction, postingAccount, cancellationToken)
                ?? throw new CashBankValidationException($"Perkiraan {postingAccount} tidak ditemukan atau tidak dapat diposting.");
            var settlementAccountId = await ReadAccountIdAsync(connection, transaction, settlement.AccountCode, cancellationToken)
                ?? throw new CashBankValidationException($"Perkiraan {settlement.AccountCode} tidak ditemukan atau tidak dapat diposting.");

            var journalLines = kind == "RECEIPT"
                ? new[] { new JournalLineRule(settlement.AccountCode, amount, 0), new JournalLineRule(postingAccount, 0, amount) }
                : new[] { new JournalLineRule(postingAccount, amount, 0), new JournalLineRule(settlement.AccountCode, 0, amount) };
            AccountingRules.ValidateBalancedJournal(journalLines);

            var cashBankId = Guid.NewGuid();
            var cashBankNumber = await LedgerRepository.NextDocumentNumberAsync(connection, transaction, "CASH_BANK", "KB", request.DocumentDate, cancellationToken);
            var journalNumber = await LedgerRepository.NextDocumentNumberAsync(connection, transaction, "CASH_BANK_JOURNAL", "JKB", request.DocumentDate, cancellationToken);
            Guid journalId;
            var memo = BuildMemo(kind, method, request.Counterparty, request.Notes);
            const string journalSql = """
                insert into journal_entry(document_number, document_year, document_date, memo, source_module, source_id, created_by_name, computer_name)
                values (@number, @year, @date, @memo, 'CASH_BANK', @sourceId, @user, @computer)
                returning id;
                """;
            await using (var command = new NpgsqlCommand(journalSql, connection, transaction))
            {
                command.Parameters.AddWithValue("number", journalNumber);
                command.Parameters.AddWithValue("year", (short)request.DocumentDate.Year);
                command.Parameters.AddWithValue("date", request.DocumentDate);
                command.Parameters.AddWithValue("memo", memo);
                command.Parameters.AddWithValue("sourceId", cashBankId);
                command.Parameters.AddWithValue("user", Db(request.UserName));
                command.Parameters.AddWithValue("computer", Db(request.ComputerName));
                journalId = (Guid)(await command.ExecuteScalarAsync(cancellationToken))!;
            }

            if (kind == "RECEIPT")
            {
                await InsertJournalLineAsync(connection, transaction, journalId, 1, settlementAccountId, memo, amount, 0, cancellationToken);
                await InsertJournalLineAsync(connection, transaction, journalId, 2, postingAccountId, memo, 0, amount, cancellationToken);
            }
            else
            {
                await InsertJournalLineAsync(connection, transaction, journalId, 1, postingAccountId, memo, amount, 0, cancellationToken);
                await InsertJournalLineAsync(connection, transaction, journalId, 2, settlementAccountId, memo, 0, amount, cancellationToken);
            }

            const string transactionSql = """
                insert into cash_bank_transaction(id, document_number, document_year, document_date, transaction_kind, method,
                    cash_account_code, bank_account_code, check_giro_number, check_giro_due_date, counterparty,
                    category_type, category_code, account_code, amount, notes, journal_entry_id, created_by_name, computer_name)
                values (@id, @number, @year, @date, @kind, @method, @cash, @bank, @giro, @due, @counterparty,
                    @categoryType, @categoryCode, @account, @amount, @notes, @journal, @user, @computer);
                """;
            await ExecuteAsync(connection, transaction, transactionSql, cancellationToken,
                ("id", cashBankId), ("number", cashBankNumber), ("year", (short)request.DocumentDate.Year), ("date", request.DocumentDate),
                ("kind", kind), ("method", method), ("cash", Db(cashCode)), ("bank", Db(bankCode)), ("giro", Db(request.CheckGiroNumber)),
                ("due", request.CheckGiroDueDate is null ? DBNull.Value : request.CheckGiroDueDate.Value), ("counterparty", Db(request.Counterparty)),
                ("categoryType", Db(category?.CategoryType)), ("categoryCode", Db(category?.Code)), ("account", postingAccount),
                ("amount", amount), ("notes", Db(request.Notes)), ("journal", journalId), ("user", Db(request.UserName)), ("computer", Db(request.ComputerName)));

            await InsertAuditAsync(connection, transaction, "CASH_BANK_TRANSACTION", cashBankId.ToString(), "CREATE", request.UserName, request.ComputerName, request, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return (await ReadTransactionAsync(cashBankId, cancellationToken))!;
        }
        catch (PostgresException exception) when (exception.SqlState is PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.UniqueViolation)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new CashBankConflictException("Transaksi kas-bank berubah saat disimpan. Muat ulang lalu coba kembali.");
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<CashBankTransactionDto?> ReadTransactionAsync(Guid id, CancellationToken cancellationToken = default)
    {
        const string sql = """
            select t.id, t.document_number, t.document_date, t.transaction_kind, t.method,
                   t.cash_account_code, c.name, t.bank_account_code, b.bank_name,
                   t.check_giro_number, t.check_giro_due_date, t.counterparty, t.category_type, t.category_code,
                   t.account_code, a.name, t.amount, t.notes, t.status, j.document_number, t.created_at
            from cash_bank_transaction t
            join ledger_account a on a.code = t.account_code
            join journal_entry j on j.id = t.journal_entry_id
            left join cash_account c on c.code = t.cash_account_code
            left join bank_account b on b.code = t.bank_account_code
            where t.id = @id;
            """;
        await using var command = database.DataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadTransaction(reader) : null;
    }

    public async Task<IReadOnlyList<CashBankTransactionLineDto>> ListTransactionLinesAsync(Guid transactionId, CancellationToken cancellationToken = default)
    {
        const string sql = """
            select l.line_number, l.settlement_method, l.settlement_code, l.settlement_account_code,
                   l.counterparty_group, l.counterparty_code, l.counterparty_name,
                   l.account_code, a.name, l.amount, l.notes, l.check_giro_number, l.check_giro_due_date
            from cash_bank_transaction_line l
            join ledger_account a on a.code = l.account_code
            where l.transaction_id = @transactionId
            order by l.line_number;
            """;
        await using var command = database.DataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("transactionId", transactionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<CashBankTransactionLineDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new(
                reader.GetInt32(0),
                reader.GetString(1),
                Text(reader, 2),
                reader.GetString(3),
                Text(reader, 4),
                Text(reader, 5),
                Text(reader, 6),
                reader.GetString(7),
                reader.GetString(8),
                reader.GetDecimal(9),
                Text(reader, 10),
                Text(reader, 11),
                reader.IsDBNull(12) ? null : reader.GetFieldValue<DateOnly>(12)));
        }

        return result;
    }

    public async Task<CashBankTransactionDto> CancelTransactionAsync(Guid id, CancelCashBankTransactionRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Reason)) throw new CashBankValidationException("Alasan hapus wajib diisi.");
        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            const string lockSql = "select document_number, status from cash_bank_transaction where id = @id for update;";
            string number;
            string status;
            await using (var command = new NpgsqlCommand(lockSql, connection, transaction))
            {
                command.Parameters.AddWithValue("id", id);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken)) throw new CashBankValidationException("Transaksi kas-bank tidak ditemukan.");
                number = reader.GetString(0);
                status = reader.GetString(1);
            }

            if (!string.Equals(status, "POSTED", StringComparison.OrdinalIgnoreCase))
                throw new CashBankConflictException("Transaksi kas-bank sudah ditandai hapus.");

            await InventoryLedgerPosting.CancelAsync(connection, transaction, id, "CASH_BANK", request.UserName, request.Reason, cancellationToken);

            const string updateSql = """
                update cash_bank_transaction
                set status = 'CANCELLED', cancelled_at = now(), cancelled_by_name = @user,
                    cancellation_reason = @reason, updated_at = now(), version = version + 1
                where id = @id;
                """;
            await ExecuteAsync(connection, transaction, updateSql, cancellationToken,
                ("id", id), ("user", Db(request.UserName)), ("reason", request.Reason.Trim()));

            await InsertAuditAsync(connection, transaction, "CASH_BANK_TRANSACTION", id.ToString(), "CANCEL", request.UserName, request.ComputerName, new { documentNumber = number, reason = request.Reason.Trim() }, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return (await ReadTransactionAsync(id, cancellationToken))!;
        }
        catch (PostgresException exception) when (exception.SqlState is PostgresErrorCodes.SerializationFailure)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new CashBankConflictException("Transaksi kas-bank berubah saat ditandai hapus. Muat ulang lalu coba kembali.");
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<CashBankTransactionDto> RestoreTransactionAsync(Guid id, RestoreCashBankTransactionRequest request, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            const string lockSql = "select document_number, status from cash_bank_transaction where id = @id for update;";
            string number;
            string status;
            await using (var command = new NpgsqlCommand(lockSql, connection, transaction))
            {
                command.Parameters.AddWithValue("id", id);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken)) throw new CashBankValidationException("Transaksi kas-bank tidak ditemukan.");
                number = reader.GetString(0);
                status = reader.GetString(1);
            }

            if (!string.Equals(status, "CANCELLED", StringComparison.OrdinalIgnoreCase))
                throw new CashBankConflictException("Transaksi kas-bank belum ditandai hapus.");

            await InventoryLedgerPosting.RestoreAsync(connection, transaction, id, "CASH_BANK", cancellationToken);

            const string updateSql = """
                update cash_bank_transaction
                set status = 'POSTED', cancelled_at = null, cancelled_by_name = null,
                    cancellation_reason = null, updated_at = now(), version = version + 1
                where id = @id;
                """;
            await ExecuteAsync(connection, transaction, updateSql, cancellationToken, ("id", id));

            await InsertAuditAsync(connection, transaction, "CASH_BANK_TRANSACTION", id.ToString(), "RESTORE", request.UserName, request.ComputerName, new { documentNumber = number, status = "POSTED" }, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return (await ReadTransactionAsync(id, cancellationToken))!;
        }
        catch (PostgresException exception) when (exception.SqlState is PostgresErrorCodes.SerializationFailure)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new CashBankConflictException("Transaksi kas-bank berubah saat batal hapus. Muat ulang lalu coba kembali.");
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static CashBankTransactionDto ReadTransaction(NpgsqlDataReader reader) => new(
        reader.GetGuid(0), reader.GetString(1), reader.GetFieldValue<DateOnly>(2), reader.GetString(3), reader.GetString(4),
        Text(reader, 5), Text(reader, 6), Text(reader, 7), Text(reader, 8), Text(reader, 9),
        reader.IsDBNull(10) ? null : reader.GetFieldValue<DateOnly>(10), Text(reader, 11), Text(reader, 12), Text(reader, 13),
        reader.GetString(14), reader.GetString(15), reader.GetDecimal(16), Text(reader, 17), reader.GetString(18), reader.GetString(19), reader.GetFieldValue<DateTimeOffset>(20));

    private static async Task InsertJournalLineAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid journalId, int lineNumber, Guid accountId, string description, decimal debit, decimal credit, CancellationToken cancellationToken)
    {
        const string sql = """
            insert into journal_entry_line(journal_entry_id, line_number, account_id, description, debit, credit)
            values (@journal, @line, @account, @description, @debit, @credit);
            """;
        await ExecuteAsync(connection, transaction, sql, cancellationToken,
            ("journal", journalId), ("line", lineNumber), ("account", accountId), ("description", description), ("debit", debit), ("credit", credit));
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<Guid?> ReadAccountIdAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string accountCode, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("select id from ledger_account where code = @code and is_active and is_postable;", connection, transaction);
        command.Parameters.AddWithValue("code", accountCode);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value as Guid?;
    }

    private static async Task<(string Code, string AccountCode)> ReadCashLedgerAccountAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string code, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("select code, account_code from cash_account where code = @code and is_active;", connection, transaction);
        command.Parameters.AddWithValue("code", code);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new CashBankValidationException($"Kas {code} tidak ditemukan atau nonaktif.");
        return (reader.GetString(0), reader.GetString(1));
    }

    private static async Task<(string Code, string AccountCode)> ReadBankLedgerAccountAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string code, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("select code, account_code from bank_account where code = @code and is_active;", connection, transaction);
        command.Parameters.AddWithValue("code", code);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new CashBankValidationException($"Bank {code} tidak ditemukan atau nonaktif.");
        return (reader.GetString(0), reader.GetString(1));
    }

    private static async Task<CashBankCategoryDto?> ResolveCategoryAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string? categoryType, string? categoryCode, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(categoryType) || string.IsNullOrWhiteSpace(categoryCode)) return null;
        var type = NormalizeCategoryType(categoryType);
        var code = NormalizeCode(categoryCode, "Kode daftar", 20);
        const string sql = """
            select c.category_type, c.code, c.name, c.account_code, a.name, c.is_deleted, c.is_active, c.version
            from cash_bank_category c
            join ledger_account a on a.code = c.account_code
            where c.category_type = @type and c.code = @code and c.is_active and not c.is_deleted;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("type", type);
        command.Parameters.AddWithValue("code", code);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new CashBankValidationException($"Daftar {code} tidak ditemukan atau sudah dihapus.");
        return new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetBoolean(5), reader.GetBoolean(6), reader.GetInt64(7));
    }

    private static async Task<bool> IsCashBankLedgerAccountAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string accountCode, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("select exists(select 1 from ledger_account where code = @code and is_active and is_postable and is_cash_bank);", connection, transaction);
        command.Parameters.AddWithValue("code", accountCode);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task InsertAuditAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string entityType, string entityId, string action, string? userName, string? computerName, object afterData, CancellationToken cancellationToken)
    {
        const string sql = """
            insert into audit_log(computer_name, module, action, entity_type, entity_id, after_data)
            values (@computer, 'KAS BANK', @action, @entityType, @entityId, @afterData);
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("computer", Db(computerName));
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("entityType", entityType);
        command.Parameters.AddWithValue("entityId", entityId);
        command.Parameters.Add(new NpgsqlParameter("afterData", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(new { userName, data = afterData }) });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string NormalizeKind(string value)
    {
        var kind = Required(value, "Jenis transaksi", 12).ToUpperInvariant();
        if (kind is not ("RECEIPT" or "PAYMENT")) throw new CashBankValidationException("Jenis transaksi harus RECEIPT atau PAYMENT.");
        return kind;
    }

    private static string NormalizeMethod(string value)
    {
        var method = Required(value, "Cara bayar", 12).ToUpperInvariant();
        if (method is not ("CASH" or "BANK" or "CHECK_GIRO")) throw new CashBankValidationException("Cara transaksi harus CASH, BANK, atau CHECK_GIRO.");
        return method;
    }

    private static string NormalizeCategoryType(string value)
    {
        var type = Required(value, "Jenis daftar", 12).ToUpperInvariant();
        if (type is not ("EXPENSE" or "INCOME")) throw new CashBankValidationException("Jenis daftar harus EXPENSE atau INCOME.");
        return type;
    }

    private static string BuildMemo(string kind, string method, string? counterparty, string? notes)
    {
        var title = kind == "RECEIPT" ? "Penerimaan" : "Pengeluaran";
        var via = method == "CHECK_GIRO" ? "cek/giro" : method == "BANK" ? "bank" : "kas";
        var suffix = string.IsNullOrWhiteSpace(counterparty) ? notes : counterparty;
        return string.IsNullOrWhiteSpace(suffix) ? $"{title} {via}" : $"{title} {via} - {suffix.Trim()}";
    }

    private static string NormalizeCode(string value, string label, int maxLength)
    {
        var code = Required(value, label, maxLength).ToUpperInvariant();
        if (code.Any(char.IsWhiteSpace)) throw new CashBankValidationException($"{label} tidak boleh mengandung spasi.");
        return code;
    }

    private static string Required(string? value, string label, int maxLength)
    {
        var text = value?.Trim();
        if (string.IsNullOrWhiteSpace(text)) throw new CashBankValidationException($"{label} wajib diisi.");
        if (text.Length > maxLength) throw new CashBankValidationException($"{label} maksimal {maxLength} karakter.");
        return text;
    }

    private static object Db(string? value) => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();
    private static string? Text(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
}
