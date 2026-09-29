alter table warehouse add column if not exists is_default boolean not null default false;
alter table warehouse add column if not exists updated_at timestamptz;
alter table warehouse add column if not exists version bigint not null default 1;
update warehouse set is_default = true where code = 'UTAMA' and not exists (select 1 from warehouse where is_default);
create unique index if not exists ux_warehouse_single_default on warehouse(is_default) where is_default;

create table if not exists inventory_category (
    code varchar(20) primary key,
    name varchar(100) not null,
    parent_code varchar(20) references inventory_category(code),
    is_active boolean not null default true,
    created_at timestamptz not null default now(),
    updated_at timestamptz,
    version bigint not null default 1
);

insert into inventory_category(code, name)
select distinct category_code, category_code
from inventory_item
where category_code is not null and btrim(category_code) <> ''
on conflict (code) do nothing;

do $$
begin
    if not exists (
        select 1 from pg_constraint
        where conname = 'fk_inventory_item_category'
          and conrelid = 'inventory_item'::regclass
    ) then
        alter table inventory_item
            add constraint fk_inventory_item_category foreign key(category_code) references inventory_category(code);
    end if;
end $$;

create table if not exists inventory_adjustment_type (
    code varchar(20) primary key,
    name varchar(100) not null,
    direction varchar(10) not null check (direction in ('IN','OUT','BOTH')),
    requires_value boolean not null default true,
    is_opening_balance boolean not null default false,
    is_active boolean not null default true,
    created_at timestamptz not null default now(),
    updated_at timestamptz,
    version bigint not null default 1
);

insert into inventory_adjustment_type(code, name, direction, requires_value, is_opening_balance)
values
    ('SALDO_AWAL', 'Saldo Awal', 'IN', true, true),
    ('PENYESUAIAN', 'Penyesuaian Persediaan', 'BOTH', true, false),
    ('KOREKSI_MASUK', 'Koreksi Barang Masuk', 'IN', true, false),
    ('KOREKSI_KELUAR', 'Koreksi Barang Keluar', 'OUT', true, false)
on conflict (code) do nothing;

alter table inventory_item add column if not exists specification text;
alter table inventory_item add column if not exists check_stock boolean not null default true;
alter table inventory_item add column if not exists image_data bytea;
alter table inventory_item add column if not exists image_content_type varchar(100);

create table if not exists inventory_item_price (
    item_id uuid not null references inventory_item(id) on delete cascade,
    tier_code varchar(20) not null check (tier_code in ('RETAIL','WHOLESALE','BULK','SPECIAL1','SPECIAL2')),
    tier_name varchar(50) not null,
    price numeric(19,2) not null default 0 check (price >= 0),
    discount_percent numeric(8,4) not null default 0 check (discount_percent between 0 and 100),
    minimum_quantity numeric(19,3) not null default 0 check (minimum_quantity >= 0),
    sort_order integer not null,
    primary key(item_id, tier_code)
);

insert into inventory_item_price(item_id, tier_code, tier_name, price, discount_percent, minimum_quantity, sort_order)
select id, tier_code, tier_name,
       case tier_code when 'RETAIL' then retail_price when 'WHOLESALE' then wholesale_price else 0 end,
       0,
       case tier_code when 'RETAIL' then 1 when 'WHOLESALE' then 2 when 'BULK' then 10 when 'SPECIAL1' then 25 else 50 end,
       sort_order
from inventory_item
cross join (values
    ('RETAIL','Eceran',1),
    ('WHOLESALE','Grosir',2),
    ('BULK','Partai',3),
    ('SPECIAL1','Khusus 1',4),
    ('SPECIAL2','Khusus 2',5)
) as tiers(tier_code, tier_name, sort_order)
on conflict (item_id, tier_code) do nothing;

create table if not exists inventory_item_package (
    id uuid primary key default gen_random_uuid(),
    item_id uuid not null references inventory_item(id) on delete cascade,
    package_name varchar(50) not null,
    package_size numeric(19,3) not null check (package_size > 0),
    barcode varchar(80),
    sort_order integer not null default 1,
    unique(item_id, package_name)
);

