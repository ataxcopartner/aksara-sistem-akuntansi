using System.Data;
using System.Text.Json;
using Aksara.Contracts;
using Aksara.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Aksara.Infrastructure;

public sealed class LedgerValidationException(string message) : InvalidOperationException(message);
public sealed class LedgerConflictException(string message) : InvalidOperationException(message);

public sealed class LedgerRepository(PostgresDatabase database)
{
    public async Task<IReadOnlyList<LedgerAccountDto>> ListAccountsAsync(
        string? query,
        bool includeInactive = true,
        bool onlyPostable = false,
        string? accountType = null,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            select id, code, name, account_type, normal_balance, parent_code, is_postable, is_cash_bank, is_active, version
            from ledger_account
            where (@includeInactive or is_active)
              and (not @onlyPostable or is_postable)
              and (@accountType = '' or account_type = @accountType)
              and (@query = '' or code ilike '%' || @query || '%' or name ilike '%' || @query || '%')
            order by code;
            """;
        await using var command = database.DataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("includeInactive", includeInactive);
        command.Parameters.AddWithValue("onlyPostable", onlyPostable);
        command.Parameters.AddWithValue("accountType", accountType?.Trim().ToUpperInvariant() ?? string.Empty);
        command.Parameters.AddWithValue("query", query?.Trim() ?? string.Empty);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<LedgerAccountDto>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadAccount(reader));
        return result;
    }

    public async Task<LedgerAccountDto> SaveAccountAsync(SaveLedgerAccountRequest request, CancellationToken cancellationToken = default)
    {
        var code = NormalizeCode(request.Code, "Kode perkiraan", 20);
        var name = Required(request.Name, "Nama perkiraan", 150);
        var type = NormalizeType(request.AccountType);
        var normal = NormalizeNormalBalance(request.NormalBalance, type);
        var parent = string.IsNullOrWhiteSpace(request.ParentCode) ? null : NormalizeCode(request.ParentCode, "Kode induk", 20);
        if (parent == code) throw new LedgerValidationException("Kode induk tidak boleh sama dengan kode perkiraan.");
        if (request.IsCashBank && type != "ASSET") throw new LedgerValidationException("Akun kas/bank harus berada pada kelompok Aktiva.");

        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            var existingVersion = await ReadVersionAsync(connection, transaction, "select version from ledger_account where code = @code for update;", cancellationToken, ("code", code));
            ValidateExpectedVersion(existingVersion, request.ExpectedVersion, $"Perkiraan {code}");
            if (parent is not null && !await ExistsAsync(connection, transaction, "ledger_account", "code", parent, cancellationToken))
                throw new LedgerValidationException($"Kode induk {parent} tidak ditemukan.");

            const string sql = """
                insert into ledger_account(code, name, account_type, normal_balance, parent_code, is_postable, is_cash_bank, is_active, updated_at, version)
                values (@code, @name, @type, @normal, @parent, @postable, @cashBank, @active, now(), 1)
                on conflict (code) do update set
                    name = excluded.name,
                    account_type = excluded.account_type,
                    normal_balance = excluded.normal_balance,
                    parent_code = excluded.parent_code,
                    is_postable = excluded.is_postable,
                    is_cash_bank = excluded.is_cash_bank,
                    is_active = excluded.is_active,
                    updated_at = now(),
                    version = ledger_account.version + 1
                returning id, code, name, account_type, normal_balance, parent_code, is_postable, is_cash_bank, is_active, version;
                """;
            LedgerAccountDto saved;
            await using (var command = new NpgsqlCommand(sql, connection, transaction))
            {
                command.Parameters.AddWithValue("code", code);
                command.Parameters.AddWithValue("name", name);
                command.Parameters.AddWithValue("type", type);
                command.Parameters.AddWithValue("normal", normal);
                command.Parameters.AddWithValue("parent", Db(parent));
                command.Parameters.AddWithValue("postable", request.IsPostable);
                command.Parameters.AddWithValue("cashBank", request.IsCashBank);
                command.Parameters.AddWithValue("active", request.IsActive);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                await reader.ReadAsync(cancellationToken);
                saved = ReadAccount(reader);
            }

            await InsertAuditAsync(connection, transaction, "LEDGER_ACCOUNT", code, existingVersion is null ? "CREATE" : "UPDATE", request.UserName, request.ComputerName, request, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return saved;
        }
        catch (PostgresException exception) when (exception.SqlState is PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.UniqueViolation)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new LedgerConflictException($"Perkiraan {code} berubah atau kodenya sudah digunakan. Muat ulang lalu coba kembali.");
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<IReadOnlyList<JournalEntrySummaryDto>> ListJournalEntriesAsync(string? query, int limit = 200, CancellationToken cancellationToken = default)
    {
        const string sql = """
            select e.id, e.document_number, e.document_date, coalesce(e.memo, ''), e.source_module, e.status,
                   coalesce(sum(l.debit), 0) debit_total, coalesce(sum(l.credit), 0) credit_total, e.created_at
            from journal_entry e
            left join journal_entry_line l on l.journal_entry_id = e.id
            where @query = ''
               or e.document_number ilike '%' || @query || '%'
               or coalesce(e.memo, '') ilike '%' || @query || '%'
            group by e.id
            order by e.document_date desc, e.document_number desc
            limit @limit;
            """;
        await using var command = database.DataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("query", query?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 1000));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<JournalEntrySummaryDto>();
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetFieldValue<DateOnly>(2), reader.GetString(3), reader.GetString(4),
                reader.GetString(5), reader.GetDecimal(6), reader.GetDecimal(7), reader.GetFieldValue<DateTimeOffset>(8)));
        return result;
    }

    public async Task<JournalEntryDto?> GetJournalEntryAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        return await ReadJournalEntryAsync(connection, null, id, cancellationToken);
    }

    public async Task<JournalEntryDto> CreateJournalEntryAsync(CreateJournalEntryRequest request, CancellationToken cancellationToken = default)
    {
        AccountingRules.ValidateBalancedJournal(request.Lines.Select(x => new JournalLineRule(x.AccountCode, x.Debit, x.Credit)));
        var normalized = request.Lines.Select((line, index) => new NormalizedJournalLine(
            index + 1,
            NormalizeCode(line.AccountCode, "Kode perkiraan", 20),
            string.IsNullOrWhiteSpace(line.Description) ? null : line.Description.Trim(),
            Math.Round(line.Debit, 2),
            Math.Round(line.Credit, 2))).ToArray();
        var memo = string.IsNullOrWhiteSpace(request.Memo) ? "Jurnal Umum" : request.Memo.Trim();

        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            var accountIds = await ReadPostableAccountIdsAsync(connection, transaction, normalized.Select(x => x.AccountCode).Distinct(StringComparer.OrdinalIgnoreCase), cancellationToken);
            foreach (var line in normalized)
                if (!accountIds.ContainsKey(line.AccountCode))
                    throw new LedgerValidationException($"Kode perkiraan {line.AccountCode} tidak ditemukan atau tidak dapat diposting.");

            var number = await NextDocumentNumberAsync(connection, transaction, "JOURNAL", "JU", request.DocumentDate, cancellationToken);
            Guid id;
            const string headerSql = """
                insert into journal_entry(document_number, document_year, document_date, memo, source_module, created_by_name, computer_name)
                values (@number, @year, @date, @memo, 'GENERAL', @user, @computer)
                returning id;
                """;
            await using (var command = new NpgsqlCommand(headerSql, connection, transaction))
            {
                command.Parameters.AddWithValue("number", number);
                command.Parameters.AddWithValue("year", (short)request.DocumentDate.Year);
                command.Parameters.AddWithValue("date", request.DocumentDate);
                command.Parameters.AddWithValue("memo", memo);
                command.Parameters.AddWithValue("user", Db(request.UserName));
                command.Parameters.AddWithValue("computer", Db(request.ComputerName));
                id = (Guid)(await command.ExecuteScalarAsync(cancellationToken))!;
            }

            foreach (var line in normalized)
            {
                const string lineSql = """
                    insert into journal_entry_line(journal_entry_id, line_number, account_id, description, debit, credit)
                    values (@journal, @line, @account, @description, @debit, @credit);
                    """;
                await ExecuteAsync(connection, transaction, lineSql, cancellationToken,
                    ("journal", id), ("line", line.LineNumber), ("account", accountIds[line.AccountCode]),
                    ("description", Db(line.Description)), ("debit", line.Debit), ("credit", line.Credit));
            }

            await InsertAuditAsync(connection, transaction, "JOURNAL_ENTRY", id.ToString(), "CREATE", request.UserName, request.ComputerName, request, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return (await GetJournalEntryAsync(id, cancellationToken))!;
        }
        catch (PostgresException exception) when (exception.SqlState is PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.UniqueViolation)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new LedgerConflictException("Nomor jurnal berubah saat disimpan. Muat ulang lalu coba kembali.");
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<IReadOnlyList<LedgerTransactionMappingDto>> ListTransactionMappingsAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            select m.mapping_code, m.mapping_name, m.account_code, a.name, m.version
            from ledger_transaction_mapping m
            left join ledger_account a on a.code = m.account_code
            order by m.mapping_code;
            """;
        await using var command = database.DataSource.CreateCommand(sql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<LedgerTransactionMappingDto>();
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new(reader.GetString(0), reader.GetString(1), Text(reader, 2), Text(reader, 3), reader.GetInt64(4)));
        return result;
    }

