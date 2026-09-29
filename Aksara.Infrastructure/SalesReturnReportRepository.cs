using System.Data;
using System.Globalization;
using System.Text.Json;
using Aksara.Contracts;
using Aksara.Domain;
using Npgsql;

namespace Aksara.Infrastructure;

public sealed class SalesReturnReportRepository(PostgresDatabase database)
{
    public async Task<SaleReturnSourceDto?> GetReturnSourceAsync(Guid saleId, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        return await ReadReturnSourceAsync(connection, null, saleId, cancellationToken);
    }

    public async Task<IReadOnlyList<SaleReturnDto>> ListReturnsAsync(string? query, int limit = 100, CancellationToken cancellationToken = default)
    {
        const string sql = """
            select sr.id
            from sale_return sr
            join sale s on s.id = sr.sale_id
            join customer c on c.id = sr.customer_id
            where (@query = '' or sr.document_number ilike '%' || @query || '%'
                or s.document_number ilike '%' || @query || '%'
                or c.code ilike '%' || @query || '%' or c.company_name ilike '%' || @query || '%')
            order by sr.document_date desc, sr.created_at desc
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

        var result = new List<SaleReturnDto>(ids.Count);
        foreach (var id in ids)
        {
            var item = await GetReturnAsync(id, cancellationToken);
            if (item is not null) result.Add(item);
        }
        return result;
    }

    public async Task<SaleReturnDto?> GetReturnAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        return await ReadReturnAsync(connection, null, id, cancellationToken);
    }

    public async Task<string> PreviewNextReturnNumberAsync(DateOnly documentDate, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        return await PreviewNextReturnNumberAsync(connection, null, documentDate, cancellationToken);
    }

    public async Task<SaleReturnDto> CreateReturnAsync(CreateSaleReturnRequest request, CancellationToken cancellationToken = default)
    {
        ValidateReturnRequest(request);
        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        try
        {
            var source = await LockSaleAsync(connection, transaction, request.SaleId, cancellationToken)
                ?? throw new SalesValidationException("Nota penjualan sumber tidak ditemukan.");
            if (!string.Equals(source.Status, "POSTED", StringComparison.OrdinalIgnoreCase))
                throw new SalesConflictException("Hanya nota penjualan berstatus POSTED yang dapat diretur.");

            var requestedLines = request.Lines
                .GroupBy(x => x.SaleLineId)
                .Select(x => new { SaleLineId = x.Key, Quantity = x.Sum(y => y.Quantity) })
                .OrderBy(x => x.SaleLineId)
                .ToArray();
            var lockedLines = new List<ReturnLockedLine>(requestedLines.Length);
            foreach (var requested in requestedLines)
            {
                var line = await LockSaleLineAsync(connection, transaction, request.SaleId, requested.SaleLineId, cancellationToken)
                    ?? throw new SalesValidationException("Rincian nota sumber tidak ditemukan.");
                var returned = await GetPostedReturnedQuantityAsync(connection, transaction, requested.SaleLineId, cancellationToken);
                var remaining = line.SoldQuantity - returned;
                if (requested.Quantity > remaining)
                    throw new SalesConflictException($"Retur {line.ItemCode} melebihi sisa yang dapat diretur. Diminta {requested.Quantity:N3}, tersisa {remaining:N3}.");
                lockedLines.Add(line with { ReturnQuantity = requested.Quantity });
            }

            var amounts = SalesReturnCalculation.Calculate(
                lockedLines.Select(x => new SalesReturnLineAmount(x.ReturnQuantity, x.UnitPrice, x.DiscountPercent, x.UnitCost)),
                request.DiscountPercent,
                request.DiscountAmount,
                request.TaxPercent);
            var returnId = Guid.NewGuid();
            var requestedDocumentNumber = NormalizeDocumentNumber(request.DocumentNumber);
            var documentNumber = requestedDocumentNumber ?? await NextReturnNumberAsync(connection, transaction, request.DocumentDate, cancellationToken);
            if (requestedDocumentNumber is not null && await DocumentNumberExistsAsync(connection, transaction, requestedDocumentNumber, request.DocumentDate.Year, cancellationToken))
                throw new SalesConflictException("No.Retur sudah dipakai. Silakan ganti nomor retur.");
            await UpdateDocumentSequenceAtLeastAsync(connection, transaction, request.DocumentDate, documentNumber, cancellationToken);

            const string headerSql = """
                insert into sale_return(
                    id, document_number, document_year, document_date, sale_id, customer_id, transaction_type,
                    sales_code, warehouse_code, subtotal, discount_percent, discount_amount, tax_percent,
                    tax_amount, grand_total, cost_total, notes, status, created_by_name, computer_name)
                values (@id, @number, @year, @date, @saleId, @customerId, @type,
                    @salesCode, @warehouse, @subtotal, @discountPercent, @discountAmount, @taxPercent,
                    @taxAmount, @grandTotal, @costTotal, @notes, 'POSTED', @userName, @computerName);
                """;
            await using (var command = new NpgsqlCommand(headerSql, connection, transaction))
            {
                command.Parameters.AddWithValue("id", returnId);
                command.Parameters.AddWithValue("number", documentNumber);
                command.Parameters.AddWithValue("year", (short)request.DocumentDate.Year);
                command.Parameters.AddWithValue("date", request.DocumentDate);
                command.Parameters.AddWithValue("saleId", source.Id);
                command.Parameters.AddWithValue("customerId", source.CustomerId);
                command.Parameters.AddWithValue("type", source.TransactionType);
                command.Parameters.AddWithValue("salesCode", (object?)source.SalesCode ?? DBNull.Value);
                command.Parameters.AddWithValue("warehouse", source.WarehouseCode);
                command.Parameters.AddWithValue("subtotal", amounts.Subtotal);
                command.Parameters.AddWithValue("discountPercent", request.DiscountPercent);
                command.Parameters.AddWithValue("discountAmount", amounts.Discount);
                command.Parameters.AddWithValue("taxPercent", request.TaxPercent);
                command.Parameters.AddWithValue("taxAmount", amounts.Tax);
                command.Parameters.AddWithValue("grandTotal", amounts.GrandTotal);
                command.Parameters.AddWithValue("costTotal", amounts.CostTotal);
                command.Parameters.AddWithValue("notes", string.IsNullOrWhiteSpace(request.Notes) ? DBNull.Value : request.Notes.Trim());
                command.Parameters.AddWithValue("userName", string.IsNullOrWhiteSpace(request.UserName) ? "unknown" : request.UserName.Trim());
                command.Parameters.AddWithValue("computerName", string.IsNullOrWhiteSpace(request.ComputerName) ? DBNull.Value : request.ComputerName.Trim());
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            const string lineSql = """
                insert into sale_return_line(
                    sale_return_id, sale_line_id, line_number, item_id, warehouse_code, quantity, unit,
                    unit_price, discount_percent, line_total, cost_amount)
                values (@returnId, @saleLineId, @lineNumber, @itemId, @warehouse, @quantity, @unit,
                    @unitPrice, @discountPercent, @lineTotal, @costAmount);
                """;
            for (var index = 0; index < lockedLines.Count; index++)
            {
                var line = lockedLines[index];
                var amount = new SalesReturnLineAmount(line.ReturnQuantity, line.UnitPrice, line.DiscountPercent, line.UnitCost);
                await using var command = new NpgsqlCommand(lineSql, connection, transaction);
                command.Parameters.AddWithValue("returnId", returnId);
                command.Parameters.AddWithValue("saleLineId", line.SaleLineId);
                command.Parameters.AddWithValue("lineNumber", index + 1);
                command.Parameters.AddWithValue("itemId", line.ItemId);
                command.Parameters.AddWithValue("warehouse", line.WarehouseCode);
                command.Parameters.AddWithValue("quantity", line.ReturnQuantity);
                command.Parameters.AddWithValue("unit", line.Unit);
                command.Parameters.AddWithValue("unitPrice", line.UnitPrice);
                command.Parameters.AddWithValue("discountPercent", line.DiscountPercent);
                command.Parameters.AddWithValue("lineTotal", amount.LineTotal);
                command.Parameters.AddWithValue("costAmount", amount.CostTotal);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var stock in AggregateStock(lockedLines))
            {
                await EnsureAndLockBalanceAsync(connection, transaction, stock.ItemId, stock.WarehouseCode, cancellationToken);
                const string stockSql = """
                    update inventory_balance
                    set quantity = quantity + @quantity, version = version + 1, updated_at = now()
                    where item_id = @itemId and warehouse_code = @warehouse;

                    insert into inventory_movement(
                        occurred_at, item_id, warehouse_code, source_type, source_id, source_number,
                        quantity_in, quantity_out, unit_cost)
                    values (now(), @itemId, @warehouse, 'SALE_RETURN', @returnId, @number,
                        @quantity, 0, @unitCost);
                    """;
                await using var command = new NpgsqlCommand(stockSql, connection, transaction);
                command.Parameters.AddWithValue("quantity", stock.Quantity);
                command.Parameters.AddWithValue("itemId", stock.ItemId);
                command.Parameters.AddWithValue("warehouse", stock.WarehouseCode);
                command.Parameters.AddWithValue("returnId", returnId);
                command.Parameters.AddWithValue("number", documentNumber);
                command.Parameters.AddWithValue("unitCost", stock.Quantity == 0 ? 0m : stock.CostAmount / stock.Quantity);
                await command.ExecuteNonQueryAsync(cancellationToken);
                await FifoInventoryCosting.ReceiveAsync(connection, transaction, stock.ItemId, stock.WarehouseCode,
                    "SALE_RETURN", returnId, null, documentNumber, request.DocumentDate, stock.Quantity,
                    stock.Quantity == 0 ? 0m : stock.CostAmount / stock.Quantity, cancellationToken);
            }

            await InventoryLedgerPosting.PostSaleReturnAsync(
                connection, transaction, returnId, documentNumber, request.DocumentDate, source.TransactionType,
                source.CashAccountCode, Math.Max(0, amounts.Subtotal - amounts.Discount), amounts.Tax,
                amounts.GrandTotal, amounts.CostTotal, request.UserName, request.ComputerName, cancellationToken);

            await InsertReturnAuditAsync(connection, transaction, "CREATE_POST", returnId, request.UserName, request.ComputerName, null, request, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return (await GetReturnAsync(returnId, cancellationToken))!;
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw new SalesConflictException("No.Retur sudah dipakai. Silakan ganti nomor retur.");
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<SaleReturnDto> CancelReturnAsync(Guid id, CancelSaleReturnRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Reason)) throw new SalesValidationException("Alasan pembatalan retur wajib diisi.");
        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        try
        {
            const string headerSql = "select document_number, status from sale_return where id = @id for update;";
            string number;
            string status;
            await using (var command = new NpgsqlCommand(headerSql, connection, transaction))
            {
                command.Parameters.AddWithValue("id", id);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken)) throw new SalesValidationException("Retur penjualan tidak ditemukan.");
                number = reader.GetString(0);
                status = reader.GetString(1);
            }
            if (!string.Equals(status, "POSTED", StringComparison.OrdinalIgnoreCase))
                throw new SalesConflictException("Retur penjualan sudah dibatalkan.");

            const string linesSql = """
                select srl.item_id, srl.warehouse_code, sum(srl.quantity), sum(srl.cost_amount)
                from sale_return_line srl
                where srl.sale_return_id = @id
                group by srl.item_id, srl.warehouse_code
                order by srl.item_id, srl.warehouse_code;
                """;
            var stocks = new List<ReturnStock>();
            await using (var command = new NpgsqlCommand(linesSql, connection, transaction))
            {
                command.Parameters.AddWithValue("id", id);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                    stocks.Add(new ReturnStock(reader.GetGuid(0), reader.GetString(1), reader.GetDecimal(2), reader.GetDecimal(3)));
            }

            foreach (var stock in stocks)
            {
                var available = await EnsureAndLockBalanceAsync(connection, transaction, stock.ItemId, stock.WarehouseCode, cancellationToken);
                if (available < stock.Quantity)
                    throw new SalesConflictException($"Pembatalan retur tidak dapat dilakukan karena stok gudang {stock.WarehouseCode} sudah terpakai. Dibutuhkan {stock.Quantity:N3}, tersedia {available:N3}.");
            }
            foreach (var stock in stocks)
            {
                var fifo = await FifoInventoryCosting.IssueAsync(connection, transaction, stock.ItemId, stock.WarehouseCode,
                    "SALE_RETURN_CANCEL", id, null, stock.Quantity, cancellationToken);
                const string stockSql = """
                    update inventory_balance
                    set quantity = quantity - @quantity, version = version + 1, updated_at = now()
                    where item_id = @itemId and warehouse_code = @warehouse;

                    insert into inventory_movement(
                        occurred_at, item_id, warehouse_code, source_type, source_id, source_number,
                        quantity_in, quantity_out, unit_cost)
                    values (now(), @itemId, @warehouse, 'SALE_RETURN_CANCEL', @returnId, @number,
                        0, @quantity, @unitCost);
                    """;
                await using var command = new NpgsqlCommand(stockSql, connection, transaction);
                command.Parameters.AddWithValue("quantity", stock.Quantity);
                command.Parameters.AddWithValue("itemId", stock.ItemId);
                command.Parameters.AddWithValue("warehouse", stock.WarehouseCode);
                command.Parameters.AddWithValue("returnId", id);
                command.Parameters.AddWithValue("number", number);
                command.Parameters.AddWithValue("unitCost", fifo.UnitCost);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await InventoryLedgerPosting.CancelAsync(connection, transaction, id, "SALES_RETURN",
                request.UserName, request.Reason, cancellationToken);

            const string cancelSql = """
                update sale_return
                set status = 'CANCELLED', cancelled_at = now(), cancelled_by_name = @userName,
                    cancellation_reason = @reason, version = version + 1
                where id = @id;
                """;
            await using (var command = new NpgsqlCommand(cancelSql, connection, transaction))
            {
                command.Parameters.AddWithValue("id", id);
                command.Parameters.AddWithValue("userName", string.IsNullOrWhiteSpace(request.UserName) ? "unknown" : request.UserName.Trim());
                command.Parameters.AddWithValue("reason", request.Reason.Trim());
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await InsertReturnAuditAsync(connection, transaction, "CANCEL", id, request.UserName, request.ComputerName, new { status = "POSTED" }, new { status = "CANCELLED", reason = request.Reason.Trim() }, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return (await GetReturnAsync(id, cancellationToken))!;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<SaleReturnDto> RestoreReturnAsync(Guid id, RestoreSaleReturnRequest request, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        try
        {
            const string headerSql = """
                select sr.document_number, sr.document_date, sr.status, sr.sale_id, s.status
                from sale_return sr
                join sale s on s.id = sr.sale_id
                where sr.id = @id
                for update of sr, s;
                """;
            string number;
            DateOnly documentDate;
            string status;
            Guid saleId;
            string saleStatus;
            await using (var command = new NpgsqlCommand(headerSql, connection, transaction))
            {
                command.Parameters.AddWithValue("id", id);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken)) throw new SalesValidationException("Retur penjualan tidak ditemukan.");
                number = reader.GetString(0);
                documentDate = reader.GetFieldValue<DateOnly>(1);
                status = reader.GetString(2);
                saleId = reader.GetGuid(3);
                saleStatus = reader.GetString(4);
            }

            if (!string.Equals(status, "CANCELLED", StringComparison.OrdinalIgnoreCase))
                throw new SalesConflictException("Retur penjualan belum ditandai hapus.");
            if (!string.Equals(saleStatus, "POSTED", StringComparison.OrdinalIgnoreCase))
                throw new SalesConflictException("Nota penjualan sumber sudah ditandai hapus, retur tidak dapat dipulihkan.");

            const string linesSql = """
                select srl.sale_line_id, srl.item_id, i.code, srl.warehouse_code, sl.quantity, srl.unit,
                       srl.unit_price, srl.discount_percent,
                       case when srl.quantity = 0 then 0 else srl.cost_amount / srl.quantity end,
                       srl.quantity
                from sale_return_line srl
                join sale_line sl on sl.id = srl.sale_line_id and sl.sale_id = @saleId
                join inventory_item i on i.id = srl.item_id
                where srl.sale_return_id = @id
                order by srl.line_number;
                """;
            var lines = new List<ReturnLockedLine>();
            await using (var command = new NpgsqlCommand(linesSql, connection, transaction))
            {
                command.Parameters.AddWithValue("id", id);
                command.Parameters.AddWithValue("saleId", saleId);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    lines.Add(new ReturnLockedLine(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3),
                        reader.GetDecimal(4), reader.GetString(5), reader.GetDecimal(6), reader.GetDecimal(7), reader.GetDecimal(8), reader.GetDecimal(9)));
                }
            }

            if (lines.Count == 0) throw new SalesConflictException("Rincian retur penjualan tidak ditemukan.");

            foreach (var line in lines)
            {
                var returnedByOthers = await GetPostedReturnedQuantityAsync(connection, transaction, line.SaleLineId, cancellationToken);
                var remaining = line.SoldQuantity - returnedByOthers;
                if (line.ReturnQuantity > remaining)
                    throw new SalesConflictException($"Batal hapus retur {line.ItemCode} melebihi sisa yang dapat diretur. Tersisa {remaining:N3}.");
            }

            foreach (var stock in AggregateStock(lines))
            {
                await EnsureAndLockBalanceAsync(connection, transaction, stock.ItemId, stock.WarehouseCode, cancellationToken);
                const string stockSql = """
                    update inventory_balance
                    set quantity = quantity + @quantity, version = version + 1, updated_at = now()
                    where item_id = @itemId and warehouse_code = @warehouse;

                    insert into inventory_movement(
                        occurred_at, item_id, warehouse_code, source_type, source_id, source_number,
                        quantity_in, quantity_out, unit_cost)
                    values (now(), @itemId, @warehouse, 'SALE_RETURN_RESTORE', @returnId, @number,
                        @quantity, 0, @unitCost);
                    """;
                await using var command = new NpgsqlCommand(stockSql, connection, transaction);
                command.Parameters.AddWithValue("quantity", stock.Quantity);
                command.Parameters.AddWithValue("itemId", stock.ItemId);
                command.Parameters.AddWithValue("warehouse", stock.WarehouseCode);
                command.Parameters.AddWithValue("returnId", id);
                command.Parameters.AddWithValue("number", number);
                command.Parameters.AddWithValue("unitCost", stock.Quantity == 0 ? 0m : stock.CostAmount / stock.Quantity);
                await command.ExecuteNonQueryAsync(cancellationToken);
                await FifoInventoryCosting.ReceiveAsync(connection, transaction, stock.ItemId, stock.WarehouseCode,
                    "SALE_RETURN_RESTORE", id, null, number, documentDate, stock.Quantity,
                    stock.Quantity == 0 ? 0m : stock.CostAmount / stock.Quantity, cancellationToken);
            }

            await InventoryLedgerPosting.RestoreAsync(connection, transaction, id, "SALES_RETURN", cancellationToken);

            const string restoreSql = """
                update sale_return
                set status = 'POSTED', cancelled_at = null, cancelled_by_name = null,
                    cancellation_reason = null, version = version + 1
                where id = @id;
                """;
            await using (var command = new NpgsqlCommand(restoreSql, connection, transaction))
            {
                command.Parameters.AddWithValue("id", id);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await InsertReturnAuditAsync(connection, transaction, "RESTORE", id, request.UserName, request.ComputerName, new { status = "CANCELLED" }, new { status = "POSTED" }, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return (await GetReturnAsync(id, cancellationToken))!;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<SalesDetailReportDto> GetDetailReportAsync(DateOnly from, DateOnly to, string? query, CancellationToken cancellationToken = default)
    {
        ValidateReportRange(from, to);
        const string sql = """
            with returned as (
                select srl.sale_line_id,
                       sum(srl.quantity) as return_quantity,
                       sum(srl.line_total) as return_sales,
                       sum(srl.cost_amount) as return_cost
                from sale_return_line srl
                join sale_return sr on sr.id = srl.sale_return_id and sr.status = 'POSTED'
                group by srl.sale_line_id
            )
            select s.document_date, s.document_number, c.code, c.company_name, s.transaction_type,
                   s.sales_code, sl.warehouse_code, i.code, i.name, sl.unit, sl.quantity,
                   coalesce(r.return_quantity, 0), sl.unit_price, sl.discount_1, sl.line_total,
                   coalesce(r.return_sales, 0), sl.cost_amount, coalesce(r.return_cost, 0)
            from sale s
            join customer c on c.id = s.customer_id
            join sale_line sl on sl.sale_id = s.id
            join inventory_item i on i.id = sl.item_id
            left join returned r on r.sale_line_id = sl.id
            where s.status = 'POSTED' and s.document_date between @from and @to
              and (@query = '' or s.document_number ilike '%' || @query || '%'
                or c.code ilike '%' || @query || '%' or c.company_name ilike '%' || @query || '%'
                or i.code ilike '%' || @query || '%' or i.name ilike '%' || @query || '%')
            order by s.document_date, s.document_number, sl.line_number;
            """;
        await using var command = database.DataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("from", from);
        command.Parameters.AddWithValue("to", to);
        command.Parameters.AddWithValue("query", query?.Trim() ?? string.Empty);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<SalesDetailReportRowDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var sold = reader.GetDecimal(10);
            var returned = reader.GetDecimal(11);
            var grossSales = reader.GetDecimal(14);
            var returnSales = reader.GetDecimal(15);
            var grossCost = reader.GetDecimal(16);
            var returnCost = reader.GetDecimal(17);
            rows.Add(new SalesDetailReportRowDto(
                reader.GetFieldValue<DateOnly>(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetString(8), reader.GetString(9),
                sold, returned, sold - returned, reader.GetDecimal(12), reader.GetDecimal(13), grossSales, returnSales,
                grossSales - returnSales, grossCost, returnCost, grossCost - returnCost,
                (grossSales - returnSales) - (grossCost - returnCost)));
        }
        return new SalesDetailReportDto(rows, Totals(rows.Select(x => (x.GrossSales, x.SalesReturn, x.NetSales, x.GrossCost, x.ReturnCost, x.NetCost, x.GrossProfit))));
    }

    public async Task<SalesHeaderReportDto> GetHeaderReportAsync(DateOnly from, DateOnly to, string? query, CancellationToken cancellationToken = default)
    {
        ValidateReportRange(from, to);
        const string sql = """
            with returned as (
                select sr.sale_id, sum(sr.grand_total) as return_sales, sum(sr.cost_total) as return_cost
                from sale_return sr
                where sr.status = 'POSTED'
                group by sr.sale_id
            )
            select s.document_date, s.document_number, c.code, c.company_name, s.transaction_type,
                   s.due_date, s.sales_code, s.warehouse_code, s.grand_total,
                   coalesce(r.return_sales, 0), s.cost_total, coalesce(r.return_cost, 0)
            from sale s
            join customer c on c.id = s.customer_id
            left join returned r on r.sale_id = s.id
            where s.status = 'POSTED' and s.document_date between @from and @to
              and (@query = '' or s.document_number ilike '%' || @query || '%'
                or c.code ilike '%' || @query || '%' or c.company_name ilike '%' || @query || '%')
            order by s.document_date, s.document_number;
            """;
        await using var command = database.DataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("from", from);
        command.Parameters.AddWithValue("to", to);
        command.Parameters.AddWithValue("query", query?.Trim() ?? string.Empty);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<SalesHeaderReportRowDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var grossSales = reader.GetDecimal(8);
            var returnSales = reader.GetDecimal(9);
            var grossCost = reader.GetDecimal(10);
            var returnCost = reader.GetDecimal(11);
            rows.Add(new SalesHeaderReportRowDto(
                reader.GetFieldValue<DateOnly>(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetFieldValue<DateOnly>(5), reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetString(7),
                grossSales, returnSales, grossSales - returnSales, grossCost, returnCost, grossCost - returnCost,
                (grossSales - returnSales) - (grossCost - returnCost)));
        }
        return new SalesHeaderReportDto(rows, Totals(rows.Select(x => (x.GrossSales, x.SalesReturn, x.NetSales, x.GrossCost, x.ReturnCost, x.NetCost, x.GrossProfit))));
    }

    public async Task<SalesReturnDetailReportDto> GetReturnDetailReportAsync(DateOnly from, DateOnly to, string? query, CancellationToken cancellationToken = default)
    {
        ValidateReportRange(from, to);
        const string sql = """
            select sr.document_date, sr.document_number, s.document_number, c.code, c.company_name,
                   sr.transaction_type, sr.sales_code, srl.warehouse_code, i.code, i.name, srl.unit,
                   srl.quantity, srl.unit_price, srl.discount_percent, srl.line_total, srl.cost_amount
            from sale_return sr
            join sale s on s.id = sr.sale_id
            join customer c on c.id = sr.customer_id
            join sale_return_line srl on srl.sale_return_id = sr.id
            join inventory_item i on i.id = srl.item_id
            where sr.status = 'POSTED' and sr.document_date between @from and @to
              and (@query = '' or sr.document_number ilike '%' || @query || '%'
                or s.document_number ilike '%' || @query || '%'
                or c.code ilike '%' || @query || '%' or c.company_name ilike '%' || @query || '%'
                or i.code ilike '%' || @query || '%' or i.name ilike '%' || @query || '%')
            order by sr.document_date, sr.document_number, srl.line_number;
            """;
        await using var command = database.DataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("from", from);
        command.Parameters.AddWithValue("to", to);
        command.Parameters.AddWithValue("query", query?.Trim() ?? string.Empty);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<SalesReturnDetailReportRowDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var returnAmount = reader.GetDecimal(14);
            var returnCost = reader.GetDecimal(15);
            rows.Add(new SalesReturnDetailReportRowDto(
                reader.GetFieldValue<DateOnly>(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetString(7), reader.GetString(8), reader.GetString(9),
                reader.GetString(10), reader.GetDecimal(11), reader.GetDecimal(12), reader.GetDecimal(13), returnAmount, returnCost,
                returnAmount - returnCost));
        }
        return new SalesReturnDetailReportDto(rows, ReturnTotals(rows.Select(x => (x.ReturnAmount, x.ReturnCost, x.ProfitReduction))));
    }

    public async Task<SalesReturnHeaderReportDto> GetReturnHeaderReportAsync(DateOnly from, DateOnly to, string? query, CancellationToken cancellationToken = default)
    {
        ValidateReportRange(from, to);
        const string sql = """
            select sr.document_date, sr.document_number, s.document_number, c.code, c.company_name,
                   sr.transaction_type, sr.sales_code, sr.warehouse_code, sr.subtotal, sr.discount_amount,
                   sr.tax_amount, sr.grand_total, sr.cost_total, sr.notes
            from sale_return sr
            join sale s on s.id = sr.sale_id
            join customer c on c.id = sr.customer_id
            where sr.status = 'POSTED' and sr.document_date between @from and @to
              and (@query = '' or sr.document_number ilike '%' || @query || '%'
                or s.document_number ilike '%' || @query || '%'
                or c.code ilike '%' || @query || '%' or c.company_name ilike '%' || @query || '%')
            order by sr.document_date, sr.document_number;
            """;
        await using var command = database.DataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("from", from);
        command.Parameters.AddWithValue("to", to);
        command.Parameters.AddWithValue("query", query?.Trim() ?? string.Empty);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<SalesReturnHeaderReportRowDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var returnAmount = reader.GetDecimal(11);
            var returnCost = reader.GetDecimal(12);
            rows.Add(new SalesReturnHeaderReportRowDto(
                reader.GetFieldValue<DateOnly>(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetString(7), reader.GetDecimal(8),
                reader.GetDecimal(9), reader.GetDecimal(10), returnAmount, returnCost, returnAmount - returnCost,
                reader.IsDBNull(13) ? null : reader.GetString(13)));
        }
        return new SalesReturnHeaderReportDto(rows, ReturnTotals(rows.Select(x => (x.ReturnAmount, x.ReturnCost, x.ProfitReduction))));
    }

    private static async Task<SaleReturnSourceDto?> ReadReturnSourceAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid saleId, CancellationToken cancellationToken)
    {
        const string headerSql = """
            select s.id, s.document_number, s.document_date, c.code, c.company_name, c.address,
                   s.transaction_type, s.sales_code, s.warehouse_code
            from sale s
            join customer c on c.id = s.customer_id
            where s.id = @id and s.status = 'POSTED';
            """;
        SourceHeader? header = null;
        await using (var command = new NpgsqlCommand(headerSql, connection, transaction))
        {
            command.Parameters.AddWithValue("id", saleId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
                header = new SourceHeader(reader.GetGuid(0), reader.GetString(1), reader.GetFieldValue<DateOnly>(2), reader.GetString(3), reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetString(8));
        }
        if (header is null) return null;

        const string linesSql = """
            select sl.id, sl.line_number, i.code, i.name, sl.warehouse_code, sl.quantity,
                   coalesce(sum(case when sr.id is not null then srl.quantity else 0 end), 0),
                   sl.unit, sl.unit_price, sl.discount_1,
                   case when sl.quantity = 0 then 0 else sl.cost_amount / sl.quantity end
            from sale_line sl
            join inventory_item i on i.id = sl.item_id
            left join sale_return_line srl on srl.sale_line_id = sl.id
            left join sale_return sr on sr.id = srl.sale_return_id and sr.status = 'POSTED'
            where sl.sale_id = @id
            group by sl.id, sl.line_number, i.code, i.name, sl.warehouse_code, sl.quantity,
                     sl.unit, sl.unit_price, sl.discount_1, sl.cost_amount
            order by sl.line_number;
            """;
        var lines = new List<SaleReturnSourceLineDto>();
        await using (var command = new NpgsqlCommand(linesSql, connection, transaction))
        {
            command.Parameters.AddWithValue("id", saleId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var sold = reader.GetDecimal(5);
                var returned = reader.GetDecimal(6);
                lines.Add(new SaleReturnSourceLineDto(reader.GetGuid(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                    sold, returned, Math.Max(0, sold - returned), reader.GetString(7), reader.GetDecimal(8), reader.GetDecimal(9), reader.GetDecimal(10)));
            }
        }
        return new SaleReturnSourceDto(header.Id, header.Number, header.Date, header.CustomerCode, header.CustomerName, header.CustomerAddress,
            header.TransactionType, header.SalesCode, header.WarehouseCode, lines);
    }

    private static async Task<SaleReturnDto?> ReadReturnAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid id, CancellationToken cancellationToken)
    {
        const string headerSql = """
            select sr.id, sr.document_number, sr.document_date, sr.sale_id, s.document_number,
                   c.code, c.company_name, c.address, sr.transaction_type, sr.sales_code, sr.warehouse_code,
                   sr.subtotal, sr.discount_amount, sr.tax_amount, sr.grand_total, sr.cost_total,
                   sr.status, sr.notes, sr.created_at
            from sale_return sr
            join sale s on s.id = sr.sale_id
            join customer c on c.id = sr.customer_id
            where sr.id = @id;
            """;
        ReturnHeader? header = null;
        await using (var command = new NpgsqlCommand(headerSql, connection, transaction))
        {
            command.Parameters.AddWithValue("id", id);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
                header = new ReturnHeader(reader.GetGuid(0), reader.GetString(1), reader.GetFieldValue<DateOnly>(2), reader.GetGuid(3), reader.GetString(4),
                    reader.GetString(5), reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetString(9),
                    reader.GetString(10), reader.GetDecimal(11), reader.GetDecimal(12), reader.GetDecimal(13), reader.GetDecimal(14), reader.GetDecimal(15),
                    reader.GetString(16), reader.IsDBNull(17) ? null : reader.GetString(17), reader.GetFieldValue<DateTimeOffset>(18));
        }
        if (header is null) return null;

        const string linesSql = """
            select srl.sale_line_id, srl.line_number, s.document_number, i.code, i.name, srl.warehouse_code,
                   srl.quantity, srl.unit, srl.unit_price, srl.discount_percent, srl.line_total, srl.cost_amount
            from sale_return_line srl
            join sale_line sl on sl.id = srl.sale_line_id
            join sale s on s.id = sl.sale_id
            join inventory_item i on i.id = srl.item_id
            where srl.sale_return_id = @id
            order by srl.line_number;
            """;
        var lines = new List<SaleReturnLineDto>();
        await using (var command = new NpgsqlCommand(linesSql, connection, transaction))
        {
            command.Parameters.AddWithValue("id", id);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                lines.Add(new SaleReturnLineDto(reader.GetGuid(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5),
                    reader.GetDecimal(6), reader.GetString(7), reader.GetDecimal(8), reader.GetDecimal(9), reader.GetDecimal(10), reader.GetDecimal(11)));
        }
        return new SaleReturnDto(header.Id, header.Number, header.Date, header.SaleId, header.SourceNumber, header.CustomerCode,
            header.CustomerName, header.CustomerAddress, header.TransactionType, header.SalesCode, header.WarehouseCode,
            header.Subtotal, header.DiscountAmount, header.TaxAmount, header.GrandTotal, header.CostTotal, header.Status,
            header.Notes, header.CreatedAt, lines);
    }

    private static void ValidateReturnRequest(CreateSaleReturnRequest request)
    {
        if (request.SaleId == Guid.Empty || request.DocumentDate == default) throw new SalesValidationException("Nota sumber dan tanggal retur wajib diisi.");
        if (request.Lines is null || request.Lines.Count == 0 || request.Lines.Any(x => x.SaleLineId == Guid.Empty || x.Quantity <= 0))
            throw new SalesValidationException("Minimal satu rincian retur dengan jumlah lebih dari nol wajib diisi.");
        if (request.DiscountPercent is < 0 or > 100 || request.DiscountAmount < 0 || request.TaxPercent is < 0 or > 100)
            throw new SalesValidationException("Diskon atau PPN retur tidak valid.");
        var documentNumber = NormalizeDocumentNumber(request.DocumentNumber);
        if (documentNumber?.Length > 30) throw new SalesValidationException("No.Retur maksimal 30 karakter.");
    }

    private static void ValidateReportRange(DateOnly from, DateOnly to)
    {
        if (from == default || to == default || to < from) throw new SalesValidationException("Rentang tanggal laporan tidak valid.");
        if (to.DayNumber - from.DayNumber > 3660) throw new SalesValidationException("Rentang laporan maksimal 10 tahun.");
    }

    private static async Task<SaleLock?> LockSaleAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, CancellationToken cancellationToken)
    {
        const string sql = """
            select s.id, s.document_number, s.customer_id, s.transaction_type, s.sales_code, s.warehouse_code, s.status, s.cash_account_code
            from sale s where s.id = @id for update;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new SaleLock(reader.GetGuid(0), reader.GetString(1), reader.GetGuid(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7))
            : null;
    }

