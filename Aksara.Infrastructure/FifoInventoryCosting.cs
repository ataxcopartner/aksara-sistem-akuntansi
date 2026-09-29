using Npgsql;

namespace Aksara.Infrastructure;

internal static class FifoInventoryCosting
{
    internal sealed record IssueResult(decimal Quantity, decimal TotalCost)
    {
        public decimal UnitCost => Quantity == 0 ? 0 : TotalCost / Quantity;
    }

    public static async Task ReceiveAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid itemId,
        string warehouseCode,
        string sourceType,
        Guid? sourceId,
        Guid? sourceLineId,
        string? sourceNumber,
        DateOnly documentDate,
        decimal quantity,
        decimal unitCost,
        CancellationToken cancellationToken)
    {
        if (quantity <= 0) return;
        const string sql = """
            insert into inventory_cost_layer(
                item_id, warehouse_code, received_at, source_type, source_id, source_line_id,
                source_number, original_quantity, remaining_quantity, unit_cost)
            values (@item, @warehouse, @receivedAt, @type, @source, @line, @number, @quantity, @quantity, @cost);
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("item", itemId);
        command.Parameters.AddWithValue("warehouse", warehouseCode);
        command.Parameters.AddWithValue("receivedAt", ToTimestamp(documentDate));
        command.Parameters.AddWithValue("type", sourceType);
        command.Parameters.AddWithValue("source", (object?)sourceId ?? DBNull.Value);
        command.Parameters.AddWithValue("line", (object?)sourceLineId ?? DBNull.Value);
        command.Parameters.AddWithValue("number", (object?)sourceNumber ?? DBNull.Value);
        command.Parameters.AddWithValue("quantity", quantity);
        command.Parameters.AddWithValue("cost", Math.Max(0, unitCost));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await RefreshAverageCostAsync(connection, transaction, itemId, cancellationToken);
    }

    public static async Task<IssueResult> IssueAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid itemId,
        string warehouseCode,
        string sourceType,
        Guid sourceId,
        Guid? sourceLineId,
        decimal quantity,
        CancellationToken cancellationToken)
    {
        if (quantity <= 0) return new(0, 0);
        const string selectSql = """
            select id, remaining_quantity, unit_cost
            from inventory_cost_layer
            where item_id = @item and warehouse_code = @warehouse and remaining_quantity > 0
            order by received_at, created_at, id
            for update;
            """;
        var layers = new List<(Guid Id, decimal Remaining, decimal Cost)>();
        await using (var command = new NpgsqlCommand(selectSql, connection, transaction))
        {
            command.Parameters.AddWithValue("item", itemId);
            command.Parameters.AddWithValue("warehouse", warehouseCode);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                layers.Add((reader.GetGuid(0), reader.GetDecimal(1), reader.GetDecimal(2)));
        }

        var available = layers.Sum(x => x.Remaining);
        if (available < quantity)
            throw new InvalidOperationException($"Lapisan FIFO di gudang {warehouseCode} tidak cukup: diminta {quantity:N3}, tersedia {available:N3}.");

        var remaining = quantity;
        var totalCost = 0m;
        foreach (var layer in layers)
        {
            if (remaining <= 0) break;
            var allocated = Math.Min(remaining, layer.Remaining);
            var amount = allocated * layer.Cost;
            const string updateSql = """
                update inventory_cost_layer
                set remaining_quantity = remaining_quantity - @quantity
                where id = @layer;

                insert into inventory_cost_allocation(
                    issue_source_type, issue_source_id, issue_line_id, layer_id, quantity, unit_cost, amount)
                values (@type, @source, @line, @layer, @quantity, @cost, @amount);
                """;
            await using var update = new NpgsqlCommand(updateSql, connection, transaction);
            update.Parameters.AddWithValue("type", sourceType);
            update.Parameters.AddWithValue("source", sourceId);
            update.Parameters.AddWithValue("line", (object?)sourceLineId ?? DBNull.Value);
            update.Parameters.AddWithValue("layer", layer.Id);
            update.Parameters.AddWithValue("quantity", allocated);
            update.Parameters.AddWithValue("cost", layer.Cost);
            update.Parameters.AddWithValue("amount", amount);
            await update.ExecuteNonQueryAsync(cancellationToken);
            remaining -= allocated;
            totalCost += amount;
        }

        await RefreshAverageCostAsync(connection, transaction, itemId, cancellationToken);
        return new(quantity, totalCost);
    }

    public static async Task<IReadOnlyList<decimal>> PreviewIssueUnitCostsAsync(
        NpgsqlConnection connection,
        IReadOnlyList<(Guid ItemId, string WarehouseCode, decimal Quantity)> requests,
        CancellationToken cancellationToken)
    {
        var remainingByLayer = new Dictionary<Guid, decimal>();
        var result = new List<decimal>(requests.Count);
        foreach (var request in requests)
        {
            const string sql = """
                select id, remaining_quantity, unit_cost
                from inventory_cost_layer
                where item_id=@item and warehouse_code=@warehouse and remaining_quantity>0
                order by received_at,created_at,id;
                """;
            var layers = new List<(Guid Id, decimal Remaining, decimal Cost)>();
            await using (var command = new NpgsqlCommand(sql, connection))
            {
                command.Parameters.AddWithValue("item", request.ItemId);
                command.Parameters.AddWithValue("warehouse", request.WarehouseCode);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var id = reader.GetGuid(0);
                    var remaining = remainingByLayer.TryGetValue(id, out var adjusted) ? adjusted : reader.GetDecimal(1);
                    layers.Add((id, remaining, reader.GetDecimal(2)));
                }
            }
            var needed = request.Quantity;
            var total = 0m;
            foreach (var layer in layers)
            {
                if (needed <= 0) break;
                var take = Math.Min(needed, layer.Remaining);
                if (take <= 0) continue;
                total += take * layer.Cost;
                needed -= take;
                remainingByLayer[layer.Id] = layer.Remaining - take;
            }
            result.Add(request.Quantity <= 0 || needed > 0 ? 0 : total / request.Quantity);
        }
        return result;
    }

    public static async Task RefreshAverageCostAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid itemId, CancellationToken cancellationToken)
    {
        const string sql = """
            update inventory_item i
            set average_cost = coalesce((
                    select case when sum(l.remaining_quantity)=0 then 0
                                else sum(l.remaining_quantity*l.unit_cost)/sum(l.remaining_quantity) end
                    from inventory_cost_layer l
                    where l.item_id=i.id and l.remaining_quantity>0
                ),0),
                updated_at=now(), version=version+1
            where i.id=@item;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("item", itemId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static DateTimeOffset ToTimestamp(DateOnly date) =>
        new(date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
}
