using System.Data;
using System.Globalization;
using System.Text.Json;
using Aksara.Contracts;
using Npgsql;

namespace Aksara.Infrastructure;

public sealed class PurchaseReturnReportRepository(PostgresDatabase database)
{
    public async Task<PurchaseReturnSourceDto?> GetReturnSourceAsync(Guid purchaseId, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        return await ReadReturnSourceAsync(connection, null, purchaseId, cancellationToken);
    }

    public async Task<IReadOnlyList<PurchaseReturnDto>> ListReturnsAsync(string? query, int limit = 100, CancellationToken cancellationToken = default)
    {
        const string sql = """
            select pr.id
            from purchase_return pr
            join purchase p on p.id = pr.purchase_id
            join supplier s on s.id = pr.supplier_id
            where (@query = '' or pr.document_number ilike '%' || @query || '%'
                or p.document_number ilike '%' || @query || '%'
                or s.code ilike '%' || @query || '%' or s.company_name ilike '%' || @query || '%')
            order by pr.document_date desc, pr.created_at desc
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
        var result = new List<PurchaseReturnDto>();
        foreach (var id in ids)
        {
            var item = await GetReturnAsync(id, cancellationToken);
            if (item is not null) result.Add(item);
        }
        return result;
    }

    public async Task<PurchaseReturnDto?> GetReturnAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        return await ReadReturnAsync(connection, null, id, cancellationToken);
    }

    public async Task<string> PreviewNextReturnNumberAsync(DateOnly documentDate, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        return await PreviewNextReturnNumberAsync(connection, null, documentDate, cancellationToken);
    }

    public async Task<PurchaseReturnDto> CreateReturnAsync(CreatePurchaseReturnRequest request, CancellationToken cancellationToken = default)
    {
        ValidateReturnRequest(request);
        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            var purchase = await LockPurchaseAsync(connection, transaction, request.PurchaseId, cancellationToken)
                ?? throw new PurchaseValidationException("Faktur pembelian sumber tidak ditemukan.");
            if (purchase.Status != "POSTED") throw new PurchaseValidationException("Faktur sumber sudah dibatalkan atau tidak dapat diretur.");

            var lockedLines = new List<ReturnLineLock>();
            foreach (var requestLine in request.Lines.OrderBy(x => x.PurchaseLineId))
            {
                var line = await LockPurchaseLineAsync(connection, transaction, purchase.Id, requestLine.PurchaseLineId, cancellationToken)
                    ?? throw new PurchaseValidationException("Rincian pembelian sumber tidak ditemukan.");
                var returned = await GetPostedReturnedQuantityAsync(connection, transaction, line.PurchaseLineId, cancellationToken);
                if (requestLine.Quantity <= 0) throw new PurchaseValidationException($"Jumlah retur {line.ItemCode} harus lebih dari nol.");
                if (requestLine.Quantity > line.PurchasedQuantity - returned)
                    throw new PurchaseConflictException($"Jumlah retur {line.ItemCode} melebihi sisa yang dapat diretur ({line.PurchasedQuantity - returned:N3}). Data mungkin baru diubah komputer lain.");
                lockedLines.Add(line with { ReturnQuantity = requestLine.Quantity });
            }

            var stockGroups = AggregateStock(lockedLines).ToArray();
            var lockedItems = new Dictionary<(Guid ItemId, string Warehouse), PurchaseInventoryRepository.LockedPurchaseItem>();
            foreach (var stock in stockGroups.OrderBy(x => x.ItemId).ThenBy(x => x.Warehouse))
            {
                var item = await PurchaseInventoryRepository.LockItemByIdAsync(connection, transaction, stock.ItemId, stock.Warehouse, cancellationToken)
                    ?? throw new PurchaseValidationException($"Barang {stock.ItemCode} tidak aktif atau lokasi stok tidak tersedia.");
                if (item.WarehouseStock < stock.Quantity)
                    throw new PurchaseValidationException($"Retur {stock.ItemCode} tidak dapat diproses. Stok {stock.Warehouse} hanya {item.WarehouseStock:N3}, dibutuhkan {stock.Quantity:N3}.");
                lockedItems[(stock.ItemId, stock.Warehouse)] = item;
            }

            var subtotal = lockedLines.Sum(x => Math.Round(x.ReturnQuantity * x.UnitPrice * (1m - x.DiscountPercent / 100m), 2));
            var discount = Math.Min(subtotal, Math.Round(subtotal * request.DiscountPercent / 100m, 2) + request.DiscountAmount);
            var net = subtotal - discount;
            var tax = Math.Round(net * request.TaxPercent / 100m, 2);
            var grandTotal = net + tax;
            var returnId = Guid.NewGuid();
            var requestedNumber = NormalizeDocumentNumber(request.DocumentNumber);
            var number = requestedNumber ?? await NextReturnNumberAsync(connection, transaction, request.DocumentDate, cancellationToken);
            if (requestedNumber is not null && await DocumentNumberExistsAsync(connection, transaction, requestedNumber, request.DocumentDate.Year, cancellationToken))
                throw new PurchaseConflictException("No.Retur sudah dipakai. Silakan ganti nomor retur.");
            await UpdateDocumentSequenceAtLeastAsync(connection, transaction, request.DocumentDate, number, cancellationToken);

            const string headerSql = """
                insert into purchase_return(
                    id, document_number, document_year, document_date, purchase_id, supplier_id, transaction_type, warehouse_code,
                    subtotal, discount_percent, discount_amount, tax_percent, tax_amount, grand_total, notes, status, created_by_name, computer_name)
                values (@id, @number, @year, @date, @purchaseId, @supplierId, @type, @warehouse,
                    @subtotal, @discountPercent, @discountAmount, @taxPercent, @taxAmount, @grandTotal, @notes, 'POSTED', @userName, @computerName);
                """;
            await using (var command = new NpgsqlCommand(headerSql, connection, transaction))
            {
                command.Parameters.AddWithValue("id", returnId);
                command.Parameters.AddWithValue("number", number);
                command.Parameters.AddWithValue("year", (short)request.DocumentDate.Year);
                command.Parameters.AddWithValue("date", request.DocumentDate);
                command.Parameters.AddWithValue("purchaseId", purchase.Id);
                command.Parameters.AddWithValue("supplierId", purchase.SupplierId);
                command.Parameters.AddWithValue("type", purchase.Type);
                command.Parameters.AddWithValue("warehouse", purchase.Warehouse);
                command.Parameters.AddWithValue("subtotal", subtotal);
                command.Parameters.AddWithValue("discountPercent", request.DiscountPercent);
                command.Parameters.AddWithValue("discountAmount", discount);
                command.Parameters.AddWithValue("taxPercent", request.TaxPercent);
                command.Parameters.AddWithValue("taxAmount", tax);
                command.Parameters.AddWithValue("grandTotal", grandTotal);
                command.Parameters.AddWithValue("notes", string.IsNullOrWhiteSpace(request.Notes) ? DBNull.Value : request.Notes.Trim());
                command.Parameters.AddWithValue("userName", string.IsNullOrWhiteSpace(request.UserName) ? DBNull.Value : request.UserName.Trim());
                command.Parameters.AddWithValue("computerName", string.IsNullOrWhiteSpace(request.ComputerName) ? DBNull.Value : request.ComputerName.Trim());
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            const string lineSql = """
                insert into purchase_return_line(
                    purchase_return_id, purchase_line_id, line_number, item_id, warehouse_code, quantity, unit, unit_price, discount_percent, line_total)
                values (@returnId, @purchaseLineId, @lineNumber, @itemId, @warehouse, @quantity, @unit, @unitPrice, @discount, @lineTotal);
                """;
            for (var index = 0; index < lockedLines.Count; index++)
            {
                var line = lockedLines[index];
                await using var command = new NpgsqlCommand(lineSql, connection, transaction);
                command.Parameters.AddWithValue("returnId", returnId);
                command.Parameters.AddWithValue("purchaseLineId", line.PurchaseLineId);
                command.Parameters.AddWithValue("lineNumber", index + 1);
                command.Parameters.AddWithValue("itemId", line.ItemId);
                command.Parameters.AddWithValue("warehouse", line.Warehouse);
                command.Parameters.AddWithValue("quantity", line.ReturnQuantity);
                command.Parameters.AddWithValue("unit", line.Unit);
                command.Parameters.AddWithValue("unitPrice", line.UnitPrice);
                command.Parameters.AddWithValue("discount", line.DiscountPercent);
                command.Parameters.AddWithValue("lineTotal", Math.Round(line.ReturnQuantity * line.UnitPrice * (1m - line.DiscountPercent / 100m), 2));
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var stock in stockGroups)
            {
                var currentItem = await PurchaseInventoryRepository.LockItemByIdAsync(connection, transaction, stock.ItemId, stock.Warehouse, cancellationToken)
                    ?? throw new PurchaseValidationException($"Barang {stock.ItemCode} tidak ditemukan saat stok retur diperbarui.");
                await PurchaseInventoryRepository.RemoveStockAsync(connection, transaction, returnId, number, "PURCHASE_RETURN",
                    currentItem, stock.Quantity, stock.UnitCost, cancellationToken);
            }

            await InventoryLedgerPosting.PostPurchaseReturnAsync(
                connection, transaction, returnId, number, request.DocumentDate, purchase.Type,
                net, tax, grandTotal, request.UserName, request.ComputerName, cancellationToken);

            await InsertAuditAsync(connection, transaction, "CREATE", returnId, request.UserName, request.ComputerName, null, request, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return (await GetReturnAsync(returnId, cancellationToken))!;
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.SerializationFailure)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new PurchaseConflictException("Pembelian/retur berubah di komputer lain. Silakan muat ulang lalu simpan kembali.");
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new PurchaseConflictException("No.Retur sudah dipakai. Silakan ganti nomor retur.");
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<PurchaseReturnDto> CancelReturnAsync(Guid id, CancelPurchaseReturnRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Reason)) throw new PurchaseValidationException("Alasan pembatalan retur wajib diisi.");
        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            const string headerSql = "select document_number, status from purchase_return where id = @id for update;";
            string number;
            string status;
            await using (var command = new NpgsqlCommand(headerSql, connection, transaction))
            {
                command.Parameters.AddWithValue("id", id);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken)) throw new PurchaseValidationException("Retur pembelian tidak ditemukan.");
                number = reader.GetString(0);
                status = reader.GetString(1);
            }
            if (status == "CANCELLED") throw new PurchaseConflictException("Retur sudah dibatalkan oleh pengguna lain.");

            const string linesSql = """
                select prl.item_id, i.code, prl.warehouse_code, sum(prl.quantity),
                       case when sum(prl.quantity) = 0 then 0 else sum(prl.line_total) / sum(prl.quantity) end
                from purchase_return_line prl join inventory_item i on i.id = prl.item_id
                where prl.purchase_return_id = @id
                group by prl.item_id, i.code, prl.warehouse_code
                order by i.code, prl.warehouse_code;
                """;
            var stocks = new List<ReturnStock>();
            await using (var command = new NpgsqlCommand(linesSql, connection, transaction))
            {
                command.Parameters.AddWithValue("id", id);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken)) stocks.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetDecimal(3), reader.GetDecimal(4)));
            }
            foreach (var stock in stocks)
            {
                var item = await PurchaseInventoryRepository.LockItemByIdAsync(connection, transaction, stock.ItemId, stock.Warehouse, cancellationToken)
                    ?? throw new PurchaseValidationException($"Barang {stock.ItemCode} tidak ditemukan.");
                await PurchaseInventoryRepository.AddStockAsync(connection, transaction, id, number, "PURCHASE_RETURN_CANCEL", item, stock.Quantity, stock.UnitCost, cancellationToken);
            }

            await InventoryLedgerPosting.CancelAsync(connection, transaction, id, "PURCHASE_RETURN",
                request.UserName, request.Reason, cancellationToken);

            const string updateSql = """
                update purchase_return set status = 'CANCELLED', cancelled_at = now(), cancelled_by_name = @user,
                    cancellation_reason = @reason, version = version + 1 where id = @id;
                """;
            await using (var command = new NpgsqlCommand(updateSql, connection, transaction))
            {
                command.Parameters.AddWithValue("id", id);
                command.Parameters.AddWithValue("user", request.UserName?.Trim() ?? string.Empty);
                command.Parameters.AddWithValue("reason", request.Reason.Trim());
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            await InsertAuditAsync(connection, transaction, "CANCEL", id, request.UserName, request.ComputerName, new { status = "POSTED" }, request, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return (await GetReturnAsync(id, cancellationToken))!;
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.SerializationFailure)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new PurchaseConflictException("Stok berubah saat pembatalan retur. Silakan periksa lalu ulangi.");
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<PurchaseReturnDto> RestoreReturnAsync(Guid id, RestorePurchaseReturnRequest request, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            const string headerSql = """
                select pr.document_number, pr.status, pr.purchase_id, p.status
                from purchase_return pr
                join purchase p on p.id = pr.purchase_id
                where pr.id = @id
                for update of pr, p;
                """;
            string number;
            string status;
            Guid purchaseId;
            string purchaseStatus;
            await using (var command = new NpgsqlCommand(headerSql, connection, transaction))
            {
                command.Parameters.AddWithValue("id", id);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken)) throw new PurchaseValidationException("Retur pembelian tidak ditemukan.");
                number = reader.GetString(0);
                status = reader.GetString(1);
                purchaseId = reader.GetGuid(2);
                purchaseStatus = reader.GetString(3);
            }

            if (!string.Equals(status, "CANCELLED", StringComparison.OrdinalIgnoreCase))
                throw new PurchaseConflictException("Retur pembelian belum ditandai hapus.");
            if (!string.Equals(purchaseStatus, "POSTED", StringComparison.OrdinalIgnoreCase))
                throw new PurchaseConflictException("Faktur pembelian sumber sudah ditandai hapus, retur tidak dapat dipulihkan.");

            const string linesSql = """
                select prl.purchase_line_id, prl.item_id, i.code, prl.warehouse_code, pl.quantity, prl.unit,
                       prl.unit_price, prl.discount_percent, prl.quantity
                from purchase_return_line prl
                join purchase_line pl on pl.id = prl.purchase_line_id and pl.purchase_id = @purchaseId
                join inventory_item i on i.id = prl.item_id
                where prl.purchase_return_id = @id
                order by prl.line_number;
                """;
            var lines = new List<ReturnLineLock>();
            await using (var command = new NpgsqlCommand(linesSql, connection, transaction))
            {
                command.Parameters.AddWithValue("id", id);
                command.Parameters.AddWithValue("purchaseId", purchaseId);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    lines.Add(new ReturnLineLock(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3),
                        reader.GetDecimal(4), reader.GetString(5), reader.GetDecimal(6), reader.GetDecimal(7), reader.GetDecimal(8)));
                }
            }

            if (lines.Count == 0) throw new PurchaseConflictException("Rincian retur pembelian tidak ditemukan.");

            foreach (var line in lines)
            {
                var returnedByOthers = await GetPostedReturnedQuantityAsync(connection, transaction, line.PurchaseLineId, cancellationToken);
                var remaining = line.PurchasedQuantity - returnedByOthers;
                if (line.ReturnQuantity > remaining)
                    throw new PurchaseConflictException($"Batal hapus retur {line.ItemCode} melebihi sisa yang dapat diretur. Tersisa {remaining:N3}.");
            }

            foreach (var stock in AggregateStock(lines))
            {
                var item = await PurchaseInventoryRepository.LockItemByIdAsync(connection, transaction, stock.ItemId, stock.Warehouse, cancellationToken)
                    ?? throw new PurchaseValidationException($"Barang {stock.ItemCode} tidak ditemukan.");
                if (item.WarehouseStock < stock.Quantity)
                    throw new PurchaseConflictException($"Batal hapus retur {stock.ItemCode} tidak dapat dilakukan. Stok {stock.Warehouse} tersedia {item.WarehouseStock:N3}, dibutuhkan {stock.Quantity:N3}.");
                await PurchaseInventoryRepository.RemoveStockAsync(connection, transaction, id, number, "PURCHASE_RETURN_RESTORE", item, stock.Quantity, stock.UnitCost, cancellationToken);
            }

            await InventoryLedgerPosting.RestoreAsync(connection, transaction, id, "PURCHASE_RETURN", cancellationToken);

            const string restoreSql = """
                update purchase_return
                set status = 'POSTED', cancelled_at = null, cancelled_by_name = null,
                    cancellation_reason = null, version = version + 1
                where id = @id;
                """;
            await using (var command = new NpgsqlCommand(restoreSql, connection, transaction))
            {
                command.Parameters.AddWithValue("id", id);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await InsertAuditAsync(connection, transaction, "RESTORE", id, request.UserName, request.ComputerName, new { status = "CANCELLED" }, new { status = "POSTED" }, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return (await GetReturnAsync(id, cancellationToken))!;
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.SerializationFailure)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new PurchaseConflictException("Stok berubah saat batal hapus retur. Silakan periksa lalu ulangi.");
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<PurchaseDetailReportDto> GetDetailReportAsync(DateOnly from, DateOnly to, string? query, CancellationToken cancellationToken = default)
    {
        ValidateRange(from, to);
        const string sql = """
            select p.document_date, p.document_number, s.code, s.company_name, p.transaction_type, pl.warehouse_code,
                   i.code, i.name, pl.unit, pl.quantity,
                   coalesce((select sum(prl.quantity) from purchase_return_line prl join purchase_return pr on pr.id = prl.purchase_return_id
                       where prl.purchase_line_id = pl.id and pr.status = 'POSTED'), 0) returned_quantity,
                   pl.unit_price, pl.discount_1, pl.line_total,
                   coalesce((select sum(prl.line_total) from purchase_return_line prl join purchase_return pr on pr.id = prl.purchase_return_id
                       where prl.purchase_line_id = pl.id and pr.status = 'POSTED'), 0) return_amount
            from purchase p
            join supplier s on s.id = p.supplier_id
            join purchase_line pl on pl.purchase_id = p.id
            join inventory_item i on i.id = pl.item_id
            where p.status = 'POSTED' and p.document_date between @from and @to
              and (@query = '' or p.document_number ilike '%' || @query || '%' or s.code ilike '%' || @query || '%'
                   or s.company_name ilike '%' || @query || '%' or i.code ilike '%' || @query || '%' or i.name ilike '%' || @query || '%')
            order by p.document_date, p.document_number, pl.line_number;
            """;
        await using var command = database.DataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("from", from);
        command.Parameters.AddWithValue("to", to);
        command.Parameters.AddWithValue("query", query?.Trim() ?? string.Empty);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<PurchaseDetailReportRowDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var purchased = reader.GetDecimal(9);
            var returned = reader.GetDecimal(10);
            var gross = reader.GetDecimal(13);
            var returnAmount = reader.GetDecimal(14);
            rows.Add(new(reader.GetFieldValue<DateOnly>(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5),
                reader.GetString(6), reader.GetString(7), reader.GetString(8), purchased, returned, purchased - returned, reader.GetDecimal(11), reader.GetDecimal(12), gross, returnAmount, gross - returnAmount));
        }
        return new(rows, Totals(rows.Select(x => (x.GrossPurchase, x.PurchaseReturn, x.NetPurchase))));
    }

    public async Task<PurchaseHeaderReportDto> GetHeaderReportAsync(DateOnly from, DateOnly to, string? query, CancellationToken cancellationToken = default)
    {
        ValidateRange(from, to);
        const string sql = """
            select p.document_date, p.document_number, s.code, s.company_name, p.supplier_invoice_number,
                   p.transaction_type, p.due_date, p.warehouse_code, p.grand_total,
                   coalesce((select sum(pr.grand_total) from purchase_return pr where pr.purchase_id = p.id and pr.status = 'POSTED'), 0)
            from purchase p join supplier s on s.id = p.supplier_id
            where p.status = 'POSTED' and p.document_date between @from and @to
              and (@query = '' or p.document_number ilike '%' || @query || '%' or coalesce(p.supplier_invoice_number, '') ilike '%' || @query || '%'
                   or s.code ilike '%' || @query || '%' or s.company_name ilike '%' || @query || '%')
            order by p.document_date, p.document_number;
            """;
        await using var command = database.DataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("from", from);
        command.Parameters.AddWithValue("to", to);
        command.Parameters.AddWithValue("query", query?.Trim() ?? string.Empty);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<PurchaseHeaderReportRowDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var gross = reader.GetDecimal(8);
            var returned = reader.GetDecimal(9);
            rows.Add(new(reader.GetFieldValue<DateOnly>(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetFieldValue<DateOnly>(6), reader.GetString(7), gross, returned, gross - returned));
        }
        return new(rows, Totals(rows.Select(x => (x.GrossPurchase, x.PurchaseReturn, x.NetPurchase))));
    }

    public async Task<PurchaseReturnDetailReportDto> GetReturnDetailReportAsync(DateOnly from, DateOnly to, string? query, CancellationToken cancellationToken = default)
    {
        ValidateRange(from, to);
        const string sql = """
            select pr.document_date, pr.document_number, p.document_number, s.code, s.company_name, pr.transaction_type, prl.warehouse_code,
                   i.code, i.name, prl.unit, prl.quantity, prl.unit_price, prl.discount_percent, prl.line_total
            from purchase_return pr
            join purchase p on p.id = pr.purchase_id
            join supplier s on s.id = pr.supplier_id
            join purchase_return_line prl on prl.purchase_return_id = pr.id
            join inventory_item i on i.id = prl.item_id
            where pr.status = 'POSTED' and pr.document_date between @from and @to
              and (@query = '' or pr.document_number ilike '%' || @query || '%' or p.document_number ilike '%' || @query || '%'
                   or s.code ilike '%' || @query || '%' or s.company_name ilike '%' || @query || '%'
                   or i.code ilike '%' || @query || '%' or i.name ilike '%' || @query || '%')
            order by pr.document_date, pr.document_number, prl.line_number;
            """;
        await using var command = database.DataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("from", from);
        command.Parameters.AddWithValue("to", to);
        command.Parameters.AddWithValue("query", query?.Trim() ?? string.Empty);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<PurchaseReturnDetailReportRowDto>();
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(new(reader.GetFieldValue<DateOnly>(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6),
                reader.GetString(7), reader.GetString(8), reader.GetString(9), reader.GetDecimal(10), reader.GetDecimal(11), reader.GetDecimal(12), reader.GetDecimal(13)));
        return new(rows, new(rows.Sum(x => x.ReturnAmount)));
    }

    public async Task<PurchaseReturnHeaderReportDto> GetReturnHeaderReportAsync(DateOnly from, DateOnly to, string? query, CancellationToken cancellationToken = default)
    {
        ValidateRange(from, to);
        const string sql = """
            select pr.document_date, pr.document_number, p.document_number, s.code, s.company_name, pr.transaction_type, pr.warehouse_code,
                   pr.subtotal, pr.discount_amount, pr.tax_amount, pr.grand_total, pr.notes
            from purchase_return pr
            join purchase p on p.id = pr.purchase_id
            join supplier s on s.id = pr.supplier_id
            where pr.status = 'POSTED' and pr.document_date between @from and @to
              and (@query = '' or pr.document_number ilike '%' || @query || '%' or p.document_number ilike '%' || @query || '%'
                   or s.code ilike '%' || @query || '%' or s.company_name ilike '%' || @query || '%')
            order by pr.document_date, pr.document_number;
            """;
        await using var command = database.DataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("from", from);
        command.Parameters.AddWithValue("to", to);
        command.Parameters.AddWithValue("query", query?.Trim() ?? string.Empty);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<PurchaseReturnHeaderReportRowDto>();
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(new(reader.GetFieldValue<DateOnly>(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6),
                reader.GetDecimal(7), reader.GetDecimal(8), reader.GetDecimal(9), reader.GetDecimal(10), reader.IsDBNull(11) ? null : reader.GetString(11)));
        return new(rows, new(rows.Sum(x => x.ReturnAmount)));
    }

    private static async Task<PurchaseReturnSourceDto?> ReadReturnSourceAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid purchaseId, CancellationToken cancellationToken)
    {
        const string headerSql = """
            select p.id, p.document_number, p.document_date, s.code, s.company_name, s.address, p.transaction_type, p.warehouse_code
            from purchase p join supplier s on s.id = p.supplier_id
            where p.id = @id and p.status = 'POSTED';
            """;
        SourceHeader? header;
        await using (var command = new NpgsqlCommand(headerSql, connection, transaction))
        {
            command.Parameters.AddWithValue("id", purchaseId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return null;
            header = new(reader.GetGuid(0), reader.GetString(1), reader.GetFieldValue<DateOnly>(2), reader.GetString(3), reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetString(6), reader.GetString(7));
        }
        const string linesSql = """
            select pl.id, pl.line_number, i.code, i.name, pl.warehouse_code, pl.quantity,
                   coalesce((select sum(prl.quantity) from purchase_return_line prl join purchase_return pr on pr.id = prl.purchase_return_id
                       where prl.purchase_line_id = pl.id and pr.status = 'POSTED'), 0),
                   pl.unit, pl.unit_price, pl.discount_1
            from purchase_line pl join inventory_item i on i.id = pl.item_id
            where pl.purchase_id = @id order by pl.line_number;
            """;
        var lines = new List<PurchaseReturnSourceLineDto>();
        await using (var command = new NpgsqlCommand(linesSql, connection, transaction))
        {
            command.Parameters.AddWithValue("id", purchaseId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var purchased = reader.GetDecimal(5);
                var returned = reader.GetDecimal(6);
                lines.Add(new(reader.GetGuid(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), purchased, returned,
                    Math.Max(0, purchased - returned), reader.GetString(7), reader.GetDecimal(8), reader.GetDecimal(9)));
            }
        }
        return new(header.Id, header.Number, header.Date, header.SupplierCode, header.SupplierName, header.SupplierAddress, header.Type, header.Warehouse, lines);
    }

    private static async Task<PurchaseReturnDto?> ReadReturnAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid id, CancellationToken cancellationToken)
    {
        const string headerSql = """
            select pr.id, pr.document_number, pr.document_date, pr.purchase_id, p.document_number, s.code, s.company_name, s.address,
                   pr.transaction_type, pr.warehouse_code, pr.subtotal, pr.discount_amount, pr.tax_amount, pr.grand_total,
                   pr.status, pr.notes, pr.created_at
            from purchase_return pr join purchase p on p.id = pr.purchase_id join supplier s on s.id = pr.supplier_id
            where pr.id = @id;
            """;
        ReturnHeader? header;
        await using (var command = new NpgsqlCommand(headerSql, connection, transaction))
        {
            command.Parameters.AddWithValue("id", id);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return null;
            header = new(reader.GetGuid(0), reader.GetString(1), reader.GetFieldValue<DateOnly>(2), reader.GetGuid(3), reader.GetString(4), reader.GetString(5), reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetString(8), reader.GetString(9), reader.GetDecimal(10), reader.GetDecimal(11), reader.GetDecimal(12),
                reader.GetDecimal(13), reader.GetString(14), reader.IsDBNull(15) ? null : reader.GetString(15), reader.GetFieldValue<DateTimeOffset>(16));
        }
        const string linesSql = """
            select prl.purchase_line_id, prl.line_number, p.document_number, i.code, i.name, prl.warehouse_code,
                   prl.quantity, prl.unit, prl.unit_price, prl.discount_percent, prl.line_total
            from purchase_return_line prl
            join inventory_item i on i.id = prl.item_id
            join purchase_return pr on pr.id = prl.purchase_return_id
            join purchase p on p.id = pr.purchase_id
            where prl.purchase_return_id = @id order by prl.line_number;
            """;
        var lines = new List<PurchaseReturnLineDto>();
        await using (var command = new NpgsqlCommand(linesSql, connection, transaction))
        {
            command.Parameters.AddWithValue("id", id);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                lines.Add(new(reader.GetGuid(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetDecimal(6),
                    reader.GetString(7), reader.GetDecimal(8), reader.GetDecimal(9), reader.GetDecimal(10)));
        }
        return new(header.Id, header.Number, header.Date, header.PurchaseId, header.SourceNumber, header.SupplierCode, header.SupplierName, header.SupplierAddress,
            header.Type, header.Warehouse, header.Subtotal, header.DiscountAmount, header.TaxAmount, header.GrandTotal, header.Status, header.Notes, header.CreatedAt, lines);
    }

    private static void ValidateReturnRequest(CreatePurchaseReturnRequest request)
    {
        if (request.PurchaseId == Guid.Empty) throw new PurchaseValidationException("Faktur pembelian sumber wajib dipilih.");
        if (request.Lines.Count == 0) throw new PurchaseValidationException("Minimal satu barang harus diretur.");
        if (request.DiscountPercent is < 0 or > 100 || request.DiscountAmount < 0) throw new PurchaseValidationException("Diskon retur tidak valid.");
        if (request.TaxPercent is < 0 or > 100) throw new PurchaseValidationException("PPN retur tidak valid.");
        var documentNumber = NormalizeDocumentNumber(request.DocumentNumber);
        if (documentNumber?.Length > 30) throw new PurchaseValidationException("No.Retur maksimal 30 karakter.");
    }

    private static void ValidateRange(DateOnly from, DateOnly to)
    {
        if (to < from) throw new PurchaseValidationException("Tanggal akhir tidak boleh lebih awal dari tanggal awal.");
        if (to.DayNumber - from.DayNumber > 3660) throw new PurchaseValidationException("Rentang laporan terlalu panjang.");
    }

    private static async Task<PurchaseLock?> LockPurchaseAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, CancellationToken cancellationToken)
    {
        const string sql = "select id, document_number, supplier_id, transaction_type, warehouse_code, status from purchase where id = @id for update;";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? new(reader.GetGuid(0), reader.GetString(1), reader.GetGuid(2), reader.GetString(3), reader.GetString(4), reader.GetString(5)) : null;
    }

    private static async Task<ReturnLineLock?> LockPurchaseLineAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid purchaseId, Guid lineId, CancellationToken cancellationToken)
    {
        const string sql = """
            select pl.id, pl.item_id, i.code, pl.warehouse_code, pl.quantity, pl.unit, pl.unit_price, pl.discount_1
            from purchase_line pl join inventory_item i on i.id = pl.item_id
            where pl.purchase_id = @purchaseId and pl.id = @lineId
            for update of pl;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("purchaseId", purchaseId);
        command.Parameters.AddWithValue("lineId", lineId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? new(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetDecimal(4),
            reader.GetString(5), reader.GetDecimal(6), reader.GetDecimal(7), 0) : null;
    }

    private static async Task<decimal> GetPostedReturnedQuantityAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid lineId, CancellationToken cancellationToken)
    {
        const string sql = """
            select coalesce(sum(prl.quantity), 0)
            from purchase_return_line prl join purchase_return pr on pr.id = prl.purchase_return_id and pr.status = 'POSTED'
            where prl.purchase_line_id = @lineId;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("lineId", lineId);
        return Convert.ToDecimal(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static string? NormalizeDocumentNumber(string? value)
    {
        var number = value?.Trim();
        if (string.IsNullOrWhiteSpace(number) || number.Equals("OTOMATIS", StringComparison.OrdinalIgnoreCase)) return null;
        return number.ToUpperInvariant();
    }

    private static async Task<string> NextReturnNumberAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, DateOnly date, CancellationToken cancellationToken)
    {
        const string sql = """
            insert into document_sequence(document_type, period, last_number)
            values ('PURCHASE_RETURN', @period, 1)
            on conflict (document_type, period)
            do update set last_number = document_sequence.last_number + 1
            returning last_number;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("period", date.ToString("yyyyMM", System.Globalization.CultureInfo.InvariantCulture));
        var sequence = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
        var configuredPrefix = await ConfigurationRepository.GetDocumentPrefixAsync(connection, transaction, "PURCHASE_RETURN", "RPB", cancellationToken);
        return $"{configuredPrefix}{date:yyMM}{sequence:000}";
    }

