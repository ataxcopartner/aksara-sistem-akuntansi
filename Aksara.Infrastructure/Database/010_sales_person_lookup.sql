create table if not exists sales_person (
    id uuid primary key default gen_random_uuid(),
    code varchar(20) not null unique,
    name varchar(100) not null,
    group_code varchar(20),
    supervisor_code varchar(20),
    is_active boolean not null default true,
    legacy_data_id integer,
    updated_at timestamptz,
    version bigint not null default 1
);

insert into sales_person(code, name, group_code, supervisor_code, is_active, legacy_data_id, updated_at)
values
    ('AR01', 'ARIE', null, null, true, 1, now()),
    ('IW01', 'IWAN', null, null, true, 2, now()),
    ('AL01', 'ALEX', null, null, true, 3, now())
on conflict (code) do update set
    name = excluded.name,
    group_code = excluded.group_code,
    supervisor_code = excluded.supervisor_code,
    is_active = excluded.is_active,
    legacy_data_id = excluded.legacy_data_id,
    updated_at = now(),
    version = sales_person.version + 1;