create table if not exists inventory_item_formula (
    item_id uuid not null references inventory_item(id) on delete cascade,
    component_item_id uuid not null references inventory_item(id),
    quantity numeric(19,6) not null check (quantity > 0),
    primary key(item_id, component_item_id),
    check (item_id <> component_item_id)
);

create table if not exists inventory_standard_cost_history (
    id bigint generated always as identity primary key,
    item_id uuid not null references inventory_item(id) on delete cascade,
    effective_at timestamptz not null default now(),
    previous_cost numeric(19,5) not null,
    new_cost numeric(19,5) not null,
    source_type varchar(30) not null,
    source_id uuid,
    source_number varchar(30),
    changed_by_name varchar(50)
);
create index if not exists ix_inventory_standard_cost_history_item_date
    on inventory_standard_cost_history(item_id, effective_at desc);

insert into inventory_standard_cost_history(item_id, previous_cost, new_cost, source_type, source_number)
select i.id, i.average_cost, i.average_cost, 'MIGRATION', 'AKSARA-0.12'
from inventory_item i
where not exists (select 1 from inventory_standard_cost_history h where h.item_id = i.id);

create or replace function capture_inventory_standard_cost_history()
returns trigger language plpgsql as $$
begin
    if tg_op = 'UPDATE' and new.average_cost is distinct from old.average_cost then
        insert into inventory_standard_cost_history(item_id, previous_cost, new_cost, source_type, changed_by_name)
        values (new.id, old.average_cost, new.average_cost, 'ITEM_COST_UPDATE', current_setting('aksara.user_name', true));
    end if;
    return new;
end $$;

drop trigger if exists trg_inventory_standard_cost_history on inventory_item;
create trigger trg_inventory_standard_cost_history
after update of average_cost on inventory_item
for each row execute function capture_inventory_standard_cost_history();

create table if not exists inventory_transfer (
    id uuid primary key default gen_random_uuid(),
    document_number varchar(30) not null,
    document_year smallint not null,
    document_date date not null,
    source_warehouse_code varchar(20) not null references warehouse(code),
    destination_warehouse_code varchar(20) not null references warehouse(code),
    notes text,
    status varchar(12) not null default 'POSTED' check (status in ('POSTED','CANCELLED')),
    created_by_name varchar(50), computer_name varchar(100), created_at timestamptz not null default now(),
    cancelled_at timestamptz, cancelled_by_name varchar(50), cancellation_reason text,
    version bigint not null default 1,
    unique(document_number, document_year),
    check (source_warehouse_code <> destination_warehouse_code)
);

create table if not exists inventory_transfer_line (
    id uuid primary key default gen_random_uuid(),
    transfer_id uuid not null references inventory_transfer(id) on delete cascade,
    line_number integer not null,
    item_id uuid not null references inventory_item(id),
    quantity numeric(19,3) not null check (quantity > 0),
    unit varchar(10) not null,
    unit_cost numeric(19,5) not null check (unit_cost >= 0),
    unique(transfer_id, line_number)
);

create table if not exists inventory_adjustment (
    id uuid primary key default gen_random_uuid(),
    document_number varchar(30) not null,
    document_year smallint not null,
    document_date date not null,
    adjustment_type_code varchar(20) not null references inventory_adjustment_type(code),
    warehouse_code varchar(20) not null references warehouse(code),
    notes text,
    status varchar(12) not null default 'POSTED' check (status in ('POSTED','CANCELLED')),
    created_by_name varchar(50), computer_name varchar(100), created_at timestamptz not null default now(),
    cancelled_at timestamptz, cancelled_by_name varchar(50), cancellation_reason text,
    version bigint not null default 1,
    unique(document_number, document_year)
);

create table if not exists inventory_adjustment_line (
    id uuid primary key default gen_random_uuid(),
    adjustment_id uuid not null references inventory_adjustment(id) on delete cascade,
    line_number integer not null,
    item_id uuid not null references inventory_item(id),
    quantity numeric(19,3) not null check (quantity <> 0),
    unit varchar(10) not null,
    unit_cost numeric(19,5) not null check (unit_cost >= 0),
    unique(adjustment_id, line_number)
);