    public async Task<LedgerTransactionMappingDto> SaveTransactionMappingAsync(SaveLedgerTransactionMappingRequest request, CancellationToken cancellationToken = default)
    {
        var mapping = NormalizeCode(request.MappingCode, "Kode mapping", 40);
        var account = string.IsNullOrWhiteSpace(request.AccountCode) ? null : NormalizeCode(request.AccountCode, "Kode perkiraan", 20);
        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            var existingVersion = await ReadVersionAsync(connection, transaction, "select version from ledger_transaction_mapping where mapping_code = @code for update;", cancellationToken, ("code", mapping));
            ValidateExpectedVersion(existingVersion, request.ExpectedVersion, $"Mapping {mapping}");
            if (account is not null && !await IsPostableAccountAsync(connection, transaction, account, cancellationToken))
                throw new LedgerValidationException($"Kode perkiraan {account} tidak ditemukan atau tidak dapat diposting.");

            const string sql = """
                insert into ledger_transaction_mapping(mapping_code, mapping_name, account_code, updated_at, version)
                values (@code, @name, @account, now(), 1)
                on conflict (mapping_code) do update set
                    account_code = excluded.account_code,
                    updated_at = now(),
                    version = ledger_transaction_mapping.version + 1
                returning mapping_code, mapping_name, account_code,
                          (select name from ledger_account where code = ledger_transaction_mapping.account_code),
                          version;
                """;
            LedgerTransactionMappingDto saved;
            await using (var command = new NpgsqlCommand(sql, connection, transaction))
            {
                command.Parameters.AddWithValue("code", mapping);
                command.Parameters.AddWithValue("name", mapping);
                command.Parameters.AddWithValue("account", Db(account));
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                await reader.ReadAsync(cancellationToken);
                saved = new(reader.GetString(0), reader.GetString(1), Text(reader, 2), Text(reader, 3), reader.GetInt64(4));
            }
            await InsertAuditAsync(connection, transaction, "LEDGER_MAPPING", mapping, "UPDATE", request.UserName, request.ComputerName, request, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return saved;
        }
        catch (PostgresException exception) when (exception.SqlState is PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.ForeignKeyViolation)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new LedgerConflictException($"Mapping {mapping} berubah saat disimpan. Muat ulang data.");
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<LedgerReportDto<LedgerAccountDto>> GetAccountReportAsync(string? query, CancellationToken cancellationToken = default)
    {
        var rows = await ListAccountsAsync(query, true, false, null, cancellationToken);
        return new(rows, new LedgerReportTotalsDto(0, 0, 0, rows.Count));
    }

    public async Task<LedgerReportDto<LedgerJournalReportRowDto>> GetJournalReportAsync(DateOnly from, DateOnly to, string? query, string? sourceFilter = null, CancellationToken cancellationToken = default)
    {
        const string sql = """
            select e.document_date, e.document_number, e.source_module, a.code, a.name, coalesce(l.description, e.memo), l.debit, l.credit
            from journal_entry e
            join journal_entry_line l on l.journal_entry_id = e.id
            join ledger_account a on a.id = l.account_id
            where e.status = 'POSTED'
              and e.document_date between @from and @to
              and (@source = ''
                   or (@source = 'OPENING' and e.source_module = 'CPS_JOURNAL')
                   or (@source = 'TRANSACTION' and e.source_module <> 'CPS_JOURNAL'))
              and (@query = '' or e.document_number ilike '%' || @query || '%' or coalesce(e.memo, '') ilike '%' || @query || '%'
                   or a.code ilike '%' || @query || '%' or a.name ilike '%' || @query || '%')
            order by e.document_date, e.document_number, l.line_number;
            """;
        await using var command = database.DataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("from", from);
        command.Parameters.AddWithValue("to", to);
        command.Parameters.AddWithValue("source", sourceFilter?.Trim().ToUpperInvariant() ?? string.Empty);
        command.Parameters.AddWithValue("query", query?.Trim() ?? string.Empty);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<LedgerJournalReportRowDto>();
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(new(reader.GetFieldValue<DateOnly>(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                Text(reader, 5), reader.GetDecimal(6), reader.GetDecimal(7)));
        return new(rows, Totals(rows.Sum(x => x.Debit), rows.Sum(x => x.Credit), 0, rows.Count));
    }

    public async Task<LedgerReportDto<GeneralLedgerReportRowDto>> GetGeneralLedgerReportAsync(DateOnly from, DateOnly to, string? accountCode, string? query, CancellationToken cancellationToken = default)
    {
        var openingBalances = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        const string openingSql = """
            select a.code, a.normal_balance, coalesce(sum(l.debit), 0), coalesce(sum(l.credit), 0)
            from ledger_account a
            join journal_entry_line l on l.account_id = a.id
            join journal_entry e on e.id = l.journal_entry_id
            where e.status = 'POSTED'
              and e.document_date < @from
              and (@account = '' or a.code = @account or a.code like @account || '%')
            group by a.code, a.normal_balance;
            """;
        await using (var openingCommand = database.DataSource.CreateCommand(openingSql))
        {
            openingCommand.Parameters.AddWithValue("from", from);
            openingCommand.Parameters.AddWithValue("account", accountCode?.Trim().ToUpperInvariant() ?? string.Empty);
            await using var openingReader = await openingCommand.ExecuteReaderAsync(cancellationToken);
            while (await openingReader.ReadAsync(cancellationToken))
            {
                var code = openingReader.GetString(0);
                var normal = openingReader.GetString(1);
                var debit = openingReader.GetDecimal(2);
                var credit = openingReader.GetDecimal(3);
                openingBalances[code] = normal == "D" ? debit - credit : credit - debit;
            }
        }

        const string sql = """
            select e.document_date, e.document_number, a.code, a.name, coalesce(l.description, e.memo), l.debit, l.credit, a.normal_balance
            from journal_entry e
            join journal_entry_line l on l.journal_entry_id = e.id
            join ledger_account a on a.id = l.account_id
            where e.status = 'POSTED'
              and e.document_date between @from and @to
              and (@account = '' or a.code = @account or a.code like @account || '%')
              and (@query = '' or e.document_number ilike '%' || @query || '%' or coalesce(e.memo, '') ilike '%' || @query || '%'
                   or a.code ilike '%' || @query || '%' or a.name ilike '%' || @query || '%')
            order by a.code, e.document_date, e.document_number, l.line_number;
            """;
        await using var command = database.DataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("from", from);
        command.Parameters.AddWithValue("to", to);
        command.Parameters.AddWithValue("account", accountCode?.Trim().ToUpperInvariant() ?? string.Empty);
        command.Parameters.AddWithValue("query", query?.Trim() ?? string.Empty);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<GeneralLedgerReportRowDto>();
        var balances = new Dictionary<string, decimal>(openingBalances, StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(cancellationToken))
        {
            var code = reader.GetString(2);
            var debit = reader.GetDecimal(5);
            var credit = reader.GetDecimal(6);
            var normal = reader.GetString(7);
            var delta = normal == "D" ? debit - credit : credit - debit;
            balances.TryGetValue(code, out var previous);
            var balance = previous + delta;
            balances[code] = balance;
            rows.Add(new(reader.GetFieldValue<DateOnly>(0), reader.GetString(1), code, reader.GetString(3), Text(reader, 4), debit, credit, balance));
        }
        return new(rows, Totals(rows.Sum(x => x.Debit), rows.Sum(x => x.Credit), rows.Sum(x => x.Balance), rows.Count));
    }

    public async Task<LedgerReportDto<TrialBalanceReportRowDto>> GetTrialBalanceReportAsync(DateOnly from, DateOnly to, string? query, bool includeZero = false, CancellationToken cancellationToken = default)
    {
        const string sql = """
            select a.code, a.name, coalesce(m.debit, 0), coalesce(m.credit, 0)
            from ledger_account a
            left join (
                select l.account_id, sum(l.debit) debit, sum(l.credit) credit
                from journal_entry_line l
                join journal_entry e on e.id = l.journal_entry_id
                where e.status = 'POSTED' and e.document_date <= @to
                group by l.account_id
            ) m on m.account_id = a.id
            where a.is_postable
              and (@query = '' or a.code ilike '%' || @query || '%' or a.name ilike '%' || @query || '%')
              and (@includeZero or coalesce(m.debit, 0) <> 0 or coalesce(m.credit, 0) <> 0)
            order by a.code;
            """;
        await using var command = database.DataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("to", to);
        command.Parameters.AddWithValue("query", query?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("includeZero", includeZero);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<TrialBalanceReportRowDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var code = reader.GetString(0);
            var name = reader.GetString(1);
            var debit = reader.GetDecimal(2);
            var credit = reader.GetDecimal(3);
            var net = debit - credit;
            rows.Add(new(code, name, Math.Max(0, net), Math.Max(0, -net)));
        }
        return new(rows, Totals(rows.Sum(x => x.Debit), rows.Sum(x => x.Credit), 0, rows.Count));
    }

    public async Task<LedgerReportDto<IncomeStatementReportRowDto>> GetIncomeStatementReportAsync(DateOnly from, DateOnly to, string? query, bool includeZero = false, CancellationToken cancellationToken = default)
    {
        const string sql = """
            select a.code, a.name, a.account_type, coalesce(m.debit, 0), coalesce(m.credit, 0)
            from ledger_account a
            left join (
                select l.account_id, sum(l.debit) debit, sum(l.credit) credit
                from journal_entry_line l
                join journal_entry e on e.id = l.journal_entry_id
                where e.status = 'POSTED' and e.document_date between @from and @to
                group by l.account_id
            ) m on m.account_id = a.id
            where a.account_type in ('REVENUE','EXPENSE','COGS')
              and a.is_postable and a.is_active
              and (@query = '' or a.code ilike '%' || @query || '%' or a.name ilike '%' || @query || '%')
              and (@includeZero or coalesce(m.debit, 0) <> 0 or coalesce(m.credit, 0) <> 0)
            order by a.code;
            """;
        await using var command = database.DataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("from", from);
        command.Parameters.AddWithValue("to", to);
        command.Parameters.AddWithValue("query", query?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("includeZero", includeZero);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<IncomeStatementReportRowDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var type = reader.GetString(2);
            var debit = reader.GetDecimal(3);
            var credit = reader.GetDecimal(4);
            var revenue = type == "REVENUE" ? credit - debit : 0;
            var expense = type is "EXPENSE" or "COGS" ? debit - credit : 0;
            rows.Add(new(reader.GetString(0), reader.GetString(1), type, revenue, expense, revenue - expense));
        }
        return new(rows, Totals(rows.Sum(x => x.Revenue), rows.Sum(x => x.Expense), rows.Sum(x => x.NetAmount), rows.Count));
    }

    public async Task<LedgerReportDto<BalanceSheetReportRowDto>> GetBalanceSheetReportAsync(DateOnly from, DateOnly to, string? query, bool includeZero = false, CancellationToken cancellationToken = default)
    {
        const string sql = """
            select a.code, a.name, a.account_type, a.normal_balance, coalesce(m.debit, 0), coalesce(m.credit, 0)
            from ledger_account a
            left join (
                select l.account_id, sum(l.debit) debit, sum(l.credit) credit
                from journal_entry_line l
                join journal_entry e on e.id = l.journal_entry_id
                where e.status = 'POSTED' and e.document_date <= @to
                group by l.account_id
            ) m on m.account_id = a.id
            where a.account_type in ('ASSET','LIABILITY','EQUITY')
              and (@query = '' or a.code ilike '%' || @query || '%' or a.name ilike '%' || @query || '%')
              and (@includeZero or coalesce(m.debit, 0) <> 0 or coalesce(m.credit, 0) <> 0)
            order by a.code;
            """;
        await using var command = database.DataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("to", to);
        command.Parameters.AddWithValue("query", query?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("includeZero", includeZero);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<BalanceSheetReportRowDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var normal = reader.GetString(3);
            var debit = reader.GetDecimal(4);
            var credit = reader.GetDecimal(5);
            var balance = normal == "D" ? debit - credit : credit - debit;
            rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), debit, credit, balance));
        }

