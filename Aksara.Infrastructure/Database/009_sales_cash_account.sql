alter table sale add column if not exists cash_account_code varchar(20);

do $$
begin
    if not exists (
        select 1
        from pg_constraint
        where conname = 'fk_sale_cash_account'
          and conrelid = 'sale'::regclass
    ) then
        alter table sale
            add constraint fk_sale_cash_account
            foreign key (cash_account_code) references cash_account(code);
    end if;
end $$;

create index if not exists ix_sale_cash_account on sale(cash_account_code);