    private static async Task<string> PreviewNextReturnNumberAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, DateOnly date, CancellationToken cancellationToken)
    {
        const string sql = """
            select greatest(
                coalesce((select last_number from document_sequence where document_type = 'PURCHASE_RETURN' and period = @period), 0),
                coalesce((
                    select max(nullif(regexp_replace(substring(document_number from char_length(@prefix) + 1), '\D', '', 'g'), '')::integer)
                    from purchase_return
                    where document_year = @year and document_number like @prefix || '%'
                ), 0)
            ) + 1;
            """;
        var configuredPrefix = await ConfigurationRepository.GetDocumentPrefixAsync(connection, transaction, "PURCHASE_RETURN", "RPB", cancellationToken);
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
        const string sql = "select exists(select 1 from purchase_return where document_number = @number and document_year = @year);";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("number", documentNumber);
        command.Parameters.AddWithValue("year", (short)documentYear);
        return Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task UpdateDocumentSequenceAtLeastAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, DateOnly date, string documentNumber, CancellationToken cancellationToken)
    {
        var configuredPrefix = await ConfigurationRepository.GetDocumentPrefixAsync(connection, transaction, "PURCHASE_RETURN", "RPB", cancellationToken);
        var prefix = $"{configuredPrefix}{date:yyMM}";
        if (!documentNumber.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return;
        if (!int.TryParse(documentNumber[prefix.Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sequence)) return;

        const string sql = """
            insert into document_sequence(document_type, period, last_number)
            values ('PURCHASE_RETURN', @period, @lastNumber)
            on conflict (document_type, period)
            do update set last_number = greatest(document_sequence.last_number, excluded.last_number);
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("period", date.ToString("yyyyMM", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("lastNumber", sequence);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static IEnumerable<ReturnStock> AggregateStock(IEnumerable<ReturnLineLock> lines) => lines
        .GroupBy(x => new { x.ItemId, x.ItemCode, x.Warehouse })
        .Select(x => new ReturnStock(x.Key.ItemId, x.Key.ItemCode, x.Key.Warehouse, x.Sum(y => y.ReturnQuantity),
            x.Sum(y => y.ReturnQuantity * y.UnitPrice * (1m - y.DiscountPercent / 100m)) / x.Sum(y => y.ReturnQuantity)));

    private static async Task InsertAuditAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string action, Guid id,
        string? userName, string? computerName, object? before, object? after, CancellationToken cancellationToken)
    {
        const string sql = """
            insert into audit_log(computer_name, module, action, entity_type, entity_id, before_data, after_data)
            values (@computerName, 'RETUR PEMBELIAN', @action, 'purchase_return', @entityId, @before::jsonb, @after::jsonb);
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("computerName", string.IsNullOrWhiteSpace(computerName) ? DBNull.Value : computerName.Trim());
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("entityId", id.ToString());
        command.Parameters.AddWithValue("before", before is null ? DBNull.Value : JsonSerializer.Serialize(before));
        command.Parameters.AddWithValue("after", after is null ? DBNull.Value : JsonSerializer.Serialize(new { userName, data = after }));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static PurchaseReportTotalsDto Totals(IEnumerable<(decimal Gross, decimal Return, decimal Net)> values)
    {
        var rows = values.ToArray();
        return new(rows.Sum(x => x.Gross), rows.Sum(x => x.Return), rows.Sum(x => x.Net));
    }

    private sealed record SourceHeader(Guid Id, string Number, DateOnly Date, string SupplierCode, string SupplierName, string? SupplierAddress, string Type, string Warehouse);
    private sealed record PurchaseLock(Guid Id, string Number, Guid SupplierId, string Type, string Warehouse, string Status);
    private sealed record ReturnLineLock(Guid PurchaseLineId, Guid ItemId, string ItemCode, string Warehouse, decimal PurchasedQuantity, string Unit,
        decimal UnitPrice, decimal DiscountPercent, decimal ReturnQuantity);
    private sealed record ReturnStock(Guid ItemId, string ItemCode, string Warehouse, decimal Quantity, decimal UnitCost);
    private sealed record ReturnHeader(Guid Id, string Number, DateOnly Date, Guid PurchaseId, string SourceNumber, string SupplierCode, string SupplierName,
        string? SupplierAddress, string Type, string Warehouse, decimal Subtotal, decimal DiscountAmount, decimal TaxAmount, decimal GrandTotal,
        string Status, string? Notes, DateTimeOffset CreatedAt);
}
