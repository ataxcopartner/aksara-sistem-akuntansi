create extension if not exists pgcrypto;

create table if not exists app_role (
    id uuid primary key default gen_random_uuid(),
    code varchar(30) not null unique,
    name varchar(100) not null,
    is_system boolean not null default false,
    created_at timestamptz not null default now()
);

create table if not exists app_user (
    id uuid primary key default gen_random_uuid(),
    username varchar(50) not null unique,
    display_name varchar(120) not null,
    password_hash text not null,
    role_id uuid not null references app_role(id),
    is_active boolean not null default true,
    last_login_at timestamptz,
    created_at timestamptz not null default now(),
    version bigint not null default 1
);

create table if not exists app_permission (
    id uuid primary key default gen_random_uuid(),
    role_id uuid not null references app_role(id) on delete cascade,
    module varchar(50) not null,
    action varchar(30) not null,
    is_allowed boolean not null default false,
    unique(role_id, module, action)
);

create table if not exists supplier (
    id uuid primary key default gen_random_uuid(),
    code varchar(20) not null unique,
    company_name varchar(150) not null,
    contact_name varchar(100),
    address text,
    city varchar(80), phone varchar(30), email varchar(150),
    tax_number varchar(40), credit_days integer not null default 0,
    is_active boolean not null default true,
    legacy_data_id integer,
    created_at timestamptz not null default now(),
    updated_at timestamptz,
    version bigint not null default 1
);

create table if not exists customer (
    id uuid primary key default gen_random_uuid(),
    code varchar(20) not null unique,
    company_name varchar(150) not null,
    contact_name varchar(100),
    address text,
    city varchar(80), phone varchar(30), email varchar(150),
    tax_number varchar(40), credit_limit numeric(19,2) not null default 0,
    credit_days integer not null default 0,
    price_group varchar(20), sales_code varchar(20),
    is_active boolean not null default true,
    legacy_data_id integer,
    created_at timestamptz not null default now(),
    updated_at timestamptz,
    version bigint not null default 1
);

create table if not exists inventory_item (
    id uuid primary key default gen_random_uuid(),
    code varchar(20) not null unique,
    barcode varchar(40), name varchar(150) not null,
    category_code varchar(20), unit varchar(10) not null,
    purchase_price numeric(19,5) not null default 0,
    average_cost numeric(19,5) not null default 0,
    retail_price numeric(19,2) not null default 0,
    wholesale_price numeric(19,2) not null default 0,
    minimum_stock numeric(19,3) not null default 0,
    maximum_stock numeric(19,3) not null default 0,
    rack varchar(20), brand varchar(50), notes text,
    is_active boolean not null default true,
    legacy_data_id integer,
    created_at timestamptz not null default now(),
    updated_at timestamptz,
    version bigint not null default 1
);

create table if not exists purchase (
    id uuid primary key default gen_random_uuid(),
    document_number varchar(30) not null,
    document_year smallint not null,
    document_date date not null,
    supplier_id uuid not null references supplier(id),
    supplier_invoice_number varchar(50),
    transaction_type varchar(10) not null check (transaction_type in ('CASH','CREDIT')),
    due_date date,
    subtotal numeric(19,2) not null,
    discount_percent numeric(8,4) not null default 0,
    discount_amount numeric(19,2) not null default 0,
    tax_percent numeric(8,4) not null default 0,
    tax_amount numeric(19,2) not null default 0,
    grand_total numeric(19,2) not null,
    down_payment numeric(19,2) not null default 0,
    balance_due numeric(19,2) not null default 0,
    notes text,
    status varchar(12) not null default 'DRAFT',
    legacy_data_id integer,
    created_by uuid references app_user(id),
    created_at timestamptz not null default now(),
    updated_at timestamptz,
    version bigint not null default 1,
    unique(document_number, document_year)
);

