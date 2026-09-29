alter table inventory_category
    add column if not exists category_type varchar(12) not null default 'GOODS';

update inventory_category
set category_type = 'GOODS'
where category_type is null;

do $$
begin
    if not exists (select 1 from pg_constraint where conname = 'ck_inventory_category_type') then
        alter table inventory_category
            add constraint ck_inventory_category_type check (category_type in ('GOODS', 'SERVICE'));
    end if;
end $$;

create index if not exists ix_inventory_category_type_code
    on inventory_category(category_type, code);
