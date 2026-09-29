create table if not exists ledger_account (
    id uuid primary key default gen_random_uuid(),
    code varchar(20) not null unique,
    name varchar(150) not null,
    account_type varchar(20) not null check (account_type in ('ASSET','LIABILITY','EQUITY','REVENUE','EXPENSE','COGS')),
    normal_balance char(1) not null check (normal_balance in ('D','C')),
    parent_code varchar(20) references ledger_account(code),
    is_postable boolean not null default true,
    is_cash_bank boolean not null default false,
    is_active boolean not null default true,
    created_at timestamptz not null default now(),
    updated_at timestamptz,
    version bigint not null default 1
);

create index if not exists ix_ledger_account_parent on ledger_account(parent_code);
create index if not exists ix_ledger_account_type on ledger_account(account_type, code);

insert into ledger_account(code, name, account_type, normal_balance, parent_code, is_postable, is_cash_bank)
values
    ('10000', 'AKTIVA', 'ASSET', 'D', null, false, false),
    ('11000', 'AKTIVA LANCAR', 'ASSET', 'D', '10000', false, false),
    ('11100', 'KAS DAN BANK', 'ASSET', 'D', '11000', false, false),
    ('11101', 'KAS BESAR', 'ASSET', 'D', '11100', true, true),
    ('11102', 'KAS KECIL', 'ASSET', 'D', '11100', true, true),
    ('11201', 'BANK BCA', 'ASSET', 'D', '11100', true, true),
    ('11202', 'BANK MANDIRI', 'ASSET', 'D', '11100', true, true),
    ('11301', 'PIUTANG USAHA', 'ASSET', 'D', '11000', true, false),
    ('11401', 'PERSEDIAAN BARANG', 'ASSET', 'D', '11000', true, false),
    ('20000', 'KEWAJIBAN', 'LIABILITY', 'C', null, false, false),
    ('21101', 'HUTANG USAHA', 'LIABILITY', 'C', '20000', true, false),
    ('21201', 'PPN KELUARAN', 'LIABILITY', 'C', '20000', true, false),
    ('30000', 'MODAL', 'EQUITY', 'C', null, false, false),
    ('31101', 'MODAL PEMILIK', 'EQUITY', 'C', '30000', true, false),
    ('31201', 'LABA DITAHAN', 'EQUITY', 'C', '30000', true, false),
    ('40000', 'PENDAPATAN', 'REVENUE', 'C', null, false, false),
    ('41101', 'PENJUALAN', 'REVENUE', 'C', '40000', true, false),
    ('41201', 'PENDAPATAN LAIN-LAIN', 'REVENUE', 'C', '40000', true, false),
    ('50000', 'HARGA POKOK PENJUALAN', 'COGS', 'D', null, false, false),
    ('51101', 'HPP PENJUALAN', 'COGS', 'D', '50000', true, false),
    ('60000', 'BEBAN', 'EXPENSE', 'D', null, false, false),
    ('61101', 'BEBAN OPERASIONAL', 'EXPENSE', 'D', '60000', true, false),
    ('61201', 'BEBAN ADMINISTRASI', 'EXPENSE', 'D', '60000', true, false),
    ('61301', 'BEBAN BANK', 'EXPENSE', 'D', '60000', true, false)
on conflict (code) do nothing;

create table if not exists journal_entry (
    id uuid primary key default gen_random_uuid(),
    document_number varchar(30) not null,
    document_year smallint not null,
    document_date date not null,
    memo text,
    source_module varchar(40) not null default 'GENERAL',
    source_id uuid,
    status varchar(12) not null default 'POSTED' check (status in ('POSTED','CANCELLED')),
    created_by_name varchar(50),
    computer_name varchar(100),
    created_at timestamptz not null default now(),
    cancelled_at timestamptz,
    cancelled_by_name varchar(50),
    cancellation_reason text,
    version bigint not null default 1,
    unique(document_number, document_year)
);

create table if not exists journal_entry_line (
    id uuid primary key default gen_random_uuid(),
    journal_entry_id uuid not null references journal_entry(id) on delete cascade,
    line_number integer not null,
    account_id uuid not null references ledger_account(id),
    description text,
    debit numeric(19,2) not null default 0 check (debit >= 0),
    credit numeric(19,2) not null default 0 check (credit >= 0),
    unique(journal_entry_id, line_number),
    check ((debit > 0 and credit = 0) or (credit > 0 and debit = 0))
);

create index if not exists ix_journal_entry_date on journal_entry(document_date, document_number);
create index if not exists ix_journal_entry_source on journal_entry(source_module, source_id);
create index if not exists ix_journal_entry_line_account on journal_entry_line(account_id);

