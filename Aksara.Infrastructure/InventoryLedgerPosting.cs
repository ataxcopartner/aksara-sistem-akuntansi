using System.Data;
using Npgsql;

namespace Aksara.Infrastructure;

internal static class InventoryLedgerPosting
{
    public static async Task PostSaleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid saleId,
        string documentNumber,
        DateOnly documentDate,
        string transactionType,
        string? cashAccountCode,
        decimal netBeforeTax,
        decimal taxAmount,
        decimal grandTotal,
        decimal downPayment,
        decimal balanceDue,
        decimal costTotal,
        string documentKind,
        string? userName,
        string? computerName,
        CancellationToken cancellationToken)
    {
        if (grandTotal <= 0) return;

        var journalId = Guid.NewGuid();
        var journalNumber = JournalNumber("JSALE-", documentNumber);
        var isService = documentKind.Equals("SERVICE", StringComparison.OrdinalIgnoreCase);
        await InsertHeaderAsync(connection, transaction, journalId, journalNumber, documentDate,
            $"Penjualan {(isService ? "jasa " : string.Empty)}{documentNumber}", "SALES", saleId, userName, computerName, cancellationToken);

        var lineNumber = 0;
        if (transactionType.Equals("CASH", StringComparison.OrdinalIgnoreCase))
        {
            var account = await ReadCashAccountAsync(connection, transaction, cashAccountCode, cancellationToken);
            await InsertLineAsync(connection, transaction, journalId, ++lineNumber, account,
                $"Kas penjualan {documentNumber}", grandTotal, 0, cancellationToken);
        }
        else
        {
            if (downPayment > 0)
            {
                var account = await ReadMappingAccountAsync(connection, transaction, "CASH_DEFAULT", cancellationToken);
                await InsertLineAsync(connection, transaction, journalId, ++lineNumber, account,
                    $"Uang muka penjualan {documentNumber}", downPayment, 0, cancellationToken);
            }

            if (balanceDue > 0 || downPayment <= 0)
            {
                var receivable = await ReadMappingAccountAsync(connection, transaction, "SALES_RECEIVABLE", cancellationToken);
                await InsertLineAsync(connection, transaction, journalId, ++lineNumber, receivable,
                    $"Piutang penjualan {documentNumber}", balanceDue > 0 ? balanceDue : grandTotal, 0, cancellationToken);
            }
        }

        var revenue = await ReadMappingAccountAsync(connection, transaction, isService ? "SERVICE_REVENUE" : "SALES_REVENUE", cancellationToken);
        await InsertLineAsync(connection, transaction, journalId, 3, revenue,
            $"Pendapatan penjualan {documentNumber}", 0, Math.Max(0, netBeforeTax), cancellationToken);

        if (taxAmount > 0)
        {
            var tax = await ReadMappingAccountAsync(connection, transaction, "SALES_TAX", cancellationToken);
            await InsertLineAsync(connection, transaction, journalId, 4, tax,
                $"PPN keluaran {documentNumber}", 0, taxAmount, cancellationToken);
        }

        if (costTotal > 0)
        {
            var cogs = await ReadMappingAccountAsync(connection, transaction, isService ? "SERVICE_COGS" : "SALES_COGS", cancellationToken);
            var inventory = await ReadMappingAccountAsync(connection, transaction, "INVENTORY", cancellationToken);
            await InsertLineAsync(connection, transaction, journalId, 5, cogs,
                $"Harga pokok penjualan {documentNumber}", costTotal, 0, cancellationToken);
            await InsertLineAsync(connection, transaction, journalId, 6, inventory,
                $"Pengurangan persediaan {documentNumber}", 0, costTotal, cancellationToken);
        }

        await EnsureBalancedAsync(connection, transaction, journalId, cancellationToken);
    }

    public static async Task PostSaleReturnAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid returnId,
        string documentNumber,
        DateOnly documentDate,
        string transactionType,
        string? cashAccountCode,
        decimal netBeforeTax,
        decimal taxAmount,
        decimal grandTotal,
        decimal costTotal,
        string? userName,
        string? computerName,
        CancellationToken cancellationToken)
    {
        if (grandTotal <= 0 && costTotal <= 0) return;

        var journalId = Guid.NewGuid();
        var journalNumber = JournalNumber("JSRET-", documentNumber);
        await InsertHeaderAsync(connection, transaction, journalId, journalNumber, documentDate,
            $"Retur penjualan {documentNumber}", "SALES_RETURN", returnId, userName, computerName, cancellationToken);

        var lineNumber = 0;
        if (netBeforeTax > 0)
        {
            var revenue = await ReadMappingAccountAsync(connection, transaction, "SALES_REVENUE", cancellationToken);
            await InsertLineAsync(connection, transaction, journalId, ++lineNumber, revenue,
                $"Pembalik pendapatan retur {documentNumber}", netBeforeTax, 0, cancellationToken);
        }

        if (taxAmount > 0)
        {
            var tax = await ReadMappingAccountAsync(connection, transaction, "SALES_TAX", cancellationToken);
            await InsertLineAsync(connection, transaction, journalId, ++lineNumber, tax,
                $"Pembalik PPN keluaran retur {documentNumber}", taxAmount, 0, cancellationToken);
        }

        if (grandTotal > 0)
        {
            var settlement = transactionType.Equals("CASH", StringComparison.OrdinalIgnoreCase)
                ? await ReadCashAccountAsync(connection, transaction, cashAccountCode, cancellationToken)
                : await ReadMappingAccountAsync(connection, transaction, "SALES_RECEIVABLE", cancellationToken);
            await InsertLineAsync(connection, transaction, journalId, ++lineNumber, settlement,
                $"Pengurang kas/piutang retur {documentNumber}", 0, grandTotal, cancellationToken);
        }

        if (costTotal > 0)
        {
            var inventory = await ReadMappingAccountAsync(connection, transaction, "INVENTORY", cancellationToken);
            var cogs = await ReadMappingAccountAsync(connection, transaction, "SALES_COGS", cancellationToken);
            await InsertLineAsync(connection, transaction, journalId, ++lineNumber, inventory,
                $"Persediaan masuk retur {documentNumber}", costTotal, 0, cancellationToken);
            await InsertLineAsync(connection, transaction, journalId, ++lineNumber, cogs,
                $"Pembalik HPP retur {documentNumber}", 0, costTotal, cancellationToken);
        }

        await EnsureBalancedAsync(connection, transaction, journalId, cancellationToken);
    }

    public static async Task PostPurchaseAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid purchaseId,
        string documentNumber,
        DateOnly documentDate,
        string transactionType,
        decimal netBeforeTax,
        decimal taxAmount,
        decimal grandTotal,
        decimal downPayment,
        decimal balanceDue,
        string documentKind,
        string? userName,
        string? computerName,
        CancellationToken cancellationToken)
    {
        if (grandTotal <= 0) return;

        var journalId = Guid.NewGuid();
        var journalNumber = JournalNumber("JPUR-", documentNumber);
        var isService = documentKind.Equals("SERVICE", StringComparison.OrdinalIgnoreCase);
        await InsertHeaderAsync(connection, transaction, journalId, journalNumber, documentDate,
            $"Pembelian {(isService ? "jasa " : string.Empty)}{documentNumber}", "PURCHASE", purchaseId, userName, computerName, cancellationToken);

        var inventory = await ReadMappingAccountAsync(connection, transaction, isService ? "SERVICE_PURCHASE_EXPENSE" : "INVENTORY", cancellationToken);
        var lineNumber = 0;
        await InsertLineAsync(connection, transaction, journalId, ++lineNumber, inventory,
            $"{(isService ? "Beban pembelian jasa" : "Persediaan pembelian")} {documentNumber}", Math.Max(0, netBeforeTax), 0, cancellationToken);

        if (taxAmount > 0)
        {
            var tax = await ReadMappingAccountAsync(connection, transaction, "PURCHASE_TAX", cancellationToken);
            await InsertLineAsync(connection, transaction, journalId, ++lineNumber, tax,
                $"PPN masukan {documentNumber}", taxAmount, 0, cancellationToken);
        }

        var settlement = await ReadMappingAccountAsync(connection, transaction,
            transactionType.Equals("CASH", StringComparison.OrdinalIgnoreCase) || downPayment > 0
                ? "CASH_DEFAULT"
                : "PURCHASE_PAYABLE", cancellationToken);
        await InsertLineAsync(connection, transaction, journalId, 3, settlement,
            $"Kas/hutang pembelian {documentNumber}", 0,
            transactionType.Equals("CASH", StringComparison.OrdinalIgnoreCase)
                ? grandTotal
                : downPayment > 0 ? downPayment : Math.Max(0, balanceDue), cancellationToken);

        if (transactionType.Equals("CREDIT", StringComparison.OrdinalIgnoreCase) && downPayment > 0 && balanceDue > 0)
        {
            var payable = await ReadMappingAccountAsync(connection, transaction, "PURCHASE_PAYABLE", cancellationToken);
            await InsertLineAsync(connection, transaction, journalId, 4, payable,
                $"Sisa hutang pembelian {documentNumber}", 0, balanceDue, cancellationToken);
        }

        await EnsureBalancedAsync(connection, transaction, journalId, cancellationToken);
    }

    public static async Task PostPurchaseReturnAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid returnId,
        string documentNumber,
        DateOnly documentDate,
        string transactionType,
        decimal netBeforeTax,
        decimal taxAmount,
        decimal grandTotal,
        string? userName,
        string? computerName,
        CancellationToken cancellationToken)
    {
        if (grandTotal <= 0) return;

        var journalId = Guid.NewGuid();
        var journalNumber = JournalNumber("JPRET-", documentNumber);
        await InsertHeaderAsync(connection, transaction, journalId, journalNumber, documentDate,
            $"Retur pembelian {documentNumber}", "PURCHASE_RETURN", returnId, userName, computerName, cancellationToken);

        var settlement = await ReadMappingAccountAsync(connection, transaction,
            transactionType.Equals("CASH", StringComparison.OrdinalIgnoreCase)
                ? "CASH_DEFAULT"
                : "PURCHASE_PAYABLE", cancellationToken);

        var lineNumber = 0;
        await InsertLineAsync(connection, transaction, journalId, ++lineNumber, settlement,
            $"Pengurang kas/hutang retur {documentNumber}", grandTotal, 0, cancellationToken);

        var inventory = await ReadMappingAccountAsync(connection, transaction, "INVENTORY", cancellationToken);
        await InsertLineAsync(connection, transaction, journalId, ++lineNumber, inventory,
            $"Persediaan keluar retur {documentNumber}", 0, Math.Max(0, netBeforeTax), cancellationToken);

        if (taxAmount > 0)
        {
            var tax = await ReadMappingAccountAsync(connection, transaction, "PURCHASE_TAX", cancellationToken);
            await InsertLineAsync(connection, transaction, journalId, ++lineNumber, tax,
                $"Pembalik PPN masukan retur {documentNumber}", 0, taxAmount, cancellationToken);
        }

        await EnsureBalancedAsync(connection, transaction, journalId, cancellationToken);
    }

    public static async Task CancelAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid sourceId,
        string sourceModule,
        string? userName,
        string? reason,
        CancellationToken cancellationToken)
    {
        const string sql = """
            update journal_entry
            set status = 'CANCELLED', cancelled_at = now(), cancelled_by_name = @user,
                cancellation_reason = @reason, version = version + 1
            where source_id = @sourceId and source_module = @module and status = 'POSTED';
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("sourceId", sourceId);
        command.Parameters.AddWithValue("module", sourceModule);
        command.Parameters.AddWithValue("user", Db(userName));
        command.Parameters.AddWithValue("reason", Db(reason));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public static async Task RestoreAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid sourceId,
        string sourceModule,
        CancellationToken cancellationToken)
    {
        const string sql = """
            update journal_entry
            set status = 'POSTED', cancelled_at = null, cancelled_by_name = null,
                cancellation_reason = null, version = version + 1
            where source_id = @sourceId and source_module = @module and status = 'CANCELLED';
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("sourceId", sourceId);
        command.Parameters.AddWithValue("module", sourceModule);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertHeaderAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid journalId,
        string journalNumber,
        DateOnly documentDate,
        string memo,
        string sourceModule,
        Guid sourceId,
        string? userName,
        string? computerName,
        CancellationToken cancellationToken)
    {
        const string sql = """
            insert into journal_entry(
                id, document_number, document_year, document_date, memo, source_module, source_id,
                status, created_by_name, computer_name)
            values (@id, @number, @year, @date, @memo, @module, @sourceId,
                    'POSTED', @user, @computer);
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("id", journalId);
        command.Parameters.AddWithValue("number", journalNumber);
        command.Parameters.AddWithValue("year", (short)documentDate.Year);
        command.Parameters.AddWithValue("date", documentDate);
        command.Parameters.AddWithValue("memo", memo);
        command.Parameters.AddWithValue("module", sourceModule);
        command.Parameters.AddWithValue("sourceId", sourceId);
        command.Parameters.AddWithValue("user", Db(userName));
        command.Parameters.AddWithValue("computer", Db(computerName));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertLineAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid journalId,
        int lineNumber,
        string accountCode,
        string description,
        decimal debit,
        decimal credit,
        CancellationToken cancellationToken)
    {
        debit = Math.Round(Math.Max(0, debit), 2);
        credit = Math.Round(Math.Max(0, credit), 2);
        if (debit == 0 && credit == 0) return;

        const string sql = """
            insert into journal_entry_line(journal_entry_id, line_number, account_id, description, debit, credit)
            select @journal, @line, id, @description, @debit, @credit
            from ledger_account
            where code = @account and is_postable and is_active;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("journal", journalId);
        command.Parameters.AddWithValue("line", lineNumber);
        command.Parameters.AddWithValue("account", accountCode);
        command.Parameters.AddWithValue("description", description);
        command.Parameters.AddWithValue("debit", debit);
        command.Parameters.AddWithValue("credit", credit);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException($"Akun {accountCode} tidak ditemukan atau tidak dapat diposting.");
    }

    private static async Task<string> ReadMappingAccountAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string mappingCode,
        CancellationToken cancellationToken)
    {
        const string sql = "select account_code from ledger_transaction_mapping where mapping_code = @mapping and account_code is not null;";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("mapping", mappingCode);
        var account = await command.ExecuteScalarAsync(cancellationToken) as string;
        return !string.IsNullOrWhiteSpace(account)
            ? account
            : throw new InvalidOperationException($"Mapping akun {mappingCode} belum dikonfigurasi.");
    }

    private static async Task<string> ReadCashAccountAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string? cashAccountCode,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(cashAccountCode))
            return await ReadMappingAccountAsync(connection, transaction, "CASH_DEFAULT", cancellationToken);

        const string sql = "select account_code from cash_account where code = @code and is_active;";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("code", cashAccountCode.Trim().ToUpperInvariant());
        var account = await command.ExecuteScalarAsync(cancellationToken) as string;
        return !string.IsNullOrWhiteSpace(account)
            ? account
            : throw new InvalidOperationException($"Kas {cashAccountCode} belum memiliki akun buku besar.");
    }

    private static async Task EnsureBalancedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid journalId,
        CancellationToken cancellationToken)
    {
        const string sql = "select count(*), coalesce(sum(debit), 0), coalesce(sum(credit), 0) from journal_entry_line where journal_entry_id = @journal;";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("journal", journalId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        var count = reader.GetInt64(0);
        var debit = reader.GetDecimal(1);
        var credit = reader.GetDecimal(2);
        if (count == 0 || debit != credit)
            throw new InvalidOperationException($"Jurnal {journalId} tidak seimbang: debit {debit:N2}, kredit {credit:N2}.");
    }

    private static string JournalNumber(string prefix, string documentNumber)
    {
        var value = $"{prefix}{documentNumber}".ToUpperInvariant();
        return value.Length <= 30 ? value : value[..30];
    }

    private static object Db(string? value) => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();
}
