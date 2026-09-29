using System.Data;
using System.Globalization;
using System.Text.Json;
using Aksara.Contracts;
using Aksara.Domain;
using Npgsql;

namespace Aksara.Infrastructure;

public sealed class SalesValidationException(string message) : InvalidOperationException(message);
public sealed class SalesConflictException(string message) : InvalidOperationException(message);

public sealed class StockUnavailableException(string itemCode, string warehouseCode, decimal requested, decimal available)
    : InvalidOperationException($"Stok {itemCode} di gudang {warehouseCode} tidak cukup. Diminta {requested:N3}, tersedia {available:N3}.")
{
    public string ItemCode { get; } = itemCode;
    public string WarehouseCode { get; } = warehouseCode;
    public decimal Requested { get; } = requested;
    public decimal Available { get; } = available;
}

public sealed class SalesInventoryRepository(PostgresDatabase database)
{
    public async Task<IReadOnlyList<SalesCustomerDto>> SearchCustomersAsync(string? query, int limit = 50, CancellationToken cancellationToken = default)
    {
        const string sql = """
            select id, code, company_name, address, credit_days, credit_limit
            from customer
            where is_active
              and code <> 'GUNGGUNGAN'
              and (@query = '' or code ilike '%' || @query || '%' or company_name ilike '%' || @query || '%')
            order by code
            limit @limit;
            """;

        await using var command = database.DataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("query", query?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 200));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<SalesCustomerDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new SalesCustomerDto(
                reader.GetGuid(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetInt32(4), reader.GetDecimal(5)));
        }
        return result;
    }

    public async Task<IReadOnlyList<SalespersonDto>> SearchSalespeopleAsync(string? query, int limit = 100, CancellationToken cancellationToken = default)
    {
        const string sql = """
            select code, name
            from sales_person
            where is_active
              and (@query = '' or code ilike '%' || @query || '%' or name ilike '%' || @query || '%')
            order by code
            limit @limit;
            """;

        await using var command = database.DataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("query", query?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 200));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<SalespersonDto>();
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new SalespersonDto(reader.GetString(0), reader.GetString(1)));
        return result;
    }

    public async Task<IReadOnlyList<SalesItemDto>> SearchItemsAsync(string? query, string? warehouseCode, int limit = 100, CancellationToken cancellationToken = default, string itemType = "GOODS")
    {
        const string sql = """
            select i.id, i.code, i.name, i.unit, i.retail_price,
                   @warehouse, coalesce(b.quantity, 0), i.item_type
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
        var result = new List<SalesItemDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new SalesItemDto(
                reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetDecimal(4), reader.GetString(5), reader.GetDecimal(6), reader.GetString(7)));
        }
        return result;
    }

    public async Task<IReadOnlyList<InventoryBalanceDto>> GetInventoryBalancesAsync(string? query, string? warehouseCode, CancellationToken cancellationToken = default)
    {
        const string sql = """
            select i.code, i.name, i.unit, b.warehouse_code, b.quantity, b.version
            from inventory_balance b
            join inventory_item i on i.id = b.item_id
            where (@warehouse = '' or b.warehouse_code = @warehouse)
              and (@query = '' or i.code ilike '%' || @query || '%' or i.name ilike '%' || @query || '%')
            order by b.warehouse_code, i.code;
            """;

        await using var command = database.DataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("query", query?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("warehouse", string.IsNullOrWhiteSpace(warehouseCode) ? string.Empty : warehouseCode.Trim().ToUpperInvariant());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<InventoryBalanceDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new InventoryBalanceDto(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetDecimal(4), reader.GetInt64(5)));
        }
        return result;
    }

    public async Task<int> CountSalesAsync(string? query, CancellationToken cancellationToken = default, string documentKind = "GOODS")
    {
        const string sql = """
            select count(*)
            from sale s
            join customer c on c.id = s.customer_id
            where s.document_kind = @documentKind
              and (@query = '' or s.document_number ilike '%' || @query || '%' or c.code ilike '%' || @query || '%' or c.company_name ilike '%' || @query || '%');
            """;

        await using var command = database.DataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("query", query?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("documentKind", NormalizeDocumentKind(documentKind));
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task<string> PreviewNextDocumentNumberAsync(DateOnly documentDate, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        return await PreviewNextDocumentNumberAsync(connection, null, documentDate, cancellationToken);
    }

    public async Task<IReadOnlyList<SaleDto>> ListSalesAsync(string? query, int limit = 100, CancellationToken cancellationToken = default, string documentKind = "GOODS")
    {
        const string sql = """
            select s.id
            from sale s
            join customer c on c.id = s.customer_id
            where s.document_kind = @documentKind
              and (@query = '' or s.document_number ilike '%' || @query || '%' or c.code ilike '%' || @query || '%' or c.company_name ilike '%' || @query || '%')
            order by s.document_date desc, s.created_at desc
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

        var result = new List<SaleDto>(ids.Count);
        foreach (var id in ids)
        {
            var sale = await GetSaleAsync(id, cancellationToken);
            if (sale is not null) result.Add(sale);
        }
        return result;
    }

    public async Task<SaleDto?> GetSaleAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        return await ReadSaleAsync(connection, null, id, cancellationToken);
    }

    public async Task<FifoCostPreviewDto> PreviewFifoCostAsync(FifoCostPreviewRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Lines.Count == 0) return new([], 0);
        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        var rows = new List<FifoCostPreviewLineDto>(request.Lines.Count);
        foreach (var (line, index) in request.Lines.Select((value, index) => (value, index)))
        {
            var code = line.ItemCode.Trim().ToUpperInvariant();
            var warehouse = NormalizeWarehouse(line.WarehouseCode);
            const string sql = "select id, item_type from inventory_item where code=@code and is_active;";
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("code", code);
            Guid itemId;
            string itemType;
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                if (!await reader.ReadAsync(cancellationToken)) throw new SalesValidationException($"Barang/Jasa {code} tidak ditemukan atau tidak aktif.");
                itemId = reader.GetGuid(0);
                itemType = reader.GetString(1);
            }

            decimal unitCost;
            if (itemType == "SERVICE")
            {
                const string formulaSql = "select component_item_id, quantity from inventory_item_formula where item_id=@itemId order by component_item_id;";
                var components = new List<(Guid ItemId, string WarehouseCode, decimal Quantity)>();
                await using (var formula = new NpgsqlCommand(formulaSql, connection))
                {
                    formula.Parameters.AddWithValue("itemId", itemId);
                    await using var reader = await formula.ExecuteReaderAsync(cancellationToken);
                    while (await reader.ReadAsync(cancellationToken))
                        components.Add((reader.GetGuid(0), warehouse, reader.GetDecimal(1) * line.Quantity));
                }
                if (components.Count == 0)
                {
                    unitCost = 0;
                }
                else
                {
                    var costs = await FifoInventoryCosting.PreviewIssueUnitCostsAsync(connection, components, cancellationToken);
                    var total = components.Select((component, componentIndex) => component.Quantity * costs[componentIndex]).Sum();
                    unitCost = line.Quantity == 0 ? 0 : total / line.Quantity;
                }
            }
            else
            {
                var costs = await FifoInventoryCosting.PreviewIssueUnitCostsAsync(connection, [(itemId, warehouse, line.Quantity)], cancellationToken);
                unitCost = costs[0];
            }
            rows.Add(new(index + 1, code, warehouse, line.Quantity, unitCost, line.Quantity * unitCost));
        }
        return new(rows, rows.Sum(x => x.CostAmount));
    }

    public Task<SaleDto> CreateSaleAsync(CreateSaleRequest request, CancellationToken cancellationToken = default) =>
        CreateSaleCoreAsync(request, "STANDARD", 0m, 0m, cancellationToken);

    public async Task<GunggunganSaleResultDto> CreateGunggunganSaleAsync(
        CreateGunggunganSaleRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.CashReceived < 0)
            throw new SalesValidationException("Jumlah pembayaran tunai tidak boleh negatif.");

        var saleRequest = new CreateSaleRequest(
            request.DocumentDate,
            request.DocumentNumber,
            "GUNGGUNGAN",
            "CASH",
            request.CashAccountCode,
            null,
            null,
            request.WarehouseCode,
            0m,
            0m,
            0m,
            0m,
            request.Notes,
            request.UserName,
            request.ComputerName,
            request.Lines);

        ValidateRequest(saleRequest);
        var total = request.Lines.Sum(line =>
            Math.Round(line.Quantity * line.UnitPrice * (1m - line.DiscountPercent / 100m), 2));
        if (request.CashReceived < total)
            throw new SalesValidationException(
                $"Pembayaran kurang. Total {total.ToString("N2", CultureInfo.GetCultureInfo("id-ID"))}, " +
                $"diterima {request.CashReceived.ToString("N2", CultureInfo.GetCultureInfo("id-ID"))}.");

        var change = request.CashReceived - total;
        var sale = await CreateSaleCoreAsync(saleRequest, "GUNGGUNGAN", request.CashReceived, change, cancellationToken);
        return new GunggunganSaleResultDto(sale, request.CashReceived, change);
    }

    public async Task<GunggunganSaleReportDto> GetGunggunganReportAsync(
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default)
    {
        if (from == default || to == default || from > to)
            throw new SalesValidationException("Rentang tanggal Penjualan Gunggungan tidak valid.");

        const string sql = """
            select s.id, s.document_date, s.document_number, s.warehouse_code,
                   coalesce(s.cash_account_code, ''), coalesce(ca.name, ''),
                   i.code, i.name, sl.quantity, sl.unit, sl.unit_price, sl.line_total, s.status
            from sale s
            join sale_line sl on sl.sale_id = s.id
            join inventory_item i on i.id = sl.item_id
            left join cash_account ca on ca.code = s.cash_account_code
            where s.sale_channel = 'GUNGGUNGAN'
              and s.document_date between @from and @to
            order by s.document_date, s.created_at, sl.line_number;
            """;

        await using var command = database.DataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("from", from);
        command.Parameters.AddWithValue("to", to);
        var rows = new List<GunggunganSaleReportRowDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new GunggunganSaleReportRowDto(
                reader.GetGuid(0), reader.GetFieldValue<DateOnly>(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7),
                reader.GetDecimal(8), reader.GetString(9), reader.GetDecimal(10), reader.GetDecimal(11), reader.GetString(12)));
        }

        var activeRows = rows.Where(row => row.Status == "POSTED").ToArray();
        return new GunggunganSaleReportDto(
            from,
            to,
            activeRows.Select(row => row.SaleId).Distinct().Count(),
            activeRows.Sum(row => row.Quantity),
            activeRows.Sum(row => row.LineTotal),
            rows);
    }

    public async Task<GunggunganSaleResultDto?> GetGunggunganSaleAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        const string sql = """
            select cash_received, cash_change
            from sale
            where id = @id and sale_channel = 'GUNGGUNGAN';
            """;
        decimal cashReceived;
        decimal cashChange;
        await using (var command = new NpgsqlCommand(sql, connection))
        {
            command.Parameters.AddWithValue("id", id);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return null;
            cashReceived = reader.GetDecimal(0);
            cashChange = reader.GetDecimal(1);
        }

        var sale = await ReadSaleAsync(connection, null, id, cancellationToken);
        return sale is null ? null : new GunggunganSaleResultDto(sale, cashReceived, cashChange);
    }

    private async Task<SaleDto> CreateSaleCoreAsync(
        CreateSaleRequest request,
        string saleChannel,
        decimal cashReceived,
        decimal cashChange,
        CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        try
        {
            var customer = await GetCustomerForUpdateAsync(connection, transaction, request.CustomerCode, cancellationToken)
                ?? throw new SalesValidationException($"Customer {request.CustomerCode} tidak ditemukan atau tidak aktif.");

            var transactionType = NormalizeTransactionType(request.TransactionType);
            var documentKind = NormalizeDocumentKind(request.DocumentKind);
            var warehouse = NormalizeWarehouse(request.WarehouseCode);
            var cashAccountCode = transactionType == "CASH" ? NormalizeCashAccountCode(request.CashAccountCode) : null;
            if (transactionType == "CASH" && cashAccountCode is null)
                throw new SalesValidationException("Kas wajib dipilih untuk transaksi tunai.");
            if (cashAccountCode is not null && !await CashAccountExistsAsync(connection, transaction, cashAccountCode, cancellationToken))
                throw new SalesValidationException($"Kas {cashAccountCode} tidak ditemukan atau tidak aktif.");
            var salesCode = NormalizeSalesCode(request.SalesCode);
            if (salesCode is not null && !await SalespersonExistsAsync(connection, transaction, salesCode, cancellationToken))
                throw new SalesValidationException($"Sales {salesCode} tidak ditemukan atau tidak aktif.");
            var requestedDocumentNumber = NormalizeDocumentNumber(request.DocumentNumber);
            var documentNumber = requestedDocumentNumber ?? await NextDocumentNumberAsync(connection, transaction, request.DocumentDate, cancellationToken);
            if (requestedDocumentNumber is not null && await DocumentNumberExistsAsync(connection, transaction, documentNumber, request.DocumentDate.Year, cancellationToken))
                throw new SalesConflictException($"No.Nota {documentNumber} sudah dipakai.");

            var normalizedLines = request.Lines.Select((line, index) => new RequestedLine(
                    index + 1,
                    line.ItemCode.Trim().ToUpperInvariant(),
                    NormalizeWarehouse(line.WarehouseCode ?? warehouse),
                    line.Quantity,
                    line.UnitPrice,
                    line.DiscountPercent))
                .ToArray();

            var saleId = Guid.NewGuid();
            var preparedLines = new List<PreparedSaleLine>(normalizedLines.Length);
            var preparedMaterials = new List<PreparedServiceMaterial>();
            var stockRequests = new List<StockRequest>();
            var lockedStockItems = new Dictionary<(string ItemCode, string WarehouseCode), LockedItem>();

            if (documentKind == "GOODS")
            {
                stockRequests.AddRange(normalizedLines
                    .GroupBy(x => new { x.ItemCode, x.WarehouseCode })
                    .Select(x => new StockRequest(x.Key.ItemCode, x.Key.WarehouseCode, x.Sum(y => y.Quantity)))
                    .OrderBy(x => x.ItemCode, StringComparer.Ordinal)
                    .ThenBy(x => x.WarehouseCode, StringComparer.Ordinal));
                foreach (var stockRequest in stockRequests)
                {
                    var item = await LockItemStockAsync(connection, transaction, stockRequest.ItemCode, stockRequest.WarehouseCode, cancellationToken)
                        ?? throw new SalesValidationException($"Barang {stockRequest.ItemCode} tidak ditemukan atau tidak aktif.");
                    if (item.AvailableStock < stockRequest.Quantity)
                        throw new StockUnavailableException(item.Code, stockRequest.WarehouseCode, stockRequest.Quantity, item.AvailableStock);
                    lockedStockItems.Add((stockRequest.ItemCode, stockRequest.WarehouseCode), item);
                }
                foreach (var line in normalizedLines)
                {
                    var item = lockedStockItems[(line.ItemCode, line.WarehouseCode)];
                    var lineId = Guid.NewGuid();
                    var fifo = await FifoInventoryCosting.IssueAsync(connection, transaction, item.Id, line.WarehouseCode,
                        "SALE", saleId, lineId, line.Quantity, cancellationToken);
                    preparedLines.Add(new PreparedSaleLine(lineId, line, item, fifo.UnitCost));
                }
            }
            else
            {
                var serviceItems = new Dictionary<string, LockedItem>(StringComparer.OrdinalIgnoreCase);
                foreach (var itemCode in normalizedLines.Select(x => x.ItemCode).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.Ordinal))
                {
                    var item = await LockServiceItemAsync(connection, transaction, itemCode, cancellationToken)
                        ?? throw new SalesValidationException($"Jasa {itemCode} tidak ditemukan atau tidak aktif.");
                    serviceItems.Add(itemCode, item);
                }

                var requirements = new List<ServiceMaterialRequirement>();
                foreach (var line in normalizedLines)
                {
                    var lineId = Guid.NewGuid();
                    var item = serviceItems[line.ItemCode];
                    var components = await ReadServiceComponentsAsync(connection, transaction, item.Id, cancellationToken);
                    requirements.AddRange(components.Select(component => new ServiceMaterialRequirement(
                        lineId, component.ItemCode, line.WarehouseCode, component.Quantity * line.Quantity)));
                    preparedLines.Add(new PreparedSaleLine(lineId, line, item, 0));
                }

                stockRequests.AddRange(requirements
                    .GroupBy(x => new { x.ItemCode, x.WarehouseCode })
                    .Select(x => new StockRequest(x.Key.ItemCode, x.Key.WarehouseCode, x.Sum(y => y.Quantity)))
                    .OrderBy(x => x.ItemCode, StringComparer.Ordinal)
                    .ThenBy(x => x.WarehouseCode, StringComparer.Ordinal));
                foreach (var stockRequest in stockRequests)
                {
                    var item = await LockItemStockAsync(connection, transaction, stockRequest.ItemCode, stockRequest.WarehouseCode, cancellationToken)
                        ?? throw new SalesValidationException($"Bahan penunjang {stockRequest.ItemCode} tidak ditemukan atau tidak aktif.");
                    if (item.AvailableStock < stockRequest.Quantity)
                        throw new StockUnavailableException(item.Code, stockRequest.WarehouseCode, stockRequest.Quantity, item.AvailableStock);
                    lockedStockItems.Add((stockRequest.ItemCode, stockRequest.WarehouseCode), item);
                }

                foreach (var requirement in requirements.OrderBy(x => x.SaleLineId).ThenBy(x => x.ItemCode, StringComparer.Ordinal))
                {
                    var item = lockedStockItems[(requirement.ItemCode, requirement.WarehouseCode)];
                    var fifo = await FifoInventoryCosting.IssueAsync(connection, transaction, item.Id, requirement.WarehouseCode,
                        "SERVICE_SALE", saleId, requirement.SaleLineId, requirement.Quantity, cancellationToken);
                    preparedMaterials.Add(new PreparedServiceMaterial(requirement.SaleLineId, item, requirement.WarehouseCode,
                        requirement.Quantity, fifo.UnitCost));
                }

                for (var index = 0; index < preparedLines.Count; index++)
                {
                    var prepared = preparedLines[index];
                    var totalCost = preparedMaterials.Where(x => x.SaleLineId == prepared.LineId).Sum(x => x.CostAmount);
                    preparedLines[index] = prepared with { FifoUnitCost = prepared.Request.Quantity == 0 ? 0 : totalCost / prepared.Request.Quantity };
                }
            }

            var domainSale = new Sale
            {
                Number = documentNumber,
                DocumentDate = request.DocumentDate,
                CustomerCode = customer.Code,
                WarehouseCode = warehouse,
                SalesCode = salesCode,
                IsCredit = transactionType == "CREDIT",
                DueDate = transactionType == "CREDIT" ? request.DueDate : null,
                DiscountPercent = request.DiscountPercent,
                DiscountAmount = request.DiscountAmount,
                TaxPercent = request.TaxPercent,
                DownPayment = transactionType == "CREDIT" ? request.DownPayment : 0m,
                Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim(),
                CreatedBy = string.IsNullOrWhiteSpace(request.UserName) ? "unknown" : request.UserName.Trim()
            };

            foreach (var prepared in preparedLines)
            {
                var line = prepared.Request;
                var item = prepared.Item;
                domainSale.AddLine(new SaleLine(item.Code, item.Name, line.Quantity, item.Unit, line.UnitPrice, line.DiscountPercent, prepared.FifoUnitCost));
            }
            domainSale.Post();

            await InsertSaleAsync(connection, transaction, saleId, customer.Id, domainSale, transactionType, cashAccountCode, documentKind,
                saleChannel, cashReceived, cashChange, cancellationToken);
            await InsertSaleLinesAsync(connection, transaction, saleId, preparedLines, cancellationToken);
            await InsertServiceMaterialUsageAsync(connection, transaction, saleId, preparedMaterials, cancellationToken);
            await InventoryLedgerPosting.PostSaleAsync(
                connection, transaction, saleId, documentNumber, request.DocumentDate,
                transactionType, cashAccountCode, domainSale.NetBeforeTax, domainSale.TaxAmount,
                domainSale.GrandTotal, domainSale.DownPayment, domainSale.BalanceDue,
                domainSale.CostTotal, documentKind, request.UserName, request.ComputerName, cancellationToken);
            await UpdateDocumentSequenceAtLeastAsync(connection, transaction, request.DocumentDate, documentNumber, cancellationToken);

            foreach (var stockRequest in stockRequests)
            {
                var item = lockedStockItems[(stockRequest.ItemCode, stockRequest.WarehouseCode)];
                var costAmount = documentKind == "SERVICE"
                    ? preparedMaterials.Where(x => x.Item.Code == stockRequest.ItemCode && x.WarehouseCode == stockRequest.WarehouseCode).Sum(x => x.CostAmount)
                    : preparedLines.Where(x => x.Request.ItemCode == stockRequest.ItemCode && x.Request.WarehouseCode == stockRequest.WarehouseCode).Sum(x => x.Request.Quantity * x.FifoUnitCost);
                await IssueStockAsync(connection, transaction, saleId, documentNumber, request.DocumentDate, item, stockRequest,
                    stockRequest.Quantity == 0 ? 0 : costAmount / stockRequest.Quantity, documentKind == "SERVICE" ? "SERVICE_SALE" : "SALE", cancellationToken);
            }

            await InsertAuditAsync(connection, transaction, "CREATE_POST", saleId, request.UserName, request.ComputerName, null, request, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return (await GetSaleAsync(saleId, cancellationToken))!;
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw new SalesConflictException("No.Nota sudah dipakai. Silakan ganti nomor nota.");
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<SaleDto> CancelSaleAsync(Guid id, CancelSaleRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Reason))
            throw new SalesValidationException("Alasan pembatalan wajib diisi.");

        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        try
        {
            const string headerSql = "select document_number, status, document_kind from sale where id = @id for update;";
            string documentNumber;
            string status;
            string documentKind;
            await using (var headerCommand = new NpgsqlCommand(headerSql, connection, transaction))
            {
                headerCommand.Parameters.AddWithValue("id", id);
                await using var reader = await headerCommand.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                    throw new SalesValidationException("Nota penjualan tidak ditemukan.");
                documentNumber = reader.GetString(0);
                status = reader.GetString(1);
                documentKind = reader.GetString(2);
            }

            if (!string.Equals(status, "POSTED", StringComparison.OrdinalIgnoreCase))
                throw new SalesConflictException(status == "CANCELLED" ? "Nota penjualan sudah dibatalkan." : "Nota belum diposting dan tidak dapat dibatalkan.");

            const string usageSql = """
                select
                  coalesce((select sum(l.amount) from customer_receipt_line l join customer_receipt h on h.id=l.customer_receipt_id
                            where l.sale_id=@id and l.source_type='SALE' and h.status='POSTED'),0)
                  + coalesce((select sum(amount) from partner_balance_document
                              where target_source_type='SALE' and target_source_id=@id and direction='DECREASE' and status='POSTED'),0);
                """;
            await using (var usageCommand = new NpgsqlCommand(usageSql, connection, transaction))
            {
                usageCommand.Parameters.AddWithValue("id", id);
                var usage = Convert.ToDecimal(await usageCommand.ExecuteScalarAsync(cancellationToken));
                if (usage > 0)
                    throw new SalesConflictException($"Nota sudah dipakai dalam penerimaan/koreksi piutang sebesar {usage:N2}. Batalkan dokumen turunannya terlebih dahulu.");
            }

            var lineSql = documentKind == "SERVICE" ? """
                select u.component_item_id, u.warehouse_code, sum(u.quantity),
                       case when sum(u.quantity)=0 then 0 else sum(u.cost_amount)/sum(u.quantity) end,
                       i.code
                from service_sale_material_usage u
                join inventory_item i on i.id = u.component_item_id
                where u.sale_id = @id
                group by u.component_item_id, u.warehouse_code, i.code
                order by u.component_item_id, u.warehouse_code;
                """ : """
                select sl.item_id, sl.warehouse_code, sum(sl.quantity),
                       case when sum(sl.quantity)=0 then 0 else sum(sl.cost_amount)/sum(sl.quantity) end,
                       i.code
                from sale_line sl
                join inventory_item i on i.id = sl.item_id
                where sl.sale_id = @id
                group by sl.item_id, sl.warehouse_code, i.code
                order by sl.item_id, sl.warehouse_code;
                """;
            var lines = new List<CancelStockLine>();
            await using (var lineCommand = new NpgsqlCommand(lineSql, connection, transaction))
            {
                lineCommand.Parameters.AddWithValue("id", id);
                await using var reader = await lineCommand.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    lines.Add(new CancelStockLine(reader.GetGuid(0), reader.GetString(1), reader.GetDecimal(2), reader.GetDecimal(3), reader.GetString(4)));
                }
            }

            foreach (var line in lines)
            {
                const string lockSql = "select quantity from inventory_balance where item_id = @itemId and warehouse_code = @warehouse for update;";
                await using var lockCommand = new NpgsqlCommand(lockSql, connection, transaction);
                lockCommand.Parameters.AddWithValue("itemId", line.ItemId);
                lockCommand.Parameters.AddWithValue("warehouse", line.WarehouseCode);
                if (await lockCommand.ExecuteScalarAsync(cancellationToken) is null)
                    throw new SalesConflictException($"Saldo stok {line.ItemCode} tidak ditemukan saat pembatalan.");
            }

            var cancelOperationId = Guid.NewGuid();
            foreach (var line in lines)
            {
                const string restoreSql = """
                    update inventory_balance
                    set quantity = quantity + @quantity, version = version + 1, updated_at = now()
                    where item_id = @itemId and warehouse_code = @warehouse;

                    insert into inventory_movement(occurred_at, item_id, warehouse_code, source_type, source_id, source_number, quantity_in, quantity_out, unit_cost)
                    values (now(), @itemId, @warehouse, 'SALE_CANCEL', @saleId, @documentNumber, @quantity, 0, @unitCost);
                    """;
                await using var restoreCommand = new NpgsqlCommand(restoreSql, connection, transaction);
                restoreCommand.Parameters.AddWithValue("quantity", line.Quantity);
                restoreCommand.Parameters.AddWithValue("itemId", line.ItemId);
                restoreCommand.Parameters.AddWithValue("warehouse", line.WarehouseCode);
                restoreCommand.Parameters.AddWithValue("saleId", cancelOperationId);
                restoreCommand.Parameters.AddWithValue("documentNumber", documentNumber);
                restoreCommand.Parameters.AddWithValue("unitCost", line.UnitCost);
                await restoreCommand.ExecuteNonQueryAsync(cancellationToken);
                await FifoInventoryCosting.ReceiveAsync(connection, transaction, line.ItemId, line.WarehouseCode,
                    "SALE_CANCEL", cancelOperationId, null, documentNumber, DateOnly.FromDateTime(DateTime.UtcNow), line.Quantity, line.UnitCost, cancellationToken);
            }

            await InventoryLedgerPosting.CancelAsync(connection, transaction, id, "SALES",
                request.UserName, request.Reason, cancellationToken);

            const string cancelSql = """
                update sale
                set status = 'CANCELLED', cancelled_at = now(), cancelled_by_name = @userName,
                    cancellation_reason = @reason, updated_at = now(), version = version + 1
                where id = @id;
                """;
            await using (var cancelCommand = new NpgsqlCommand(cancelSql, connection, transaction))
            {
                cancelCommand.Parameters.AddWithValue("id", id);
                cancelCommand.Parameters.AddWithValue("userName", string.IsNullOrWhiteSpace(request.UserName) ? "unknown" : request.UserName.Trim());
                cancelCommand.Parameters.AddWithValue("reason", request.Reason.Trim());
                await cancelCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await InsertAuditAsync(connection, transaction, "CANCEL", id, request.UserName, request.ComputerName, new { status = "POSTED" }, new { status = "CANCELLED", reason = request.Reason.Trim() }, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return (await GetSaleAsync(id, cancellationToken))!;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<SaleDto> RestoreSaleAsync(Guid id, RestoreSaleRequest request, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        try
        {
            const string headerSql = """
                select document_number, document_date, status, transaction_type, cash_account_code,
                       subtotal, discount_amount, tax_amount, grand_total, down_payment, balance_due, cost_total, document_kind
                from sale
                where id = @id
                for update;
                """;
            string documentNumber;
            DateOnly documentDate;
            string status;
            string transactionType;
            string? cashAccountCode;
            decimal subtotal;
            decimal discountAmount;
            decimal taxAmount;
            decimal grandTotal;
            decimal downPayment;
            decimal balanceDue;
            decimal costTotal;
            string documentKind;
            await using (var headerCommand = new NpgsqlCommand(headerSql, connection, transaction))
            {
                headerCommand.Parameters.AddWithValue("id", id);
                await using var reader = await headerCommand.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                    throw new SalesValidationException("Nota penjualan tidak ditemukan.");
                documentNumber = reader.GetString(0);
                documentDate = reader.GetFieldValue<DateOnly>(1);
                status = reader.GetString(2);
                transactionType = reader.GetString(3);
                cashAccountCode = reader.IsDBNull(4) ? null : reader.GetString(4);
                subtotal = reader.GetDecimal(5);
                discountAmount = reader.GetDecimal(6);
                taxAmount = reader.GetDecimal(7);
                grandTotal = reader.GetDecimal(8);
                downPayment = reader.GetDecimal(9);
                balanceDue = reader.GetDecimal(10);
                costTotal = reader.GetDecimal(11);
                documentKind = reader.GetString(12);
            }

            if (string.Equals(status, "POSTED", StringComparison.OrdinalIgnoreCase))
                throw new SalesConflictException("Nota penjualan sudah aktif.");
            if (!string.Equals(status, "CANCELLED", StringComparison.OrdinalIgnoreCase))
                throw new SalesConflictException("Nota penjualan belum dapat dibatalkan hapus.");

            var lineSql = documentKind == "SERVICE" ? """
                select u.component_item_id, u.warehouse_code, sum(u.quantity),
                       case when sum(u.quantity)=0 then 0 else sum(u.cost_amount)/sum(u.quantity) end,
                       i.code
                from service_sale_material_usage u
                join inventory_item i on i.id = u.component_item_id
                where u.sale_id = @id
                group by u.component_item_id, u.warehouse_code, i.code
                order by u.component_item_id, u.warehouse_code;
                """ : """
                select sl.item_id, sl.warehouse_code, sum(sl.quantity),
                       case when sum(sl.quantity)=0 then 0 else sum(sl.cost_amount)/sum(sl.quantity) end,
                       i.code
                from sale_line sl
                join inventory_item i on i.id = sl.item_id
                where sl.sale_id = @id
                group by sl.item_id, sl.warehouse_code, i.code
                order by sl.item_id, sl.warehouse_code;
                """;
            var lines = new List<CancelStockLine>();
            await using (var lineCommand = new NpgsqlCommand(lineSql, connection, transaction))
            {
                lineCommand.Parameters.AddWithValue("id", id);
                await using var reader = await lineCommand.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                    lines.Add(new CancelStockLine(reader.GetGuid(0), reader.GetString(1), reader.GetDecimal(2), reader.GetDecimal(3), reader.GetString(4)));
            }

            var restoreOperationId = Guid.NewGuid();
            foreach (var line in lines)
            {
                var item = await LockItemStockAsync(connection, transaction, line.ItemCode, line.WarehouseCode, cancellationToken)
                    ?? throw new SalesValidationException($"Barang {line.ItemCode} tidak ditemukan atau tidak aktif.");
                if (item.AvailableStock < line.Quantity)
                    throw new StockUnavailableException(item.Code, line.WarehouseCode, line.Quantity, item.AvailableStock);
                var fifo = await FifoInventoryCosting.IssueAsync(connection, transaction, item.Id, line.WarehouseCode,
                    "SALE_RESTORE", restoreOperationId, null, line.Quantity, cancellationToken);

                const string restoreStockSql = """
                    update inventory_balance
                    set quantity = quantity - @quantity, version = version + 1, updated_at = now()
                    where item_id = @itemId and warehouse_code = @warehouse;

                    insert into inventory_movement(occurred_at, item_id, warehouse_code, source_type, source_id, source_number, quantity_in, quantity_out, unit_cost)
                    values (@occurredAt, @itemId, @warehouse, 'SALE_RESTORE', @saleId, @documentNumber, 0, @quantity, @unitCost);
                    """;
                await using var stockCommand = new NpgsqlCommand(restoreStockSql, connection, transaction);
                stockCommand.Parameters.AddWithValue("quantity", line.Quantity);
                stockCommand.Parameters.AddWithValue("itemId", item.Id);
                stockCommand.Parameters.AddWithValue("warehouse", line.WarehouseCode);
                stockCommand.Parameters.AddWithValue("saleId", restoreOperationId);
                stockCommand.Parameters.AddWithValue("documentNumber", documentNumber);
                stockCommand.Parameters.AddWithValue("unitCost", fifo.UnitCost);
                stockCommand.Parameters.AddWithValue("occurredAt", new DateTimeOffset(documentDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)));
                await stockCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await InventoryLedgerPosting.RestoreAsync(connection, transaction, id, "SALES", cancellationToken);

            const string restoreSql = """
                update sale
                set status = 'POSTED', cancelled_at = null, cancelled_by_name = null,
                    cancellation_reason = null, updated_at = now(), version = version + 1
                where id = @id;
                """;
            await using (var restoreCommand = new NpgsqlCommand(restoreSql, connection, transaction))
            {
                restoreCommand.Parameters.AddWithValue("id", id);
                await restoreCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await InsertAuditAsync(connection, transaction, "RESTORE", id, request.UserName, request.ComputerName, new { status = "CANCELLED" }, new { status = "POSTED" }, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return (await GetSaleAsync(id, cancellationToken))!;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static void ValidateRequest(CreateSaleRequest request)
    {
        if (request.DocumentDate == default) throw new SalesValidationException("Tanggal nota wajib diisi.");
        if (!string.IsNullOrWhiteSpace(request.DocumentNumber) && request.DocumentNumber.Trim().Length > 30)
            throw new SalesValidationException("No.Nota maksimal 30 karakter.");
        if (string.IsNullOrWhiteSpace(request.CustomerCode)) throw new SalesValidationException("Kode customer wajib diisi.");
        if (request.Lines is null || request.Lines.Count == 0) throw new SalesValidationException("Minimal satu barang harus diisi.");
        if (request.Lines.Any(x => string.IsNullOrWhiteSpace(x.ItemCode) || x.Quantity <= 0 || x.UnitPrice < 0 || x.DiscountPercent is < 0 or > 100))
            throw new SalesValidationException("Terdapat rincian barang yang tidak valid.");
        if (request.DiscountPercent is < 0 or > 100 || request.DiscountAmount < 0 || request.TaxPercent is < 0 or > 100 || request.DownPayment < 0)
            throw new SalesValidationException("Diskon, PPN, atau uang muka tidak valid.");
        if (!string.IsNullOrWhiteSpace(request.CashAccountCode) && request.CashAccountCode.Trim().Length > 20)
            throw new SalesValidationException("Kode kas maksimal 20 karakter.");
        if (!string.IsNullOrWhiteSpace(request.SalesCode) && request.SalesCode.Trim().Length > 20)
            throw new SalesValidationException("Kode sales maksimal 20 karakter.");

        var type = NormalizeTransactionType(request.TransactionType);
        if (type == "CASH" && string.IsNullOrWhiteSpace(request.CashAccountCode)) throw new SalesValidationException("Kas wajib dipilih untuk transaksi tunai.");
        if (type == "CREDIT" && request.DueDate is null) throw new SalesValidationException("Tanggal jatuh tempo wajib untuk penjualan kredit.");
        if (type == "CREDIT" && request.DueDate < request.DocumentDate) throw new SalesValidationException("Tanggal jatuh tempo tidak boleh lebih awal dari tanggal nota.");
    }

    private static string NormalizeTransactionType(string value) => value.Trim().ToUpperInvariant() switch
    {
        "T" or "TUNAI" or "CASH" => "CASH",
        "K" or "KREDIT" or "CREDIT" => "CREDIT",
        _ => throw new SalesValidationException("Jenis transaksi harus Tunai atau Kredit.")
    };

    private static string NormalizeDocumentKind(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "SERVICE" or "JASA" => "SERVICE",
        "GOODS" or "BARANG" or null or "" => "GOODS",
        _ => throw new SalesValidationException("Jenis dokumen harus GOODS/BARANG atau SERVICE/JASA.")
    };

    private static string NormalizeWarehouse(string? value) => string.IsNullOrWhiteSpace(value) ? "UTAMA" : value.Trim().ToUpperInvariant();
    private static string? NormalizeCashAccountCode(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();
    private static string? NormalizeSalesCode(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();
    private static string? NormalizeDocumentNumber(string? value)
    {
        var number = value?.Trim();
        if (string.IsNullOrWhiteSpace(number) || number.Equals("OTOMATIS", StringComparison.OrdinalIgnoreCase)) return null;
        return number.ToUpperInvariant();
    }

    private static async Task<CustomerLock?> GetCustomerForUpdateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string customerCode, CancellationToken cancellationToken)
    {
        const string sql = "select id, code, company_name from customer where code = @code and is_active for share;";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("code", customerCode.Trim().ToUpperInvariant());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new CustomerLock(reader.GetGuid(0), reader.GetString(1), reader.GetString(2))
            : null;
    }

    private static async Task<bool> CashAccountExistsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string cashAccountCode, CancellationToken cancellationToken)
    {
        const string sql = "select exists(select 1 from cash_account where code = @code and is_active);";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("code", cashAccountCode);
        return Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<bool> SalespersonExistsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string salesCode, CancellationToken cancellationToken)
    {
        const string sql = "select exists(select 1 from sales_person where code = @code and is_active);";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("code", salesCode);
        return Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<string> NextDocumentNumberAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, DateOnly documentDate, CancellationToken cancellationToken)
    {
        const string sql = """
            insert into document_sequence(document_type, period, last_number)
            values ('SALE', @period, 1)
            on conflict (document_type, period)
            do update set last_number = document_sequence.last_number + 1
            returning last_number;
            """;
        var period = documentDate.ToString("yyyyMM", System.Globalization.CultureInfo.InvariantCulture);
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("period", period);
        var sequence = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
        var configuredPrefix = await ConfigurationRepository.GetDocumentPrefixAsync(connection, transaction, "SALE", "PJL", cancellationToken);
        return $"{configuredPrefix}{documentDate:yyMM}{sequence:000}";
    }

    private static async Task<string> PreviewNextDocumentNumberAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, DateOnly documentDate, CancellationToken cancellationToken)
    {
        const string sql = """
            select greatest(
                coalesce((select last_number from document_sequence where document_type = 'SALE' and period = @period), 0),
                coalesce((
                    select max(nullif(regexp_replace(substring(document_number from char_length(@prefix) + 1), '\D', '', 'g'), '')::integer)
                    from sale
                    where document_year = @year and document_number like @prefix || '%'
                ), 0)
            ) + 1;
            """;
        var configuredPrefix = await ConfigurationRepository.GetDocumentPrefixAsync(connection, transaction, "SALE", "PJL", cancellationToken);
        var prefix = $"{configuredPrefix}{documentDate:yyMM}";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("period", documentDate.ToString("yyyyMM", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("year", (short)documentDate.Year);
        command.Parameters.AddWithValue("prefix", prefix);
        var sequence = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
        return $"{prefix}{sequence:000}";
    }

    private static async Task<bool> DocumentNumberExistsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string documentNumber, int documentYear, CancellationToken cancellationToken)
    {
        const string sql = "select exists(select 1 from sale where document_number = @number and document_year = @year);";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("number", documentNumber);
        command.Parameters.AddWithValue("year", (short)documentYear);
        return Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task UpdateDocumentSequenceAtLeastAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, DateOnly documentDate, string documentNumber, CancellationToken cancellationToken)
    {
        var configuredPrefix = await ConfigurationRepository.GetDocumentPrefixAsync(connection, transaction, "SALE", "PJL", cancellationToken);
        var prefix = $"{configuredPrefix}{documentDate:yyMM}";
        if (!documentNumber.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return;
        if (!int.TryParse(documentNumber[prefix.Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sequence)) return;

        const string sql = """
            insert into document_sequence(document_type, period, last_number)
            values ('SALE', @period, @lastNumber)
            on conflict (document_type, period)
            do update set last_number = greatest(document_sequence.last_number, excluded.last_number);
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("period", documentDate.ToString("yyyyMM", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("lastNumber", sequence);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<LockedItem?> LockItemStockAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string itemCode, string warehouseCode, CancellationToken cancellationToken)
    {
        const string ensureSql = """
            insert into inventory_balance(item_id, warehouse_code, quantity)
            select i.id, @warehouse, 0
            from inventory_item i
            where i.code = @code and i.item_type = 'GOODS' and i.is_active
            on conflict (item_id, warehouse_code) do nothing;
            """;
        await using (var ensure = new NpgsqlCommand(ensureSql, connection, transaction))
        {
            ensure.Parameters.AddWithValue("warehouse", warehouseCode);
            ensure.Parameters.AddWithValue("code", itemCode);
            await ensure.ExecuteNonQueryAsync(cancellationToken);
        }

        const string lockSql = """
            select i.id, i.code, i.name, i.unit, i.average_cost, b.quantity
            from inventory_item i
            join inventory_balance b on b.item_id = i.id and b.warehouse_code = @warehouse
            where i.code = @code and i.item_type = 'GOODS' and i.is_active
            for update of b;
            """;
        await using var command = new NpgsqlCommand(lockSql, connection, transaction);
        command.Parameters.AddWithValue("warehouse", warehouseCode);
        command.Parameters.AddWithValue("code", itemCode);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new LockedItem(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetDecimal(4), reader.GetDecimal(5))
            : null;
    }

    private static async Task<LockedItem?> LockServiceItemAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string itemCode, CancellationToken cancellationToken)
    {
        const string sql = "select id, code, name, unit, average_cost from inventory_item where code=@code and item_type='SERVICE' and is_active for update;";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("code", itemCode);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new LockedItem(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetDecimal(4), 0)
            : null;
    }

    private static async Task<IReadOnlyList<ServiceComponent>> ReadServiceComponentsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid serviceItemId, CancellationToken cancellationToken)
    {
        const string sql = """
            select component.code, formula.quantity
            from inventory_item_formula formula
            join inventory_item component on component.id = formula.component_item_id
            where formula.item_id = @serviceItemId
              and component.item_type = 'GOODS'
              and component.is_active
            order by component.code;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("serviceItemId", serviceItemId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<ServiceComponent>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(new(reader.GetString(0), reader.GetDecimal(1)));
        return result;
    }

    private static async Task InsertSaleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid saleId,
        Guid customerId,
        Sale sale,
        string transactionType,
        string? cashAccountCode,
        string documentKind,
        string saleChannel,
        decimal cashReceived,
        decimal cashChange,
        CancellationToken cancellationToken)
    {
        const string sql = """
            insert into sale(
                id, document_number, document_year, document_date, customer_id, transaction_type, cash_account_code, due_date,
                sales_code, warehouse_code, subtotal, discount_percent, discount_amount, tax_percent, tax_amount,
                grand_total, down_payment, balance_due, cost_total, notes, status, document_kind, sale_channel, cash_received, cash_change)
            values (
                @id, @number, @year, @date, @customerId, @type, @cashAccount, @dueDate,
                @salesCode, @warehouse, @subtotal, @discountPercent, @discountAmount, @taxPercent, @taxAmount,
                @grandTotal, @downPayment, @balanceDue, @costTotal, @notes, 'POSTED', @documentKind, @saleChannel, @cashReceived, @cashChange);
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("id", saleId);
        command.Parameters.AddWithValue("number", sale.Number);
        command.Parameters.AddWithValue("year", (short)sale.DocumentDate.Year);
        command.Parameters.AddWithValue("date", sale.DocumentDate);
        command.Parameters.AddWithValue("customerId", customerId);
        command.Parameters.AddWithValue("type", transactionType);
        command.Parameters.AddWithValue("cashAccount", (object?)cashAccountCode ?? DBNull.Value);
        command.Parameters.AddWithValue("dueDate", (object?)sale.DueDate ?? DBNull.Value);
        command.Parameters.AddWithValue("salesCode", (object?)sale.SalesCode ?? DBNull.Value);
        command.Parameters.AddWithValue("warehouse", sale.WarehouseCode);
        command.Parameters.AddWithValue("subtotal", sale.Subtotal);
        command.Parameters.AddWithValue("discountPercent", sale.DiscountPercent);
        command.Parameters.AddWithValue("discountAmount", sale.CalculatedDiscount);
        command.Parameters.AddWithValue("taxPercent", sale.TaxPercent);
        command.Parameters.AddWithValue("taxAmount", sale.TaxAmount);
        command.Parameters.AddWithValue("grandTotal", sale.GrandTotal);
        command.Parameters.AddWithValue("downPayment", sale.DownPayment);
        command.Parameters.AddWithValue("balanceDue", sale.BalanceDue);
        command.Parameters.AddWithValue("costTotal", sale.CostTotal);
        command.Parameters.AddWithValue("notes", (object?)sale.Notes ?? DBNull.Value);
        command.Parameters.AddWithValue("documentKind", documentKind);
        command.Parameters.AddWithValue("saleChannel", saleChannel);
        command.Parameters.AddWithValue("cashReceived", cashReceived);
        command.Parameters.AddWithValue("cashChange", cashChange);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertSaleLinesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid saleId,
        IReadOnlyList<PreparedSaleLine> lines,
        CancellationToken cancellationToken)
    {
        const string sql = """
            insert into sale_line(
                id, sale_id, line_number, item_id, quantity, unit, unit_price, discount_1, line_total, cost_amount, warehouse_code)
            values (@id, @saleId, @lineNumber, @itemId, @quantity, @unit, @unitPrice, @discount, @lineTotal, @costAmount, @warehouse);
            """;
        foreach (var prepared in lines)
        {
            var line = prepared.Request;
            var item = prepared.Item;
            var domainLine = new SaleLine(item.Code, item.Name, line.Quantity, item.Unit, line.UnitPrice, line.DiscountPercent, prepared.FifoUnitCost);
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("id", prepared.LineId);
            command.Parameters.AddWithValue("saleId", saleId);
            command.Parameters.AddWithValue("lineNumber", line.LineNumber);
            command.Parameters.AddWithValue("itemId", item.Id);
            command.Parameters.AddWithValue("quantity", line.Quantity);
            command.Parameters.AddWithValue("unit", item.Unit);
            command.Parameters.AddWithValue("unitPrice", line.UnitPrice);
            command.Parameters.AddWithValue("discount", line.DiscountPercent);
            command.Parameters.AddWithValue("lineTotal", domainLine.Total);
            command.Parameters.AddWithValue("costAmount", domainLine.CostTotal);
            command.Parameters.AddWithValue("warehouse", line.WarehouseCode);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task InsertServiceMaterialUsageAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid saleId,
        IReadOnlyList<PreparedServiceMaterial> materials,
        CancellationToken cancellationToken)
    {
        const string sql = """
            insert into service_sale_material_usage(
                sale_id, sale_line_id, component_item_id, warehouse_code, quantity, unit_cost, cost_amount)
            values (@saleId, @saleLineId, @itemId, @warehouse, @quantity, @unitCost, @costAmount);
            """;
        foreach (var material in materials)
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("saleId", saleId);
            command.Parameters.AddWithValue("saleLineId", material.SaleLineId);
            command.Parameters.AddWithValue("itemId", material.Item.Id);
            command.Parameters.AddWithValue("warehouse", material.WarehouseCode);
            command.Parameters.AddWithValue("quantity", material.Quantity);
            command.Parameters.AddWithValue("unitCost", material.UnitCost);
            command.Parameters.AddWithValue("costAmount", material.CostAmount);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task IssueStockAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid saleId, string documentNumber,
        DateOnly documentDate, LockedItem item, StockRequest stockRequest, decimal fifoUnitCost, string sourceType, CancellationToken cancellationToken)
    {
        const string sql = """
            update inventory_balance
            set quantity = quantity - @quantity, version = version + 1, updated_at = now()
            where item_id = @itemId and warehouse_code = @warehouse;

            insert into inventory_movement(occurred_at, item_id, warehouse_code, source_type, source_id, source_number, quantity_in, quantity_out, unit_cost)
            values (@occurredAt, @itemId, @warehouse, @sourceType, @saleId, @documentNumber, 0, @quantity, @unitCost);
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("quantity", stockRequest.Quantity);
        command.Parameters.AddWithValue("itemId", item.Id);
        command.Parameters.AddWithValue("warehouse", stockRequest.WarehouseCode);
        command.Parameters.AddWithValue("saleId", saleId);
        command.Parameters.AddWithValue("documentNumber", documentNumber);
        command.Parameters.AddWithValue("sourceType", sourceType);
        command.Parameters.AddWithValue("unitCost", fifoUnitCost);
        command.Parameters.AddWithValue("occurredAt", new DateTimeOffset(documentDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertAuditAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string action, Guid saleId, string? userName, string? computerName, object? before, object? after, CancellationToken cancellationToken)
    {
        const string sql = """
            insert into audit_log(computer_name, module, action, entity_type, entity_id, before_data, after_data)
            values (@computerName, 'PENJUALAN', @action, 'sale', @entityId, @before::jsonb, @after::jsonb);
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("computerName", string.IsNullOrWhiteSpace(computerName) ? DBNull.Value : computerName.Trim());
        command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("entityId", saleId.ToString());
        command.Parameters.AddWithValue("before", before is null ? DBNull.Value : JsonSerializer.Serialize(before));
        command.Parameters.AddWithValue("after", after is null ? DBNull.Value : JsonSerializer.Serialize(new { userName, data = after }));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<SaleDto?> ReadSaleAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid id, CancellationToken cancellationToken)
    {
        const string headerSql = """
            select s.id, s.document_number, s.document_date, c.code, c.company_name, s.transaction_type,
                   s.cash_account_code, ca.name, s.due_date, s.sales_code, s.warehouse_code, s.subtotal, s.discount_amount, s.tax_amount,
                   s.grand_total, s.down_payment, s.balance_due, s.cost_total, s.status, s.notes, s.created_at, s.document_kind
            from sale s
            join customer c on c.id = s.customer_id
            left join cash_account ca on ca.code = s.cash_account_code
            where s.id = @id;
            """;
        SaleHeader? header = null;
        await using (var command = new NpgsqlCommand(headerSql, connection, transaction))
        {
            command.Parameters.AddWithValue("id", id);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                header = new SaleHeader(
                    reader.GetGuid(0), reader.GetString(1), reader.GetFieldValue<DateOnly>(2), reader.GetString(3), reader.GetString(4),
                    reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetFieldValue<DateOnly>(8), reader.IsDBNull(9) ? null : reader.GetString(9),
                    reader.GetString(10), reader.GetDecimal(11), reader.GetDecimal(12), reader.GetDecimal(13), reader.GetDecimal(14), reader.GetDecimal(15),
                    reader.GetDecimal(16), reader.GetDecimal(17), reader.GetString(18), reader.IsDBNull(19) ? null : reader.GetString(19), reader.GetFieldValue<DateTimeOffset>(20), reader.GetString(21));
            }
        }
        if (header is null) return null;

        const string linesSql = """
            select sl.line_number, i.code, i.name, sl.quantity, sl.unit, sl.unit_price, sl.discount_1,
                   sl.line_total, sl.cost_amount, sl.warehouse_code
            from sale_line sl
            join inventory_item i on i.id = sl.item_id
            where sl.sale_id = @id
            order by sl.line_number;
            """;
        var lines = new List<SaleLineDto>();
        await using (var command = new NpgsqlCommand(linesSql, connection, transaction))
        {
            command.Parameters.AddWithValue("id", id);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                lines.Add(new SaleLineDto(
                    reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetDecimal(3), reader.GetString(4),
                    reader.GetDecimal(5), reader.GetDecimal(6), reader.GetDecimal(7), reader.GetDecimal(8), reader.GetString(9)));
            }
        }

        return new SaleDto(
            header.Id, header.DocumentNumber, header.DocumentDate, header.CustomerCode, header.CustomerName,
            header.TransactionType, header.CashAccountCode, header.CashAccountName, header.DueDate, header.SalesCode, header.WarehouseCode, header.Subtotal,
            header.DiscountAmount, header.TaxAmount, header.GrandTotal, header.DownPayment, header.BalanceDue,
            header.CostTotal, header.Status, header.Notes, header.CreatedAt, lines, header.DocumentKind);
    }

    private sealed record CustomerLock(Guid Id, string Code, string Name);
    private sealed record RequestedLine(int LineNumber, string ItemCode, string WarehouseCode, decimal Quantity, decimal UnitPrice, decimal DiscountPercent);
    private sealed record PreparedSaleLine(Guid LineId, RequestedLine Request, LockedItem Item, decimal FifoUnitCost);
    private sealed record StockRequest(string ItemCode, string WarehouseCode, decimal Quantity);
    private sealed record LockedItem(Guid Id, string Code, string Name, string Unit, decimal AverageCost, decimal AvailableStock);
    private sealed record ServiceComponent(string ItemCode, decimal Quantity);
    private sealed record ServiceMaterialRequirement(Guid SaleLineId, string ItemCode, string WarehouseCode, decimal Quantity);
    private sealed record PreparedServiceMaterial(Guid SaleLineId, LockedItem Item, string WarehouseCode, decimal Quantity, decimal UnitCost)
    {
        public decimal CostAmount => Math.Round(Quantity * UnitCost, 5);
    }
    private sealed record CancelStockLine(Guid ItemId, string WarehouseCode, decimal Quantity, decimal UnitCost, string ItemCode);
    private sealed record SaleHeader(
        Guid Id, string DocumentNumber, DateOnly DocumentDate, string CustomerCode, string CustomerName, string TransactionType,
        string? CashAccountCode, string? CashAccountName, DateOnly? DueDate, string? SalesCode, string WarehouseCode, decimal Subtotal, decimal DiscountAmount, decimal TaxAmount,
        decimal GrandTotal, decimal DownPayment, decimal BalanceDue, decimal CostTotal, string Status, string? Notes, DateTimeOffset CreatedAt, string DocumentKind);
}
