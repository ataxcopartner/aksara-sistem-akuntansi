using Aksara.Contracts;
using Aksara.Domain;
using Npgsql;

namespace Aksara.Infrastructure;

public sealed class InventoryReportRepository(PostgresDatabase database)
{
    public async Task<InventoryReportDto<InventoryPriceReportRowDto>> GetPriceReportAsync(string? query, CancellationToken cancellationToken = default)
    {
        const string sql = """
            select coalesce(i.category_code,''),i.code,i.name,i.unit,i.purchase_price,i.average_cost,
                   coalesce(max(p.price) filter(where p.tier_code='RETAIL'),i.retail_price),
                   coalesce(max(p.price) filter(where p.tier_code='WHOLESALE'),i.wholesale_price),
                   coalesce(max(p.price) filter(where p.tier_code='BULK'),0),
                   coalesce(max(p.price) filter(where p.tier_code='SPECIAL1'),0),
                   coalesce(max(p.price) filter(where p.tier_code='SPECIAL2'),0),
                   case when i.average_cost=0 then 0 else round((coalesce(max(p.price) filter(where p.tier_code='RETAIL'),i.retail_price)-i.average_cost)/i.average_cost*100,2) end,
                   coalesce((select sum(b.quantity) from inventory_balance b where b.item_id=i.id),0)
            from inventory_item i left join inventory_item_price p on p.item_id=i.id
            where i.is_active and (@query='' or i.code ilike '%'||@query||'%' or i.name ilike '%'||@query||'%' or coalesce(i.category_code,'') ilike '%'||@query||'%')
            group by i.id order by coalesce(i.category_code,''),i.code;
            """;
        await using var command=database.DataSource.CreateCommand(sql);command.Parameters.AddWithValue("query",query?.Trim()??string.Empty);
        await using var reader=await command.ExecuteReaderAsync(cancellationToken);var rows=new List<InventoryPriceReportRowDto>();
        while(await reader.ReadAsync(cancellationToken))rows.Add(new(reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.GetDecimal(4),reader.GetDecimal(5),reader.GetDecimal(6),reader.GetDecimal(7),reader.GetDecimal(8),reader.GetDecimal(9),reader.GetDecimal(10),reader.GetDecimal(11),reader.GetDecimal(12)));
        return new(rows,Totals(rowCount:rows.Count,ending:rows.Sum(x=>x.TotalStock),value:rows.Sum(x=>x.TotalStock*x.AverageCost)));
    }

