using System.Data;
using System.Globalization;
using System.Text.Json;
using Aksara.Contracts;
using Aksara.Domain;
using Npgsql;

namespace Aksara.Infrastructure;

public sealed class PurchaseValidationException(string message) : InvalidOperationException(message);
public sealed class PurchaseConflictException(string message) : InvalidOperationException(message);

public sealed class PurchaseInventoryRepository(PostgresDatabase database)
{
    public async Task<IReadOnlyList<PurchaseSupplierDto>> SearchSuppliersAsync(string? query, int limit = 50, CancellationToken cancellationToken = default)
    {
        const string sql = """
            select id, code, company_name, address, credit_days
            from supplier
            where is_active
              and (@query = '' or code ilike '%' || @query || '%' or company_name ilike '%' || @query || '%')
            order by code
            limit @limit;
            """;
        await using var command = database.DataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("query", query?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 200));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<PurchaseSupplierDto>();
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetInt32(4)));
        return result;
    }

    public async Task<IReadOnlyList<PurchaseItemDto>> SearchItemsAsync(string? query, string? warehouseCode, int limit = 100, CancellationToken cancellationToken = default, string itemType = "GOODS")
    {
        const string sql = """
            select i.id, i.code, i.name, i.unit, i.purchase_price, @warehouse, coalesce(b.quantity, 0), i.item_type
            from inventory_item i
            left join inventory_balance b on b.item_id = i.id and b.warehouse_code = @warehouse
            where i.is_active
              and i.item_type = @itemType
              and (@query = '' or i.code ilike '%' || @query || '%' or i.name ilike '%' || @query || '%')
            order by i.code
            limit @limit;
            """;
        var warehouse = NormalizeWarehouse(warehouseCode);
        await using var command = database.DataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("query", query?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("warehouse", warehouse);
        command.Parameters.AddWithValue("itemType", NormalizeDocumentKind(itemType));
        command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 500));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<PurchaseItemDto>();
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetDecimal(4), reader.GetString(5), reader.GetDecimal(6), reader.GetString(7)));
        return result;
    }

    public async Task<IReadOnlyList<PurchaseDto>> ListPurchasesAsync(string? query, int limit = 100, CancellationToken cancellationToken = default, string documentKind = "GOODS")
    {
        const string sql = """
            select p.id
            from purchase p
            join supplier s on s.id = p.supplier_id
            where p.document_kind = @documentKind
              and (@query = '' or p.document_number ilike '%' || @query || '%'
                or coalesce(p.supplier_invoice_number, '') ilike '%' || @query || '%'
                or s.code ilike '%' || @query || '%' or s.company_name ilike '%' || @query || '%')
            order by p.document_date desc, p.created_at desc
            limit @limit;
            """;
        var ids = new List<Guid>();
        await using (var command = database.DataSource.CreateCommand(sql))
        {
            command.Parameters.AddWithValue("query", query?.Trim() ?? string.Empty);
            command.Parameters.AddWithValue("documentKind", NormalizeDocumentKind(documentKind));
            command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 500));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) ids.Add(reader.GetGuid(0));
        }
        var result = new List<PurchaseDto>(ids.Count);
        foreach (var id in ids)
        {
            var item = await GetPurchaseAsync(id, cancellationToken);
            if (item is not null) result.Add(item);
        }
        return result;
    }

    public async Task<int> CountPurchasesAsync(string? query, CancellationToken cancellationToken = default, string documentKind = "GOODS")
    {
        const string sql = """
            select count(*)
            from purchase p
            join supplier s on s.id = p.supplier_id
            where p.document_kind = @documentKind
              and (@query = '' or p.document_number ilike '%' || @query || '%'
                or coalesce(p.supplier_invoice_number, '') ilike '%' || @query || '%'
                or s.code ilike '%' || @query || '%' or s.company_name ilike '%' || @query || '%');
            """;

        await using var command = database.DataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("query", query?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("documentKind", NormalizeDocumentKind(documentKind));
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task<PurchaseDto?> GetPurchaseAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        return await ReadPurchaseAsync(connection, null, id, cancellationToken);
    }

    public async Task<string> PreviewNextDocumentNumberAsync(DateOnly documentDate, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        return await PreviewNextDocumentNumberAsync(connection, null, documentDate, cancellationToken);
    }

    public async Task<PurchaseDto> CreatePurchaseAsync(CreatePurchaseRequest request, CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        var type = NormalizeTransactionType(request.TransactionType);
        var documentKind = NormalizeDocumentKind(request.DocumentKind);
        var defaultWarehouse = NormalizeWarehouse(request.WarehouseCode);
        var requested = request.Lines.Select((line, index) => new RequestedLine(
            index + 1,
            line.ItemCode.Trim().ToUpperInvariant(),
            NormalizeWarehouse(line.WarehouseCode ?? defaultWarehouse),
            line.Quantity,
            line.UnitPrice,
            line.DiscountPercent,
            string.IsNullOrWhiteSpace(line.LineNote) ? null : line.LineNote.Trim())).ToArray();

        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            var supplier = await GetSupplierForUpdateAsync(connection, transaction, request.SupplierCode, cancellationToken)
                ?? throw new PurchaseValidationException($"Supplier {request.SupplierCode} tidak ditemukan atau tidak aktif.");
            var requestedNumber = NormalizeDocumentNumber(request.DocumentNumber);
            var number = requestedNumber ?? await NextDocumentNumberAsync(connection, transaction, request.DocumentDate, cancellationToken);
            if (requestedNumber is not null && await DocumentNumberExistsAsync(connection, transaction, number, request.DocumentDate.Year, cancellationToken))
                throw new PurchaseConflictException($"No.Fak. {number} sudah dipakai.");
            var purchaseId = Guid.NewGuid();
            var locked = new Dictionary<(string ItemCode, string WarehouseCode), LockedPurchaseItem>();
            foreach (var key in requested.Select(x => (x.ItemCode, x.WarehouseCode)).Distinct().OrderBy(x => x.ItemCode).ThenBy(x => x.WarehouseCode))
            {
                var item = await LockItemAsync(connection, transaction, key.ItemCode, key.WarehouseCode, documentKind, cancellationToken)
                    ?? throw new PurchaseValidationException($"{(documentKind == "SERVICE" ? "Jasa" : "Barang")} {key.ItemCode} tidak ditemukan atau tidak aktif.");
                locked[key] = item;
            }

            var purchase = new Purchase
            {
                Number = number,
                DocumentDate = request.DocumentDate,
                SupplierCode = supplier.Code,
                IsCredit = type == "CREDIT",
                DueDate = type == "CREDIT" ? request.DueDate : null,
                WarehouseCode = defaultWarehouse,
                DiscountPercent = request.DiscountPercent,
                DiscountAmount = request.DiscountAmount,
                TaxPercent = request.TaxPercent,
                DownPayment = type == "CREDIT" ? request.DownPayment : 0m,
                Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim(),
                CreatedBy = request.UserName
            };
            foreach (var line in requested)
            {
                var item = locked[(line.ItemCode, line.WarehouseCode)];
                purchase.AddLine(new PurchaseLine(item.Code, item.Name, line.Quantity, item.Unit, line.UnitPrice, line.DiscountPercent));
            }
            purchase.Post();

            await InsertPurchaseAsync(connection, transaction, purchaseId, supplier.Id, purchase, request.SupplierInvoiceNumber, type, documentKind, request.UserName, request.ComputerName, cancellationToken);
            await InsertLinesAsync(connection, transaction, purchaseId, requested, locked, cancellationToken);
            await InventoryLedgerPosting.PostPurchaseAsync(
                connection, transaction, purchaseId, number, request.DocumentDate, type,
                purchase.NetBeforeTax, purchase.TaxAmount, purchase.GrandTotal, purchase.DownPayment,
                purchase.BalanceDue, documentKind, request.UserName, request.ComputerName, cancellationToken);
            await UpdateDocumentSequenceAtLeastAsync(connection, transaction, request.DocumentDate, number, cancellationToken);

            foreach (var group in requested.Where(_ => documentKind == "GOODS").GroupBy(x => (x.ItemCode, x.WarehouseCode)).OrderBy(x => x.Key.ItemCode).ThenBy(x => x.Key.WarehouseCode))
            {
                var sourceItem = locked[group.Key];
                var item = await LockItemByIdAsync(connection, transaction, sourceItem.Id, group.Key.WarehouseCode, cancellationToken)
                    ?? throw new PurchaseValidationException($"Barang {group.Key.ItemCode} tidak ditemukan saat stok diperbarui.");
                var quantity = group.Sum(x => x.Quantity);
                var value = group.Sum(x => Math.Round(x.Quantity * x.UnitPrice * (1m - x.DiscountPercent / 100m), 2));
                var unitCost = quantity == 0 ? 0 : value / quantity;
                await ReceiveStockAsync(connection, transaction, purchaseId, number, request.DocumentDate, item, group.Key.WarehouseCode, quantity, unitCost, cancellationToken);
            }

            await InsertAuditAsync(connection, transaction, "CREATE", purchaseId, request.UserName, request.ComputerName, null, request, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return (await GetPurchaseAsync(purchaseId, cancellationToken))!;
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.SerializationFailure)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new PurchaseConflictException("Transaksi bersamaan terdeteksi. Silakan simpan kembali; tidak ada data setengah jadi yang tersimpan.");
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new PurchaseConflictException("No.Fak. sudah dipakai. Silakan ganti nomor faktur.");
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<PurchaseDto> CancelPurchaseAsync(Guid id, CancelPurchaseRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Reason)) throw new PurchaseValidationException("Alasan pembatalan wajib diisi.");
        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            const string headerSql = "select document_number, status, document_kind from purchase where id = @id for update;";
            string number;
            string status;
            string documentKind;
            await using (var command = new NpgsqlCommand(headerSql, connection, transaction))
            {
                command.Parameters.AddWithValue("id", id);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken)) throw new PurchaseValidationException("Faktur pembelian tidak ditemukan.");
                number = reader.GetString(0);
                status = reader.GetString(1);
                documentKind = reader.GetString(2);
            }
            if (status == "CANCELLED") throw new PurchaseConflictException("Faktur pembelian sudah dibatalkan oleh pengguna lain.");
            if (status != "POSTED") throw new PurchaseValidationException("Hanya pembelian tersimpan yang dapat dibatalkan.");

            const string usageSql = """
                select
                  coalesce((select sum(l.amount) from supplier_payment_line l join supplier_payment h on h.id=l.supplier_payment_id
                            where l.purchase_id=@id and l.source_type='PURCHASE' and h.status='POSTED'),0)
                  + coalesce((select sum(amount) from partner_balance_document
                              where target_source_type='PURCHASE' and target_source_id=@id and direction='DECREASE' and status='POSTED'),0);
                """;
            await using (var usageCommand = new NpgsqlCommand(usageSql, connection, transaction))
            {
                usageCommand.Parameters.AddWithValue("id", id);
                var usage = Convert.ToDecimal(await usageCommand.ExecuteScalarAsync(cancellationToken));
                if (usage > 0)
                    throw new PurchaseConflictException($"Faktur sudah dipakai dalam pembayaran/koreksi utang sebesar {usage:N2}. Batalkan dokumen turunannya terlebih dahulu.");
            }

            var lines = documentKind == "SERVICE"
                ? []
                : await ReadCancellationLinesAsync(connection, transaction, id, cancellationToken);
            var cancelOperationId = Guid.NewGuid();
            foreach (var line in lines.OrderBy(x => x.ItemCode).ThenBy(x => x.WarehouseCode))
            {
                var item = await LockItemByIdAsync(connection, transaction, line.ItemId, line.WarehouseCode, cancellationToken);
                if (item is null || item.WarehouseStock < line.Quantity)
                    throw new PurchaseValidationException($"Pembelian tidak dapat dibatalkan karena stok {line.ItemCode} tinggal {item?.WarehouseStock ?? 0:N3}, sedangkan {line.Quantity:N3} harus dikeluarkan. Gunakan Retur Pembelian sesuai jumlah stok yang tersedia.");
                await RemoveStockAsync(connection, transaction, cancelOperationId, number, "PURCHASE_CANCEL", item, line.Quantity, line.UnitCost, cancellationToken);
            }

            await InventoryLedgerPosting.CancelAsync(connection, transaction, id, "PURCHASE",
                request.UserName, request.Reason, cancellationToken);

            const string updateSql = """
                update purchase
                set status = 'CANCELLED', cancelled_at = now(), cancelled_by_name = @userName,
                    cancellation_reason = @reason, updated_at = now(), version = version + 1
                where id = @id;
                """;
            await using (var command = new NpgsqlCommand(updateSql, connection, transaction))
            {
                command.Parameters.AddWithValue("id", id);
                command.Parameters.AddWithValue("userName", request.UserName?.Trim() ?? string.Empty);
                command.Parameters.AddWithValue("reason", request.Reason.Trim());
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            await InsertAuditAsync(connection, transaction, "CANCEL", id, request.UserName, request.ComputerName, new { status = "POSTED" }, request, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return (await GetPurchaseAsync(id, cancellationToken))!;
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.SerializationFailure)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new PurchaseConflictException("Stok berubah saat pembatalan diproses. Silakan periksa data lalu ulangi.");
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<PurchaseDto> RestorePurchaseAsync(Guid id, RestorePurchaseRequest request, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            const string headerSql = """
                select document_number, document_date, status, transaction_type,
                       subtotal, discount_amount, tax_amount, grand_total, down_payment, balance_due, document_kind
                from purchase
                where id = @id
                for update;
                """;
            string number;
            DateOnly date;
            string status;
            string type;
            decimal subtotal;
            decimal discountAmount;
            decimal taxAmount;
            decimal grandTotal;
            decimal downPayment;
            decimal balanceDue;
            string documentKind;
            await using (var command = new NpgsqlCommand(headerSql, connection, transaction))
            {
                command.Parameters.AddWithValue("id", id);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken)) throw new PurchaseValidationException("Faktur pembelian tidak ditemukan.");
                number = reader.GetString(0);
                date = reader.GetFieldValue<DateOnly>(1);
                status = reader.GetString(2);
                type = reader.GetString(3);
                subtotal = reader.GetDecimal(4);
                discountAmount = reader.GetDecimal(5);
                taxAmount = reader.GetDecimal(6);
                grandTotal = reader.GetDecimal(7);
                downPayment = reader.GetDecimal(8);
                balanceDue = reader.GetDecimal(9);
                documentKind = reader.GetString(10);
            }

            if (status == "POSTED") throw new PurchaseConflictException("Faktur pembelian sudah aktif.");
            if (status != "CANCELLED") throw new PurchaseConflictException("Faktur pembelian belum dapat dibatalkan hapus.");

            var lines = documentKind == "SERVICE"
                ? []
                : await ReadCancellationLinesAsync(connection, transaction, id, cancellationToken);
            var restoreOperationId = Guid.NewGuid();
            foreach (var line in lines.OrderBy(x => x.ItemCode).ThenBy(x => x.WarehouseCode))
            {
                var item = await LockItemByIdAsync(connection, transaction, line.ItemId, line.WarehouseCode, cancellationToken)
                    ?? throw new PurchaseValidationException($"Barang {line.ItemCode} tidak ditemukan atau tidak aktif.");
                await AddStockAsync(connection, transaction, restoreOperationId, number, "PURCHASE_RESTORE", item, line.Quantity, line.UnitCost, cancellationToken);
            }

            await InventoryLedgerPosting.RestoreAsync(connection, transaction, id, "PURCHASE", cancellationToken);

            const string updateSql = """
                update purchase
                set status = 'POSTED', cancelled_at = null, cancelled_by_name = null,
                    cancellation_reason = null, updated_at = now(), version = version + 1
                where id = @id;
                """;
            await using (var command = new NpgsqlCommand(updateSql, connection, transaction))
            {
                command.Parameters.AddWithValue("id", id);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await InsertAuditAsync(connection, transaction, "RESTORE", id, request.UserName, request.ComputerName, new { status = "CANCELLED" }, new { status = "POSTED" }, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return (await GetPurchaseAsync(id, cancellationToken))!;
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.SerializationFailure)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new PurchaseConflictException("Stok berubah saat batal hapus diproses. Silakan periksa data lalu ulangi.");
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static void ValidateRequest(CreatePurchaseRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.DocumentNumber) && request.DocumentNumber.Trim().Length > 30)
            throw new PurchaseValidationException("No.Fak. maksimal 30 karakter.");
        if (string.IsNullOrWhiteSpace(request.SupplierCode)) throw new PurchaseValidationException("Supplier wajib dipilih.");
        if (request.Lines.Count == 0) throw new PurchaseValidationException("Minimal satu barang harus diisi.");
        if (request.DiscountPercent is < 0 or > 100 || request.DiscountAmount < 0) throw new PurchaseValidationException("Diskon faktur tidak valid.");
        if (request.TaxPercent is < 0 or > 100) throw new PurchaseValidationException("PPN tidak valid.");
        foreach (var line in request.Lines)
        {
            if (string.IsNullOrWhiteSpace(line.ItemCode)) throw new PurchaseValidationException("Kode barang wajib diisi.");
            if (line.Quantity <= 0) throw new PurchaseValidationException($"Jumlah {line.ItemCode} harus lebih dari nol.");
            if (line.UnitPrice < 0) throw new PurchaseValidationException($"Harga beli {line.ItemCode} tidak boleh negatif.");
            if (line.DiscountPercent is < 0 or > 100) throw new PurchaseValidationException($"Diskon {line.ItemCode} tidak valid.");
        }
    }

    private static string NormalizeTransactionType(string value) => value.Trim().ToUpperInvariant() switch
    {
        "CASH" or "T" or "TUNAI" => "CASH",
        "CREDIT" or "K" or "KREDIT" => "CREDIT",
        _ => throw new PurchaseValidationException("Jenis transaksi harus CASH/TUNAI atau CREDIT/KREDIT.")
    };

    private static string NormalizeDocumentKind(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "SERVICE" or "JASA" => "SERVICE",
        "GOODS" or "BARANG" or null or "" => "GOODS",
        _ => throw new PurchaseValidationException("Jenis dokumen harus GOODS/BARANG atau SERVICE/JASA.")
    };

    private static string NormalizeWarehouse(string? value) => string.IsNullOrWhiteSpace(value) ? "UTAMA" : value.Trim().ToUpperInvariant();
    private static string? NormalizeDocumentNumber(string? value)
    {
        var number = value?.Trim();
        if (string.IsNullOrWhiteSpace(number) || number.Equals("OTOMATIS", StringComparison.OrdinalIgnoreCase)) return null;
        return number.ToUpperInvariant();
    }

    private static async Task<SupplierLock?> GetSupplierForUpdateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string code, CancellationToken cancellationToken)
    {
        const string sql = "select id, code, company_name from supplier where code = @code and is_active for update;";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("code", code.Trim());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2)) : null;
    }

    private static async Task<string> NextDocumentNumberAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, DateOnly date, CancellationToken cancellationToken)
    {
        const string sql = """
            insert into document_sequence(document_type, period, last_number)
            values ('PURCHASE', @period, 1)
            on conflict (document_type, period)
            do update set last_number = document_sequence.last_number + 1
            returning last_number;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("period", date.ToString("yyyyMM", System.Globalization.CultureInfo.InvariantCulture));
        var sequence = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
        var configuredPrefix = await ConfigurationRepository.GetDocumentPrefixAsync(connection, transaction, "PURCHASE", "PBM", cancellationToken);
        return $"{configuredPrefix}{date:yyMM}{sequence:000}";
    }

    private static async Task<string> PreviewNextDocumentNumberAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, DateOnly date, CancellationToken cancellationToken)
    {
        const string sql = """
            select greatest(
                coalesce((select last_number from document_sequence where document_type = 'PURCHASE' and period = @period), 0),
                coalesce((
                    select max(nullif(regexp_replace(substring(document_number from char_length(@prefix) + 1), '\D', '', 'g'), '')::integer)
                    from purchase
                    where document_year = @year and document_number like @prefix || '%'
                ), 0)
            ) + 1;
            """;
        var configuredPrefix = await ConfigurationRepository.GetDocumentPrefixAsync(connection, transaction, "PURCHASE", "PBM", cancellationToken);
        var prefix = $"{configuredPrefix}{date:yyMM}";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("period", date.ToString("yyyyMM", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("year", (short)date.Year);
        command.Parameters.AddWithValue("prefix", prefix);
        var sequence = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
        return $"{prefix}{sequence:000}";
    }

    private static async Task<bool> DocumentNumberExistsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string documentNumber, int documentYear, CancellationToken cancellationToken)
    {
        const string sql = "select exists(select 1 from purchase where document_number = @number and document_year = @year);";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("number", documentNumber);
        command.Parameters.AddWithValue("year", (short)documentYear);
        return Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task UpdateDocumentSequenceAtLeastAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, DateOnly date, string documentNumber, CancellationToken cancellationToken)
    {
        var configuredPrefix = await ConfigurationRepository.GetDocumentPrefixAsync(connection, transaction, "PURCHASE", "PBM", cancellationToken);
        var prefix = $"{configuredPrefix}{date:yyMM}";
        if (!documentNumber.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return;
        if (!int.TryParse(documentNumber[prefix.Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sequence)) return;

        const string sql = """
            insert into document_sequence(document_type, period, last_number)
            values ('PURCHASE', @period, @lastNumber)
            on conflict (document_type, period)
            do update set last_number = greatest(document_sequence.last_number, excluded.last_number);
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("period", date.ToString("yyyyMM", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("lastNumber", sequence);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<LockedPurchaseItem?> LockItemAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string code, string warehouse, string itemType, CancellationToken cancellationToken)
    {
        const string findSql = "select id from inventory_item where code = @code and item_type = @itemType and is_active;";
        await using var find = new NpgsqlCommand(findSql, connection, transaction);
        find.Parameters.AddWithValue("code", code);
        find.Parameters.AddWithValue("itemType", itemType);
        var rawId = await find.ExecuteScalarAsync(cancellationToken);
        if (rawId is not Guid id) return null;
        if (itemType == "SERVICE")
        {
            const string serviceSql = "select id, code, name, unit, average_cost, purchase_price from inventory_item where id=@id and item_type='SERVICE' and is_active for update;";
            await using var service = new NpgsqlCommand(serviceSql, connection, transaction);
            service.Parameters.AddWithValue("id", id);
            await using var reader = await service.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken)
                ? new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetDecimal(4), reader.GetDecimal(5), 0, 0, warehouse)
                : null;
        }
        return await LockItemByIdAsync(connection, transaction, id, warehouse, cancellationToken);
    }

    internal static async Task<LockedPurchaseItem?> LockItemByIdAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, string warehouse, CancellationToken cancellationToken)
    {
        const string ensureSql = """
            insert into inventory_balance(item_id, warehouse_code, quantity)
            values (@id, @warehouse, 0)
            on conflict (item_id, warehouse_code) do nothing;
            """;
        await using (var ensure = new NpgsqlCommand(ensureSql, connection, transaction))
        {
            ensure.Parameters.AddWithValue("id", id);
            ensure.Parameters.AddWithValue("warehouse", warehouse);
            await ensure.ExecuteNonQueryAsync(cancellationToken);
        }
        const string lockSql = """
            select i.id, i.code, i.name, i.unit, i.average_cost, i.purchase_price, b.quantity,
                   (select coalesce(sum(all_b.quantity), 0) from inventory_balance all_b where all_b.item_id = i.id)
            from inventory_item i
            join inventory_balance b on b.item_id = i.id and b.warehouse_code = @warehouse
            where i.id = @id and i.is_active
            for update of i, b;
            """;
        await using var command = new NpgsqlCommand(lockSql, connection, transaction);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("warehouse", warehouse);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetDecimal(4), reader.GetDecimal(5), reader.GetDecimal(6), reader.GetDecimal(7), warehouse)
            : null;
    }

    private static async Task InsertPurchaseAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, Guid supplierId, Purchase purchase,
        string? supplierInvoice, string type, string documentKind, string? userName, string? computerName, CancellationToken cancellationToken)
    {
        const string sql = """
            insert into purchase(
                id, document_number, document_year, document_date, supplier_id, supplier_invoice_number,
                transaction_type, due_date, warehouse_code, subtotal, discount_percent, discount_amount,
                tax_percent, tax_amount, grand_total, down_payment, balance_due, notes, status, document_kind, created_by_name, computer_name)
            values (
                @id, @number, @year, @date, @supplierId, @supplierInvoice,
                @type, @dueDate, @warehouse, @subtotal, @discountPercent, @discountAmount,
                @taxPercent, @taxAmount, @grandTotal, @downPayment, @balanceDue, @notes, 'POSTED', @documentKind, @userName, @computerName);
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("number", purchase.Number);
        command.Parameters.AddWithValue("year", (short)purchase.DocumentDate.Year);
        command.Parameters.AddWithValue("date", purchase.DocumentDate);
        command.Parameters.AddWithValue("supplierId", supplierId);
        command.Parameters.AddWithValue("supplierInvoice", string.IsNullOrWhiteSpace(supplierInvoice) ? DBNull.Value : supplierInvoice.Trim());
        command.Parameters.AddWithValue("type", type);
        command.Parameters.AddWithValue("dueDate", (object?)purchase.DueDate ?? DBNull.Value);
        command.Parameters.AddWithValue("warehouse", purchase.WarehouseCode);
        command.Parameters.AddWithValue("subtotal", purchase.Subtotal);
        command.Parameters.AddWithValue("discountPercent", purchase.DiscountPercent);
        command.Parameters.AddWithValue("discountAmount", purchase.CalculatedDiscount);
        command.Parameters.AddWithValue("taxPercent", purchase.TaxPercent);
        command.Parameters.AddWithValue("taxAmount", purchase.TaxAmount);
        command.Parameters.AddWithValue("grandTotal", purchase.GrandTotal);
        command.Parameters.AddWithValue("downPayment", purchase.DownPayment);
        command.Parameters.AddWithValue("balanceDue", purchase.BalanceDue);
        command.Parameters.AddWithValue("notes", (object?)purchase.Notes ?? DBNull.Value);
        command.Parameters.AddWithValue("documentKind", documentKind);
        command.Parameters.AddWithValue("userName", string.IsNullOrWhiteSpace(userName) ? DBNull.Value : userName.Trim());
        command.Parameters.AddWithValue("computerName", string.IsNullOrWhiteSpace(computerName) ? DBNull.Value : computerName.Trim());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertLinesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid purchaseId,
        IReadOnlyList<RequestedLine> lines, IReadOnlyDictionary<(string ItemCode, string WarehouseCode), LockedPurchaseItem> locked, CancellationToken cancellationToken)
    {
        const string sql = """
            insert into purchase_line(purchase_id, line_number, item_id, warehouse_code, quantity, unit, unit_price, discount_1, line_total, notes)
            values (@purchaseId, @lineNumber, @itemId, @warehouse, @quantity, @unit, @unitPrice, @discount, @lineTotal, @notes);
            """;
        foreach (var line in lines)
        {
            var item = locked[(line.ItemCode, line.WarehouseCode)];
            var domainLine = new PurchaseLine(item.Code, item.Name, line.Quantity, item.Unit, line.UnitPrice, line.DiscountPercent);
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("purchaseId", purchaseId);
            command.Parameters.AddWithValue("lineNumber", line.LineNumber);
            command.Parameters.AddWithValue("itemId", item.Id);
            command.Parameters.AddWithValue("warehouse", line.WarehouseCode);
            command.Parameters.AddWithValue("quantity", line.Quantity);
            command.Parameters.AddWithValue("unit", item.Unit);
            command.Parameters.AddWithValue("unitPrice", line.UnitPrice);
            command.Parameters.AddWithValue("discount", line.DiscountPercent);
            command.Parameters.AddWithValue("lineTotal", domainLine.Total);
            command.Parameters.AddWithValue("notes", (object?)line.LineNote ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task ReceiveStockAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid purchaseId, string number, DateOnly documentDate,
        LockedPurchaseItem item, string warehouse, decimal quantity, decimal unitCost, CancellationToken cancellationToken)
    {
        const string sql = """
            update inventory_balance
            set quantity = quantity + @quantity, version = version + 1, updated_at = now()
            where item_id = @itemId and warehouse_code = @warehouse;

            update inventory_item
            set purchase_price = @unitCost, updated_at = now(), version = version + 1
            where id = @itemId;

            insert into inventory_movement(occurred_at, item_id, warehouse_code, source_type, source_id, source_number, quantity_in, quantity_out, unit_cost)
            values (@occurredAt, @itemId, @warehouse, 'PURCHASE', @purchaseId, @number, @quantity, 0, @unitCost);
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("quantity", quantity);
        command.Parameters.AddWithValue("itemId", item.Id);
        command.Parameters.AddWithValue("warehouse", warehouse);
        command.Parameters.AddWithValue("unitCost", unitCost);
        command.Parameters.AddWithValue("purchaseId", purchaseId);
        command.Parameters.AddWithValue("number", number);
        command.Parameters.AddWithValue("occurredAt", new DateTimeOffset(documentDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await FifoInventoryCosting.ReceiveAsync(connection, transaction, item.Id, warehouse, "PURCHASE", purchaseId, null, number,
            documentDate, quantity, unitCost, cancellationToken);
    }

    internal static async Task RemoveStockAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid sourceId, string number, string sourceType,
        LockedPurchaseItem item, decimal quantity, decimal unitCost, CancellationToken cancellationToken)
    {
        var fifo = await FifoInventoryCosting.IssueAsync(connection, transaction, item.Id, item.WarehouseCode,
            sourceType, sourceId, null, quantity, cancellationToken);
        const string sql = """
            update inventory_balance
            set quantity = quantity - @quantity, version = version + 1, updated_at = now()
            where item_id = @itemId and warehouse_code = @warehouse;

            insert into inventory_movement(occurred_at, item_id, warehouse_code, source_type, source_id, source_number, quantity_in, quantity_out, unit_cost)
            values (now(), @itemId, @warehouse, @sourceType, @sourceId, @number, 0, @quantity, @unitCost);
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("quantity", quantity);
        command.Parameters.AddWithValue("itemId", item.Id);
        command.Parameters.AddWithValue("warehouse", item.WarehouseCode);
        command.Parameters.AddWithValue("sourceType", sourceType);
        command.Parameters.AddWithValue("sourceId", sourceId);
        command.Parameters.AddWithValue("number", number);
        command.Parameters.AddWithValue("unitCost", fifo.UnitCost);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal static async Task AddStockAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid sourceId, string number, string sourceType,
        LockedPurchaseItem item, decimal quantity, decimal unitCost, CancellationToken cancellationToken)
    {
        const string sql = """
            update inventory_balance
            set quantity = quantity + @quantity, version = version + 1, updated_at = now()
            where item_id = @itemId and warehouse_code = @warehouse;

            insert into inventory_movement(occurred_at, item_id, warehouse_code, source_type, source_id, source_number, quantity_in, quantity_out, unit_cost)
            values (now(), @itemId, @warehouse, @sourceType, @sourceId, @number, @quantity, 0, @unitCost);
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("quantity", quantity);
        command.Parameters.AddWithValue("itemId", item.Id);
        command.Parameters.AddWithValue("warehouse", item.WarehouseCode);
        command.Parameters.AddWithValue("sourceType", sourceType);
        command.Parameters.AddWithValue("sourceId", sourceId);
        command.Parameters.AddWithValue("number", number);
        command.Parameters.AddWithValue("unitCost", unitCost);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await FifoInventoryCosting.ReceiveAsync(connection, transaction, item.Id, item.WarehouseCode, sourceType, sourceId, null, number,
            DateOnly.FromDateTime(DateTime.UtcNow), quantity, unitCost, cancellationToken);
    }

    private static async Task<IReadOnlyList<CancelStockLine>> ReadCancellationLinesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, CancellationToken cancellationToken)
    {
        const string sql = """
            select pl.item_id, i.code, pl.warehouse_code, sum(pl.quantity),
                   case when sum(pl.quantity) = 0 then 0 else sum(pl.line_total) / sum(pl.quantity) end
            from purchase_line pl
            join inventory_item i on i.id = pl.item_id
            where pl.purchase_id = @id
            group by pl.item_id, i.code, pl.warehouse_code
            order by i.code, pl.warehouse_code;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<CancelStockLine>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetDecimal(3), reader.GetDecimal(4)));
        return result;
    }

    private static async Task InsertAuditAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string action, Guid id,
        string? userName, string? computerName, object? before, object? after, CancellationToken cancellationToken)
    {
        const string sql = """
            insert into audit_log(computer_name, module, action, entity_type, entity_id, before_data, after_data)
            values (@computerName, 'PEMBELIAN', @action, 'purchase', @entityId, @before::jsonb, @after::jsonb);
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("computerName", string.IsNullOrWhiteSpace(computerName) ? DBNull.Value : computerName.Trim());
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("entityId", id.ToString());
        command.Parameters.AddWithValue("before", before is null ? DBNull.Value : JsonSerializer.Serialize(before));
        command.Parameters.AddWithValue("after", after is null ? DBNull.Value : JsonSerializer.Serialize(new { userName, data = after }));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<PurchaseDto?> ReadPurchaseAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid id, CancellationToken cancellationToken)
    {
        const string headerSql = """
            select p.id, p.document_number, p.document_date, s.code, s.company_name, s.address,
                   p.supplier_invoice_number, p.transaction_type, p.due_date, p.warehouse_code,
                   p.subtotal, p.discount_percent, p.discount_amount, p.tax_percent, p.tax_amount,
                   p.grand_total, p.down_payment, p.balance_due, p.status, p.notes, p.created_at, p.document_kind
            from purchase p join supplier s on s.id = p.supplier_id
            where p.id = @id;
            """;
        PurchaseHeader? header;
        await using (var command = new NpgsqlCommand(headerSql, connection, transaction))
        {
            command.Parameters.AddWithValue("id", id);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return null;
            header = new(reader.GetGuid(0), reader.GetString(1), reader.GetFieldValue<DateOnly>(2), reader.GetString(3), reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetFieldValue<DateOnly>(8), reader.GetString(9), reader.GetDecimal(10), reader.GetDecimal(11), reader.GetDecimal(12),
                reader.GetDecimal(13), reader.GetDecimal(14), reader.GetDecimal(15), reader.GetDecimal(16), reader.GetDecimal(17), reader.GetString(18),
                reader.IsDBNull(19) ? null : reader.GetString(19), reader.GetFieldValue<DateTimeOffset>(20), reader.GetString(21));
        }
        const string lineSql = """
            select pl.id, pl.line_number, i.code, i.name, pl.quantity, pl.unit, pl.unit_price,
                   pl.discount_1, pl.line_total, pl.warehouse_code, pl.notes
            from purchase_line pl join inventory_item i on i.id = pl.item_id
            where pl.purchase_id = @id order by pl.line_number;
            """;
        var lines = new List<PurchaseLineDto>();
        await using (var command = new NpgsqlCommand(lineSql, connection, transaction))
        {
            command.Parameters.AddWithValue("id", id);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                lines.Add(new(reader.GetGuid(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3), reader.GetDecimal(4), reader.GetString(5), reader.GetDecimal(6), reader.GetDecimal(7), reader.GetDecimal(8), reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetString(10)));
        }
        return new(header.Id, header.Number, header.Date, header.SupplierCode, header.SupplierName, header.SupplierAddress, header.SupplierInvoice,
            header.Type, header.DueDate, header.Warehouse, header.Subtotal, header.DiscountPercent, header.DiscountAmount, header.TaxPercent, header.TaxAmount,
            header.GrandTotal, header.DownPayment, header.BalanceDue, header.Status, header.Notes, header.CreatedAt, lines, header.DocumentKind);
    }

    internal sealed record LockedPurchaseItem(Guid Id, string Code, string Name, string Unit, decimal AverageCost, decimal PurchasePrice,
        decimal WarehouseStock, decimal TotalStock, string WarehouseCode);
    private sealed record SupplierLock(Guid Id, string Code, string Name);
    private sealed record RequestedLine(int LineNumber, string ItemCode, string WarehouseCode, decimal Quantity, decimal UnitPrice, decimal DiscountPercent, string? LineNote);
    private sealed record CancelStockLine(Guid ItemId, string ItemCode, string WarehouseCode, decimal Quantity, decimal UnitCost);
    private sealed record PurchaseHeader(Guid Id, string Number, DateOnly Date, string SupplierCode, string SupplierName, string? SupplierAddress,
        string? SupplierInvoice, string Type, DateOnly? DueDate, string Warehouse, decimal Subtotal, decimal DiscountPercent, decimal DiscountAmount,
        decimal TaxPercent, decimal TaxAmount, decimal GrandTotal, decimal DownPayment, decimal BalanceDue, string Status, string? Notes, DateTimeOffset CreatedAt,
        string DocumentKind);
}
