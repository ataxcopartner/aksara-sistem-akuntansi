create table if not exists program_configuration (
    singleton boolean primary key default true check (singleton),
    company_name varchar(150) not null default 'AKSARA',
    owner_name varchar(120) not null default '',
    address text not null default '',
    city varchar(80) not null default '',
    phone varchar(40) not null default '',
    email varchar(150) not null default '',
    tax_number varchar(50) not null default '',
    tax_enabled boolean not null default true,
    default_tax_percent numeric(8,4) not null default 0,
    transaction_detail_rows integer not null default 20 check (transaction_detail_rows between 1 and 200),
    minimum_print_rows integer not null default 20 check (minimum_print_rows between 1 and 60),
    sale_number_prefix varchar(10) not null default 'PJL',
    purchase_number_prefix varchar(10) not null default 'PBM',
    sale_return_number_prefix varchar(10) not null default 'RJL',
    purchase_return_number_prefix varchar(10) not null default 'RPB',
    cash_bank_number_prefix varchar(10) not null default 'KB',
    customer_receipt_number_prefix varchar(10) not null default 'BPT',
    supplier_payment_number_prefix varchar(10) not null default 'BUT',
    check_giro_number_prefix varchar(10) not null default 'CG',
    show_print_date_time boolean not null default false,
    show_item_code boolean not null default true,
    show_printed_by boolean not null default false,
    print_header_mode varchar(12) not null default 'TEXT' check (print_header_mode in ('NONE','TEXT','LOGO_TEXT')),
    sale_title varchar(80) not null default 'NOTA PENJUALAN',
    purchase_title varchar(80) not null default 'NOTA PEMBELIAN',
    sale_return_title varchar(80) not null default 'RETUR PENJUALAN',
    purchase_return_title varchar(80) not null default 'RETUR PEMBELIAN',
    delivery_title varchar(80) not null default 'SURAT JALAN',
    cash_bank_title varchar(80) not null default 'BUKTI KAS / BANK',
    signature_1 varchar(80) not null default 'Dibuat Oleh',
    signature_2 varchar(80) not null default 'Diperiksa Oleh',
    signature_3 varchar(80) not null default 'Diterima Oleh',
    print_footer_note text not null default '',
    updated_at timestamptz not null default now(),
    updated_by_name varchar(80),
    computer_name varchar(100),
    version bigint not null default 1
);

insert into program_configuration(singleton)
values (true)
on conflict (singleton) do nothing;

alter table app_role add column if not exists is_active boolean not null default true;
alter table app_role add column if not exists version bigint not null default 1;
