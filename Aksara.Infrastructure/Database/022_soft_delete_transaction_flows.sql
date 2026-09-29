alter table cash_bank_transaction
    add column if not exists status varchar(12) not null default 'POSTED',
    add column if not exists cancelled_at timestamptz,
    add column if not exists cancelled_by_name varchar(50),
    add column if not exists cancellation_reason text,
    add column if not exists updated_at timestamptz;

do $$
begin
    if not exists (
        select 1
        from pg_constraint
        where conname = 'cash_bank_transaction_status_check'
          and conrelid = 'cash_bank_transaction'::regclass
    ) then
        alter table cash_bank_transaction
            add constraint cash_bank_transaction_status_check
            check (status in ('POSTED','CANCELLED'));
    end if;
end $$;

create index if not exists ix_cash_bank_transaction_status
    on cash_bank_transaction(status, document_date, document_number);

alter table customer_check_giro
    add column if not exists cancelled_at timestamptz,
    add column if not exists cancelled_by_name varchar(50),
    add column if not exists cancellation_reason text;