    public async Task<InventoryReportDto<InventoryPriceHistoryRowDto>> GetPriceHistoryAsync(string historyType,DateOnly from,DateOnly to,string? query,CancellationToken cancellationToken=default)
    {
        var type=historyType.Trim().ToUpperInvariant();
        if(type is not("PURCHASE" or "PURCHASE_RETURN" or "SALE" or "SALE_RETURN" or "STANDARD_COST"))throw new InventoryValidationException("Jenis histori harga tidak dikenal.");
        const string sql="""
            select * from (
              select p.document_date,p.document_number,'PURCHASE'::text history_type,s.code party_code,s.company_name party_name,i.code item_code,i.name item_name,l.quantity,i.unit,l.unit_price,l.discount_1 discount
              from purchase p join supplier s on s.id=p.supplier_id join purchase_line l on l.purchase_id=p.id join inventory_item i on i.id=l.item_id where p.status='POSTED'
              union all
              select r.document_date,r.document_number,'PURCHASE_RETURN',s.code,s.company_name,i.code,i.name,l.quantity,i.unit,l.unit_price,l.discount_percent
              from purchase_return r join supplier s on s.id=r.supplier_id join purchase_return_line l on l.purchase_return_id=r.id join inventory_item i on i.id=l.item_id where r.status='POSTED'
              union all
              select s.document_date,s.document_number,'SALE',coalesce(c.code,''),coalesce(c.company_name,'Tunai'),i.code,i.name,l.quantity,i.unit,l.unit_price,l.discount_1
              from sale s left join customer c on c.id=s.customer_id join sale_line l on l.sale_id=s.id join inventory_item i on i.id=l.item_id where s.status='POSTED'
              union all
              select r.document_date,r.document_number,'SALE_RETURN',c.code,c.company_name,i.code,i.name,l.quantity,i.unit,l.unit_price,l.discount_percent
              from sale_return r join customer c on c.id=r.customer_id join sale_return_line l on l.sale_return_id=r.id join inventory_item i on i.id=l.item_id where r.status='POSTED'
              union all
              select h.effective_at::date,coalesce(h.source_number,''),'STANDARD_COST','',coalesce(h.changed_by_name,''),i.code,i.name,0,i.unit,h.new_cost,0
              from inventory_standard_cost_history h join inventory_item i on i.id=h.item_id
            ) h
            where h.history_type=@type and h.document_date between @from and @to
              and (@query='' or h.document_number ilike '%'||@query||'%' or h.party_code ilike '%'||@query||'%' or h.party_name ilike '%'||@query||'%' or h.item_code ilike '%'||@query||'%' or h.item_name ilike '%'||@query||'%')
            order by h.document_date desc,h.document_number desc,h.item_code;
            """;
        await using var command=database.DataSource.CreateCommand(sql);command.Parameters.AddWithValue("type",type);command.Parameters.AddWithValue("from",from);command.Parameters.AddWithValue("to",to);command.Parameters.AddWithValue("query",query?.Trim()??string.Empty);
        await using var reader=await command.ExecuteReaderAsync(cancellationToken);var rows=new List<InventoryPriceHistoryRowDto>();while(await reader.ReadAsync(cancellationToken))rows.Add(new(reader.GetFieldValue<DateOnly>(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.GetString(4),reader.GetString(5),reader.GetString(6),reader.GetDecimal(7),reader.GetString(8),reader.GetDecimal(9),reader.GetDecimal(10)));
        return new(rows,Totals(rowCount:rows.Count,amount:rows.Sum(x=>x.Quantity*x.UnitPrice)));
    }

    public async Task<InventoryReportDto<InventoryStockCardRowDto>> GetStockCardAsync(DateOnly from,DateOnly to,string? query,string? warehouse,bool withValue,CancellationToken cancellationToken=default)
    {
        const string sql="""
            with opening as (
              select item_id,warehouse_code,sum(quantity_in-quantity_out) quantity,
                     sum((quantity_in-quantity_out)*unit_cost) inventory_value
              from inventory_movement where occurred_at < @from group by item_id,warehouse_code
            ), movements as (
              select m.*,coalesce(o.quantity,0)+sum(m.quantity_in-m.quantity_out) over(partition by m.item_id,m.warehouse_code order by m.occurred_at,m.created_at,m.id) balance,
                     coalesce(o.inventory_value,0)+sum((m.quantity_in-m.quantity_out)*m.unit_cost) over(partition by m.item_id,m.warehouse_code order by m.occurred_at,m.created_at,m.id) balance_value
              from inventory_movement m left join opening o on o.item_id=m.item_id and o.warehouse_code=m.warehouse_code
              where m.occurred_at>=@from and m.occurred_at<@toExclusive
            )
            select m.occurred_at,m.warehouse_code,i.code,i.name,m.source_type,m.source_number,m.quantity_in,m.quantity_out,m.balance,
                   case when @withValue then m.unit_cost else 0 end,
                   case when @withValue then m.balance_value else 0 end
            from movements m join inventory_item i on i.id=m.item_id
            where (@warehouse='' or m.warehouse_code=@warehouse)
              and (@query='' or i.code ilike '%'||@query||'%' or i.name ilike '%'||@query||'%' or m.source_number ilike '%'||@query||'%')
            order by i.code,m.warehouse_code,m.occurred_at,m.created_at,m.id;
            """;
        await using var command=database.DataSource.CreateCommand(sql);command.Parameters.AddWithValue("from",Utc(from));command.Parameters.AddWithValue("toExclusive",Utc(to.AddDays(1)));command.Parameters.AddWithValue("warehouse",warehouse?.Trim()??string.Empty);command.Parameters.AddWithValue("query",query?.Trim()??string.Empty);command.Parameters.AddWithValue("withValue",withValue);
        await using var reader=await command.ExecuteReaderAsync(cancellationToken);var rows=new List<InventoryStockCardRowDto>();while(await reader.ReadAsync(cancellationToken))rows.Add(new(reader.GetFieldValue<DateTimeOffset>(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.GetString(4),reader.GetString(5),reader.GetDecimal(6),reader.GetDecimal(7),reader.GetDecimal(8),reader.GetDecimal(9),reader.GetDecimal(10)));
        var endings=rows.GroupBy(x=>(x.ItemCode,x.WarehouseCode)).Select(x=>x.Last()).ToArray();
        return new(rows,Totals(rows.Sum(x=>x.QuantityIn),rows.Sum(x=>x.QuantityOut),endings.Sum(x=>x.BalanceQuantity),endings.Sum(x=>x.BalanceValue),rowCount:rows.Count));
    }

    public async Task<InventoryReportDto<InventoryValuationRowDto>> GetValuationAsync(string? query,string? warehouse,string mode,CancellationToken cancellationToken=default)
    {
        var normalizedMode=mode.Trim().ToUpperInvariant();if(normalizedMode is not("ALL" or "NO_COST" or "MIN" or "MAX" or "LIMITS"))throw new InventoryValidationException("Mode laporan persediaan tidak dikenal.");
        const string sql="""
            select b.warehouse_code,i.code,i.name,i.unit,b.quantity,
                   case when coalesce(f.quantity,0)=0 then 0 else f.inventory_value/f.quantity end fifo_cost,
                   coalesce(f.inventory_value,0),i.minimum_stock,i.maximum_stock,
                   case when i.check_stock and b.quantity<i.minimum_stock then 'MINIMUM'
                        when i.check_stock and i.maximum_stock>0 and b.quantity>i.maximum_stock then 'MAXIMUM' else 'NORMAL' end status
            from inventory_balance b join inventory_item i on i.id=b.item_id
            left join lateral (
                select sum(l.remaining_quantity) quantity,sum(l.remaining_quantity*l.unit_cost) inventory_value
                from inventory_cost_layer l where l.item_id=b.item_id and l.warehouse_code=b.warehouse_code and l.remaining_quantity>0
            ) f on true
            where i.is_active and (@warehouse='' or b.warehouse_code=@warehouse)
              and (@query='' or i.code ilike '%'||@query||'%' or i.name ilike '%'||@query||'%')
              and (@mode='ALL' or (@mode='NO_COST' and coalesce(f.inventory_value,0)=0) or (@mode='MIN' and i.check_stock and b.quantity<i.minimum_stock) or (@mode='MAX' and i.check_stock and i.maximum_stock>0 and b.quantity>i.maximum_stock) or (@mode='LIMITS' and (i.minimum_stock>0 or i.maximum_stock>0)))
            order by b.warehouse_code,i.code;
            """;
        await using var command=database.DataSource.CreateCommand(sql);command.Parameters.AddWithValue("warehouse",warehouse?.Trim()??string.Empty);command.Parameters.AddWithValue("query",query?.Trim()??string.Empty);command.Parameters.AddWithValue("mode",normalizedMode);
        await using var reader=await command.ExecuteReaderAsync(cancellationToken);var rows=new List<InventoryValuationRowDto>();while(await reader.ReadAsync(cancellationToken))rows.Add(new(reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.GetDecimal(4),reader.GetDecimal(5),reader.GetDecimal(6),reader.GetDecimal(7),reader.GetDecimal(8),reader.GetString(9)));
        return new(rows,Totals(ending:rows.Sum(x=>x.Quantity),value:rows.Sum(x=>x.InventoryValue),rowCount:rows.Count));
    }

    public async Task<InventoryReportDto<InventoryDailyActivityRowDto>> GetDailyActivityAsync(DateOnly from,DateOnly to,string? query,string? warehouse,CancellationToken cancellationToken=default)
    {
        const string sql="""
            with daily as (
              select m.occurred_at::date activity_date,m.item_id,m.warehouse_code,sum(m.quantity_in) quantity_in,sum(m.quantity_out) quantity_out,
                     sum((m.quantity_in-m.quantity_out)*m.unit_cost) value_change
              from inventory_movement m where m.occurred_at<@toExclusive group by m.occurred_at::date,m.item_id,m.warehouse_code
            ), running as (
              select d.*,sum(d.quantity_in-d.quantity_out) over(partition by d.item_id,d.warehouse_code order by d.activity_date) ending_quantity,
                     sum(d.value_change) over(partition by d.item_id,d.warehouse_code order by d.activity_date) ending_value
              from daily d
            )
            select r.activity_date,r.warehouse_code,i.code,i.name,r.quantity_in,r.quantity_out,r.ending_quantity,
                   case when r.ending_quantity=0 then 0 else r.ending_value/r.ending_quantity end,r.ending_value
            from running r join inventory_item i on i.id=r.item_id
            where r.activity_date between @from and @to and (@warehouse='' or r.warehouse_code=@warehouse)
              and (@query='' or i.code ilike '%'||@query||'%' or i.name ilike '%'||@query||'%')
            order by r.activity_date,r.warehouse_code,i.code;
            """;
        await using var command=database.DataSource.CreateCommand(sql);command.Parameters.AddWithValue("from",from);command.Parameters.AddWithValue("to",to);command.Parameters.AddWithValue("toExclusive",Utc(to.AddDays(1)));command.Parameters.AddWithValue("warehouse",warehouse?.Trim()??string.Empty);command.Parameters.AddWithValue("query",query?.Trim()??string.Empty);
        await using var reader=await command.ExecuteReaderAsync(cancellationToken);var rows=new List<InventoryDailyActivityRowDto>();while(await reader.ReadAsync(cancellationToken))rows.Add(new(reader.GetFieldValue<DateOnly>(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.GetDecimal(4),reader.GetDecimal(5),reader.GetDecimal(6),reader.GetDecimal(7),reader.GetDecimal(8)));
        var endings=rows.GroupBy(x=>(x.ItemCode,x.WarehouseCode)).Select(x=>x.Last()).ToArray();
        return new(rows,Totals(rows.Sum(x=>x.QuantityIn),rows.Sum(x=>x.QuantityOut),endings.Sum(x=>x.EndingQuantity),endings.Sum(x=>x.EndingValue),rowCount:rows.Count));
    }

    public async Task<InventoryReportDto<InventoryTurnoverRowDto>> GetTurnoverAsync(DateOnly from,DateOnly to,string? query,string? warehouse,CancellationToken cancellationToken=default)
    {
        const string sql="""
            with x as (
              select i.id,i.code,i.name,b.warehouse_code,
                coalesce(sum(m.quantity_in-m.quantity_out) filter(where m.occurred_at<@from),0) opening,
                coalesce(sum(m.quantity_in) filter(where m.occurred_at>=@from and m.occurred_at<@toExclusive),0) incoming,
                coalesce(sum(m.quantity_out) filter(where m.occurred_at>=@from and m.occurred_at<@toExclusive),0) outgoing
              from inventory_item i join inventory_balance b on b.item_id=i.id left join inventory_movement m on m.item_id=i.id and m.warehouse_code=b.warehouse_code
              where i.is_active and (@warehouse='' or b.warehouse_code=@warehouse) and (@query='' or i.code ilike '%'||@query||'%' or i.name ilike '%'||@query||'%')
              group by i.id,b.warehouse_code
            )
            select warehouse_code,code,name,opening,incoming,outgoing,opening+incoming-outgoing,
                   (opening+(opening+incoming-outgoing))/2 average_quantity,
                   case when (opening+(opening+incoming-outgoing))/2=0 then 0 else outgoing/((opening+(opening+incoming-outgoing))/2) end turnover,
                   case when outgoing=0 then 'TIDAK BERGERAK' when case when (opening+(opening+incoming-outgoing))/2=0 then 0 else outgoing/((opening+(opening+incoming-outgoing))/2) end>=2 then 'CEPAT' else 'LAMBAT' end
            from x order by turnover desc,code;
            """;
        await using var command=database.DataSource.CreateCommand(sql);command.Parameters.AddWithValue("from",Utc(from));command.Parameters.AddWithValue("toExclusive",Utc(to.AddDays(1)));command.Parameters.AddWithValue("warehouse",warehouse?.Trim()??string.Empty);command.Parameters.AddWithValue("query",query?.Trim()??string.Empty);
        await using var reader=await command.ExecuteReaderAsync(cancellationToken);var rows=new List<InventoryTurnoverRowDto>();while(await reader.ReadAsync(cancellationToken))rows.Add(new(reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetDecimal(3),reader.GetDecimal(4),reader.GetDecimal(5),reader.GetDecimal(6),reader.GetDecimal(7),reader.GetDecimal(8),reader.GetString(9)));
        return new(rows,Totals(rows.Sum(x=>x.QuantityIn),rows.Sum(x=>x.QuantityOut),rows.Sum(x=>x.EndingQuantity),rowCount:rows.Count));
    }

    public async Task<InventoryReportDto<InventoryTransactionReportRowDto>> GetTransactionReportAsync(DateOnly from,DateOnly to,string? documentType,string? query,CancellationToken cancellationToken=default)
    {
        const string sql="""
            select * from (
              select t.document_date,'TRANSFER'::text document_type,t.document_number,t.status,t.source_warehouse_code,t.destination_warehouse_code,null::text counterparty,i.code item_code,i.name item_name,l.quantity,i.unit,l.unit_cost,l.quantity*l.unit_cost amount from inventory_transfer t join inventory_transfer_line l on l.transfer_id=t.id join inventory_item i on i.id=l.item_id
              union all select a.document_date,'ADJUSTMENT',a.document_number,a.status,a.warehouse_code,null,ty.name,i.code,i.name,l.quantity,i.unit,l.unit_cost,l.quantity*l.unit_cost from inventory_adjustment a join inventory_adjustment_type ty on ty.code=a.adjustment_type_code join inventory_adjustment_line l on l.adjustment_id=a.id join inventory_item i on i.id=l.item_id
              union all select s.document_date,'STOCK_COUNT',s.document_number,s.status,s.warehouse_code,null,null,i.code,i.name,l.difference_quantity,i.unit,l.unit_cost,l.difference_quantity*l.unit_cost from inventory_stock_count s join inventory_stock_count_line l on l.stock_count_id=s.id join inventory_item i on i.id=l.item_id
              union all select m.document_date,'MBAP_SEND',m.document_number,m.status,m.source_warehouse_code,null,b.name,i.code,i.name,l.quantity,i.unit,l.unit_cost,l.quantity*l.unit_cost from inventory_mbap_send m join business_unit b on b.code=m.destination_business_unit_code join inventory_mbap_send_line l on l.send_id=m.id join inventory_item i on i.id=l.item_id
              union all select r.document_date,'MBAP_RECEIVE',r.document_number,r.status,null,r.destination_warehouse_code,s.document_number,i.code,i.name,l.quantity,i.unit,l.unit_cost,l.quantity*l.unit_cost from inventory_mbap_receive r join inventory_mbap_send s on s.id=r.send_id join inventory_mbap_receive_line l on l.receive_id=r.id join inventory_item i on i.id=l.item_id
            ) x where x.document_date between @from and @to and (@type='' or x.document_type=@type)
              and (@query='' or x.document_number ilike '%'||@query||'%' or x.item_code ilike '%'||@query||'%' or x.item_name ilike '%'||@query||'%' or coalesce(x.counterparty,'') ilike '%'||@query||'%')
            order by x.document_date,x.document_number,x.item_code;
            """;
        await using var command=database.DataSource.CreateCommand(sql);command.Parameters.AddWithValue("from",from);command.Parameters.AddWithValue("to",to);command.Parameters.AddWithValue("type",documentType?.Trim().ToUpperInvariant()??string.Empty);command.Parameters.AddWithValue("query",query?.Trim()??string.Empty);
        await using var reader=await command.ExecuteReaderAsync(cancellationToken);var rows=new List<InventoryTransactionReportRowDto>();while(await reader.ReadAsync(cancellationToken))rows.Add(new(reader.GetFieldValue<DateOnly>(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),Text(reader,4),Text(reader,5),Text(reader,6),reader.GetString(7),reader.GetString(8),reader.GetDecimal(9),reader.GetString(10),reader.GetDecimal(11),reader.GetDecimal(12)));
        return new(rows,Totals(amount:rows.Sum(x=>x.Amount),rowCount:rows.Count));
    }

    public async Task<PurchasePriceCheckReportDto> GetPurchasePriceCheckAsync(string itemCode, CancellationToken cancellationToken = default)
    {
        var normalizedCode = itemCode.Trim();
        if (normalizedCode.Length == 0) throw new InventoryValidationException("Barang wajib dipilih.");

        const string itemSql = """
            select i.id, i.code, i.name, i.unit,
                   coalesce((select sum(b.quantity) from inventory_balance b where b.item_id=i.id), 0)
            from inventory_item i
            where upper(i.code)=upper(@code);
            """;
        Guid itemId;
        string code;
        string name;
        string unit;
        decimal currentStock;
        await using (var command = database.DataSource.CreateCommand(itemSql))
        {
            command.Parameters.AddWithValue("code", normalizedCode);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) throw new InventoryValidationException("Barang tidak ditemukan.");
            itemId = reader.GetGuid(0);
            code = reader.GetString(1);
            name = reader.GetString(2);
            unit = reader.GetString(3);
            currentStock = reader.GetDecimal(4);
        }

        const string lotSql = """
            select pl.id, p.document_date, p.document_number, s.code, s.company_name, pl.warehouse_code,
                   pl.quantity,
                   coalesce((
                       select sum(prl.quantity)
                       from purchase_return_line prl
                       join purchase_return pr on pr.id=prl.purchase_return_id and pr.status='POSTED'
                       where prl.purchase_line_id=pl.id
                   ), 0) return_quantity,
                   case when pl.quantity=0 then 0 else pl.line_total/pl.quantity end unit_cost,
                   pl.line_number
            from purchase_line pl
            join purchase p on p.id=pl.purchase_id and p.status='POSTED'
            join supplier s on s.id=p.supplier_id
            where pl.item_id=@item
            order by p.document_date, p.created_at, p.document_number, pl.line_number, pl.id;
            """;
        var sourceRows = new List<PurchaseCostSourceRow>();
        await using (var command = database.DataSource.CreateCommand(lotSql))
        {
            command.Parameters.AddWithValue("item", itemId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var sequence = 0;
            while (await reader.ReadAsync(cancellationToken))
            {
                var purchaseQuantity = reader.GetDecimal(6);
                var returnQuantity = Math.Min(purchaseQuantity, reader.GetDecimal(7));
                sourceRows.Add(new(
                    reader.GetGuid(0), reader.GetFieldValue<DateOnly>(1), reader.GetString(2), reader.GetString(3),
                    reader.GetString(4), reader.GetString(5), purchaseQuantity, returnQuantity,
                    Math.Max(0, purchaseQuantity - returnQuantity), reader.GetDecimal(8), sequence++));
            }
        }

        const string soldSql = """
            select sl.warehouse_code,
                   sum(greatest(sl.quantity-coalesce((
                       select sum(srl.quantity)
                       from sale_return_line srl
                       join sale_return sr on sr.id=srl.sale_return_id and sr.status='POSTED'
                       where srl.sale_line_id=sl.id
                   ), 0), 0)) sold_quantity
            from sale_line sl
            join sale s on s.id=sl.sale_id and s.status='POSTED'
            where sl.item_id=@item
            group by sl.warehouse_code;
            """;
        var soldByWarehouse = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        await using (var command = database.DataSource.CreateCommand(soldSql))
        {
            command.Parameters.AddWithValue("item", itemId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) soldByWarehouse[reader.GetString(0).Trim().ToUpperInvariant()] = reader.GetDecimal(1);
        }

        var lots = sourceRows.Select(x => new InventoryCostLot(x.Id, x.WarehouseCode, x.PurchaseDate, x.Sequence, x.NetQuantity, x.UnitCost)).ToArray();
        var fifo = InventoryCostAnalysis.Allocate(lots, soldByWarehouse, InventoryCostFlowMethod.Fifo);
        var lifoNextIssueUnitCost = InventoryCostAnalysis.GetNextIssueUnitCost(lots, fifo.Lots, InventoryCostFlowMethod.Lifo);
        var fifoById = fifo.Lots.ToDictionary(x => x.LotId);
        var rows = sourceRows.Select(x =>
        {
            var allocation = fifoById[x.Id];
            return new PurchasePriceCheckRowDto(
                x.PurchaseDate, x.PurchaseNumber, x.SupplierCode, x.SupplierName, x.WarehouseCode,
                x.PurchaseQuantity, x.ReturnQuantity, allocation.ConsumedQuantity, allocation.RemainingQuantity,
                x.UnitCost, allocation.RemainingValue);
        }).ToArray();

        var averageUnitCost = fifo.RemainingQuantity == 0 ? 0 : fifo.RemainingValue / fifo.RemainingQuantity;
        var summary = new PurchasePriceCheckSummaryDto(
            sourceRows.Sum(x => x.PurchaseQuantity), sourceRows.Sum(x => x.ReturnQuantity), fifo.RequestedQuantity,
            fifo.RemainingQuantity, fifo.UnallocatedQuantity,
            new(averageUnitCost, fifo.CostOfGoodsSold, fifo.RemainingValue),
            new(fifo.NextIssueUnitCost, fifo.CostOfGoodsSold, fifo.RemainingValue),
            new(lifoNextIssueUnitCost, fifo.CostOfGoodsSold, fifo.RemainingValue));
        return new(code, name, unit, currentStock, rows, summary);
    }

    private static InventoryReportTotalsDto Totals(decimal quantityIn=0,decimal quantityOut=0,decimal ending=0,decimal value=0,decimal amount=0,int rowCount=0)=>new(quantityIn,quantityOut,ending,value,amount,rowCount);
    private static DateTimeOffset Utc(DateOnly date)=>new(date.ToDateTime(TimeOnly.MinValue,DateTimeKind.Utc));
    private static string? Text(NpgsqlDataReader reader,int ordinal)=>reader.IsDBNull(ordinal)?null:reader.GetString(ordinal);

    private sealed record PurchaseCostSourceRow(
        Guid Id, DateOnly PurchaseDate, string PurchaseNumber, string SupplierCode, string SupplierName,
        string WarehouseCode, decimal PurchaseQuantity, decimal ReturnQuantity, decimal NetQuantity,
        decimal UnitCost, int Sequence);
}