create table if not exists ledger_transaction_mapping (
    mapping_code varchar(40) primary key,
    mapping_name varchar(150) not null,
    account_code varchar(20) references ledger_account(code),
    created_at timestamptz not null default now(),
    updated_at timestamptz,
    version bigint not null default 1
);

insert into ledger_transaction_mapping(mapping_code, mapping_name, account_code)
values
    ('CASH_DEFAULT', 'Kas default transaksi tunai', '11101'),
    ('BANK_DEFAULT', 'Bank default transaksi transfer/giro', '11201'),
    ('SALES_REVENUE', 'Penjualan barang', '41101'),
    ('SALES_RECEIVABLE', 'Piutang penjualan kredit', '11301'),
    ('SALES_TAX', 'PPN keluaran penjualan', '21201'),
    ('SALES_COGS', 'Harga pokok penjualan', '51101'),
    ('INVENTORY', 'Persediaan barang', '11401'),
    ('PURCHASE_PAYABLE', 'Hutang pembelian kredit', '21101'),
    ('OTHER_INCOME', 'Pendapatan lain-lain', '41201'),
    ('OPERATING_EXPENSE', 'Beban operasional', '61101')
on conflict (mapping_code) do nothing;

create table if not exists cash_account (
    code varchar(20) primary key,
    name varchar(100) not null,
    account_code varchar(20) not null references ledger_account(code),
    is_active boolean not null default true,
    created_at timestamptz not null default now(),
    updated_at timestamptz,
    version bigint not null default 1
);

insert into cash_account(code, name, account_code)
values
    ('KAS', 'Kas Besar', '11101'),
    ('KASKECIL', 'Kas Kecil', '11102')
on conflict (code) do nothing;

create table if not exists bank_account (
    code varchar(20) primary key,
    bank_name varchar(100) not null,
    account_number varchar(60),
    account_holder varchar(100),
    account_code varchar(20) not null references ledger_account(code),
    is_active boolean not null default true,
    created_at timestamptz not null default now(),
    updated_at timestamptz,
    version bigint not null default 1
);

insert into bank_account(code, bank_name, account_number, account_holder, account_code)
values
    ('BCA', 'Bank BCA', null, null, '11201'),
    ('MANDIRI', 'Bank Mandiri', null, null, '11202')
on conflict (code) do nothing;

create table if not exists cash_bank_category (
    category_type varchar(12) not null check (category_type in ('EXPENSE','INCOME')),
    code varchar(20) not null,
    name varchar(120) not null,
    account_code varchar(20) not null references ledger_account(code),
    is_deleted boolean not null default false,
    is_active boolean not null default true,
    created_at timestamptz not null default now(),
    updated_at timestamptz,
    version bigint not null default 1,
    primary key(category_type, code)
);

insert into cash_bank_category(category_type, code, name, account_code)
values
    ('EXPENSE', 'OPERASIONAL', 'Biaya Operasional', '61101'),
    ('EXPENSE', 'ADMIN', 'Biaya Administrasi', '61201'),
    ('EXPENSE', 'BANK', 'Biaya Bank', '61301'),
    ('INCOME', 'LAIN', 'Pendapatan Lain-lain', '41201')
on conflict (category_type, code) do nothing;

create table if not exists cash_bank_transaction (
    id uuid primary key default gen_random_uuid(),
    document_number varchar(30) not null,
    document_year smallint not null,
    document_date date not null,
    transaction_kind varchar(12) not null check (transaction_kind in ('RECEIPT','PAYMENT')),
    method varchar(12) not null check (method in ('CASH','BANK','CHECK_GIRO')),
    cash_account_code varchar(20) references cash_account(code),
    bank_account_code varchar(20) references bank_account(code),
    check_giro_number varchar(50),
    check_giro_due_date date,
    counterparty varchar(150),
    category_type varchar(12),
    category_code varchar(20),
    account_code varchar(20) not null references ledger_account(code),
    amount numeric(19,2) not null check (amount > 0),
    notes text,
    journal_entry_id uuid not null references journal_entry(id),
    created_by_name varchar(50),
    computer_name varchar(100),
    created_at timestamptz not null default now(),
    version bigint not null default 1,
    unique(document_number, document_year),
    foreign key(category_type, category_code) references cash_bank_category(category_type, code),
    check ((method = 'CASH' and cash_account_code is not null) or (method in ('BANK','CHECK_GIRO') and bank_account_code is not null))
);

create index if not exists ix_cash_bank_transaction_date on cash_bank_transaction(document_date, document_number);
create index if not exists ix_cash_bank_transaction_account on cash_bank_transaction(account_code);
create index if not exists ix_cash_bank_transaction_journal on cash_bank_transaction(journal_entry_id);