    private static async Task<ReturnLockedLine?> LockSaleLineAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid saleId, Guid lineId, CancellationToken cancellationToken)
    {
        const string sql = """
            select sl.id, sl.item_id, i.code, sl.warehouse_code, sl.quantity, sl.unit, sl.unit_price,
                   sl.discount_1, case when sl.quantity = 0 then 0 else sl.cost_amount / sl.quantity end
            from sale_line sl join inventory_item i on i.id = sl.item_id
            where sl.sale_id = @saleId and sl.id = @lineId
            for update of sl;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("saleId", saleId);
        command.Parameters.AddWithValue("lineId", lineId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new ReturnLockedLine(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetDecimal(4),
                reader.GetString(5), reader.GetDecimal(6), reader.GetDecimal(7), reader.GetDecimal(8), 0m)
            : null;
    }

    private static async Task<decimal> GetPostedReturnedQuantityAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid saleLineId, CancellationToken cancellationToken)
    {
        const string sql = """
            select coalesce(sum(srl.quantity), 0)
            from sale_return_line srl
            join sale_return sr on sr.id = srl.sale_return_id and sr.status = 'POSTED'
            where srl.sale_line_id = @saleLineId;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("saleLineId", saleLineId);
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
            values ('SALE_RETURN', @period, 1)
            on conflict (document_type, period)
            do update set last_number = document_sequence.last_number + 1
            returning last_number;
            """;
        var period = date.ToString("yyyyMM");
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("period", period);
        var number = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
        var configuredPrefix = await ConfigurationRepository.GetDocumentPrefixAsync(connection, transaction, "SALE_RETURN", "RJL", cancellationToken);
        return $"{configuredPrefix}{date:yyMM}{number:000}";
    }

    private static async Task<string> PreviewNextReturnNumberAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, DateOnly date, CancellationToken cancellationToken)
    {
        const string sql = """
            select greatest(
                coalesce((select last_number from document_sequence where document_type = 'SALE_RETURN' and period = @period), 0),
                coalesce((
                    select max(nullif(regexp_replace(substring(document_number from char_length(@prefix) + 1), '\D', '', 'g'), '')::integer)
                    from sale_return
                    where document_year = @year and document_number like @prefix || '%'
                ), 0)
            ) + 1;
            """;
        var configuredPrefix = await ConfigurationRepository.GetDocumentPrefixAsync(connection, transaction, "SALE_RETURN", "RJL", cancellationToken);
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
        const string sql = "select exists(select 1 from sale_return where document_number = @number and document_year = @year);";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("number", documentNumber);
        command.Parameters.AddWithValue("year", (short)documentYear);
        return Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task UpdateDocumentSequenceAtLeastAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, DateOnly date, string documentNumber, CancellationToken cancellationToken)
    {
        var configuredPrefix = await ConfigurationRepository.GetDocumentPrefixAsync(connection, transaction, "SALE_RETURN", "RJL", cancellationToken);
        var prefix = $"{configuredPrefix}{date:yyMM}";
        if (!documentNumber.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return;
        if (!int.TryParse(documentNumber[prefix.Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sequence)) return;

        const string sql = """
            insert into document_sequence(document_type, period, last_number)
            values ('SALE_RETURN', @period, @lastNumber)
            on conflict (document_type, period)
            do update set last_number = greatest(document_sequence.last_number, excluded.last_number);
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("period", date.ToString("yyyyMM", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("lastNumber", sequence);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static IEnumerable<ReturnStock> AggregateStock(IEnumerable<ReturnLockedLine> lines) => lines
        .GroupBy(x => new { x.ItemId, x.WarehouseCode })
        .Select(x => new ReturnStock(x.Key.ItemId, x.Key.WarehouseCode, x.Sum(y => y.ReturnQuantity), x.Sum(y => y.ReturnQuantity * y.UnitCost)))
        .OrderBy(x => x.ItemId).ThenBy(x => x.WarehouseCode, StringComparer.Ordinal);

    private static async Task<decimal> EnsureAndLockBalanceAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid itemId, string warehouse, CancellationToken cancellationToken)
    {
        const string ensureSql = """
            insert into inventory_balance(item_id, warehouse_code, quantity)
            values (@itemId, @warehouse, 0)
            on conflict (item_id, warehouse_code) do nothing;
            """;
        await using (var command = new NpgsqlCommand(ensureSql, connection, transaction))
        {
            command.Parameters.AddWithValue("itemId", itemId);
            command.Parameters.AddWithValue("warehouse", warehouse);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        const string lockSql = "select quantity from inventory_balance where item_id = @itemId and warehouse_code = @warehouse for update;";
        await using var lockCommand = new NpgsqlCommand(lockSql, connection, transaction);
        lockCommand.Parameters.AddWithValue("itemId", itemId);
        lockCommand.Parameters.AddWithValue("warehouse", warehouse);
        return Convert.ToDecimal(await lockCommand.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task InsertReturnAuditAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string action, Guid id,
        string? userName, string? computerName, object? before, object? after, CancellationToken cancellationToken)
    {
        const string sql = """
            insert into audit_log(computer_name, module, action, entity_type, entity_id, before_data, after_data)
            values (@computerName, 'RETUR PENJUALAN', @action, 'sale_return', @entityId, @before::jsonb, @after::jsonb);
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("computerName", string.IsNullOrWhiteSpace(computerName) ? DBNull.Value : computerName.Trim());
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("entityId", id.ToString());
        command.Parameters.AddWithValue("before", before is null ? DBNull.Value : JsonSerializer.Serialize(before));
        command.Parameters.AddWithValue("after", after is null ? DBNull.Value : JsonSerializer.Serialize(new { userName, data = after }));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static SalesReportTotalsDto Totals(IEnumerable<(decimal GrossSales, decimal SalesReturn, decimal NetSales, decimal GrossCost, decimal ReturnCost, decimal NetCost, decimal GrossProfit)> values)
    {
        var rows = values.ToArray();
        return new SalesReportTotalsDto(rows.Sum(x => x.GrossSales), rows.Sum(x => x.SalesReturn), rows.Sum(x => x.NetSales),
            rows.Sum(x => x.GrossCost), rows.Sum(x => x.ReturnCost), rows.Sum(x => x.NetCost), rows.Sum(x => x.GrossProfit));
    }

    private static SalesReturnReportTotalsDto ReturnTotals(IEnumerable<(decimal ReturnAmount, decimal ReturnCost, decimal ProfitReduction)> values)
    {
        var rows = values.ToArray();
        return new SalesReturnReportTotalsDto(rows.Sum(x => x.ReturnAmount), rows.Sum(x => x.ReturnCost), rows.Sum(x => x.ProfitReduction));
    }

    private sealed record SourceHeader(Guid Id, string Number, DateOnly Date, string CustomerCode, string CustomerName, string? CustomerAddress, string TransactionType, string? SalesCode, string WarehouseCode);
    private sealed record SaleLock(Guid Id, string Number, Guid CustomerId, string TransactionType, string? SalesCode, string WarehouseCode, string Status, string? CashAccountCode);
    private sealed record ReturnLockedLine(Guid SaleLineId, Guid ItemId, string ItemCode, string WarehouseCode, decimal SoldQuantity, string Unit, decimal UnitPrice, decimal DiscountPercent, decimal UnitCost, decimal ReturnQuantity);
    private sealed record ReturnStock(Guid ItemId, string WarehouseCode, decimal Quantity, decimal CostAmount);
    private sealed record ReturnHeader(Guid Id, string Number, DateOnly Date, Guid SaleId, string SourceNumber, string CustomerCode, string CustomerName, string? CustomerAddress,
        string TransactionType, string? SalesCode, string WarehouseCode, decimal Subtotal, decimal DiscountAmount, decimal TaxAmount, decimal GrandTotal, decimal CostTotal,
        string Status, string? Notes, DateTimeOffset CreatedAt);
}
