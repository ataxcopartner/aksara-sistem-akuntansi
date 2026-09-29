insert into ledger_account(code, name, account_type, normal_balance, parent_code, is_postable, is_cash_bank, is_active)
values ('11302', 'Cek Giro Dagang', 'ASSET', 'D', '11000', true, true, true)
on conflict (code) do update
set name = excluded.name,
    account_type = excluded.account_type,
    normal_balance = excluded.normal_balance,
    is_postable = excluded.is_postable,
    is_cash_bank = excluded.is_cash_bank,
    is_active = excluded.is_active,
    updated_at = now(),
    version = ledger_account.version + 1;

insert into ledger_transaction_mapping(mapping_code, mapping_name, account_code)
values ('CHECK_GIRO_RECEIVABLE', 'Cek/Giro diterima dari customer', '11302')
on conflict (mapping_code) do update
set mapping_name = excluded.mapping_name,
    account_code = excluded.account_code,
    updated_at = now(),
    version = ledger_transaction_mapping.version + 1;

create table if not exists customer_check_giro (
    id uuid primary key default gen_random_uuid(),
    sequence_number varchar(30) not null unique,
    customer_id uuid not null references customer(id),
    bank_name varchar(80) not null,
    check_giro_number varchar(50) not null,
    check_giro_type varchar(10),
    due_date date,
    amount numeric(19,2) not null check (amount > 0),
    status varchar(12) not null default 'OPEN' check (status in ('OPEN','USED','CANCELLED')),
    source varchar(30) not null default 'MANUAL',
    legacy_no varchar(20) unique,
    notes text,
    created_by_name varchar(50),
    computer_name varchar(100),
    created_at timestamptz not null default now(),
    used_receipt_id uuid,
    used_at timestamptz,
    updated_at timestamptz,
    version bigint not null default 1
);

create index if not exists ix_customer_check_giro_customer on customer_check_giro(customer_id, status, sequence_number);
create index if not exists ix_customer_check_giro_due on customer_check_giro(due_date, sequence_number);

create table if not exists customer_receipt (
    id uuid primary key default gen_random_uuid(),
    document_number varchar(30) not null,
    document_year smallint not null,
    document_date date not null,
    customer_id uuid not null references customer(id),
    collector_code varchar(20),
    method varchar(12) not null check (method in ('CASH','BANK','CHECK_GIRO')),
    cash_account_code varchar(20) references cash_account(code),
    bank_account_code varchar(20) references bank_account(code),
    check_giro_id uuid references customer_check_giro(id),
    receipt_amount numeric(19,2) not null check (receipt_amount > 0),
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
        or (method = 'BANK' and bank_account_code is not null)
        or (method = 'CHECK_GIRO' and check_giro_id is not null)
    )
);

create table if not exists customer_receipt_line (
    id uuid primary key default gen_random_uuid(),
    customer_receipt_id uuid not null references customer_receipt(id) on delete cascade,
    sale_id uuid not null references sale(id),
    line_number integer not null,
    original_balance numeric(19,2) not null,
    paid_before numeric(19,2) not null,
    amount numeric(19,2) not null check (amount > 0),
    remaining_after numeric(19,2) not null,
    unique(customer_receipt_id, line_number),
    unique(customer_receipt_id, sale_id)
);

do $$
begin
    if not exists (
        select 1
        from pg_constraint
        where conname = 'customer_check_giro_used_receipt_id_fkey'
          and conrelid = 'customer_check_giro'::regclass
    ) then
        alter table customer_check_giro
            add constraint customer_check_giro_used_receipt_id_fkey
            foreign key (used_receipt_id) references customer_receipt(id);
    end if;
end $$;

create index if not exists ix_customer_receipt_date on customer_receipt(document_date, document_number);
create index if not exists ix_customer_receipt_customer on customer_receipt(customer_id, status);
create index if not exists ix_customer_receipt_line_sale on customer_receipt_line(sale_id);

insert into customer_check_giro(
    id, sequence_number, customer_id, bank_name, check_giro_number, check_giro_type, due_date,
    amount, status, source, legacy_no, notes, created_by_name, updated_at, version)
select
    md5('CPS_CHECK_GIRO:' || l.no)::uuid,
    l.no,
    c.id,
    coalesce(nullif(trim(l.bank_name), ''), '-'),
    coalesce(nullif(trim(l.check_giro_number), ''), l.no),
    case l.check_giro_type when 1 then 'C' when 2 then 'G' else null end,
    l.due_date,
    coalesce(nullif(l.amount, 0), nullif(l.original_amount + l.additional_amount, 0), 0),
    case when l.rejected_date is null then 'OPEN' else 'CANCELLED' end,
    'CPS_LEGACY',
    l.no,
    l.notes,
    l.created_by_name,
    now(),
    1
from legacy_cps_check_giro l
join customer c on c.code = upper(trim(l.customer_code))
where l.customer_code is not null
  and coalesce(nullif(l.amount, 0), nullif(l.original_amount + l.additional_amount, 0), 0) > 0
on conflict (sequence_number) do update
set customer_id = excluded.customer_id,
    bank_name = excluded.bank_name,
    check_giro_number = excluded.check_giro_number,
    check_giro_type = excluded.check_giro_type,
    due_date = excluded.due_date,
    amount = excluded.amount,
    legacy_no = excluded.legacy_no,
    notes = excluded.notes,
    updated_at = now(),
    version = customer_check_giro.version + 1
where customer_check_giro.source = 'CPS_LEGACY'
  and customer_check_giro.status <> 'USED';
