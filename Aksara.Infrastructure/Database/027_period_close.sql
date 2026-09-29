create table if not exists accounting_period_close (
    id uuid primary key default gen_random_uuid(),
    period_name varchar(100) not null,
    cutoff_date date not null,
    opening_date date not null,
    status varchar(20) not null default 'PROCESSING' check (status in ('PROCESSING', 'POSTED')),
    created_by_name varchar(80) not null,
    computer_name varchar(100),
    created_at timestamptz not null default now(),
    backup_location text not null,
    backup_file_name varchar(180) not null default '',
    snapshot_sha256 char(64) not null default '',
    archive_location text not null default '',
    archive_file_name varchar(180) not null default '',
    archive_sha256 char(64) not null default '',
    summary_json jsonb not null default '{}'::jsonb,
    export_payload text not null default '',
    archive_payload text not null default '',
    idempotency_key varchar(80) not null unique,
    constraint ck_accounting_period_close_opening_date check (opening_date >= cutoff_date),
    unique (cutoff_date)
);

alter table accounting_period_close add column if not exists archive_location text not null default '';
alter table accounting_period_close add column if not exists archive_file_name varchar(180) not null default '';
alter table accounting_period_close add column if not exists archive_sha256 char(64) not null default '';
alter table accounting_period_close add column if not exists archive_payload text not null default '';
alter table accounting_period_close drop constraint if exists accounting_period_close_check;
alter table accounting_period_close drop constraint if exists ck_accounting_period_close_opening_date;
alter table accounting_period_close add constraint ck_accounting_period_close_opening_date check (opening_date >= cutoff_date);

create unique index if not exists ux_accounting_period_close_name
    on accounting_period_close (lower(period_name));
create index if not exists ix_accounting_period_close_dates
    on accounting_period_close (cutoff_date desc, opening_date desc);

create table if not exists period_close_ledger_balance (
    period_close_id uuid not null references accounting_period_close(id) on delete restrict,
    account_code varchar(20) not null,
    account_name varchar(150) not null,
    normal_balance varchar(1) not null,
    debit_total numeric(19,2) not null,
    credit_total numeric(19,2) not null,
    balance numeric(19,2) not null,
    primary key (period_close_id, account_code)
);

create table if not exists period_close_cash_bank_balance (
    period_close_id uuid not null references accounting_period_close(id) on delete restrict,
    resource_type varchar(10) not null check (resource_type in ('CASH', 'BANK')),
    resource_code varchar(20) not null,
    resource_name varchar(150) not null,
    account_code varchar(20) not null,
    balance numeric(19,2) not null,
    primary key (period_close_id, resource_type, resource_code)
);

create table if not exists period_close_partner_balance (
    period_close_id uuid not null references accounting_period_close(id) on delete restrict,
    partner_type varchar(10) not null check (partner_type in ('SUPPLIER', 'CUSTOMER')),
    partner_code varchar(20) not null,
    partner_name varchar(150) not null,
    source_type varchar(30) not null,
    source_id uuid not null,
    document_number varchar(30) not null,
    document_date date not null,
    due_date date,
    balance numeric(19,2) not null,
    primary key (period_close_id, partner_type, source_type, source_id)
);

create table if not exists period_close_inventory_balance (
    period_close_id uuid not null references accounting_period_close(id) on delete restrict,
    item_code varchar(20) not null,
    item_name varchar(150) not null,
    warehouse_code varchar(20) not null,
    quantity numeric(19,3) not null,
    unit_cost numeric(19,5) not null,
    inventory_value numeric(19,2) not null,
    primary key (period_close_id, item_code, warehouse_code)
);

create table if not exists period_close_inventory_cost_layer (
    id uuid primary key default gen_random_uuid(),
    period_close_id uuid not null references accounting_period_close(id) on delete restrict,
    item_code varchar(20) not null,
    item_name varchar(150) not null,
    warehouse_code varchar(20) not null,
    received_at timestamptz not null,
    source_type varchar(30) not null,
    source_id uuid not null,
    source_number varchar(30) not null,
    original_quantity numeric(19,3) not null,
    remaining_quantity numeric(19,3) not null,
    unit_cost numeric(19,5) not null
);
create index if not exists ix_period_close_inventory_layer_lookup
    on period_close_inventory_cost_layer(period_close_id, item_code, warehouse_code, received_at);

