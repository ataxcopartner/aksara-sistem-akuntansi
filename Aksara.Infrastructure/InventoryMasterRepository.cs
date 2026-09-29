using System.Data;
using System.Text.Json;
using Aksara.Contracts;
using Aksara.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Aksara.Infrastructure;

public sealed class InventoryValidationException(string message) : InvalidOperationException(message);
public sealed class InventoryConflictException(string message) : InvalidOperationException(message);

public sealed class InventoryMasterRepository(PostgresDatabase database)
{
    public async Task<IReadOnlyList<InventoryWarehouseDto>> ListWarehousesAsync(bool includeInactive = true, CancellationToken cancellationToken = default)
    {
        const string sql = """
            select code, name, address1, address2, district, city, postal_code, phone1, phone2, fax, is_default, is_active, version
            from warehouse
            where @includeInactive or is_active
            order by is_default desc, code;
            """;
        await using var command = database.DataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("includeInactive", includeInactive);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<InventoryWarehouseDto>();
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetBoolean(10), reader.GetBoolean(11), reader.GetInt64(12), Text(reader, 2), Text(reader, 3), Text(reader, 4), Text(reader, 5), Text(reader, 6), Text(reader, 7), Text(reader, 8), Text(reader, 9)));
        return result;
    }

    public async Task<InventoryWarehouseDto> SaveWarehouseAsync(SaveInventoryWarehouseRequest request, CancellationToken cancellationToken = default)
    {
        var code = NormalizeCode(request.Code, "Kode lokasi/gudang");
        var name = RequireText(request.Name, "Nama lokasi/gudang", 100);
        if (request.IsDefault && !request.IsActive) throw new InventoryValidationException("Gudang default harus aktif.");

        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            var existingVersion = await ReadVersionForUpdateAsync(connection, transaction, "warehouse", "code", code, cancellationToken);
            ValidateExpectedVersion(existingVersion, request.ExpectedVersion, $"Gudang {code}");
            if (!request.IsActive && await HasStockAsync(connection, transaction, code, cancellationToken))
                throw new InventoryValidationException("Gudang yang masih memiliki stok tidak dapat dinonaktifkan.");
            if (request.IsDefault)
                await ExecuteAsync(connection, transaction, "update warehouse set is_default = false, updated_at = now(), version = version + 1 where is_default and code <> @code;", cancellationToken, ("code", code));

            const string sql = """
                insert into warehouse(code, name, address1, address2, district, city, postal_code, phone1, phone2, fax, is_default, is_active, updated_at, version)
                values (@code, @name, @address1, @address2, @district, @city, @postalCode, @phone1, @phone2, @fax, @isDefault, @isActive, now(), 1)
                on conflict (code) do update
                set name = excluded.name, address1 = excluded.address1, address2 = excluded.address2,
                    district = excluded.district, city = excluded.city, postal_code = excluded.postal_code,
                    phone1 = excluded.phone1, phone2 = excluded.phone2, fax = excluded.fax,
                    is_default = excluded.is_default, is_active = excluded.is_active,
                    updated_at = now(), version = warehouse.version + 1
                returning code, name, address1, address2, district, city, postal_code, phone1, phone2, fax, is_default, is_active, version;
                """;
            InventoryWarehouseDto saved;
            await using (var command = new NpgsqlCommand(sql, connection, transaction))
            {
                command.Parameters.AddWithValue("code", code); command.Parameters.AddWithValue("name", name);
                command.Parameters.AddWithValue("address1", Db(request.Address1)); command.Parameters.AddWithValue("address2", Db(request.Address2));
                command.Parameters.AddWithValue("district", Db(request.District)); command.Parameters.AddWithValue("city", Db(request.City));
                command.Parameters.AddWithValue("postalCode", Db(request.PostalCode)); command.Parameters.AddWithValue("phone1", Db(request.Phone1));
                command.Parameters.AddWithValue("phone2", Db(request.Phone2)); command.Parameters.AddWithValue("fax", Db(request.Fax));
                command.Parameters.AddWithValue("isDefault", request.IsDefault); command.Parameters.AddWithValue("isActive", request.IsActive);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                await reader.ReadAsync(cancellationToken);
                saved = new(reader.GetString(0), reader.GetString(1), reader.GetBoolean(10), reader.GetBoolean(11), reader.GetInt64(12), Text(reader, 2), Text(reader, 3), Text(reader, 4), Text(reader, 5), Text(reader, 6), Text(reader, 7), Text(reader, 8), Text(reader, 9));
            }
            await InsertAuditAsync(connection, transaction, "WAREHOUSE", code, existingVersion is null ? "CREATE" : "UPDATE", request.UserName, request.ComputerName, request, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return saved;
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.SerializationFailure)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new InventoryConflictException("Data gudang berubah saat disimpan. Muat ulang lalu coba kembali.");
        }
        catch { await transaction.RollbackAsync(cancellationToken); throw; }
    }

    public async Task<IReadOnlyList<InventoryAdjustmentTypeDto>> ListAdjustmentTypesAsync(bool includeInactive = true, CancellationToken cancellationToken = default)
    {
        const string sql = """
            select code, name, direction, requires_value, is_opening_balance, is_active, version
            from inventory_adjustment_type
            where @includeInactive or is_active
            order by is_opening_balance desc, code;
            """;
        await using var command = database.DataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("includeInactive", includeInactive);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<InventoryAdjustmentTypeDto>();
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3), reader.GetBoolean(4), reader.GetBoolean(5), reader.GetInt64(6)));
        return result;
    }

    public async Task<InventoryAdjustmentTypeDto> SaveAdjustmentTypeAsync(SaveInventoryAdjustmentTypeRequest request, CancellationToken cancellationToken = default)
    {
        var code = NormalizeCode(request.Code, "Kode jenis penyesuaian");
        var name = RequireText(request.Name, "Nama jenis penyesuaian", 100);
        var direction = request.Direction.Trim().ToUpperInvariant();
        if (direction is not ("IN" or "OUT" or "BOTH")) throw new InventoryValidationException("Arah penyesuaian harus IN, OUT, atau BOTH.");
        if (request.IsOpeningBalance && direction != "IN") throw new InventoryValidationException("Saldo Awal harus berupa persediaan masuk.");

        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            var existingVersion = await ReadVersionForUpdateAsync(connection, transaction, "inventory_adjustment_type", "code", code, cancellationToken);
            ValidateExpectedVersion(existingVersion, request.ExpectedVersion, $"Jenis penyesuaian {code}");
            if (request.IsOpeningBalance)
                await ExecuteAsync(connection, transaction, "update inventory_adjustment_type set is_opening_balance = false, updated_at = now(), version = version + 1 where is_opening_balance and code <> @code;", cancellationToken, ("code", code));
            const string sql = """
                insert into inventory_adjustment_type(code, name, direction, requires_value, is_opening_balance, is_active, updated_at, version)
                values (@code, @name, @direction, @requiresValue, @opening, @active, now(), 1)
                on conflict (code) do update
                set name = excluded.name, direction = excluded.direction, requires_value = excluded.requires_value,
                    is_opening_balance = excluded.is_opening_balance, is_active = excluded.is_active,
                    updated_at = now(), version = inventory_adjustment_type.version + 1
                returning code, name, direction, requires_value, is_opening_balance, is_active, version;
                """;
            InventoryAdjustmentTypeDto saved;
            await using (var command = new NpgsqlCommand(sql, connection, transaction))
            {
                command.Parameters.AddWithValue("code", code); command.Parameters.AddWithValue("name", name);
                command.Parameters.AddWithValue("direction", direction); command.Parameters.AddWithValue("requiresValue", request.RequiresValue);
                command.Parameters.AddWithValue("opening", request.IsOpeningBalance); command.Parameters.AddWithValue("active", request.IsActive);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken); await reader.ReadAsync(cancellationToken);
                saved = new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3), reader.GetBoolean(4), reader.GetBoolean(5), reader.GetInt64(6));
            }
            await InsertAuditAsync(connection, transaction, "INVENTORY_ADJUSTMENT_TYPE", code, existingVersion is null ? "CREATE" : "UPDATE", request.UserName, request.ComputerName, request, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return saved;
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.SerializationFailure)
        {
            await transaction.RollbackAsync(cancellationToken); throw new InventoryConflictException("Jenis penyesuaian berubah saat disimpan. Muat ulang lalu coba kembali.");
        }
        catch { await transaction.RollbackAsync(cancellationToken); throw; }
    }

    public async Task<IReadOnlyList<InventoryUnitDto>> ListUnitsAsync(CancellationToken cancellationToken = default)
    {
        await using var command = database.DataSource.CreateCommand("select name, is_system from inventory_unit order by name;");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<InventoryUnitDto>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(new(reader.GetString(0), reader.GetBoolean(1)));
        return result;
    }

    public async Task<InventoryUnitDto> SaveUnitAsync(SaveInventoryUnitRequest request, CancellationToken cancellationToken = default)
    {
        var name = RequireText(request.Name, "Nama satuan", 10).ToUpperInvariant();
        const string sql = """
            insert into inventory_unit(name, is_system) values (@name, false)
            on conflict (name) do update set name = excluded.name
            returning name, is_system;
            """;
        await using var command = database.DataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("name", name);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new(reader.GetString(0), reader.GetBoolean(1));
    }

    public async Task<IReadOnlyList<InventoryCategoryDto>> ListCategoriesAsync(bool includeInactive = true, string categoryType = "GOODS", CancellationToken cancellationToken = default)
    {
        categoryType = NormalizeItemType(categoryType);
        const string sql = """
            select c.code, c.name, c.parent_code, c.is_active, c.version,
                   c.uses_id_per_transaction, c.is_production_material, c.is_product,
                   c.is_not_trade_goods, c.is_not_service_support,
                   c.mov_r1_qty, c.mov_r2_qty, c.mov_r3_qty, c.point_value,
                   count(i.id)::int as item_usage_count, c.category_type
            from inventory_category c
            left join inventory_item i on i.category_code = c.code and i.item_type = c.category_type and i.is_active
            where c.category_type = @categoryType and (@includeInactive or c.is_active)
            group by c.code, c.name, c.parent_code, c.is_active, c.version,
                     c.uses_id_per_transaction, c.is_production_material, c.is_product,
                     c.is_not_trade_goods, c.is_not_service_support,
                     c.mov_r1_qty, c.mov_r2_qty, c.mov_r3_qty, c.point_value, c.category_type
            order by c.code;
            """;
        await using var command = database.DataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("includeInactive", includeInactive);
        command.Parameters.AddWithValue("categoryType", categoryType);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<InventoryCategoryDto>();
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetBoolean(3), reader.GetInt64(4),
                reader.GetBoolean(5), reader.GetBoolean(6), reader.GetBoolean(7), reader.GetBoolean(8), reader.GetBoolean(9),
                reader.GetDecimal(10), reader.GetDecimal(11), reader.GetDecimal(12), reader.GetDecimal(13), reader.GetInt32(14), reader.GetString(15)));
        return result;
    }

    public async Task<InventoryCategoryDto> SaveCategoryAsync(SaveInventoryCategoryRequest request, CancellationToken cancellationToken = default)
    {
        var categoryType = NormalizeItemType(request.CategoryType);
        var entityName = categoryType == "SERVICE" ? "Kelompok Jasa" : "Kelompok Barang";
        var code = NormalizeCode(request.Code, $"Kode {entityName}");
        var name = RequireText(request.Name, $"Nama {entityName}", 100);
        var parentCode = string.IsNullOrWhiteSpace(request.ParentCode) ? null : NormalizeCode(request.ParentCode, "Kelompok induk");
        if (parentCode == code) throw new InventoryValidationException($"{entityName} tidak dapat menjadi induk dirinya sendiri.");

        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            var existingVersion = await ReadVersionForUpdateAsync(connection, transaction, "inventory_category", "code", code, cancellationToken);
            ValidateExpectedVersion(existingVersion, request.ExpectedVersion, $"{entityName} {code}");
            if (existingVersion is not null)
            {
                await using var typeCommand = new NpgsqlCommand("select category_type from inventory_category where code = @code;", connection, transaction);
                typeCommand.Parameters.AddWithValue("code", code);
                var existingType = (string?)await typeCommand.ExecuteScalarAsync(cancellationToken);
                if (!string.Equals(existingType, categoryType, StringComparison.OrdinalIgnoreCase))
                    throw new InventoryValidationException($"Kode {code} sudah digunakan oleh {(existingType == "SERVICE" ? "Kelompok Jasa" : "Kelompok Barang")}.");
            }
            if (parentCode is not null && !await CategoryExistsAsync(connection, transaction, parentCode, categoryType, cancellationToken))
                throw new InventoryValidationException($"Kelompok induk {parentCode} tidak ditemukan.");
            const string sql = """
                insert into inventory_category(code, name, parent_code, is_active, category_type,
                    uses_id_per_transaction, is_production_material, is_product,
                    is_not_trade_goods, is_not_service_support,
                    mov_r1_qty, mov_r2_qty, mov_r3_qty, point_value, updated_at, version)
                values (@code, @name, @parentCode, @active, @categoryType,
                    @usesId, @productionMaterial, @product, @notTradeGoods, @notServiceSupport,
                    @movR1, @movR2, @movR3, @pointValue, now(), 1)
                on conflict (code) do update
                set name = excluded.name, parent_code = excluded.parent_code, is_active = excluded.is_active,
                    uses_id_per_transaction = excluded.uses_id_per_transaction,
                    is_production_material = excluded.is_production_material,
                    is_product = excluded.is_product,
                    is_not_trade_goods = excluded.is_not_trade_goods,
                    is_not_service_support = excluded.is_not_service_support,
                    mov_r1_qty = excluded.mov_r1_qty, mov_r2_qty = excluded.mov_r2_qty,
                    mov_r3_qty = excluded.mov_r3_qty, point_value = excluded.point_value,
                    updated_at = now(), version = inventory_category.version + 1
                returning code, name, parent_code, is_active, version,
                    uses_id_per_transaction, is_production_material, is_product,
                    is_not_trade_goods, is_not_service_support,
                    mov_r1_qty, mov_r2_qty, mov_r3_qty, point_value, category_type;
                """;
            InventoryCategoryDto saved;
            await using (var command = new NpgsqlCommand(sql, connection, transaction))
            {
                command.Parameters.AddWithValue("code", code); command.Parameters.AddWithValue("name", name);
                command.Parameters.AddWithValue("parentCode", (object?)parentCode ?? DBNull.Value); command.Parameters.AddWithValue("active", request.IsActive);
                command.Parameters.AddWithValue("categoryType", categoryType);
                command.Parameters.AddWithValue("usesId", request.UsesIdPerTransaction);
                command.Parameters.AddWithValue("productionMaterial", request.IsProductionMaterial);
                command.Parameters.AddWithValue("product", request.IsProduct);
                command.Parameters.AddWithValue("notTradeGoods", request.IsNotTradeGoods);
                command.Parameters.AddWithValue("notServiceSupport", request.IsNotServiceSupport);
                command.Parameters.AddWithValue("movR1", request.MovR1Qty);
                command.Parameters.AddWithValue("movR2", request.MovR2Qty);
                command.Parameters.AddWithValue("movR3", request.MovR3Qty);
                command.Parameters.AddWithValue("pointValue", request.PointValue);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken); await reader.ReadAsync(cancellationToken);
                saved = new(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetBoolean(3), reader.GetInt64(4),
                    reader.GetBoolean(5), reader.GetBoolean(6), reader.GetBoolean(7), reader.GetBoolean(8), reader.GetBoolean(9),
                    reader.GetDecimal(10), reader.GetDecimal(11), reader.GetDecimal(12), reader.GetDecimal(13), 0, reader.GetString(14));
            }
            await InsertAuditAsync(connection, transaction, categoryType == "SERVICE" ? "SERVICE_CATEGORY" : "INVENTORY_CATEGORY", code, existingVersion is null ? "CREATE" : "UPDATE", request.UserName, request.ComputerName, request, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return saved;
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.SerializationFailure)
        {
            await transaction.RollbackAsync(cancellationToken); throw new InventoryConflictException($"{entityName} berubah saat disimpan. Muat ulang lalu coba kembali.");
        }
        catch { await transaction.RollbackAsync(cancellationToken); throw; }
    }

    public async Task<IReadOnlyList<InventoryItemSummaryDto>> ListItemsAsync(string? query, string? categoryCode, string? itemType = "GOODS", bool includeInactive = true, int limit = 1000, CancellationToken cancellationToken = default)
    {
        const string sql = """
            with fifo_cost as (
                select item_id,
                       sum(remaining_quantity * unit_cost) / nullif(sum(remaining_quantity), 0) unit_cost
                from inventory_cost_layer
                where remaining_quantity > 0
                group by item_id
            ), movement_cost as (
                select item_id,
                       case when sum(quantity_in - quantity_out) > 0
                            then sum((quantity_in - quantity_out) * unit_cost) / sum(quantity_in - quantity_out)
                       end unit_cost
                from inventory_movement
                group by item_id
            ), latest_purchase_cost as (
                select distinct on (pl.item_id) pl.item_id,
                       pl.line_total / nullif(pl.quantity, 0) unit_cost
                from purchase_line pl
                join purchase p on p.id = pl.purchase_id
                where p.status = 'POSTED' and pl.quantity > 0
                order by pl.item_id, p.document_date desc, p.created_at desc, pl.line_number desc
            )
            select i.id, i.code, i.name, i.unit, i.category_code, c.name, i.barcode,
                   coalesce(sum(b.quantity), 0),
                   coalesce(nullif(fc.unit_cost, 0), nullif(mc.unit_cost, 0), nullif(i.average_cost, 0),
                             nullif(lpc.unit_cost, 0), nullif(i.purchase_price, 0), 0),
                   i.retail_price, i.wholesale_price,
                   i.minimum_stock, i.maximum_stock, i.check_stock, i.is_active, i.version,
                   (select p.package_name from inventory_item_package p where p.item_id = i.id order by p.sort_order limit 1),
                   (select p.package_size from inventory_item_package p where p.item_id = i.id order by p.sort_order limit 1),
                   i.item_type
            from inventory_item i
            left join inventory_category c on c.code = i.category_code
            left join inventory_balance b on b.item_id = i.id
            left join fifo_cost fc on fc.item_id = i.id
            left join movement_cost mc on mc.item_id = i.id
            left join latest_purchase_cost lpc on lpc.item_id = i.id
            where (@includeInactive or i.is_active)
              and (@category = '' or i.category_code = @category)
              and (@itemType = '' or i.item_type = @itemType)
              and (@query = '' or i.code ilike '%' || @query || '%' or i.name ilike '%' || @query || '%' or coalesce(i.barcode, '') ilike '%' || @query || '%')
            group by i.id, c.name, fc.unit_cost, mc.unit_cost, lpc.unit_cost
            order by coalesce(i.category_code, ''), i.code
            limit @limit;
            """;
        await using var command = database.DataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("includeInactive", includeInactive); command.Parameters.AddWithValue("category", categoryCode?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("itemType", string.IsNullOrWhiteSpace(itemType) ? string.Empty : NormalizeItemType(itemType));
        command.Parameters.AddWithValue("query", query?.Trim() ?? string.Empty); command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 5000));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<InventoryItemSummaryDto>();
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetDecimal(7), reader.GetDecimal(8),
                reader.GetDecimal(9), reader.GetDecimal(10), reader.GetDecimal(11), reader.GetDecimal(12), reader.GetBoolean(13), reader.GetBoolean(14), reader.GetInt64(15),
                reader.IsDBNull(16) ? null : reader.GetString(16), reader.IsDBNull(17) ? null : reader.GetDecimal(17), reader.GetString(18)));
        return result;
    }

    public async Task<InventoryItemDto?> GetItemAsync(string code, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        return await ReadItemAsync(connection, null, NormalizeCode(code, "Kode Barang"), cancellationToken);
    }

    public async Task<InventoryItemDto> SaveItemAsync(SaveInventoryItemRequest request, CancellationToken cancellationToken = default)
    {
        ValidateItemRequest(request);
        var code = NormalizeCode(request.Code, "Kode Barang");
        var itemType = NormalizeItemType(request.ItemType);
        var unit = itemType == "SERVICE" ? "JASA" : RequireText(request.Unit, "Satuan", 10).ToUpperInvariant();
        IReadOnlyList<InventoryFormulaLineDto> formulaLines = itemType == "SERVICE" ? [] : request.Formula;
        var image = DecodeImage(request.ImageBase64, request.ImageContentType);
        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            Guid itemId;
            long? existingVersion;
            string? existingItemType;
            await using (var command = new NpgsqlCommand("select id, version, item_type from inventory_item where code = @code for update;", connection, transaction))
            {
                command.Parameters.AddWithValue("code", code);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken)) { itemId = reader.GetGuid(0); existingVersion = reader.GetInt64(1); existingItemType = reader.GetString(2); }
                else { itemId = Guid.NewGuid(); existingVersion = null; existingItemType = null; }
            }
            ValidateExpectedVersion(existingVersion, request.ExpectedVersion, $"Barang {code}");
            if (existingItemType is not null && existingItemType != itemType)
                throw new InventoryValidationException($"Jenis master {code} tidak dapat diubah dari {existingItemType} menjadi {itemType}. Buat kode baru agar histori transaksi tetap konsisten.");
            var categoryLabel = itemType == "SERVICE" ? "Kelompok Jasa" : "Kelompok Barang";
            var category = string.IsNullOrWhiteSpace(request.CategoryCode) ? null : NormalizeCode(request.CategoryCode, categoryLabel);
            if (category is not null && !await CategoryExistsAsync(connection, transaction, category, itemType, cancellationToken))
                throw new InventoryValidationException($"{categoryLabel} {category} tidak ditemukan.");
            var components = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
            foreach (var formula in formulaLines)
            {
                var componentCode = NormalizeCode(formula.ComponentItemCode, "Kode bahan formula");
                if (componentCode == code) throw new InventoryValidationException("Barang tidak dapat memakai dirinya sendiri sebagai bahan formula.");
                if (formula.Quantity <= 0) throw new InventoryValidationException($"Jumlah bahan {componentCode} harus lebih dari nol.");
                if (!components.TryAdd(componentCode, await ReadItemIdAsync(connection, transaction, componentCode, cancellationToken)
                    ?? throw new InventoryValidationException($"Bahan formula {componentCode} tidak ditemukan.")))
                    throw new InventoryValidationException($"Bahan formula {componentCode} tercantum lebih dari sekali.");
                if (itemType == "SERVICE" && await ReadItemTypeAsync(connection, transaction, componentCode, cancellationToken) != "GOODS")
                    throw new InventoryValidationException($"Bahan penunjang jasa {componentCode} harus berupa master Barang.");
            }

            const string sql = """
                insert into inventory_item(id, code, barcode, name, category_code, unit, purchase_price, average_cost,
                    retail_price, wholesale_price, minimum_stock, maximum_stock, rack, brand, notes, specification,
                    check_stock, image_data, image_content_type, is_active, item_type, updated_at, version)
                values (@id, @code, @barcode, @name, @category, @unit, @purchasePrice, @averageCost,
                    @retailPrice, @wholesalePrice, @minimumStock, @maximumStock, @rack, @brand, @notes, @specification,
                    @checkStock, @imageData, @imageType, @active, @itemType, now(), 1)
                on conflict (code) do update set
                    barcode = excluded.barcode, name = excluded.name, category_code = excluded.category_code, unit = excluded.unit,
                    purchase_price = excluded.purchase_price, average_cost = excluded.average_cost,
                    retail_price = excluded.retail_price, wholesale_price = excluded.wholesale_price,
                    minimum_stock = excluded.minimum_stock, maximum_stock = excluded.maximum_stock,
                    rack = excluded.rack, brand = excluded.brand, notes = excluded.notes, specification = excluded.specification,
                    check_stock = excluded.check_stock, image_data = excluded.image_data, image_content_type = excluded.image_content_type,
                    is_active = excluded.is_active, item_type = excluded.item_type, updated_at = now(), version = inventory_item.version + 1
                returning id;
                """;
            var prices = NormalizePrices(request.Prices);
            await using (var command = new NpgsqlCommand(sql, connection, transaction))
            {
                command.Parameters.AddWithValue("id", itemId); command.Parameters.AddWithValue("code", code);
                command.Parameters.AddWithValue("barcode", Db(request.Barcode)); command.Parameters.AddWithValue("name", RequireText(request.Name, "Nama Barang", 150));
                command.Parameters.AddWithValue("category", (object?)category ?? DBNull.Value); command.Parameters.AddWithValue("unit", unit);
                command.Parameters.AddWithValue("purchasePrice", itemType == "SERVICE" ? 0m : request.PurchasePrice); command.Parameters.AddWithValue("averageCost", itemType == "SERVICE" ? 0m : request.AverageCost);
                command.Parameters.AddWithValue("retailPrice", prices.Single(x => x.TierCode == "RETAIL").Price);
                command.Parameters.AddWithValue("wholesalePrice", prices.Single(x => x.TierCode == "WHOLESALE").Price);
                command.Parameters.AddWithValue("minimumStock", itemType == "SERVICE" ? 0m : request.MinimumStock); command.Parameters.AddWithValue("maximumStock", itemType == "SERVICE" ? 0m : request.MaximumStock);
                command.Parameters.AddWithValue("rack", Db(request.Rack)); command.Parameters.AddWithValue("brand", Db(request.Brand));
                command.Parameters.AddWithValue("notes", Db(request.Notes)); command.Parameters.AddWithValue("specification", Db(request.Specification));
                command.Parameters.AddWithValue("checkStock", itemType != "SERVICE" && request.CheckStock);
                command.Parameters.AddWithValue("imageData", (object?)image ?? DBNull.Value); command.Parameters.AddWithValue("imageType", Db(request.ImageContentType));
                command.Parameters.AddWithValue("active", request.IsActive);
                command.Parameters.AddWithValue("itemType", itemType);
                itemId = (Guid)(await command.ExecuteScalarAsync(cancellationToken))!;
            }

            await ExecuteAsync(connection, transaction, "delete from inventory_item_price where item_id = @id;", cancellationToken, ("id", itemId));
            foreach (var (price, index) in prices.Select((value, index) => (value, index)))
            {
                const string priceSql = """
                    insert into inventory_item_price(item_id, tier_code, tier_name, price, discount_percent, minimum_quantity, sort_order)
                    values (@id, @code, @name, @price, @discount, @minimum, @sort);
                    """;
                await ExecuteAsync(connection, transaction, priceSql, cancellationToken,
                    ("id", itemId), ("code", price.TierCode), ("name", price.TierName), ("price", price.Price),
                    ("discount", price.DiscountPercent), ("minimum", price.MinimumQuantity), ("sort", index + 1));
            }

            await ExecuteAsync(connection, transaction, "delete from inventory_item_package where item_id = @id;", cancellationToken, ("id", itemId));
            foreach (var package in request.Packages.OrderBy(x => x.SortOrder))
            {
                if (string.IsNullOrWhiteSpace(package.PackageName) || package.PackageSize <= 0)
                    throw new InventoryValidationException("Nama dan isi koli/kemasan wajib valid.");
                await ExecuteAsync(connection, transaction,
                    "insert into inventory_item_package(item_id, package_name, package_size, barcode, sort_order) values (@id, @name, @size, @barcode, @sort);",
                    cancellationToken, ("id", itemId), ("name", package.PackageName.Trim()), ("size", package.PackageSize),
                    ("barcode", Db(package.Barcode)), ("sort", package.SortOrder));
            }

            await ExecuteAsync(connection, transaction, "delete from inventory_item_formula where item_id = @id;", cancellationToken, ("id", itemId));
            foreach (var formula in formulaLines)
            {
                var componentCode = formula.ComponentItemCode.Trim().ToUpperInvariant();
                await ExecuteAsync(connection, transaction,
                    "insert into inventory_item_formula(item_id, component_item_id, quantity) values (@id, @componentId, @quantity);",
                    cancellationToken, ("id", itemId), ("componentId", components[componentCode]), ("quantity", formula.Quantity));
            }
            await InsertAuditAsync(connection, transaction, "INVENTORY_ITEM", itemId.ToString(), existingVersion is null ? "CREATE" : "UPDATE", request.UserName, request.ComputerName, request with { ImageBase64 = image is null ? null : "<image>", Formula = formulaLines }, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return (await GetItemAsync(code, cancellationToken))!;
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.SerializationFailure)
        {
            await transaction.RollbackAsync(cancellationToken); throw new InventoryConflictException("Barang berubah saat disimpan. Muat ulang lalu coba kembali.");
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await transaction.RollbackAsync(cancellationToken); throw new InventoryConflictException("Kode Barang, barcode, atau nama kemasan sudah digunakan.");
        }
        catch { await transaction.RollbackAsync(cancellationToken); throw; }
    }

    public async Task<IReadOnlyList<InventoryBusinessUnitDto>> ListBusinessUnitsAsync(CancellationToken cancellationToken = default)
    {
        await using var command = database.DataSource.CreateCommand("select code, name, is_active from business_unit where is_active order by code;");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<InventoryBusinessUnitDto>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetBoolean(2)));
        return result;
    }

    private static void ValidateItemRequest(SaveInventoryItemRequest request)
    {
        RequireText(request.Code, "Kode Barang", 20); RequireText(request.Name, "Nama Barang", 150);
        if (NormalizeItemType(request.ItemType) != "SERVICE") RequireText(request.Unit, "Satuan", 10);
        if (request.PurchasePrice < 0 || request.AverageCost < 0) throw new InventoryValidationException("Harga beli dan HPP tidak boleh negatif.");
        if (request.MinimumStock < 0 || request.MaximumStock < 0) throw new InventoryValidationException("Batas stok tidak boleh negatif.");
        if (request.MaximumStock > 0 && request.MaximumStock < request.MinimumStock) throw new InventoryValidationException("Max. Stok tidak boleh lebih kecil dari Min. Stok.");
        InventoryRules.ValidatePriceTiers(request.Prices.Select(x => new InventoryTierRule(x.TierCode, x.Price, x.DiscountPercent, x.MinimumQuantity)).ToArray());
    }

    private static IReadOnlyList<InventoryPriceTierDto> NormalizePrices(IReadOnlyList<InventoryPriceTierDto> prices)
    {
        var names = new Dictionary<string, string> { ["RETAIL"] = "Eceran", ["WHOLESALE"] = "Grosir", ["BULK"] = "Partai", ["SPECIAL1"] = "Khusus 1", ["SPECIAL2"] = "Khusus 2", ["SPECIAL3"] = "Khusus 3" };
        return prices.Select(x => x with { TierCode = x.TierCode.Trim().ToUpperInvariant(), TierName = names[x.TierCode.Trim().ToUpperInvariant()] })
            .OrderBy(x => Array.IndexOf(["RETAIL", "WHOLESALE", "BULK", "SPECIAL1", "SPECIAL2", "SPECIAL3"], x.TierCode)).ToArray();
    }

    private static byte[]? DecodeImage(string? base64, string? contentType)
    {
        if (string.IsNullOrWhiteSpace(base64)) return null;
        if (contentType is not ("image/png" or "image/jpeg" or "image/webp")) throw new InventoryValidationException("Format gambar harus PNG, JPEG, atau WebP.");
        try
        {
            var bytes = Convert.FromBase64String(base64);
            if (bytes.Length > 5 * 1024 * 1024) throw new InventoryValidationException("Ukuran gambar Barang maksimal 5 MB.");
            return bytes;
        }
        catch (FormatException) { throw new InventoryValidationException("Data gambar Barang tidak valid."); }
    }

    private static async Task<InventoryItemDto?> ReadItemAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string code, CancellationToken cancellationToken)
    {
        const string headerSql = """
            select i.id, i.code, i.name, i.unit, i.category_code, c.name, i.barcode, i.rack, i.brand,
                   i.purchase_price,
                   coalesce(
                       nullif((select sum(l.remaining_quantity * l.unit_cost) / nullif(sum(l.remaining_quantity), 0)
                               from inventory_cost_layer l where l.item_id = i.id and l.remaining_quantity > 0), 0),
                       nullif((select case when sum(m.quantity_in - m.quantity_out) > 0
                                          then sum((m.quantity_in - m.quantity_out) * m.unit_cost) / sum(m.quantity_in - m.quantity_out)
                                     end
                               from inventory_movement m where m.item_id = i.id), 0),
                       nullif(i.average_cost, 0),
                       nullif((select pl.line_total / nullif(pl.quantity, 0)
                               from purchase_line pl join purchase p on p.id = pl.purchase_id
                               where pl.item_id = i.id and p.status = 'POSTED' and pl.quantity > 0
                               order by p.document_date desc, p.created_at desc, pl.line_number desc limit 1), 0),
                       nullif(i.purchase_price, 0), 0),
                   i.minimum_stock, i.maximum_stock, i.check_stock,
                   i.notes, i.specification, i.image_content_type, i.image_data, i.is_active, i.version, i.item_type
            from inventory_item i left join inventory_category c on c.code = i.category_code
            where i.code = @code;
            """;
        Guid id; string name; string unit; string? categoryCode; string? categoryName; string? barcode; string? rack; string? brand;
        decimal purchasePrice; decimal averageCost; decimal min; decimal max; bool checkStock; string? notes; string? specification; string? imageType; string? imageBase64; bool active; long version; string itemType;
        await using (var command = new NpgsqlCommand(headerSql, connection, transaction))
        {
            command.Parameters.AddWithValue("code", code);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return null;
            id = reader.GetGuid(0); name = reader.GetString(2); unit = reader.GetString(3); categoryCode = Text(reader, 4); categoryName = Text(reader, 5);
            barcode = Text(reader, 6); rack = Text(reader, 7); brand = Text(reader, 8); purchasePrice = reader.GetDecimal(9); averageCost = reader.GetDecimal(10);
            min = reader.GetDecimal(11); max = reader.GetDecimal(12); checkStock = reader.GetBoolean(13); notes = Text(reader, 14); specification = Text(reader, 15);
            imageType = Text(reader, 16); imageBase64 = reader.IsDBNull(17) ? null : Convert.ToBase64String(reader.GetFieldValue<byte[]>(17)); active = reader.GetBoolean(18); version = reader.GetInt64(19); itemType = reader.GetString(20);
        }
        var prices = new List<InventoryPriceTierDto>();
        await using (var command = new NpgsqlCommand("select tier_code, tier_name, price, discount_percent, minimum_quantity from inventory_item_price where item_id = @id order by sort_order;", connection, transaction))
        {
            command.Parameters.AddWithValue("id", id); await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) prices.Add(new(reader.GetString(0), reader.GetString(1), reader.GetDecimal(2), reader.GetDecimal(3), reader.GetDecimal(4)));
        }
        var packages = new List<InventoryPackageDto>();
        await using (var command = new NpgsqlCommand("select package_name, package_size, barcode, sort_order from inventory_item_package where item_id = @id order by sort_order;", connection, transaction))
        {
            command.Parameters.AddWithValue("id", id); await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) packages.Add(new(reader.GetString(0), reader.GetDecimal(1), Text(reader, 2), reader.GetInt32(3)));
        }
        var formula = new List<InventoryFormulaLineDto>();
        await using (var command = new NpgsqlCommand("select c.code, c.name, f.quantity, c.unit from inventory_item_formula f join inventory_item c on c.id = f.component_item_id where f.item_id = @id order by c.code;", connection, transaction))
        {
            command.Parameters.AddWithValue("id", id); await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) formula.Add(new(reader.GetString(0), reader.GetString(1), reader.GetDecimal(2), reader.GetString(3)));
        }
        var balances = new List<InventoryItemBalanceDto>();
        await using (var command = new NpgsqlCommand("select w.code, w.name, coalesce(b.quantity, 0) from warehouse w left join inventory_balance b on b.warehouse_code = w.code and b.item_id = @id where w.is_active order by w.is_default desc, w.code;", connection, transaction))
        {
            command.Parameters.AddWithValue("id", id); await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) balances.Add(new(reader.GetString(0), reader.GetString(1), reader.GetDecimal(2)));
        }
        return new(id, code, name, unit, categoryCode, categoryName, barcode, rack, brand, purchasePrice, averageCost, min, max, checkStock,
            notes, specification, imageType, imageBase64, active, version, prices, packages, formula, balances, itemType);
    }

    private static async Task<long?> ReadVersionForUpdateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string table, string keyColumn, string key, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"select version from {table} where {keyColumn} = @key for update;", connection, transaction);
        command.Parameters.AddWithValue("key", key); var value = await command.ExecuteScalarAsync(cancellationToken); return value is null ? null : Convert.ToInt64(value);
    }

    private static void ValidateExpectedVersion(long? actual, long? expected, string entity)
    {
        if (actual is null && expected is not null) throw new InventoryConflictException($"{entity} sudah dihapus atau belum tersedia.");
        if (actual is not null && expected is null) throw new InventoryConflictException($"{entity} sudah ada. Gunakan mode Edit.");
        if (actual is not null && actual != expected) throw new InventoryConflictException($"{entity} telah diubah pengguna lain. Muat ulang data.");
    }

    private static async Task<bool> HasStockAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string warehouse, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("select exists(select 1 from inventory_balance where warehouse_code = @warehouse and quantity <> 0);", connection, transaction);
        command.Parameters.AddWithValue("warehouse", warehouse); return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task<bool> ExistsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string table, string column, string value, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"select exists(select 1 from {table} where {column} = @value);", connection, transaction);
        command.Parameters.AddWithValue("value", value); return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task<bool> CategoryExistsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string code, string categoryType, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("select exists(select 1 from inventory_category where code = @code and category_type = @categoryType);", connection, transaction);
        command.Parameters.AddWithValue("code", code);
        command.Parameters.AddWithValue("categoryType", categoryType);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task<Guid?> ReadItemIdAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string code, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("select id from inventory_item where code = @code and is_active;", connection, transaction);
        command.Parameters.AddWithValue("code", code); var value = await command.ExecuteScalarAsync(cancellationToken); return value as Guid?;
    }

    private static async Task<string?> ReadItemTypeAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string code, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("select item_type from inventory_item where code = @code and is_active;", connection, transaction);
        command.Parameters.AddWithValue("code", code);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    internal static async Task InsertAuditAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string entityType, string entityId, string action, string? userName, string? computerName, object afterData, CancellationToken cancellationToken)
    {
        const string sql = """
            insert into audit_log(computer_name, module, action, entity_type, entity_id, after_data)
            values (@computer, 'INVENTORY', @action, @entityType, @entityId, @afterData);
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("computer", Db(computerName)); command.Parameters.AddWithValue("action", action);
        command.Parameters.AddWithValue("entityType", entityType); command.Parameters.AddWithValue("entityId", entityId);
        command.Parameters.Add(new NpgsqlParameter("afterData", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(new { userName, data = afterData }) });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal static string NormalizeCode(string value, string label)
    {
        var code = RequireText(value, label, 20).ToUpperInvariant();
        if (code.Any(char.IsWhiteSpace)) throw new InventoryValidationException($"{label} tidak boleh mengandung spasi.");
        return code;
    }

    private static string NormalizeItemType(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "SERVICE" or "JASA" => "SERVICE",
        "GOODS" or "BARANG" or null or "" => "GOODS",
        _ => throw new InventoryValidationException("Jenis master harus GOODS/BARANG atau SERVICE/JASA.")
    };

    internal static string RequireText(string? value, string label, int maxLength)
    {
        var text = value?.Trim();
        if (string.IsNullOrWhiteSpace(text)) throw new InventoryValidationException($"{label} wajib diisi.");
        if (text.Length > maxLength) throw new InventoryValidationException($"{label} maksimal {maxLength} karakter.");
        return text;
    }

    internal static object Db(string? value) => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim();
    private static string? Text(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
}
