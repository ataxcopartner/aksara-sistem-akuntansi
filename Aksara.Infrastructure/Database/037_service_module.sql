alter table inventory_item
    add column if not exists item_type varchar(12) not null default 'GOODS';

update inventory_item set item_type = 'GOODS' where item_type is null;

do $$
begin
    if not exists (select 1 from pg_constraint where conname = 'ck_inventory_item_type') then
        alter table inventory_item
            add constraint ck_inventory_item_type check (item_type in ('GOODS', 'SERVICE'));
    end if;
end $$;

alter table sale
    add column if not exists document_kind varchar(12) not null default 'GOODS';
alter table purchase
    add column if not exists document_kind varchar(12) not null default 'GOODS';

do $$
begin
    if not exists (select 1 from pg_constraint where conname = 'ck_sale_document_kind') then
        alter table sale add constraint ck_sale_document_kind check (document_kind in ('GOODS', 'SERVICE'));
    end if;
    if not exists (select 1 from pg_constraint where conname = 'ck_purchase_document_kind') then
        alter table purchase add constraint ck_purchase_document_kind check (document_kind in ('GOODS', 'SERVICE'));
    end if;
end $$;

create index if not exists ix_inventory_item_type_code on inventory_item(item_type, code);
create index if not exists ix_sale_kind_date on sale(document_kind, document_date desc);
create index if not exists ix_purchase_kind_date on purchase(document_kind, document_date desc);

create table if not exists service_sale_material_usage (
    id uuid primary key default gen_random_uuid(),
    sale_id uuid not null references sale(id) on delete cascade,
    sale_line_id uuid not null references sale_line(id) on delete cascade,
    component_item_id uuid not null references inventory_item(id),
    warehouse_code varchar(20) not null references warehouse(code),
    quantity numeric(19,6) not null check (quantity > 0),
    unit_cost numeric(19,5) not null check (unit_cost >= 0),
    cost_amount numeric(19,5) not null check (cost_amount >= 0),
    unique(sale_line_id, component_item_id, warehouse_code)
);

create index if not exists ix_service_sale_material_sale on service_sale_material_usage(sale_id);
create index if not exists ix_service_sale_material_component on service_sale_material_usage(component_item_id, warehouse_code);

insert into ledger_account(code, name, account_type, normal_balance, parent_code, is_postable, is_cash_bank)
values
    ('41102', 'PENDAPATAN JASA', 'REVENUE', 'C', '40000', true, false),
    ('51102', 'HPP JASA', 'COGS', 'D', '50000', true, false),
    ('61102', 'BEBAN PEMBELIAN JASA', 'EXPENSE', 'D', '60000', true, false)
on conflict (code) do nothing;

insert into ledger_transaction_mapping(mapping_code, mapping_name, account_code)
values
    ('SERVICE_REVENUE', 'Pendapatan penjualan jasa', '41102'),
    ('SERVICE_COGS', 'Harga pokok jasa dari bahan penunjang', '51102'),
    ('SERVICE_PURCHASE_EXPENSE', 'Beban pembelian jasa', '61102')
on conflict (mapping_code) do update
set mapping_name = excluded.mapping_name,
    account_code = coalesce(ledger_transaction_mapping.account_code, excluded.account_code),
    updated_at = now(),
    version = ledger_transaction_mapping.version + 1;
