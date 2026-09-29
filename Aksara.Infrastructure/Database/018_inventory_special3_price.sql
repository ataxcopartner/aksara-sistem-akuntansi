alter table inventory_item_price
    drop constraint if exists inventory_item_price_tier_code_check;

alter table inventory_item_price
    add constraint inventory_item_price_tier_code_check
    check (tier_code in ('RETAIL','WHOLESALE','BULK','SPECIAL1','SPECIAL2','SPECIAL3'));

insert into inventory_item_price(item_id, tier_code, tier_name, price, discount_percent, minimum_quantity, sort_order)
select id, 'SPECIAL3', 'Khusus 3', 0, 0, 100, 6
from inventory_item
on conflict (item_id, tier_code) do nothing;