        var currentProfit = await GetCurrentProfitAsync(to, cancellationToken);
        if (includeZero || currentProfit != 0)
            rows.Add(new("3.99.99.99", currentProfit >= 0 ? "LABA BERJALAN" : "RUGI BERJALAN", "EQUITY",
                currentProfit < 0 ? Math.Abs(currentProfit) : 0, currentProfit > 0 ? currentProfit : 0, currentProfit));

        var assetTotal = rows.Where(x => x.AccountType == "ASSET").Sum(x => x.Balance);
        var liabilityEquityTotal = rows.Where(x => x.AccountType is "LIABILITY" or "EQUITY").Sum(x => x.Balance);
        return new(rows, Totals(assetTotal, liabilityEquityTotal, assetTotal - liabilityEquityTotal, rows.Count));
    }

    private async Task<decimal> GetCurrentProfitAsync(DateOnly to, CancellationToken cancellationToken)
    {
        const string sql = """
            select coalesce(sum(case
                when a.account_type = 'REVENUE' then l.credit - l.debit
                when a.account_type in ('EXPENSE','COGS') then -(l.debit - l.credit)
                else 0 end), 0)
            from journal_entry e
            join journal_entry_line l on l.journal_entry_id = e.id
            join ledger_account a on a.id = l.account_id
            where e.status = 'POSTED'
              and e.document_date <= @to
              and a.account_type in ('REVENUE','EXPENSE','COGS');
            """;
        await using var command = database.DataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("to", to);
        return Math.Round((decimal)(await command.ExecuteScalarAsync(cancellationToken) ?? 0m), 2);
    }

    private async Task<JournalEntryDto?> ReadJournalEntryAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid id, CancellationToken cancellationToken)
    {
        const string headerSql = """
            select e.id, e.document_number, e.document_date, coalesce(e.memo, ''), e.source_module, e.status,
                   coalesce(sum(l.debit), 0) debit_total, coalesce(sum(l.credit), 0) credit_total, e.created_at
            from journal_entry e
            left join journal_entry_line l on l.journal_entry_id = e.id
            where e.id = @id
            group by e.id;
            """;
        JournalEntryDto? header;
        await using (var command = new NpgsqlCommand(headerSql, connection, transaction))
        {
            command.Parameters.AddWithValue("id", id);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return null;
            header = new(reader.GetGuid(0), reader.GetString(1), reader.GetFieldValue<DateOnly>(2), reader.GetString(3), reader.GetString(4),
                reader.GetString(5), reader.GetDecimal(6), reader.GetDecimal(7), reader.GetFieldValue<DateTimeOffset>(8), []);
        }

        const string lineSql = """
            select l.line_number, a.code, a.name, l.description, l.debit, l.credit
            from journal_entry_line l
            join ledger_account a on a.id = l.account_id
            where l.journal_entry_id = @id
            order by l.line_number;
            """;
        var lines = new List<JournalEntryLineDto>();
        await using (var command = new NpgsqlCommand(lineSql, connection, transaction))
        {
            command.Parameters.AddWithValue("id", id);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                lines.Add(new(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), Text(reader, 3), reader.GetDecimal(4), reader.GetDecimal(5)));
        }
        return header with { Lines = lines };
    }

    internal static async Task<string> NextDocumentNumberAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string type, string prefix, DateOnly date, CancellationToken cancellationToken)
    {
        var period = date.ToString("yyyyMM");
        const string sql = """
            insert into document_sequence(document_type, period, last_number)
            values (@type, @period, 1)
            on conflict (document_type, period)
            do update set last_number = document_sequence.last_number + 1
            returning last_number;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("type", type);
        command.Parameters.AddWithValue("period", period);
        var number = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
        var configuredPrefix = await ConfigurationRepository.GetDocumentPrefixAsync(connection, transaction, type, prefix, cancellationToken);
        return $"{configuredPrefix}-{period}-{number:0000}";
    }

    internal static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal static async Task InsertAuditAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string entityType, string entityId, string action, string? userName, string? computerName, object afterData, CancellationToken cancellationToken)
    {
        const string sql = """
            insert into audit_log(computer_name, module, action, entity_type, entity_id, after_data)
            values (@computer, 'LEDGER', @action, @entityType, @entityId, @afterData);
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("computer", Db(computerName));
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("entityType", entityType);
        command.Parameters.AddWithValue("entityId", entityId);
        command.Parameters.Add(new NpgsqlParameter("afterData", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(new { userName, data = afterData }) });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal static async Task<bool> IsPostableAccountAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string accountCode, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("select exists(select 1 from ledger_account where code = @code and is_active and is_postable);", connection, transaction);
        command.Parameters.AddWithValue("code", accountCode);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private async Task<Dictionary<string, Guid>> ReadPostableAccountIdsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, IEnumerable<string> accountCodes, CancellationToken cancellationToken)
    {
        var codes = accountCodes.ToArray();
        await using var command = new NpgsqlCommand("select code, id from ledger_account where code = any(@codes) and is_active and is_postable;", connection, transaction);
        command.Parameters.AddWithValue("codes", codes);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(cancellationToken)) result[reader.GetString(0)] = reader.GetGuid(1);
        return result;
    }

    private static LedgerAccountDto ReadAccount(NpgsqlDataReader reader) => new(
        reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
        Text(reader, 5), reader.GetBoolean(6), reader.GetBoolean(7), reader.GetBoolean(8), reader.GetInt64(9));

    internal static async Task<long?> ReadVersionAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null ? null : Convert.ToInt64(value);
    }

    internal static void ValidateExpectedVersion(long? actual, long? expected, string entity)
    {
        if (actual is null && expected is not null) throw new LedgerConflictException($"{entity} sudah dihapus atau belum tersedia.");
        if (actual is not null && expected is null) throw new LedgerConflictException($"{entity} sudah ada. Gunakan mode Ubah.");
        if (actual is not null && actual != expected) throw new LedgerConflictException($"{entity} telah diubah pengguna lain. Muat ulang data.");
    }

    internal static string NormalizeCode(string value, string label, int maxLength)
    {
        var code = Required(value, label, maxLength).ToUpperInvariant();
        if (code.Any(char.IsWhiteSpace)) throw new LedgerValidationException($"{label} tidak boleh mengandung spasi.");
        return code;
    }

    internal static string Required(string? value, string label, int maxLength)
    {
        var text = value?.Trim();
        if (string.IsNullOrWhiteSpace(text)) throw new LedgerValidationException($"{label} wajib diisi.");
        if (text.Length > maxLength) throw new LedgerValidationException($"{label} maksimal {maxLength} karakter.");
        return text;
    }

    internal static object Db(string? value) => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();
    internal static string? Text(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static LedgerReportTotalsDto Totals(decimal debit, decimal credit, decimal balance, int rows) => new(Math.Round(debit, 2), Math.Round(credit, 2), Math.Round(balance, 2), rows);

    private static string NormalizeType(string type)
    {
        var result = Required(type, "Jenis perkiraan", 20).ToUpperInvariant();
        if (result is not ("ASSET" or "LIABILITY" or "EQUITY" or "REVENUE" or "EXPENSE" or "COGS"))
            throw new LedgerValidationException("Jenis perkiraan tidak valid.");
        return result;
    }

    private static string NormalizeNormalBalance(string normalBalance, string accountType)
    {
        var result = Required(normalBalance, "Saldo normal", 1).ToUpperInvariant()[0].ToString();
        if (result is not ("D" or "C")) throw new LedgerValidationException("Saldo normal harus D atau C.");
        var expected = accountType is "ASSET" or "EXPENSE" or "COGS" ? "D" : "C";
        if (result != expected) throw new LedgerValidationException($"Saldo normal untuk {accountType} harus {expected}.");
        return result;
    }

    private static async Task<bool> ExistsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string table, string column, string value, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"select exists(select 1 from {table} where {column} = @value);", connection, transaction);
        command.Parameters.AddWithValue("value", value);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private sealed record NormalizedJournalLine(int LineNumber, string AccountCode, string? Description, decimal Debit, decimal Credit);
}
