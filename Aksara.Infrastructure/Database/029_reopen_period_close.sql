alter table accounting_period_close
    add column if not exists reopened_by_name varchar(80),
    add column if not exists reopen_computer_name varchar(100),
    add column if not exists reopened_at timestamptz,
    add column if not exists reopen_reason varchar(300);

alter table accounting_period_close drop constraint if exists accounting_period_close_status_check;
alter table accounting_period_close drop constraint if exists ck_accounting_period_close_status;
alter table accounting_period_close drop constraint if exists ck_accounting_period_close_reopen_audit;
alter table accounting_period_close drop constraint if exists accounting_period_close_cutoff_date_key;

alter table accounting_period_close
    add constraint ck_accounting_period_close_status
    check (status in ('PROCESSING', 'POSTED', 'REOPENED'));

alter table accounting_period_close
    add constraint ck_accounting_period_close_reopen_audit
    check (status <> 'REOPENED' or (reopened_by_name is not null and reopened_at is not null));

drop index if exists ux_accounting_period_close_name;
drop index if exists ux_accounting_period_close_active_name;
create unique index ux_accounting_period_close_name
    on accounting_period_close (lower(period_name))
    where status in ('PROCESSING', 'POSTED');
create unique index if not exists ux_accounting_period_close_active_cutoff
    on accounting_period_close (cutoff_date)
    where status in ('PROCESSING', 'POSTED');

create or replace function aksara_guard_period_close_record()
returns trigger
language plpgsql
as $$
declare
    latest_posted_id uuid;
begin
    if tg_op = 'INSERT' then
        if new.status <> 'PROCESSING' then
            raise exception using errcode = 'PCL02', message = 'Histori Tutup Buku hanya boleh dibuat melalui proses resmi.', detail = 'PERIOD_CLOSE_IMMUTABLE';
        end if;
        return new;
    end if;

    if tg_op = 'DELETE' then
        raise exception using errcode = 'PCL02', message = 'Histori Tutup Buku bersifat tetap dan tidak dapat dihapus.', detail = 'PERIOD_CLOSE_IMMUTABLE';
    end if;

    if old.status = 'POSTED' and new.status = 'REOPENED' then
        select id into latest_posted_id
        from accounting_period_close
        where status = 'POSTED'
        order by cutoff_date desc, created_at desc, id desc
        limit 1;

        if coalesce(current_setting('aksara.period_close_reopen_id', true), '') = old.id::text
           and latest_posted_id = old.id
           and new.reopened_by_name is not null
           and new.reopened_at is not null
           and (to_jsonb(new) - 'status' - 'reopened_by_name' - 'reopen_computer_name' - 'reopened_at' - 'reopen_reason')
               = (to_jsonb(old) - 'status' - 'reopened_by_name' - 'reopen_computer_name' - 'reopened_at' - 'reopen_reason') then
            return new;
        end if;
    end if;

    if old.status in ('POSTED', 'REOPENED') then
        raise exception using errcode = 'PCL02', message = 'Histori Tutup Buku bersifat tetap dan hanya penutupan terakhir yang dapat dibatalkan.', detail = 'PERIOD_CLOSE_IMMUTABLE';
    end if;
    return new;
end;
$$;

drop trigger if exists trg_accounting_period_close_immutable on accounting_period_close;
create trigger trg_accounting_period_close_immutable
before insert or update or delete on accounting_period_close
for each row execute function aksara_guard_period_close_record();