create table if not exists inventory_stock_count (
    id uuid primary key default gen_random_uuid(),
    document_number varchar(30) not null,
    document_year smallint not null,
    document_date date not null,
    warehouse_code varchar(20) not null references warehouse(code),
    notes text,
    status varchar(12) not null default 'POSTED' check (status in ('POSTED','CANCELLED')),
    created_by_name varchar(50), computer_name varchar(100), created_at timestamptz not null default now(),
    cancelled_at timestamptz, cancelled_by_name varchar(50), cancellation_reason text,
    version bigint not null default 1,
    unique(document_number, document_year)
);

create table if not exists inventory_stock_count_line (
    id uuid primary key default gen_random_uuid(),
    stock_count_id uuid not null references inventory_stock_count(id) on delete cascade,
    line_number integer not null,
    item_id uuid not null references inventory_item(id),
    system_quantity numeric(19,3) not null,
    counted_quantity numeric(19,3) not null check (counted_quantity >= 0),
    difference_quantity numeric(19,3) not null,
    unit varchar(10) not null,
    unit_cost numeric(19,5) not null check (unit_cost >= 0),
    unique(stock_count_id, line_number)
);

create table if not exists business_unit (
    code varchar(20) primary key,
    name varchar(100) not null,
    is_active boolean not null default true
);
insert into business_unit(code, name) values ('MAIN', 'Wilayah Usaha Utama') on conflict (code) do nothing;

create table if not exists inventory_mbap_send (
    id uuid primary key default gen_random_uuid(),
    document_number varchar(30) not null,
    document_year smallint not null,
    document_date date not null,
    source_warehouse_code varchar(20) not null references warehouse(code),
    destination_business_unit_code varchar(20) not null references business_unit(code),
    settlement_date date,
    bank_code varchar(20),
    notes text,
    status varchar(12) not null default 'POSTED' check (status in ('POSTED','PARTIAL','RECEIVED','CANCELLED')),
    created_by_name varchar(50), computer_name varchar(100), created_at timestamptz not null default now(),
    cancelled_at timestamptz, cancelled_by_name varchar(50), cancellation_reason text,
    version bigint not null default 1,
    unique(document_number, document_year)
);

create table if not exists inventory_mbap_send_line (
    id uuid primary key default gen_random_uuid(),
    send_id uuid not null references inventory_mbap_send(id) on delete cascade,
    line_number integer not null,
    item_id uuid not null references inventory_item(id),
    quantity numeric(19,3) not null check (quantity > 0),
    unit varchar(10) not null,
    unit_cost numeric(19,5) not null check (unit_cost >= 0),
    unique(send_id, line_number)
);

create table if not exists inventory_mbap_receive (
    id uuid primary key default gen_random_uuid(),
    document_number varchar(30) not null,
    document_year smallint not null,
    document_date date not null,
    send_id uuid not null references inventory_mbap_send(id),
    destination_warehouse_code varchar(20) not null references warehouse(code),
    notes text,
    status varchar(12) not null default 'POSTED' check (status in ('POSTED','CANCELLED')),
    created_by_name varchar(50), computer_name varchar(100), created_at timestamptz not null default now(),
    cancelled_at timestamptz, cancelled_by_name varchar(50), cancellation_reason text,
    version bigint not null default 1,
    unique(document_number, document_year)
);

create table if not exists inventory_mbap_receive_line (
    id uuid primary key default gen_random_uuid(),
    receive_id uuid not null references inventory_mbap_receive(id) on delete cascade,
    send_line_id uuid not null references inventory_mbap_send_line(id),
    line_number integer not null,
    item_id uuid not null references inventory_item(id),
    quantity numeric(19,3) not null check (quantity > 0),
    unit varchar(10) not null,
    unit_cost numeric(19,5) not null check (unit_cost >= 0),
    unique(receive_id, line_number)
);

create index if not exists ix_inventory_transfer_date on inventory_transfer(document_date);
create index if not exists ix_inventory_adjustment_date on inventory_adjustment(document_date);
create index if not exists ix_inventory_stock_count_date on inventory_stock_count(document_date);
create index if not exists ix_inventory_mbap_send_date on inventory_mbap_send(document_date);
create index if not exists ix_inventory_mbap_receive_date on inventory_mbap_receive(document_date);
create index if not exists ix_inventory_mbap_receive_send on inventory_mbap_receive(send_id, status);
create index if not exists ix_inventory_item_category on inventory_item(category_code);
create index if not exists ix_inventory_item_name on inventory_item(name);
