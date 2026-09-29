do $$
declare constraint_row record;
begin
    for constraint_row in
        select conname
        from pg_constraint
        where conrelid = 'cash_bank_transaction'::regclass
          and contype = 'c'
          and pg_get_constraintdef(oid) ilike '%method%'
          and pg_get_constraintdef(oid) ilike '%cash_account_code%'
          and pg_get_constraintdef(oid) ilike '%bank_account_code%'
    loop
        execute format('alter table cash_bank_transaction drop constraint %I', constraint_row.conname);
    end loop;
end $$;

alter table cash_bank_transaction
    add constraint cash_bank_transaction_method_account_check
    check (
        (method = 'CASH' and cash_account_code is not null)
        or (method = 'BANK' and bank_account_code is not null)
        or method = 'CHECK_GIRO'
    );

create table if not exists cash_bank_transaction_line (
    id uuid primary key default gen_random_uuid(),
    transaction_id uuid not null references cash_bank_transaction(id) on delete cascade,
    line_number integer not null,
    settlement_method varchar(12) not null check (settlement_method in ('CASH','BANK','CHECK_GIRO')),
    settlement_code varchar(20),
    settlement_account_code varchar(20) not null references ledger_account(code),
    counterparty_group varchar(12),
    counterparty_code varchar(20),
    counterparty_name varchar(150),
    account_code varchar(20) not null references ledger_account(code),
    amount numeric(19,2) not null check (amount > 0),
    notes text,
    check_giro_number varchar(50),
    check_giro_due_date date,
    legacy_data_id integer,
    created_at timestamptz not null default now(),
    updated_at timestamptz,
    version bigint not null default 1,
    unique(transaction_id, line_number)
);

create index if not exists ix_cash_bank_transaction_line_transaction on cash_bank_transaction_line(transaction_id, line_number);
create index if not exists ix_cash_bank_transaction_line_account on cash_bank_transaction_line(account_code);
create index if not exists ix_cash_bank_transaction_line_settlement on cash_bank_transaction_line(settlement_account_code);

create table if not exists legacy_cps_check_giro (
    no varchar(20) primary key,
    document_year smallint not null,
    document_date date not null,
    bank_name varchar(80),
    check_giro_number varchar(50),
    check_giro_type smallint,
    due_date date,
    original_amount numeric(19,2) not null default 0,
    additional_amount numeric(19,2) not null default 0,
    amount numeric(19,2) not null default 0,
    paid_amount numeric(19,2) not null default 0,
    customer_code varchar(20),
    origin_code smallint,
    opening_balance_flag boolean not null default false,
    rejected_date date,
    rejection_release_date date,
    currency_code varchar(3),
    exchange_rate numeric(19,2),
    notes text,
    legacy_data_id integer,
    created_by_name varchar(50),
    created_at timestamptz not null default now(),
    updated_at timestamptz,
    version bigint not null default 1
);

create index if not exists ix_legacy_cps_check_giro_due on legacy_cps_check_giro(due_date, no);
create index if not exists ix_legacy_cps_check_giro_customer on legacy_cps_check_giro(customer_code);

create table if not exists legacy_cps_import_issue (
    source_table varchar(40) not null,
    source_key varchar(80) not null,
    severity varchar(12) not null,
    message text not null,
    created_at timestamptz not null default now(),
    primary key(source_table, source_key, message)
);