create table if not exists purchase_line (
    id uuid primary key default gen_random_uuid(),
    purchase_id uuid not null references purchase(id) on delete cascade,
    line_number integer not null,
    item_id uuid not null references inventory_item(id),
    warehouse_code varchar(20),
    quantity numeric(19,3) not null check (quantity > 0),
    unit varchar(10) not null,
    unit_price numeric(19,5) not null,
    discount_1 numeric(8,4) not null default 0,
    discount_2 numeric(8,4) not null default 0,
    discount_3 numeric(8,4) not null default 0,
    line_total numeric(19,2) not null,
    legacy_data_id integer,
    unique(purchase_id, line_number)
);

create table if not exists sale (
    id uuid primary key default gen_random_uuid(),
    document_number varchar(30) not null,
    document_year smallint not null,
    document_date date not null,
    customer_id uuid references customer(id),
    transaction_type varchar(10) not null check (transaction_type in ('CASH','CREDIT')),
    due_date date, sales_code varchar(20), warehouse_code varchar(20),
    subtotal numeric(19,2) not null,
    discount_amount numeric(19,2) not null default 0,
    tax_amount numeric(19,2) not null default 0,
    grand_total numeric(19,2) not null,
    down_payment numeric(19,2) not null default 0,
    balance_due numeric(19,2) not null default 0,
    cost_total numeric(19,5) not null default 0,
    notes text, status varchar(12) not null default 'DRAFT',
    legacy_data_id integer,
    created_by uuid references app_user(id),
    created_at timestamptz not null default now(),
    updated_at timestamptz,
    version bigint not null default 1,
    unique(document_number, document_year)
);

create table if not exists sale_line (
    id uuid primary key default gen_random_uuid(),
    sale_id uuid not null references sale(id) on delete cascade,
    line_number integer not null,
    item_id uuid not null references inventory_item(id),
    quantity numeric(19,3) not null check (quantity > 0),
    unit varchar(10) not null,
    unit_price numeric(19,2) not null,
    discount_1 numeric(8,4) not null default 0,
    discount_2 numeric(8,4) not null default 0,
    discount_3 numeric(8,4) not null default 0,
    line_total numeric(19,2) not null,
    cost_amount numeric(19,5) not null default 0,
    legacy_data_id integer,
    unique(sale_id, line_number)
);

create table if not exists inventory_movement (
    id uuid primary key default gen_random_uuid(),
    occurred_at timestamptz not null,
    item_id uuid not null references inventory_item(id),
    warehouse_code varchar(20) not null,
    source_type varchar(30) not null,
    source_id uuid not null,
    source_number varchar(30) not null,
    quantity_in numeric(19,3) not null default 0,
    quantity_out numeric(19,3) not null default 0,
    unit_cost numeric(19,5) not null default 0,
    created_at timestamptz not null default now()
);

create table if not exists audit_log (
    id bigint generated always as identity primary key,
    occurred_at timestamptz not null default now(),
    user_id uuid references app_user(id),
    computer_name varchar(100), ip_address inet,
    module varchar(50) not null, action varchar(30) not null,
    entity_type varchar(80), entity_id varchar(80),
    before_data jsonb, after_data jsonb
);

create table if not exists legacy_migration (
    id bigint generated always as identity primary key,
    source_table varchar(80) not null,
    source_key varchar(200) not null,
    target_table varchar(80) not null,
    target_id uuid not null,
    migrated_at timestamptz not null default now(),
    checksum varchar(64),
    unique(source_table, source_key, target_table)
);

create index if not exists ix_purchase_date on purchase(document_date);
create index if not exists ix_sale_date on sale(document_date);
create index if not exists ix_inventory_movement_item_date on inventory_movement(item_id, occurred_at);
create index if not exists ix_audit_log_entity on audit_log(entity_type, entity_id);

insert into app_role(code, name, is_system)
values ('MASTER', 'Master Administrator', true)
on conflict (code) do nothing;
