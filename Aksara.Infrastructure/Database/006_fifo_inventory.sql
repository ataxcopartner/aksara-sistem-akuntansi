create table if not exists inventory_cost_layer (
    id uuid primary key default gen_random_uuid(),
    item_id uuid not null references inventory_item(id),
    warehouse_code varchar(20) not null references warehouse(code),
    received_at timestamptz not null,
    source_type varchar(40) not null,
    source_id uuid,
    source_line_id uuid,
    source_number varchar(40),
    original_quantity numeric(19,3) not null check (original_quantity > 0),
    remaining_quantity numeric(19,3) not null check (remaining_quantity >= 0 and remaining_quantity <= original_quantity),
    unit_cost numeric(19,5) not null check (unit_cost >= 0),
    created_at timestamptz not null default now()
);

create index if not exists ix_inventory_cost_layer_fifo
    on inventory_cost_layer(item_id, warehouse_code, received_at, created_at, id)
    where remaining_quantity > 0;
create index if not exists ix_inventory_cost_layer_source
    on inventory_cost_layer(source_type, source_id);

create table if not exists inventory_cost_allocation (
    id uuid primary key default gen_random_uuid(),
    issue_source_type varchar(40) not null,
    issue_source_id uuid not null,
    issue_line_id uuid,
    layer_id uuid not null references inventory_cost_layer(id),
    quantity numeric(19,3) not null check (quantity > 0),
    unit_cost numeric(19,5) not null check (unit_cost >= 0),
    amount numeric(19,5) not null check (amount >= 0),
    created_at timestamptz not null default now()
);

create index if not exists ix_inventory_cost_allocation_issue
    on inventory_cost_allocation(issue_source_type, issue_source_id, issue_line_id);
create index if not exists ix_inventory_cost_allocation_layer
    on inventory_cost_allocation(layer_id);

-- Riwayat sebelum FIFO tidak selalu mempunyai identitas lot yang lengkap. Saldo
-- aktif dimigrasikan menjadi satu lapisan pembuka per barang/gudang agar transaksi
-- baru dapat langsung memakai FIFO tanpa mengubah saldo yang telah disepakati.
insert into inventory_cost_layer(
    item_id, warehouse_code, received_at, source_type, source_number,
    original_quantity, remaining_quantity, unit_cost)
select b.item_id, b.warehouse_code, timestamp with time zone '1970-01-01 00:00:00+00',
       'FIFO_OPENING', 'MIGRASI-0.14', b.quantity, b.quantity, i.average_cost
from inventory_balance b
join inventory_item i on i.id = b.item_id
where b.quantity > 0
  and not exists (
      select 1 from inventory_cost_layer l
      where l.item_id = b.item_id and l.warehouse_code = b.warehouse_code
  );
