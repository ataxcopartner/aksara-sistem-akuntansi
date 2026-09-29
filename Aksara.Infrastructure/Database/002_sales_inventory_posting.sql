create table if not exists warehouse (
    code varchar(20) primary key,
    name varchar(100) not null,
    is_active boolean not null default true,
    created_at timestamptz not null default now()
);

insert into warehouse(code, name)
values ('UTAMA', 'Gudang Utama')
on conflict (code) do nothing;

create table if not exists inventory_balance (
    item_id uuid not null references inventory_item(id),
    warehouse_code varchar(20) not null references warehouse(code),
    quantity numeric(19,3) not null default 0,
    version bigint not null default 1,
    updated_at timestamptz not null default now(),
    primary key(item_id, warehouse_code)
);

create table if not exists document_sequence (
    document_type varchar(20) not null,
    period char(6) not null,
    last_number integer not null default 0,
    primary key(document_type, period)
);

alter table sale add column if not exists discount_percent numeric(8,4) not null default 0;
alter table sale add column if not exists tax_percent numeric(8,4) not null default 0;
alter table sale add column if not exists cancelled_at timestamptz;
alter table sale add column if not exists cancelled_by_name varchar(50);
alter table sale add column if not exists cancellation_reason text;

alter table sale_line add column if not exists warehouse_code varchar(20);
update sale_line sl
set warehouse_code = coalesce(s.warehouse_code, 'UTAMA')
from sale s
where s.id = sl.sale_id and sl.warehouse_code is null;
alter table sale_line alter column warehouse_code set default 'UTAMA';
alter table sale_line alter column warehouse_code set not null;

create unique index if not exists ux_inventory_movement_sale_item_warehouse
    on inventory_movement(source_type, source_id, item_id, warehouse_code);
create index if not exists ix_inventory_balance_warehouse_item
    on inventory_balance(warehouse_code, item_id);

insert into inventory_balance(item_id, warehouse_code, quantity)
select item_id,
       warehouse_code,
       sum(quantity_in - quantity_out)
from inventory_movement
group by item_id, warehouse_code
on conflict (item_id, warehouse_code) do nothing;
