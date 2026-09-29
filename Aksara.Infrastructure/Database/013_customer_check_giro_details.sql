alter table customer_check_giro
    add column if not exists document_date date,
    add column if not exists origin_area varchar(12) not null default 'LOCAL',
    add column if not exists initial_balance boolean not null default false,
    add column if not exists is_rejected boolean not null default false,
    add column if not exists rejected_date date,
    add column if not exists additional_amount numeric(19,2) not null default 0,
    add column if not exists settlement_date date,
    add column if not exists face_amount numeric(19,2);

update customer_check_giro cg
set document_date = coalesce(cg.document_date, l.document_date),
    origin_area = case l.origin_code when 2 then 'OUT_OF_TOWN' else 'LOCAL' end,
    initial_balance = l.opening_balance_flag,
    is_rejected = l.rejected_date is not null,
    rejected_date = l.rejected_date,
    additional_amount = l.additional_amount,
    settlement_date = l.rejection_release_date,
    face_amount = coalesce(nullif(l.original_amount, 0), nullif(l.amount - l.additional_amount, 0), l.amount)
from legacy_cps_check_giro l
where cg.source = 'CPS_LEGACY'
  and cg.legacy_no = l.no;

update customer_check_giro
set document_date = coalesce(document_date, created_at::date),
    face_amount = coalesce(face_amount, amount - additional_amount, amount);

create index if not exists ix_customer_check_giro_document_date on customer_check_giro(document_date, sequence_number);
