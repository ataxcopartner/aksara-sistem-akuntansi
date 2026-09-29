create table if not exists supplier_payment (
    id uuid primary key default gen_random_uuid(),
    document_number varchar(30) not null,
    document_year smallint not null,
    document_date date not null,
    supplier_id uuid not null references supplier(id),
    method varchar(12) not null check (method in ('CASH','BANK','CHECK_GIRO')),
    cash_account_code varchar(20) references cash_account(code),
    bank_account_code varchar(20) references bank_account(code),
    check_giro_number varchar(50),
    check_giro_due_date date,
    payment_amount numeric(19,2) not null check (payment_amount > 0),
    notes text,
    journal_entry_id uuid not null references journal_entry(id),
    status varchar(12) not null default 'POSTED' check (status in ('POSTED','CANCELLED')),
    created_by_name varchar(50),
    computer_name varchar(100),
    created_at timestamptz not null default now(),
    cancelled_at timestamptz,
    cancelled_by_name varchar(50),
    cancellation_reason text,
    version bigint not null default 1,
    unique(document_number, document_year),
    check (
        (method = 'CASH' and cash_account_code is not null)
        or (method in ('BANK','CHECK_GIRO') and bank_account_code is not null)
    )
);

create table if not exists supplier_payment_line (
    id uuid primary key default gen_random_uuid(),
    supplier_payment_id uuid not null references supplier_payment(id) on delete cascade,
    purchase_id uuid not null references purchase(id),
    line_number integer not null,
    original_balance numeric(19,2) not null,
    paid_before numeric(19,2) not null,
    amount numeric(19,2) not null check (amount > 0),
    remaining_after numeric(19,2) not null,
    unique(supplier_payment_id, line_number),
    unique(supplier_payment_id, purchase_id)
);

create index if not exists ix_supplier_payment_date on supplier_payment(document_date, document_number);
create index if not exists ix_supplier_payment_supplier on supplier_payment(supplier_id, status);
create index if not exists ix_supplier_payment_line_purchase on supplier_payment_line(purchase_id);
