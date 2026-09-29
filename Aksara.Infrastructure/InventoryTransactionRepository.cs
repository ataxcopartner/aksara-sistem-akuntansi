using System.Data;
using Aksara.Contracts;
using Aksara.Domain;
using Npgsql;

namespace Aksara.Infrastructure;

public sealed class InventoryTransactionRepository(PostgresDatabase database)
{
    public async Task<IReadOnlyList<InventoryDocumentSummaryDto>> ListDocumentsAsync(string? documentType, string? query, int limit = 250, CancellationToken cancellationToken = default)
    {
        const string sql = """
            select * from (
                select t.id, 'TRANSFER'::text document_type, t.document_number, t.document_date, t.status,
                       t.source_warehouse_code source_warehouse, t.destination_warehouse_code destination_warehouse,
                       null::text counterparty, coalesce(sum(l.quantity),0) total_quantity,
                       coalesce(sum(l.quantity*l.unit_cost),0) total_amount, t.created_at
                from inventory_transfer t left join inventory_transfer_line l on l.transfer_id=t.id group by t.id
                union all
                select a.id, 'ADJUSTMENT', a.document_number, a.document_date, a.status, a.warehouse_code, null,
                       ty.name, coalesce(sum(abs(l.quantity)),0), coalesce(sum(abs(l.quantity)*l.unit_cost),0), a.created_at
                from inventory_adjustment a join inventory_adjustment_type ty on ty.code=a.adjustment_type_code
                left join inventory_adjustment_line l on l.adjustment_id=a.id group by a.id,ty.name
                union all
                select s.id, 'STOCK_COUNT', s.document_number, s.document_date, s.status, s.warehouse_code, null, null,
                       coalesce(sum(abs(l.difference_quantity)),0), coalesce(sum(abs(l.difference_quantity)*l.unit_cost),0), s.created_at
                from inventory_stock_count s left join inventory_stock_count_line l on l.stock_count_id=s.id group by s.id
                union all
                select m.id, 'MBAP_SEND', m.document_number, m.document_date, m.status, m.source_warehouse_code, null, b.name,
                       coalesce(sum(l.quantity),0), coalesce(sum(l.quantity*l.unit_cost),0), m.created_at
                from inventory_mbap_send m join business_unit b on b.code=m.destination_business_unit_code
                left join inventory_mbap_send_line l on l.send_id=m.id group by m.id,b.name
                union all
                select r.id, 'MBAP_RECEIVE', r.document_number, r.document_date, r.status, null, r.destination_warehouse_code, s.document_number,
                       coalesce(sum(l.quantity),0), coalesce(sum(l.quantity*l.unit_cost),0), r.created_at
                from inventory_mbap_receive r join inventory_mbap_send s on s.id=r.send_id
                left join inventory_mbap_receive_line l on l.receive_id=r.id group by r.id,s.document_number
            ) d
            where (@type='' or d.document_type=@type)
              and (@query='' or d.document_number ilike '%'||@query||'%' or coalesce(d.counterparty,'') ilike '%'||@query||'%')
            order by d.document_date desc,d.created_at desc limit @limit;
            """;
        await using var command = database.DataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("type", documentType?.Trim().ToUpperInvariant() ?? string.Empty);
        command.Parameters.AddWithValue("query", query?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 1000));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<InventoryDocumentSummaryDto>();
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetFieldValue<DateOnly>(3), reader.GetString(4), Text(reader,5), Text(reader,6), Text(reader,7), reader.GetDecimal(8), reader.GetDecimal(9), reader.GetFieldValue<DateTimeOffset>(10)));
        return result;
    }

    public async Task<InventoryTransferDto> CreateTransferAsync(CreateInventoryTransferRequest request, CancellationToken cancellationToken = default)
    {
        InventoryRules.ValidateTransfer(request.SourceWarehouseCode, request.DestinationWarehouseCode);
        ValidateLines(request.Lines, allowSignedQuantity: false);
        var source = InventoryMasterRepository.NormalizeCode(request.SourceWarehouseCode, "Gudang asal");
        var destination = InventoryMasterRepository.NormalizeCode(request.DestinationWarehouseCode, "Gudang tujuan");
        var lines = NormalizeLines(request.Lines, source, allowSigned: false);
        var createdId = await InSerializableTransactionAsync(async (connection, transaction) =>
        {
            await EnsureWarehouseAsync(connection, transaction, source, cancellationToken);
            await EnsureWarehouseAsync(connection, transaction, destination, cancellationToken);
            var locked = await LockItemsAsync(connection, transaction,
                lines.SelectMany(x => new[] { (x.ItemCode, source), (x.ItemCode, destination) }), cancellationToken);
            foreach (var line in lines)
                if (locked[(line.ItemCode, source)].Balance < line.Quantity)
                    throw StockError(line.ItemCode, source, line.Quantity, locked[(line.ItemCode, source)].Balance);

            var id = Guid.NewGuid(); var number = await NextNumberAsync(connection, transaction, "INV_TRANSFER", "MTS", request.DocumentDate, cancellationToken);
            const string headerSql = """
                insert into inventory_transfer(id,document_number,document_year,document_date,source_warehouse_code,destination_warehouse_code,notes,created_by_name,computer_name)
                values(@id,@number,@year,@date,@source,@destination,@notes,@user,@computer);
                """;
            await InventoryMasterRepository.ExecuteAsync(connection, transaction, headerSql, cancellationToken,
                ("id",id),("number",number),("year",(short)request.DocumentDate.Year),("date",request.DocumentDate),("source",source),("destination",destination),
                ("notes",InventoryMasterRepository.Db(request.Notes)),("user",InventoryMasterRepository.Db(request.UserName)),("computer",InventoryMasterRepository.Db(request.ComputerName)));
            var index = 0;
            foreach (var line in lines)
            {
                var item = locked[(line.ItemCode, source)]; var cost = line.UnitCost > 0 ? line.UnitCost : item.AverageCost;
                cost = await ApplyMovementAsync(connection, transaction, id, number, request.DocumentDate, "INVENTORY_TRANSFER", item, source, -line.Quantity, cost, cancellationToken);
                await InsertDocumentLineAsync(connection, transaction, "inventory_transfer_line", "transfer_id", id, ++index, item, line.Quantity, cost, cancellationToken);
                await ApplyMovementAsync(connection, transaction, id, number, request.DocumentDate, "INVENTORY_TRANSFER", item, destination, line.Quantity, cost, cancellationToken);
            }
            await InventoryMasterRepository.InsertAuditAsync(connection, transaction, "INVENTORY_TRANSFER", id.ToString(), "CREATE", request.UserName, request.ComputerName, request, cancellationToken);
            return id;
        }, "Mutasi Barang berubah karena transaksi bersamaan.", cancellationToken);
        return (await GetTransferAsync(createdId, cancellationToken))!;
    }

    public async Task<InventoryTransferDto?> GetTransferAsync(Guid id, CancellationToken cancellationToken = default)
    {
        const string headerSql = """
            select t.document_number,t.document_date,t.source_warehouse_code,sw.name,t.destination_warehouse_code,dw.name,
                   t.status,t.notes,t.created_at
            from inventory_transfer t join warehouse sw on sw.code=t.source_warehouse_code join warehouse dw on dw.code=t.destination_warehouse_code
            where t.id=@id;
            """;
        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken);
        string number; DateOnly date; string source; string sourceName; string destination; string destinationName; string status; string? notes; DateTimeOffset created;
        await using (var command = new NpgsqlCommand(headerSql, connection))
        {
            command.Parameters.AddWithValue("id",id); await using var reader=await command.ExecuteReaderAsync(cancellationToken);
            if(!await reader.ReadAsync(cancellationToken)) return null;
            number=reader.GetString(0); date=reader.GetFieldValue<DateOnly>(1); source=reader.GetString(2); sourceName=reader.GetString(3); destination=reader.GetString(4); destinationName=reader.GetString(5); status=reader.GetString(6); notes=Text(reader,7); created=reader.GetFieldValue<DateTimeOffset>(8);
        }
        var lines=await ReadLinesAsync(connection,"inventory_transfer_line","transfer_id",id,source,cancellationToken);
        return new(id,number,date,source,sourceName,destination,destinationName,status,notes,lines.Sum(x=>x.Quantity),lines.Sum(x=>x.Amount),created,lines);
    }

    public async Task<InventoryAdjustmentDto> CreateAdjustmentAsync(CreateInventoryAdjustmentRequest request, CancellationToken cancellationToken = default)
    {
        ValidateLines(request.Lines, allowSignedQuantity: true);
        var warehouse=InventoryMasterRepository.NormalizeCode(request.WarehouseCode,"Gudang");
        var typeCode=InventoryMasterRepository.NormalizeCode(request.AdjustmentTypeCode,"Jenis penyesuaian");
        var rawLines=NormalizeLines(request.Lines,warehouse,allowSigned:true);
        var createdId = await InSerializableTransactionAsync(async (connection,transaction)=>
        {
            await EnsureWarehouseAsync(connection,transaction,warehouse,cancellationToken);
            var type=await LockAdjustmentTypeAsync(connection,transaction,typeCode,cancellationToken) ?? throw new InventoryValidationException($"Jenis penyesuaian {typeCode} tidak ditemukan atau tidak aktif.");
            var lines=rawLines.Select(x=>x with {Quantity=InventoryRules.ResolveAdjustmentQuantity(type.Direction,x.Quantity)}).ToArray();
            var locked=await LockItemsAsync(connection,transaction,lines.Select(x=>(x.ItemCode,warehouse)),cancellationToken);
            foreach(var line in lines)
            {
                var current=locked[(line.ItemCode,warehouse)];
                if(type.IsOpeningBalance && current.Balance!=0) throw new InventoryValidationException($"Saldo Awal {line.ItemCode} hanya dapat dimasukkan ketika stok gudang masih nol.");
                if(line.Quantity<0 && current.Balance < -line.Quantity) throw StockError(line.ItemCode,warehouse,-line.Quantity,current.Balance);
            }
            var id=Guid.NewGuid(); var number=await NextNumberAsync(connection,transaction,"INV_ADJUSTMENT","PSY",request.DocumentDate,cancellationToken);
            const string headerSql="""
                insert into inventory_adjustment(id,document_number,document_year,document_date,adjustment_type_code,warehouse_code,notes,created_by_name,computer_name)
                values(@id,@number,@year,@date,@type,@warehouse,@notes,@user,@computer);
                """;
            await InventoryMasterRepository.ExecuteAsync(connection,transaction,headerSql,cancellationToken,("id",id),("number",number),("year",(short)request.DocumentDate.Year),("date",request.DocumentDate),("type",typeCode),("warehouse",warehouse),("notes",InventoryMasterRepository.Db(request.Notes)),("user",InventoryMasterRepository.Db(request.UserName)),("computer",InventoryMasterRepository.Db(request.ComputerName)));
            var index=0;
            foreach(var line in lines)
            {
                var item=locked[(line.ItemCode,warehouse)]; var cost=line.UnitCost>0?line.UnitCost:item.AverageCost;
                await InsertSignedAdjustmentLineAsync(connection,transaction,id,++index,item,line.Quantity,cost,cancellationToken);
                await ApplyMovementAsync(connection,transaction,id,number,request.DocumentDate,"INVENTORY_ADJUSTMENT",item,warehouse,line.Quantity,cost,cancellationToken);
            }
            await InventoryMasterRepository.InsertAuditAsync(connection,transaction,"INVENTORY_ADJUSTMENT",id.ToString(),"CREATE",request.UserName,request.ComputerName,request,cancellationToken);
            return id;
        },"Penyesuaian Persediaan berubah karena transaksi bersamaan.",cancellationToken);
        return (await GetAdjustmentAsync(createdId,cancellationToken))!;
    }

    public async Task<InventoryAdjustmentDto?> GetAdjustmentAsync(Guid id,CancellationToken cancellationToken=default)
    {
        const string sql="""
            select a.document_number,a.document_date,a.adjustment_type_code,t.name,t.direction,a.warehouse_code,w.name,a.status,a.notes,a.created_at
            from inventory_adjustment a join inventory_adjustment_type t on t.code=a.adjustment_type_code join warehouse w on w.code=a.warehouse_code where a.id=@id;
            """;
        await using var connection=await database.DataSource.OpenConnectionAsync(cancellationToken);
        string number;DateOnly date;string type;string typeName;string direction;string warehouse;string warehouseName;string status;string? notes;DateTimeOffset created;
        await using(var command=new NpgsqlCommand(sql,connection)){command.Parameters.AddWithValue("id",id);await using var reader=await command.ExecuteReaderAsync(cancellationToken);if(!await reader.ReadAsync(cancellationToken))return null;number=reader.GetString(0);date=reader.GetFieldValue<DateOnly>(1);type=reader.GetString(2);typeName=reader.GetString(3);direction=reader.GetString(4);warehouse=reader.GetString(5);warehouseName=reader.GetString(6);status=reader.GetString(7);notes=Text(reader,8);created=reader.GetFieldValue<DateTimeOffset>(9);}
        var lines=await ReadLinesAsync(connection,"inventory_adjustment_line","adjustment_id",id,warehouse,cancellationToken,signed:true);
        return new(id,number,date,type,typeName,direction,warehouse,warehouseName,status,notes,lines.Sum(x=>Math.Abs(x.Quantity)),lines.Sum(x=>Math.Abs(x.Amount)),created,lines);
    }

    public async Task<InventoryStockCountDto> CreateStockCountAsync(CreateInventoryStockCountRequest request,CancellationToken cancellationToken=default)
    {
        if(request.Lines.Count==0)throw new InventoryValidationException("Minimal satu Barang harus dihitung.");
        var warehouse=InventoryMasterRepository.NormalizeCode(request.WarehouseCode,"Gudang");
        var input=request.Lines.Select((x,i)=>new CountInput(i+1,InventoryMasterRepository.NormalizeCode(x.ItemCode,"Kode Barang"),x.CountedQuantity,x.UnitCost)).ToArray();
        if(input.Any(x=>x.CountedQuantity<0))throw new InventoryValidationException("Hasil Stok Fisik tidak boleh negatif.");
        if(input.Any(x=>x.UnitCost<0))throw new InventoryValidationException("HPP Stok Fisik tidak boleh negatif.");
        if(input.GroupBy(x=>x.ItemCode).Any(x=>x.Count()>1))throw new InventoryValidationException("Satu Barang hanya boleh muncul sekali pada Stok Fisik.");
        var createdId = await InSerializableTransactionAsync(async(connection,transaction)=>
        {
            await EnsureWarehouseAsync(connection,transaction,warehouse,cancellationToken);
            var locked=await LockItemsAsync(connection,transaction,input.Select(x=>(x.ItemCode,warehouse)),cancellationToken);
            var id=Guid.NewGuid();var number=await NextNumberAsync(connection,transaction,"INV_COUNT","STF",request.DocumentDate,cancellationToken);
            const string h="insert into inventory_stock_count(id,document_number,document_year,document_date,warehouse_code,notes,created_by_name,computer_name) values(@id,@number,@year,@date,@warehouse,@notes,@user,@computer);";
            await InventoryMasterRepository.ExecuteAsync(connection,transaction,h,cancellationToken,("id",id),("number",number),("year",(short)request.DocumentDate.Year),("date",request.DocumentDate),("warehouse",warehouse),("notes",InventoryMasterRepository.Db(request.Notes)),("user",InventoryMasterRepository.Db(request.UserName)),("computer",InventoryMasterRepository.Db(request.ComputerName)));
            foreach(var line in input)
            {
                var item=locked[(line.ItemCode,warehouse)];var difference=InventoryRules.CalculateStockCountDifference(item.Balance,line.CountedQuantity);var cost=line.UnitCost>0?line.UnitCost:item.AverageCost;
                const string l="""
                    insert into inventory_stock_count_line(stock_count_id,line_number,item_id,system_quantity,counted_quantity,difference_quantity,unit,unit_cost)
                    values(@id,@line,@item,@system,@counted,@difference,@unit,@cost);
                    """;
                await InventoryMasterRepository.ExecuteAsync(connection,transaction,l,cancellationToken,("id",id),("line",line.LineNumber),("item",item.Id),("system",item.Balance),("counted",line.CountedQuantity),("difference",difference),("unit",item.Unit),("cost",cost));
                if(difference!=0)await ApplyMovementAsync(connection,transaction,id,number,request.DocumentDate,"INVENTORY_STOCK_COUNT",item,warehouse,difference,cost,cancellationToken);
            }
            await InventoryMasterRepository.InsertAuditAsync(connection,transaction,"INVENTORY_STOCK_COUNT",id.ToString(),"CREATE",request.UserName,request.ComputerName,request,cancellationToken);return id;
        },"Stok berubah saat Stok Fisik disimpan. Silakan hitung ulang.",cancellationToken);
        return (await GetStockCountAsync(createdId,cancellationToken))!;
    }

    public async Task<InventoryStockCountDto?> GetStockCountAsync(Guid id,CancellationToken cancellationToken=default)
    {
        const string h="select s.document_number,s.document_date,s.warehouse_code,w.name,s.status,s.notes,s.created_at from inventory_stock_count s join warehouse w on w.code=s.warehouse_code where s.id=@id;";
        await using var connection=await database.DataSource.OpenConnectionAsync(cancellationToken);string number;DateOnly date;string warehouse;string warehouseName;string status;string? notes;DateTimeOffset created;
        await using(var command=new NpgsqlCommand(h,connection)){command.Parameters.AddWithValue("id",id);await using var reader=await command.ExecuteReaderAsync(cancellationToken);if(!await reader.ReadAsync(cancellationToken))return null;number=reader.GetString(0);date=reader.GetFieldValue<DateOnly>(1);warehouse=reader.GetString(2);warehouseName=reader.GetString(3);status=reader.GetString(4);notes=Text(reader,5);created=reader.GetFieldValue<DateTimeOffset>(6);}
        const string l="select l.id,l.line_number,i.code,i.name,i.unit,l.system_quantity,l.counted_quantity,l.difference_quantity,l.unit_cost from inventory_stock_count_line l join inventory_item i on i.id=l.item_id where l.stock_count_id=@id order by l.line_number;";
        var lines=new List<InventoryStockCountLineDto>();await using(var command=new NpgsqlCommand(l,connection)){command.Parameters.AddWithValue("id",id);await using var reader=await command.ExecuteReaderAsync(cancellationToken);while(await reader.ReadAsync(cancellationToken)){var diff=reader.GetDecimal(7);var cost=reader.GetDecimal(8);lines.Add(new(reader.GetGuid(0),reader.GetInt32(1),reader.GetString(2),reader.GetString(3),reader.GetString(4),reader.GetDecimal(5),reader.GetDecimal(6),diff,cost,diff*cost));}}
        return new(id,number,date,warehouse,warehouseName,status,notes,lines.Sum(x=>x.DifferenceQuantity),lines.Sum(x=>x.DifferenceAmount),created,lines);
    }

    public async Task<InventoryMbapSendDto> CreateMbapSendAsync(CreateInventoryMbapSendRequest request,CancellationToken cancellationToken=default)
    {
        ValidateLines(request.Lines,false);var warehouse=InventoryMasterRepository.NormalizeCode(request.SourceWarehouseCode,"Gudang asal");var unit=InventoryMasterRepository.NormalizeCode(request.DestinationBusinessUnitCode,"Wilayah usaha tujuan");var lines=NormalizeLines(request.Lines,warehouse,false);
        var createdId = await InSerializableTransactionAsync(async(connection,transaction)=>
        {
            await EnsureWarehouseAsync(connection,transaction,warehouse,cancellationToken);await EnsureBusinessUnitAsync(connection,transaction,unit,cancellationToken);
            var locked=await LockItemsAsync(connection,transaction,lines.Select(x=>(x.ItemCode,warehouse)),cancellationToken);foreach(var line in lines)if(locked[(line.ItemCode,warehouse)].Balance<line.Quantity)throw StockError(line.ItemCode,warehouse,line.Quantity,locked[(line.ItemCode,warehouse)].Balance);
            var id=Guid.NewGuid();var number=await NextNumberAsync(connection,transaction,"MBAP_SEND","PSD",request.DocumentDate,cancellationToken);
            const string h="""
                insert into inventory_mbap_send(id,document_number,document_year,document_date,source_warehouse_code,destination_business_unit_code,settlement_date,bank_code,notes,created_by_name,computer_name)
                values(@id,@number,@year,@date,@warehouse,@unit,@settlement,@bank,@notes,@user,@computer);
                """;
            await InventoryMasterRepository.ExecuteAsync(connection,transaction,h,cancellationToken,("id",id),("number",number),("year",(short)request.DocumentDate.Year),("date",request.DocumentDate),("warehouse",warehouse),("unit",unit),("settlement",(object?)request.SettlementDate??DBNull.Value),("bank",InventoryMasterRepository.Db(request.BankCode)),("notes",InventoryMasterRepository.Db(request.Notes)),("user",InventoryMasterRepository.Db(request.UserName)),("computer",InventoryMasterRepository.Db(request.ComputerName)));
            var index=0;foreach(var line in lines){var item=locked[(line.ItemCode,warehouse)];var cost=line.UnitCost>0?line.UnitCost:item.AverageCost;cost=await ApplyMovementAsync(connection,transaction,id,number,request.DocumentDate,"MBAP_SEND",item,warehouse,-line.Quantity,cost,cancellationToken);await InsertDocumentLineAsync(connection,transaction,"inventory_mbap_send_line","send_id",id,++index,item,line.Quantity,cost,cancellationToken);}
            await InventoryMasterRepository.InsertAuditAsync(connection,transaction,"INVENTORY_MBAP_SEND",id.ToString(),"CREATE",request.UserName,request.ComputerName,request,cancellationToken);return id;
        },"MBAP Kirim berubah karena transaksi bersamaan.",cancellationToken);
        return (await GetMbapSendAsync(createdId,cancellationToken))!;
    }

    public async Task<InventoryMbapSendDto?> GetMbapSendAsync(Guid id,CancellationToken cancellationToken=default)
    {
        const string h="select s.document_number,s.document_date,s.source_warehouse_code,s.destination_business_unit_code,b.name,s.settlement_date,s.bank_code,s.status,s.notes,s.created_at from inventory_mbap_send s join business_unit b on b.code=s.destination_business_unit_code where s.id=@id;";
        await using var connection=await database.DataSource.OpenConnectionAsync(cancellationToken);string number;DateOnly date;string warehouse;string unit;string unitName;DateOnly? settlement;string? bank;string status;string? notes;DateTimeOffset created;
        await using(var command=new NpgsqlCommand(h,connection)){command.Parameters.AddWithValue("id",id);await using var reader=await command.ExecuteReaderAsync(cancellationToken);if(!await reader.ReadAsync(cancellationToken))return null;number=reader.GetString(0);date=reader.GetFieldValue<DateOnly>(1);warehouse=reader.GetString(2);unit=reader.GetString(3);unitName=reader.GetString(4);settlement=reader.IsDBNull(5)?null:reader.GetFieldValue<DateOnly>(5);bank=Text(reader,6);status=reader.GetString(7);notes=Text(reader,8);created=reader.GetFieldValue<DateTimeOffset>(9);}
        var lines=await ReadLinesAsync(connection,"inventory_mbap_send_line","send_id",id,warehouse,cancellationToken);return new(id,number,date,warehouse,unit,unitName,settlement,bank,status,notes,lines.Sum(x=>x.Quantity),lines.Sum(x=>x.Amount),created,lines);
    }

    public async Task<InventoryMbapReceiveSourceDto?> GetMbapReceiveSourceAsync(Guid sendId,CancellationToken cancellationToken=default)
    {
        const string h="select s.document_number,s.document_date,s.source_warehouse_code,s.destination_business_unit_code,b.name,s.status from inventory_mbap_send s join business_unit b on b.code=s.destination_business_unit_code where s.id=@id;";
        await using var connection=await database.DataSource.OpenConnectionAsync(cancellationToken);string number;DateOnly date;string warehouse;string unit;string unitName;string status;
        await using(var command=new NpgsqlCommand(h,connection)){command.Parameters.AddWithValue("id",sendId);await using var reader=await command.ExecuteReaderAsync(cancellationToken);if(!await reader.ReadAsync(cancellationToken))return null;number=reader.GetString(0);date=reader.GetFieldValue<DateOnly>(1);warehouse=reader.GetString(2);unit=reader.GetString(3);unitName=reader.GetString(4);status=reader.GetString(5);}
        if(status=="CANCELLED")return null;
        const string l="""
            select sl.id,i.code,i.name,i.unit,sl.quantity,
                   coalesce(sum(case when r.status='POSTED' then rl.quantity else 0 end),0),sl.unit_cost
            from inventory_mbap_send_line sl join inventory_item i on i.id=sl.item_id
            left join inventory_mbap_receive_line rl on rl.send_line_id=sl.id
            left join inventory_mbap_receive r on r.id=rl.receive_id
            where sl.send_id=@id group by sl.id,i.code,i.name,i.unit order by sl.line_number;
            """;
        var lines=new List<InventoryMbapReceiveSourceLineDto>();await using(var command=new NpgsqlCommand(l,connection)){command.Parameters.AddWithValue("id",sendId);await using var reader=await command.ExecuteReaderAsync(cancellationToken);while(await reader.ReadAsync(cancellationToken)){var sent=reader.GetDecimal(4);var received=reader.GetDecimal(5);lines.Add(new(reader.GetGuid(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),sent,received,sent-received,reader.GetDecimal(6)));}}
        return new(sendId,number,date,warehouse,unit,unitName,lines);
    }

    public async Task<InventoryMbapReceiveDto> CreateMbapReceiveAsync(CreateInventoryMbapReceiveRequest request,CancellationToken cancellationToken=default)
    {
        if(request.Lines.Count==0)throw new InventoryValidationException("Minimal satu rincian MBAP Terima wajib diisi.");var warehouse=InventoryMasterRepository.NormalizeCode(request.DestinationWarehouseCode,"Gudang penerima");
        var createdId = await InSerializableTransactionAsync(async(connection,transaction)=>
        {
            await EnsureWarehouseAsync(connection,transaction,warehouse,cancellationToken);
            string sendNumber;string sendStatus;await using(var command=new NpgsqlCommand("select document_number,status from inventory_mbap_send where id=@id for update;",connection,transaction)){command.Parameters.AddWithValue("id",request.SendId);await using var reader=await command.ExecuteReaderAsync(cancellationToken);if(!await reader.ReadAsync(cancellationToken))throw new InventoryValidationException("MBAP Kirim tidak ditemukan.");sendNumber=reader.GetString(0);sendStatus=reader.GetString(1);}if(sendStatus=="CANCELLED")throw new InventoryValidationException("MBAP Kirim sudah dibatalkan.");
            var sourceLines=await LockMbapSendLinesAsync(connection,transaction,request.SendId,cancellationToken);var requested=request.Lines.GroupBy(x=>x.SendLineId).ToDictionary(x=>x.Key,x=>x.Sum(y=>y.Quantity));foreach(var pair in requested){if(!sourceLines.TryGetValue(pair.Key,out var source))throw new InventoryValidationException("Rincian MBAP Kirim tidak ditemukan.");InventoryRules.ValidateMbapReceipt(source.SentQuantity,source.ReceivedQuantity,pair.Value);}
            var locked=await LockItemsAsync(connection,transaction,requested.Keys.Select(id=>(sourceLines[id].ItemCode,warehouse)),cancellationToken);
            var id=Guid.NewGuid();var number=await NextNumberAsync(connection,transaction,"MBAP_RECEIVE","PST",request.DocumentDate,cancellationToken);
            const string h="insert into inventory_mbap_receive(id,document_number,document_year,document_date,send_id,destination_warehouse_code,notes,created_by_name,computer_name) values(@id,@number,@year,@date,@send,@warehouse,@notes,@user,@computer);";
            await InventoryMasterRepository.ExecuteAsync(connection,transaction,h,cancellationToken,("id",id),("number",number),("year",(short)request.DocumentDate.Year),("date",request.DocumentDate),("send",request.SendId),("warehouse",warehouse),("notes",InventoryMasterRepository.Db(request.Notes)),("user",InventoryMasterRepository.Db(request.UserName)),("computer",InventoryMasterRepository.Db(request.ComputerName)));
            var index=0;foreach(var pair in requested.OrderBy(x=>sourceLines[x.Key].ItemCode)){var source=sourceLines[pair.Key];var item=locked[(source.ItemCode,warehouse)];const string l="insert into inventory_mbap_receive_line(receive_id,send_line_id,line_number,item_id,quantity,unit,unit_cost) values(@receive,@sendLine,@line,@item,@quantity,@unit,@cost);";await InventoryMasterRepository.ExecuteAsync(connection,transaction,l,cancellationToken,("receive",id),("sendLine",pair.Key),("line",++index),("item",item.Id),("quantity",pair.Value),("unit",item.Unit),("cost",source.UnitCost));await ApplyMovementAsync(connection,transaction,id,number,request.DocumentDate,"MBAP_RECEIVE",item,warehouse,pair.Value,source.UnitCost,cancellationToken);}
            await RefreshMbapSendStatusAsync(connection,transaction,request.SendId,cancellationToken);await InventoryMasterRepository.InsertAuditAsync(connection,transaction,"INVENTORY_MBAP_RECEIVE",id.ToString(),"CREATE",request.UserName,request.ComputerName,request,cancellationToken);return id;
        },"MBAP Kirim diterima pengguna lain pada saat bersamaan. Muat ulang sumber kirim.",cancellationToken);
        return (await GetMbapReceiveAsync(createdId,cancellationToken))!;
    }

    public async Task<InventoryMbapReceiveDto?> GetMbapReceiveAsync(Guid id,CancellationToken cancellationToken=default)
    {
        const string h="select r.document_number,r.document_date,r.send_id,s.document_number,r.destination_warehouse_code,r.status,r.notes,r.created_at from inventory_mbap_receive r join inventory_mbap_send s on s.id=r.send_id where r.id=@id;";
        await using var connection=await database.DataSource.OpenConnectionAsync(cancellationToken);string number;DateOnly date;Guid sendId;string sendNumber;string warehouse;string status;string? notes;DateTimeOffset created;
        await using(var command=new NpgsqlCommand(h,connection)){command.Parameters.AddWithValue("id",id);await using var reader=await command.ExecuteReaderAsync(cancellationToken);if(!await reader.ReadAsync(cancellationToken))return null;number=reader.GetString(0);date=reader.GetFieldValue<DateOnly>(1);sendId=reader.GetGuid(2);sendNumber=reader.GetString(3);warehouse=reader.GetString(4);status=reader.GetString(5);notes=Text(reader,6);created=reader.GetFieldValue<DateTimeOffset>(7);}
        var lines=await ReadLinesAsync(connection,"inventory_mbap_receive_line","receive_id",id,warehouse,cancellationToken);return new(id,number,date,sendId,sendNumber,warehouse,status,notes,lines.Sum(x=>x.Quantity),lines.Sum(x=>x.Amount),created,lines);
    }

    public Task<InventoryTransferDto> CancelTransferAsync(Guid id,CancelInventoryDocumentRequest request,CancellationToken cancellationToken=default)=>CancelAsync(id,request,"inventory_transfer","inventory_transfer_line","transfer_id","INVENTORY_TRANSFER","INVENTORY_TRANSFER_CANCEL",async()=> (await GetTransferAsync(id,cancellationToken))!,cancellationToken);
    public async Task<InventoryAdjustmentDto> CancelAdjustmentAsync(Guid id,CancelInventoryDocumentRequest request,CancellationToken cancellationToken=default)
    {
        await using(var command=database.DataSource.CreateCommand("select exists(select 1 from initial_setup_posting where entity_type='INVENTORY_ADJUSTMENT' and entity_id=@id);"))
        {
            command.Parameters.AddWithValue("id",id);
            if((bool)(await command.ExecuteScalarAsync(cancellationToken))!)
                throw new InventoryValidationException("Saldo awal dari Setup Data Awal hanya dapat diubah melalui reopen setup atau transaksi koreksi.");
        }
        return await CancelAsync(id,request,"inventory_adjustment","inventory_adjustment_line","adjustment_id","INVENTORY_ADJUSTMENT","INVENTORY_ADJUSTMENT_CANCEL",async()=> (await GetAdjustmentAsync(id,cancellationToken))!,cancellationToken);
    }
    public Task<InventoryStockCountDto> CancelStockCountAsync(Guid id,CancelInventoryDocumentRequest request,CancellationToken cancellationToken=default)=>CancelAsync(id,request,"inventory_stock_count","inventory_stock_count_line","stock_count_id","INVENTORY_STOCK_COUNT","INVENTORY_STOCK_COUNT_CANCEL",async()=> (await GetStockCountAsync(id,cancellationToken))!,cancellationToken);
    public Task<InventoryMbapSendDto> CancelMbapSendAsync(Guid id,CancelInventoryDocumentRequest request,CancellationToken cancellationToken=default)=>CancelMbapSendInternalAsync(id,request,cancellationToken);
    public Task<InventoryMbapReceiveDto> CancelMbapReceiveAsync(Guid id,CancelInventoryDocumentRequest request,CancellationToken cancellationToken=default)=>CancelMbapReceiveInternalAsync(id,request,cancellationToken);

    private async Task<T> CancelAsync<T>(Guid id,CancelInventoryDocumentRequest request,string table,string lineTable,string foreignKey,string sourceType,string cancelType,Func<Task<T>> reload,CancellationToken cancellationToken)
    {
        ValidateCancellation(request);await InSerializableTransactionAsync(async(connection,transaction)=>
        {
            var header=await LockHeaderAsync(connection,transaction,table,id,cancellationToken);if(header.Status=="CANCELLED")throw new InventoryConflictException("Dokumen sudah dibatalkan pengguna lain.");
            var movements=await LockSourceMovementsAsync(connection,transaction,id,sourceType,cancellationToken);
            foreach(var movement in movements.OrderBy(x=>x.ItemCode).ThenBy(x=>x.WarehouseCode)){var item=(await LockItemsAsync(connection,transaction,[(movement.ItemCode,movement.WarehouseCode)],cancellationToken))[(movement.ItemCode,movement.WarehouseCode)];var reverse=movement.QuantityOut-movement.QuantityIn;if(reverse<0 && item.Balance < -reverse)throw StockError(item.Code,movement.WarehouseCode,-reverse,item.Balance);await ApplyMovementAsync(connection,transaction,id,header.Number,header.Date,cancelType,item,movement.WarehouseCode,reverse,movement.UnitCost,cancellationToken);}
            await MarkCancelledAsync(connection,transaction,table,id,request,cancellationToken);await InventoryMasterRepository.InsertAuditAsync(connection,transaction,table.ToUpperInvariant(),id.ToString(),"CANCEL",request.UserName,request.ComputerName,request,cancellationToken);return true;
        },"Stok berubah saat pembatalan diproses.",cancellationToken);return await reload();
    }

    private async Task<InventoryMbapSendDto> CancelMbapSendInternalAsync(Guid id,CancelInventoryDocumentRequest request,CancellationToken cancellationToken)
    {
        ValidateCancellation(request);await InSerializableTransactionAsync(async(connection,transaction)=>{var header=await LockHeaderAsync(connection,transaction,"inventory_mbap_send",id,cancellationToken);if(header.Status=="CANCELLED")throw new InventoryConflictException("MBAP Kirim sudah dibatalkan.");await using(var check=new NpgsqlCommand("select exists(select 1 from inventory_mbap_receive where send_id=@id and status='POSTED');",connection,transaction)){check.Parameters.AddWithValue("id",id);if((bool)(await check.ExecuteScalarAsync(cancellationToken))!)throw new InventoryValidationException("MBAP Kirim yang sudah diterima tidak dapat dibatalkan. Batalkan MBAP Terima terlebih dahulu.");}var movements=await LockSourceMovementsAsync(connection,transaction,id,"MBAP_SEND",cancellationToken);foreach(var movement in movements){var item=(await LockItemsAsync(connection,transaction,[(movement.ItemCode,movement.WarehouseCode)],cancellationToken))[(movement.ItemCode,movement.WarehouseCode)];await ApplyMovementAsync(connection,transaction,id,header.Number,header.Date,"MBAP_SEND_CANCEL",item,movement.WarehouseCode,movement.QuantityOut,movement.UnitCost,cancellationToken);}await MarkCancelledAsync(connection,transaction,"inventory_mbap_send",id,request,cancellationToken);return true;},"MBAP Kirim berubah saat dibatalkan.",cancellationToken);return (await GetMbapSendAsync(id,cancellationToken))!;
    }

    private async Task<InventoryMbapReceiveDto> CancelMbapReceiveInternalAsync(Guid id,CancelInventoryDocumentRequest request,CancellationToken cancellationToken)
    {
        ValidateCancellation(request);Guid sendId=Guid.Empty;await InSerializableTransactionAsync(async(connection,transaction)=>{var header=await LockHeaderAsync(connection,transaction,"inventory_mbap_receive",id,cancellationToken);if(header.Status=="CANCELLED")throw new InventoryConflictException("MBAP Terima sudah dibatalkan.");await using(var c=new NpgsqlCommand("select send_id from inventory_mbap_receive where id=@id;",connection,transaction)){c.Parameters.AddWithValue("id",id);sendId=(Guid)(await c.ExecuteScalarAsync(cancellationToken))!;}var movements=await LockSourceMovementsAsync(connection,transaction,id,"MBAP_RECEIVE",cancellationToken);foreach(var movement in movements){var item=(await LockItemsAsync(connection,transaction,[(movement.ItemCode,movement.WarehouseCode)],cancellationToken))[(movement.ItemCode,movement.WarehouseCode)];if(item.Balance<movement.QuantityIn)throw StockError(item.Code,movement.WarehouseCode,movement.QuantityIn,item.Balance);await ApplyMovementAsync(connection,transaction,id,header.Number,header.Date,"MBAP_RECEIVE_CANCEL",item,movement.WarehouseCode,-movement.QuantityIn,movement.UnitCost,cancellationToken);}await MarkCancelledAsync(connection,transaction,"inventory_mbap_receive",id,request,cancellationToken);await RefreshMbapSendStatusAsync(connection,transaction,sendId,cancellationToken);return true;},"Stok MBAP Terima berubah saat dibatalkan.",cancellationToken);return (await GetMbapReceiveAsync(id,cancellationToken))!;
    }

    private async Task<TResult> InSerializableTransactionAsync<TResult>(Func<NpgsqlConnection,NpgsqlTransaction,Task<TResult>> operation,string conflictMessage,CancellationToken cancellationToken)
    {
        await using var connection=await database.DataSource.OpenConnectionAsync(cancellationToken);await using var transaction=await connection.BeginTransactionAsync(IsolationLevel.Serializable,cancellationToken);
        try{var result=await operation(connection,transaction);await transaction.CommitAsync(cancellationToken);return result;}
        catch(PostgresException ex)when(ex.SqlState==PostgresErrorCodes.SerializationFailure){await transaction.RollbackAsync(cancellationToken);throw new InventoryConflictException(conflictMessage);}
        catch{await transaction.RollbackAsync(cancellationToken);throw;}
    }

    private static async Task<Dictionary<(string ItemCode,string WarehouseCode),LockedItem>> LockItemsAsync(NpgsqlConnection connection,NpgsqlTransaction transaction,IEnumerable<(string ItemCode,string WarehouseCode)> keys,CancellationToken cancellationToken)
    {
        var result=new Dictionary<(string,string),LockedItem>();foreach(var key in keys.Select(x=>(InventoryMasterRepository.NormalizeCode(x.ItemCode,"Kode Barang"),InventoryMasterRepository.NormalizeCode(x.WarehouseCode,"Gudang"))).Distinct().OrderBy(x=>x.Item1).ThenBy(x=>x.Item2))
        {
            Guid itemId;await using(var find=new NpgsqlCommand("select id from inventory_item where code=@code and is_active;",connection,transaction)){find.Parameters.AddWithValue("code",key.Item1);var raw=await find.ExecuteScalarAsync(cancellationToken);if(raw is not Guid id)throw new InventoryValidationException($"Barang {key.Item1} tidak ditemukan atau tidak aktif.");itemId=id;}
            await InventoryMasterRepository.ExecuteAsync(connection,transaction,"insert into inventory_balance(item_id,warehouse_code,quantity) values(@item,@warehouse,0) on conflict do nothing;",cancellationToken,("item",itemId),("warehouse",key.Item2));
            const string lockSql="select i.id,i.code,i.name,i.unit,i.average_cost,b.quantity from inventory_item i join inventory_balance b on b.item_id=i.id and b.warehouse_code=@warehouse where i.id=@id for update of i,b;";
            await using var command=new NpgsqlCommand(lockSql,connection,transaction);command.Parameters.AddWithValue("id",itemId);command.Parameters.AddWithValue("warehouse",key.Item2);await using var reader=await command.ExecuteReaderAsync(cancellationToken);await reader.ReadAsync(cancellationToken);result[key]=new(reader.GetGuid(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.GetDecimal(4),reader.GetDecimal(5));
        }return result;
    }

    private static async Task<decimal> ApplyMovementAsync(NpgsqlConnection connection,NpgsqlTransaction transaction,Guid sourceId,string sourceNumber,DateOnly date,string sourceType,LockedItem item,string warehouse,decimal signedQuantity,decimal unitCost,CancellationToken cancellationToken)
    {
        if(signedQuantity==0)return unitCost;
        if(signedQuantity<0)
        {
            var issue=await FifoInventoryCosting.IssueAsync(connection,transaction,item.Id,warehouse,sourceType,sourceId,null,-signedQuantity,cancellationToken);
            unitCost=issue.UnitCost;
        }
        await InventoryMasterRepository.ExecuteAsync(connection,transaction,"update inventory_balance set quantity=quantity+@quantity,version=version+1,updated_at=now() where item_id=@item and warehouse_code=@warehouse;",cancellationToken,("quantity",signedQuantity),("item",item.Id),("warehouse",warehouse));
        const string sql="""
            insert into inventory_movement(occurred_at,item_id,warehouse_code,source_type,source_id,source_number,quantity_in,quantity_out,unit_cost)
            values(@at,@item,@warehouse,@type,@source,@number,@in,@out,@cost);
            """;
        await InventoryMasterRepository.ExecuteAsync(connection,transaction,sql,cancellationToken,("at",new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue),TimeSpan.Zero)),("item",item.Id),("warehouse",warehouse),("type",sourceType),("source",sourceId),("number",sourceNumber),("in",Math.Max(0,signedQuantity)),("out",Math.Max(0,-signedQuantity)),("cost",unitCost));
        if(signedQuantity>0)
            await FifoInventoryCosting.ReceiveAsync(connection,transaction,item.Id,warehouse,sourceType,sourceId,null,sourceNumber,date,signedQuantity,unitCost,cancellationToken);
        return unitCost;
    }

    private static async Task<string> NextNumberAsync(NpgsqlConnection connection,NpgsqlTransaction transaction,string documentType,string prefix,DateOnly date,CancellationToken cancellationToken)
    {
        const string sql="""
            insert into document_sequence(document_type,period,last_number) values(@type,@period,1)
            on conflict(document_type,period) do update set last_number=document_sequence.last_number+1 returning last_number;
            """;
        await using var command=new NpgsqlCommand(sql,connection,transaction);command.Parameters.AddWithValue("type",documentType);command.Parameters.AddWithValue("period",date.ToString("yyyyMM",System.Globalization.CultureInfo.InvariantCulture));var value=Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));return $"{prefix}{date:yyMM}{value:0000}";
    }

    private static async Task InsertDocumentLineAsync(NpgsqlConnection connection,NpgsqlTransaction transaction,string table,string foreignKey,Guid documentId,int lineNumber,LockedItem item,decimal quantity,decimal cost,CancellationToken cancellationToken)
    {await InventoryMasterRepository.ExecuteAsync(connection,transaction,$"insert into {table}({foreignKey},line_number,item_id,quantity,unit,unit_cost) values(@document,@line,@item,@quantity,@unit,@cost);",cancellationToken,("document",documentId),("line",lineNumber),("item",item.Id),("quantity",quantity),("unit",item.Unit),("cost",cost));}
    private static async Task InsertSignedAdjustmentLineAsync(NpgsqlConnection connection,NpgsqlTransaction transaction,Guid id,int line,LockedItem item,decimal quantity,decimal cost,CancellationToken cancellationToken)=>await InventoryMasterRepository.ExecuteAsync(connection,transaction,"insert into inventory_adjustment_line(adjustment_id,line_number,item_id,quantity,unit,unit_cost) values(@id,@line,@item,@quantity,@unit,@cost);",cancellationToken,("id",id),("line",line),("item",item.Id),("quantity",quantity),("unit",item.Unit),("cost",cost));

    private static async Task<IReadOnlyList<InventoryDocumentLineDto>> ReadLinesAsync(NpgsqlConnection connection,string table,string foreignKey,Guid id,string warehouse,CancellationToken cancellationToken,bool signed=false)
    {
        var sql=$"select l.id,l.line_number,i.code,i.name,l.quantity,i.unit,l.unit_cost from {table} l join inventory_item i on i.id=l.item_id where l.{foreignKey}=@id order by l.line_number;";var lines=new List<InventoryDocumentLineDto>();await using var command=new NpgsqlCommand(sql,connection);command.Parameters.AddWithValue("id",id);await using var reader=await command.ExecuteReaderAsync(cancellationToken);while(await reader.ReadAsync(cancellationToken)){var q=reader.GetDecimal(4);var c=reader.GetDecimal(6);lines.Add(new(reader.GetGuid(0),reader.GetInt32(1),reader.GetString(2),reader.GetString(3),warehouse,q,reader.GetString(5),c,q*c));}return lines;
    }

    private static async Task EnsureWarehouseAsync(NpgsqlConnection connection,NpgsqlTransaction transaction,string code,CancellationToken cancellationToken){await using var command=new NpgsqlCommand("select exists(select 1 from warehouse where code=@code and is_active);",connection,transaction);command.Parameters.AddWithValue("code",code);if(!(bool)(await command.ExecuteScalarAsync(cancellationToken))!)throw new InventoryValidationException($"Gudang {code} tidak ditemukan atau tidak aktif.");}
    private static async Task EnsureBusinessUnitAsync(NpgsqlConnection connection,NpgsqlTransaction transaction,string code,CancellationToken cancellationToken){await using var command=new NpgsqlCommand("select exists(select 1 from business_unit where code=@code and is_active);",connection,transaction);command.Parameters.AddWithValue("code",code);if(!(bool)(await command.ExecuteScalarAsync(cancellationToken))!)throw new InventoryValidationException($"Wilayah usaha {code} tidak ditemukan atau tidak aktif.");}
    private static async Task<AdjustmentTypeLock?> LockAdjustmentTypeAsync(NpgsqlConnection connection,NpgsqlTransaction transaction,string code,CancellationToken cancellationToken){await using var command=new NpgsqlCommand("select direction,requires_value,is_opening_balance from inventory_adjustment_type where code=@code and is_active for update;",connection,transaction);command.Parameters.AddWithValue("code",code);await using var reader=await command.ExecuteReaderAsync(cancellationToken);return await reader.ReadAsync(cancellationToken)?new(reader.GetString(0),reader.GetBoolean(1),reader.GetBoolean(2)):null;}
    private static async Task<HeaderLock> LockHeaderAsync(NpgsqlConnection connection,NpgsqlTransaction transaction,string table,Guid id,CancellationToken cancellationToken){await using var command=new NpgsqlCommand($"select document_number,document_date,status from {table} where id=@id for update;",connection,transaction);command.Parameters.AddWithValue("id",id);await using var reader=await command.ExecuteReaderAsync(cancellationToken);if(!await reader.ReadAsync(cancellationToken))throw new InventoryValidationException("Dokumen tidak ditemukan.");return new(reader.GetString(0),reader.GetFieldValue<DateOnly>(1),reader.GetString(2));}
    private static async Task<IReadOnlyList<MovementLock>> LockSourceMovementsAsync(NpgsqlConnection connection,NpgsqlTransaction transaction,Guid sourceId,string sourceType,CancellationToken cancellationToken){const string sql="select m.item_id,i.code,m.warehouse_code,m.quantity_in,m.quantity_out,m.unit_cost from inventory_movement m join inventory_item i on i.id=m.item_id where m.source_id=@id and m.source_type=@type order by i.code,m.warehouse_code for update of m;";var result=new List<MovementLock>();await using var command=new NpgsqlCommand(sql,connection,transaction);command.Parameters.AddWithValue("id",sourceId);command.Parameters.AddWithValue("type",sourceType);await using var reader=await command.ExecuteReaderAsync(cancellationToken);while(await reader.ReadAsync(cancellationToken))result.Add(new(reader.GetGuid(0),reader.GetString(1),reader.GetString(2),reader.GetDecimal(3),reader.GetDecimal(4),reader.GetDecimal(5)));return result;}
    private static async Task MarkCancelledAsync(NpgsqlConnection connection,NpgsqlTransaction transaction,string table,Guid id,CancelInventoryDocumentRequest request,CancellationToken cancellationToken)=>await InventoryMasterRepository.ExecuteAsync(connection,transaction,$"update {table} set status='CANCELLED',cancelled_at=now(),cancelled_by_name=@user,cancellation_reason=@reason,version=version+1 where id=@id;",cancellationToken,("user",InventoryMasterRepository.Db(request.UserName)),("reason",request.Reason.Trim()),("id",id));

    private static async Task<Dictionary<Guid,MbapLineLock>> LockMbapSendLinesAsync(NpgsqlConnection connection,NpgsqlTransaction transaction,Guid sendId,CancellationToken cancellationToken){const string sql="""
        select sl.id,sl.item_id,i.code,sl.quantity,
               coalesce((select sum(rl.quantity) from inventory_mbap_receive_line rl join inventory_mbap_receive r on r.id=rl.receive_id where rl.send_line_id=sl.id and r.status='POSTED'),0),
               sl.unit_cost
        from inventory_mbap_send_line sl join inventory_item i on i.id=sl.item_id
        where sl.send_id=@id order by sl.line_number for update of sl;
        """;var result=new Dictionary<Guid,MbapLineLock>();await using var command=new NpgsqlCommand(sql,connection,transaction);command.Parameters.AddWithValue("id",sendId);await using var reader=await command.ExecuteReaderAsync(cancellationToken);while(await reader.ReadAsync(cancellationToken)){var value=new MbapLineLock(reader.GetGuid(0),reader.GetGuid(1),reader.GetString(2),reader.GetDecimal(3),reader.GetDecimal(4),reader.GetDecimal(5));result[value.SendLineId]=value;}return result;}
    private static async Task RefreshMbapSendStatusAsync(NpgsqlConnection connection,NpgsqlTransaction transaction,Guid sendId,CancellationToken cancellationToken){const string sql="""
        update inventory_mbap_send s set status=case
          when coalesce(x.received,0)=0 then 'POSTED'
          when coalesce(x.received,0)>=coalesce(x.sent,0) then 'RECEIVED' else 'PARTIAL' end,version=version+1
        from (select sl.send_id,sum(sl.quantity) sent,sum(coalesce(rq.received,0)) received from inventory_mbap_send_line sl left join (select rl.send_line_id,sum(rl.quantity) received from inventory_mbap_receive_line rl join inventory_mbap_receive r on r.id=rl.receive_id and r.status='POSTED' group by rl.send_line_id) rq on rq.send_line_id=sl.id where sl.send_id=@id group by sl.send_id) x
        where s.id=x.send_id;
        """;await InventoryMasterRepository.ExecuteAsync(connection,transaction,sql,cancellationToken,("id",sendId));}

    private static void ValidateLines(IReadOnlyList<InventoryDocumentLineRequest> lines,bool allowSignedQuantity){if(lines.Count==0)throw new InventoryValidationException("Minimal satu rincian Barang wajib diisi.");foreach(var line in lines){InventoryMasterRepository.NormalizeCode(line.ItemCode,"Kode Barang");if((allowSignedQuantity&&line.Quantity==0)||(!allowSignedQuantity&&line.Quantity<=0))throw new InventoryValidationException($"Jumlah {line.ItemCode} tidak valid.");if(line.UnitCost<0)throw new InventoryValidationException($"Harga/HPP {line.ItemCode} tidak boleh negatif.");}}
    private static NormalizedLine[] NormalizeLines(IReadOnlyList<InventoryDocumentLineRequest> lines,string warehouse,bool allowSigned)=>lines.GroupBy(x=>InventoryMasterRepository.NormalizeCode(x.ItemCode,"Kode Barang")).Select(g=>new NormalizedLine(g.Key,g.Sum(x=>x.Quantity),g.Where(x=>x.UnitCost>0).Select(x=>x.UnitCost).DefaultIfEmpty(0).Last(),warehouse)).ToArray();
    private static void ValidateCancellation(CancelInventoryDocumentRequest request){if(string.IsNullOrWhiteSpace(request.Reason))throw new InventoryValidationException("Alasan pembatalan wajib diisi.");}
    private static InventoryConflictException StockError(string item,string warehouse,decimal requested,decimal available)=>new($"Stok {item} di gudang {warehouse} tidak cukup: diminta {requested:N3}, tersedia {available:N3}.");
    private static string? Text(NpgsqlDataReader reader,int ordinal)=>reader.IsDBNull(ordinal)?null:reader.GetString(ordinal);

    private sealed record LockedItem(Guid Id,string Code,string Name,string Unit,decimal AverageCost,decimal Balance);
    private sealed record NormalizedLine(string ItemCode,decimal Quantity,decimal UnitCost,string WarehouseCode);
    private sealed record CountInput(int LineNumber,string ItemCode,decimal CountedQuantity,decimal UnitCost);
    private sealed record AdjustmentTypeLock(string Direction,bool RequiresValue,bool IsOpeningBalance);
    private sealed record HeaderLock(string Number,DateOnly Date,string Status);
    private sealed record MovementLock(Guid ItemId,string ItemCode,string WarehouseCode,decimal QuantityIn,decimal QuantityOut,decimal UnitCost);
    private sealed record MbapLineLock(Guid SendLineId,Guid ItemId,string ItemCode,decimal SentQuantity,decimal ReceivedQuantity,decimal UnitCost);
}
