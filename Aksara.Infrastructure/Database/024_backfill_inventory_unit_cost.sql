-- Recover per-unit inventory cost for legacy items whose CPS master cost was zero.
-- Positive FIFO costs and manually maintained costs are left untouched.
with latest_purchase_cost as (
    select distinct on (pl.item_id)
           pl.item_id,
           pl.line_total / nullif(pl.quantity, 0) unit_cost
    from purchase_line pl
    join purchase p on p.id = pl.purchase_id
    where p.status = 'POSTED' and pl.quantity > 0
    order by pl.item_id, p.document_date desc, p.created_at desc, pl.line_number desc
)
update inventory_cost_layer layer
set unit_cost = coalesce(nullif(item.average_cost, 0), nullif(latest.unit_cost, 0), nullif(item.purchase_price, 0), 0)
from inventory_item item
left join latest_purchase_cost latest on latest.item_id = item.id
where layer.item_id = item.id
  and layer.source_type = 'FIFO_OPENING'
  and layer.remaining_quantity > 0
  and layer.unit_cost = 0
  and coalesce(nullif(item.average_cost, 0), nullif(latest.unit_cost, 0), nullif(item.purchase_price, 0), 0) > 0;

with latest_purchase_cost as (
    select distinct on (pl.item_id)
           pl.item_id,
           pl.line_total / nullif(pl.quantity, 0) unit_cost
    from purchase_line pl
    join purchase p on p.id = pl.purchase_id
    where p.status = 'POSTED' and pl.quantity > 0
    order by pl.item_id, p.document_date desc, p.created_at desc, pl.line_number desc
), fifo_cost as (
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
), effective_cost as (
    select item.id,
           coalesce(nullif(fifo.unit_cost, 0), nullif(movement.unit_cost, 0), nullif(item.average_cost, 0),
                    nullif(latest.unit_cost, 0), nullif(item.purchase_price, 0), 0) average_cost,
           coalesce(nullif(item.purchase_price, 0), nullif(latest.unit_cost, 0), 0) purchase_price
    from inventory_item item
    left join fifo_cost fifo on fifo.item_id = item.id
    left join movement_cost movement on movement.item_id = item.id
    left join latest_purchase_cost latest on latest.item_id = item.id
)
update inventory_item item
set average_cost = effective.average_cost,
    purchase_price = effective.purchase_price,
    updated_at = now(),
    version = item.version + 1
from effective_cost effective
where item.id = effective.id
  and (item.average_cost is distinct from effective.average_cost
       or item.purchase_price is distinct from effective.purchase_price);
