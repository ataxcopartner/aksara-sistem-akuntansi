alter table purchase add column if not exists warehouse_code varchar(20);
update purchase set warehouse_code = 'UTAMA' where warehouse_code is null;
alter table purchase alter column warehouse_code set default 'UTAMA';
alter table purchase alter column warehouse_code set not null;
alter table purchase add column if not exists created_by_name varchar(50);
alter table purchase add column if not exists computer_name varchar(100);
alter table purchase add column if not exists cancelled_at timestamptz;
alter table purchase add column if not exists cancelled_by_name varchar(50);
alter table purchase add column if not exists cancellation_reason text;

update purchase_line set warehouse_code = 'UTAMA' where warehouse_code is null;
alter table purchase_line alter column warehouse_code set default 'UTAMA';
alter table purchase_line alter column warehouse_code set not null;

create unique index if not exists ux_inventory_movement_purchase_item_warehouse
    on inventory_movement(source_type, source_id, item_id, warehouse_code)
    where source_type in ('PURCHASE', 'PURCHASE_CANCEL');

create table if not exists purchase_return (
    id uuid primary key default gen_random_uuid(),
    document_number varchar(30) not null,
    document_year smallint not null,
    document_date date not null,
    purchase_id uuid not null references purchase(id),
    supplier_id uuid not null references supplier(id),
    transaction_type varchar(10) not null check (transaction_type in ('CASH','CREDIT')),
    warehouse_code varchar(20) not null references warehouse(code),
    subtotal numeric(19,2) not null,
    discount_percent numeric(8,4) not null default 0,
    discount_amount numeric(19,2) not null default 0,
    tax_percent numeric(8,4) not null default 0,
    tax_amount numeric(19,2) not null default 0,
    grand_total numeric(19,2) not null,
    notes text,
    status varchar(12) not null default 'POSTED',
    created_by_name varchar(50),
    computer_name varchar(100),
    created_at timestamptz not null default now(),
    cancelled_at timestamptz,
    cancelled_by_name varchar(50),
    cancellation_reason text,
    version bigint not null default 1,
    unique(document_number, document_year)
);

create table if not exists purchase_return_line (
    id uuid primary key default gen_random_uuid(),
    purchase_return_id uuid not null references purchase_return(id) on delete cascade,
    purchase_line_id uuid not null references purchase_line(id),
    line_number integer not null,
    item_id uuid not null references inventory_item(id),
    warehouse_code varchar(20) not null references warehouse(code),
    quantity numeric(19,3) not null check (quantity > 0),
    unit varchar(10) not null,
    unit_price numeric(19,5) not null,
    discount_percent numeric(8,4) not null default 0,
    line_total numeric(19,2) not null,
    unique(purchase_return_id, line_number)
);

create index if not exists ix_purchase_return_date on purchase_return(document_date);
create index if not exists ix_purchase_return_purchase on purchase_return(purchase_id, status);
create index if not exists ix_purchase_return_line_source on purchase_return_line(purchase_line_id);

create unique index if not exists ux_inventory_movement_purchase_return_item_warehouse
    on inventory_movement(source_type, source_id, item_id, warehouse_code)
    where source_type in ('PURCHASE_RETURN', 'PURCHASE_RETURN_CANCEL');