create or replace function aksara_assert_period_open(target_date date)
returns void
language plpgsql
as $$
declare
    closed_cutoff date;
begin
    if target_date is null or coalesce(current_setting('aksara.period_close_bypass', true), '') = 'on' then
        return;
    end if;

    select max(cutoff_date) into closed_cutoff
    from accounting_period_close
    where status = 'POSTED';

    if closed_cutoff is not null and target_date <= closed_cutoff then
        raise exception using
            errcode = 'PCL01',
            message = format('Periode tanggal %s sudah ditutup sampai %s. Transaksi tidak dapat dibuat, diubah, dihapus, dibatalkan, atau dipulihkan.', target_date, closed_cutoff),
            detail = 'PERIOD_CLOSED';
    end if;
end;
$$;

create or replace function aksara_guard_closed_period_header()
returns trigger
language plpgsql
as $$
declare
    old_date date;
    new_date date;
begin
    if tg_op <> 'INSERT' then
        if tg_nargs > 1 and tg_argv[1] = 'timestamp' then
            old_date := nullif(to_jsonb(old) ->> tg_argv[0], '')::timestamptz::date;
        else
            old_date := nullif(to_jsonb(old) ->> tg_argv[0], '')::date;
        end if;
        perform aksara_assert_period_open(old_date);
    end if;

    if tg_op <> 'DELETE' then
        if tg_nargs > 1 and tg_argv[1] = 'timestamp' then
            new_date := nullif(to_jsonb(new) ->> tg_argv[0], '')::timestamptz::date;
        else
            new_date := nullif(to_jsonb(new) ->> tg_argv[0], '')::date;
        end if;
        perform aksara_assert_period_open(new_date);
    end if;

    if tg_op = 'DELETE' then return old; end if;
    return new;
end;
$$;

create or replace function aksara_guard_closed_period_child()
returns trigger
language plpgsql
as $$
declare
    old_parent_id uuid;
    new_parent_id uuid;
    parent_date date;
begin
    if tg_op <> 'INSERT' then
        old_parent_id := nullif(to_jsonb(old) ->> tg_argv[1], '')::uuid;
        if old_parent_id is not null then
            execute format('select %I from %I where id = $1', tg_argv[2], tg_argv[0])
                into parent_date using old_parent_id;
            perform aksara_assert_period_open(parent_date);
        end if;
    end if;

    if tg_op <> 'DELETE' then
        new_parent_id := nullif(to_jsonb(new) ->> tg_argv[1], '')::uuid;
        if new_parent_id is not null then
            execute format('select %I from %I where id = $1', tg_argv[2], tg_argv[0])
                into parent_date using new_parent_id;
            perform aksara_assert_period_open(parent_date);
        end if;
    end if;

    if tg_op = 'DELETE' then return old; end if;
    return new;
end;
$$;

do $$
declare
    item record;
begin
    for item in
        select * from (values
            ('journal_entry', 'document_date', 'date'),
            ('cash_bank_transaction', 'document_date', 'date'),
            ('purchase', 'document_date', 'date'),
            ('purchase_return', 'document_date', 'date'),
            ('sale', 'document_date', 'date'),
            ('sale_return', 'document_date', 'date'),
            ('supplier_payment', 'document_date', 'date'),
            ('customer_receipt', 'document_date', 'date'),
            ('customer_check_giro', 'document_date', 'date'),
            ('partner_balance_document', 'document_date', 'date'),
            ('inventory_adjustment', 'document_date', 'date'),
            ('inventory_transfer', 'document_date', 'date'),
            ('inventory_stock_count', 'document_date', 'date'),
            ('inventory_mbap_send', 'document_date', 'date'),
            ('inventory_mbap_receive', 'document_date', 'date'),
            ('inventory_movement', 'occurred_at', 'timestamp'),
            ('inventory_standard_cost_history', 'effective_at', 'timestamp')
        ) as x(table_name, date_column, date_kind)
    loop
        execute format('drop trigger if exists trg_period_close_%I on %I', item.table_name, item.table_name);
        execute format(
            'create trigger trg_period_close_%I before insert or update or delete on %I for each row execute function aksara_guard_closed_period_header(%L, %L)',
            item.table_name, item.table_name, item.date_column, item.date_kind);
    end loop;
