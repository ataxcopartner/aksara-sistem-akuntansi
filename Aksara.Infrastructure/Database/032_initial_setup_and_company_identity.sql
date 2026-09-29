create table if not exists company_instance (
    singleton boolean primary key default true check (singleton),
    instance_id uuid not null default gen_random_uuid() unique,
    company_code varchar(40) not null default 'PERUSAHAAN-UTAMA',
    company_name varchar(150) not null,
    isolation_mode varchar(20) not null default 'DATABASE' check (isolation_mode = 'DATABASE'),
    created_at timestamptz not null default now(),
    version bigint not null default 1
);

insert into company_instance(singleton, company_name)
select true, company_name from program_configuration where singleton
on conflict (singleton) do nothing;

create table if not exists initial_setup (
    singleton boolean primary key default true check (singleton),
    id uuid not null default gen_random_uuid() unique,
    status varchar(12) not null default 'DRAFT' check (status in ('DRAFT','ACTIVE')),
    start_date date,
    cutoff_date date,
    legacy_protected boolean not null default false,
    activated_at timestamptz,
    activated_by_name varchar(80),
    activated_computer_name varchar(100),
    version bigint not null default 1,
    check (start_date is null or cutoff_date is null or cutoff_date < start_date)
);

insert into initial_setup(singleton, status, legacy_protected, activated_at, activated_by_name)
select true,
       case when exists(select 1 from journal_entry)
                  or exists(select 1 from inventory_movement)
                  or exists(select 1 from purchase)
                  or exists(select 1 from sale)
                  or exists(select 1 from partner_balance_document)
                  or exists(select 1 from cash_bank_transaction)
            then 'ACTIVE' else 'DRAFT' end,
       case when exists(select 1 from journal_entry)
                  or exists(select 1 from inventory_movement)
                  or exists(select 1 from purchase)
                  or exists(select 1 from sale)
                  or exists(select 1 from partner_balance_document)
                  or exists(select 1 from cash_bank_transaction)
            then true else false end,
       case when exists(select 1 from journal_entry)
                  or exists(select 1 from inventory_movement)
                  or exists(select 1 from purchase)
                  or exists(select 1 from sale)
                  or exists(select 1 from partner_balance_document)
                  or exists(select 1 from cash_bank_transaction)
            then now() else null end,
       case when exists(select 1 from journal_entry)
                  or exists(select 1 from inventory_movement)
                  or exists(select 1 from purchase)
                  or exists(select 1 from sale)
                  or exists(select 1 from partner_balance_document)
                  or exists(select 1 from cash_bank_transaction)
            then 'MIGRASI AKSARA' else null end
on conflict (singleton) do nothing;

create table if not exists initial_setup_inventory_draft (
    id uuid primary key default gen_random_uuid(),
    setup_id uuid not null references initial_setup(id) on delete cascade,
    warehouse_code varchar(20) not null references warehouse(code),
    item_id uuid not null references inventory_item(id),
    quantity numeric(19,3) not null check (quantity > 0),
    unit_cost numeric(19,5) not null check (unit_cost >= 0),
    unique(setup_id, warehouse_code, item_id)
);

create table if not exists initial_setup_partner_draft (
    id uuid primary key default gen_random_uuid(),
    setup_id uuid not null references initial_setup(id) on delete cascade,
    partner_type varchar(10) not null check (partner_type in ('SUPPLIER','CUSTOMER')),
    supplier_id uuid references supplier(id),
    customer_id uuid references customer(id),
    document_number varchar(30) not null,
    due_date date,
    amount numeric(19,2) not null check (amount > 0),
    notes text,
    check ((partner_type='SUPPLIER' and supplier_id is not null and customer_id is null)
        or (partner_type='CUSTOMER' and customer_id is not null and supplier_id is null)),
    unique(setup_id, partner_type, document_number)
);

create table if not exists initial_setup_ledger_draft (
    id uuid primary key default gen_random_uuid(),
    setup_id uuid not null references initial_setup(id) on delete cascade,
    account_id uuid not null references ledger_account(id),
    description text,
    debit numeric(19,2) not null default 0 check (debit >= 0),
    credit numeric(19,2) not null default 0 check (credit >= 0),
    check ((debit > 0 and credit = 0) or (credit > 0 and debit = 0)),
    unique(setup_id, account_id)
);

create table if not exists initial_setup_posting (
    id bigint generated always as identity primary key,
    setup_id uuid not null references initial_setup(id),
    entity_type varchar(50) not null,
    entity_id uuid not null,
    entity_number varchar(40),
    created_at timestamptz not null default now(),
    unique(setup_id, entity_type, entity_id)
);

create table if not exists initial_setup_audit (
    id bigint generated always as identity primary key,
    setup_id uuid not null references initial_setup(id),
    action varchar(20) not null,
    user_name varchar(80),
    computer_name varchar(100),
    reason text,
    snapshot jsonb,
    occurred_at timestamptz not null default now()
);

create or replace function sync_company_instance_name()
returns trigger language plpgsql as $$
begin
    update company_instance set company_name=new.company_name, version=version+1 where singleton;
    return new;
end $$;

drop trigger if exists trg_program_configuration_company_identity on program_configuration;
create trigger trg_program_configuration_company_identity after update of company_name on program_configuration
for each row when (new.company_name is distinct from old.company_name)
execute function sync_company_instance_name();

create or replace function guard_initial_setup_draft_mutation()
returns trigger language plpgsql as $$
declare current_status varchar(12);
begin
    if tg_op = 'DELETE' then
        select status into current_status from initial_setup where id=old.setup_id;
    else
        select status into current_status from initial_setup where id=new.setup_id;
    end if;
    if current_status <> 'DRAFT' then
        raise exception using errcode='P0001', message='Setup Data Awal sudah aktif dan draft dikunci.';
    end if;
    if tg_op = 'DELETE' then return old; end if;
    return new;
end $$;

drop trigger if exists trg_setup_inventory_draft_guard on initial_setup_inventory_draft;
create trigger trg_setup_inventory_draft_guard before insert or update or delete on initial_setup_inventory_draft
for each row execute function guard_initial_setup_draft_mutation();
drop trigger if exists trg_setup_partner_draft_guard on initial_setup_partner_draft;
create trigger trg_setup_partner_draft_guard before insert or update or delete on initial_setup_partner_draft
for each row execute function guard_initial_setup_draft_mutation();
drop trigger if exists trg_setup_ledger_draft_guard on initial_setup_ledger_draft;
create trigger trg_setup_ledger_draft_guard before insert or update or delete on initial_setup_ledger_draft
for each row execute function guard_initial_setup_draft_mutation();
