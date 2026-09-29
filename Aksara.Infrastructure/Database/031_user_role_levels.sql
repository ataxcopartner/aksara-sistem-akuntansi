update app_role
set name = 'Superadmin', version = version + 1
where code = 'MASTER' and name <> 'Superadmin';

with new_roles as (
    insert into app_role(code, name, is_system, is_active)
    values ('ADMIN', 'Admin', true, true),
           ('MANAJEMEN', 'Manajemen', true, true),
           ('STAFF', 'Staff', true, true)
    on conflict (code) do nothing
    returning id, code
), modules(module) as (
    values ('LEDGER'), ('CASH_BANK'), ('SUPPLIER'), ('PURCHASE'),
           ('CUSTOMER'), ('SALES'), ('INVENTORY'), ('MAINTENANCE'), ('CONFIGURATION')
), actions(action) as (
    values ('VIEW'), ('MANAGE'), ('DELETE')
)
insert into app_permission(role_id, module, action, is_allowed)
select r.id, m.module, a.action, true
from new_roles r
cross join modules m
cross join actions a
where (r.code = 'ADMIN' and (
        m.module in ('LEDGER', 'CASH_BANK', 'SUPPLIER', 'PURCHASE', 'CUSTOMER', 'SALES', 'INVENTORY')
        or (m.module = 'CONFIGURATION' and a.action in ('VIEW', 'MANAGE'))
        or (m.module = 'MAINTENANCE' and a.action = 'VIEW')
    ))
    or (r.code = 'MANAJEMEN' and (
        (m.module in ('LEDGER', 'CASH_BANK', 'SUPPLIER', 'PURCHASE', 'CUSTOMER', 'SALES', 'INVENTORY')
         and a.action in ('VIEW', 'MANAGE'))
        or (m.module = 'MAINTENANCE' and a.action = 'VIEW')
    ))
    or (r.code = 'STAFF' and
        m.module in ('CASH_BANK', 'SUPPLIER', 'PURCHASE', 'CUSTOMER', 'SALES', 'INVENTORY')
        and a.action in ('VIEW', 'MANAGE'))
on conflict (role_id, module, action) do nothing;