end;
$$;

do $$
declare
    item record;
begin
    for item in
        select * from (values
            ('journal_entry_line', 'journal_entry', 'journal_entry_id'),
            ('cash_bank_transaction_line', 'cash_bank_transaction', 'transaction_id'),
            ('purchase_line', 'purchase', 'purchase_id'),
            ('purchase_return_line', 'purchase_return', 'purchase_return_id'),
            ('sale_line', 'sale', 'sale_id'),
            ('sale_return_line', 'sale_return', 'sale_return_id'),
            ('supplier_payment_line', 'supplier_payment', 'supplier_payment_id'),
            ('customer_receipt_line', 'customer_receipt', 'customer_receipt_id'),
            ('inventory_adjustment_line', 'inventory_adjustment', 'adjustment_id'),
            ('inventory_transfer_line', 'inventory_transfer', 'transfer_id'),
            ('inventory_stock_count_line', 'inventory_stock_count', 'stock_count_id'),
            ('inventory_mbap_send_line', 'inventory_mbap_send', 'send_id'),
            ('inventory_mbap_receive_line', 'inventory_mbap_receive', 'receive_id')
        ) as x(table_name, parent_table, foreign_key)
    loop
        execute format('drop trigger if exists trg_period_close_%I on %I', item.table_name, item.table_name);
        execute format(
            'create trigger trg_period_close_%I before insert or update or delete on %I for each row execute function aksara_guard_closed_period_child(%L, %L, %L)',
            item.table_name, item.table_name, item.parent_table, item.foreign_key, 'document_date');
    end loop;
end;
$$;

create or replace function aksara_guard_period_close_record()
returns trigger
language plpgsql
as $$
begin
    if tg_op = 'INSERT' then
        if new.status <> 'PROCESSING' then
            raise exception using errcode = 'PCL02', message = 'Histori Tutup Buku hanya boleh dibuat melalui proses resmi.', detail = 'PERIOD_CLOSE_IMMUTABLE';
        end if;
        return new;
    end if;
    if tg_op = 'DELETE' or old.status = 'POSTED' then
        raise exception using errcode = 'PCL02', message = 'Histori Tutup Buku bersifat tetap dan tidak dapat diubah atau dihapus.', detail = 'PERIOD_CLOSE_IMMUTABLE';
    end if;
    return new;
end;
$$;

drop trigger if exists trg_accounting_period_close_immutable on accounting_period_close;
create trigger trg_accounting_period_close_immutable
before insert or update or delete on accounting_period_close
for each row execute function aksara_guard_period_close_record();

create or replace function aksara_guard_period_close_snapshot()
returns trigger
language plpgsql
as $$
declare
    parent_status varchar(20);
begin
    if tg_op = 'INSERT' then
        select status into parent_status from accounting_period_close where id = new.period_close_id;
        if parent_status = 'PROCESSING' then return new; end if;
    end if;
    raise exception using errcode = 'PCL02', message = 'Snapshot Tutup Buku bersifat tetap dan tidak dapat diubah atau dihapus.', detail = 'PERIOD_CLOSE_IMMUTABLE';
end;
$$;

do $$
declare
    table_name text;
begin
    foreach table_name in array array[
        'period_close_ledger_balance',
        'period_close_cash_bank_balance',
        'period_close_partner_balance',
        'period_close_inventory_balance',
        'period_close_inventory_cost_layer'
    ]
    loop
        execute format('drop trigger if exists trg_%I_immutable on %I', table_name, table_name);
        execute format(
            'create trigger trg_%I_immutable before insert or update or delete on %I for each row execute function aksara_guard_period_close_snapshot()',
            table_name, table_name);
    end loop;
end;
$$;
