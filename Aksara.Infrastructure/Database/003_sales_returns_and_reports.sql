create table if not exists sale_return (
    id uuid primary key default gen_random_uuid(),
    document_number varchar(30) not null,
    document_year smallint not null,
    document_date date not null,
    sale_id uuid not null references sale(id),
    customer_id uuid not null references customer(id),
    transaction_type varchar(10) not null check (transaction_type in ('CASH','CREDIT')),
    sales_code varchar(20),
    warehouse_code varchar(20) not null references warehouse(code),
    subtotal numeric(19,2) not null,
    discount_percent numeric(8,4) not null default 0,
    discount_amount numeric(19,2) not null default 0,
    tax_percent numeric(8,4) not null default 0,
    tax_amount numeric(19,2) not null default 0,
    grand_total numeric(19,2) not null,
    cost_total numeric(19,5) not null default 0,
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

create table if not exists sale_return_line (
    id uuid primary key default gen_random_uuid(),
    sale_return_id uuid not null references sale_return(id) on delete cascade,
    sale_line_id uuid not null references sale_line(id),
    line_number integer not null,
    item_id uuid not null references inventory_item(id),
    warehouse_code varchar(20) not null references warehouse(code),
    quantity numeric(19,3) not null check (quantity > 0),
    unit varchar(10) not null,
    unit_price numeric(19,2) not null,
    discount_percent numeric(8,4) not null default 0,
    line_total numeric(19,2) not null,
    cost_amount numeric(19,5) not null default 0,
    unique(sale_return_id, line_number)
);

create index if not exists ix_sale_return_date on sale_return(document_date);
create index if not exists ix_sale_return_sale on sale_return(sale_id, status);
create index if not exists ix_sale_return_line_source on sale_return_line(sale_line_id);

create unique index if not exists ux_inventory_movement_sale_return_item_warehouse
    on inventory_movement(source_type, source_id, item_id, warehouse_code)
    where source_type in ('SALE_RETURN', 'SALE_RETURN_CANCEL');
