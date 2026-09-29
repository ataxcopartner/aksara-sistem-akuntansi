create table if not exists partner_balance_document (
    id uuid primary key default gen_random_uuid(),
    partner_type varchar(10) not null check (partner_type in ('SUPPLIER','CUSTOMER')),
    document_kind varchar(12) not null check (document_kind in ('OPENING','CORRECTION')),
    direction varchar(10) not null check (direction in ('INCREASE','DECREASE')),
    document_number varchar(30) not null,
    document_year smallint not null,
    document_date date not null,
    supplier_id uuid references supplier(id),
    customer_id uuid references customer(id),
    due_date date,
    amount numeric(19,2) not null check (amount > 0),
    target_source_type varchar(24),
    target_source_id uuid,
    notes text,
    journal_entry_id uuid not null references journal_entry(id),
    status varchar(12) not null default 'POSTED' check (status in ('POSTED','CANCELLED')),
    created_by_name varchar(50),
    computer_name varchar(100),
    created_at timestamptz not null default now(),
    updated_at timestamptz,
    updated_by_name varchar(50),
    cancelled_at timestamptz,
    cancelled_by_name varchar(50),
    cancellation_reason text,
    version bigint not null default 1 check (version > 0),
    unique(document_number, document_year),
    check ((partner_type = 'SUPPLIER' and supplier_id is not null and customer_id is null)
        or (partner_type = 'CUSTOMER' and customer_id is not null and supplier_id is null)),
    check ((document_kind = 'OPENING' and direction = 'INCREASE' and target_source_id is null and target_source_type is null)
        or (document_kind = 'CORRECTION' and direction = 'INCREASE' and target_source_id is null and target_source_type is null)
        or (document_kind = 'CORRECTION' and direction = 'DECREASE' and target_source_id is not null and target_source_type is not null)),
    check (target_source_type is null
        or (partner_type = 'SUPPLIER' and target_source_type in ('PURCHASE','BALANCE_DOCUMENT'))
        or (partner_type = 'CUSTOMER' and target_source_type in ('SALE','BALANCE_DOCUMENT')))
);

create index if not exists ix_partner_balance_supplier on partner_balance_document(supplier_id, status, document_date);
create index if not exists ix_partner_balance_customer on partner_balance_document(customer_id, status, document_date);
create index if not exists ix_partner_balance_target on partner_balance_document(target_source_type, target_source_id, status);

alter table supplier_payment_line alter column purchase_id drop not null;
alter table supplier_payment_line add column if not exists source_type varchar(24) not null default 'PURCHASE';
alter table supplier_payment_line add column if not exists balance_document_id uuid references partner_balance_document(id);
update supplier_payment_line set source_type = 'PURCHASE' where source_type is null or source_type = '';

alter table customer_receipt_line alter column sale_id drop not null;
alter table customer_receipt_line add column if not exists source_type varchar(24) not null default 'SALE';
alter table customer_receipt_line add column if not exists balance_document_id uuid references partner_balance_document(id);
update customer_receipt_line set source_type = 'SALE' where source_type is null or source_type = '';

do $$
begin
    if not exists (select 1 from pg_constraint where conname = 'ck_supplier_payment_line_source'
        and conrelid = 'supplier_payment_line'::regclass) then
        alter table supplier_payment_line add constraint ck_supplier_payment_line_source check (
            (source_type = 'PURCHASE' and purchase_id is not null and balance_document_id is null)
            or (source_type = 'BALANCE_DOCUMENT' and purchase_id is null and balance_document_id is not null));
    end if;
    if not exists (select 1 from pg_constraint where conname = 'ck_customer_receipt_line_source'
        and conrelid = 'customer_receipt_line'::regclass) then
        alter table customer_receipt_line add constraint ck_customer_receipt_line_source check (
            (source_type = 'SALE' and sale_id is not null and balance_document_id is null)
            or (source_type = 'BALANCE_DOCUMENT' and sale_id is null and balance_document_id is not null));
    end if;
end $$;

create unique index if not exists ux_supplier_payment_line_balance
    on supplier_payment_line(supplier_payment_id, balance_document_id) where balance_document_id is not null;
create index if not exists ix_supplier_payment_line_balance on supplier_payment_line(balance_document_id);
create unique index if not exists ux_customer_receipt_line_balance
    on customer_receipt_line(customer_receipt_id, balance_document_id) where balance_document_id is not null;
create index if not exists ix_customer_receipt_line_balance on customer_receipt_line(balance_document_id);

insert into ledger_transaction_mapping(mapping_code, mapping_name, account_code)
values
    ('OPENING_BALANCE_EQUITY', 'Akun lawan saldo awal utang/piutang', null),
    ('PAYABLE_CORRECTION', 'Akun lawan koreksi utang', null),
    ('RECEIVABLE_CORRECTION', 'Akun lawan koreksi piutang', null)
on conflict (mapping_code) do update set mapping_name = excluded.mapping_name;
