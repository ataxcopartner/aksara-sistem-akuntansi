alter table sale
    add column if not exists sale_channel varchar(20) not null default 'STANDARD',
    add column if not exists cash_received numeric(19,2) not null default 0,
    add column if not exists cash_change numeric(19,2) not null default 0;

do $$
begin
    if not exists (
        select 1
        from pg_constraint
        where conname = 'sale_channel_check'
          and conrelid = 'sale'::regclass
    ) then
        alter table sale
            add constraint sale_channel_check
            check (sale_channel in ('STANDARD', 'GUNGGUNGAN'));
    end if;

    if not exists (
        select 1
        from pg_constraint
        where conname = 'sale_cash_tender_check'
          and conrelid = 'sale'::regclass
    ) then
        alter table sale
            add constraint sale_cash_tender_check
            check (cash_received >= 0 and cash_change >= 0);
    end if;
end $$;

create index if not exists ix_sale_channel_date
    on sale(sale_channel, document_date, created_at);

insert into customer(
    code, company_name, contact_name, address, city, phone, email,
    tax_number, credit_limit, credit_days, price_group, sales_code, is_active)
values (
    'GUNGGUNGAN', 'PENJUALAN TUNAI', null, null, null, null, null,
    null, 0, 0, null, null, true)
on conflict(code) do update
set company_name = 'PENJUALAN TUNAI',
    is_active = true,
    updated_at = now(),
    version = customer.version + 1
where customer.company_name is distinct from 'PENJUALAN TUNAI'
   or customer.is_active is distinct from true;
