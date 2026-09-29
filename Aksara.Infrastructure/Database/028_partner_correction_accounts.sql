insert into ledger_account(code, name, account_type, normal_balance, parent_code, is_postable, is_cash_bank)
values
    ('80000', 'POS DILUAR OPERASI', 'REVENUE', 'C', null, false, false),
    ('82000', 'PENDAPATAN LAIN', 'REVENUE', 'C', '80000', false, false),
    ('83000', 'BEBAN LAIN', 'EXPENSE', 'D', '80000', false, false),
    ('82101', 'Koreksi Piutang', 'REVENUE', 'C', '82000', true, false),
    ('83101', 'Koreksi Utang', 'EXPENSE', 'D', '83000', true, false)
on conflict (code) do nothing;

insert into ledger_transaction_mapping(mapping_code, mapping_name, account_code)
values
    ('PAYABLE_CORRECTION', 'Akun lawan koreksi utang', '83101'),
    ('RECEIVABLE_CORRECTION', 'Akun lawan koreksi piutang', '82101')
on conflict (mapping_code) do update set
    mapping_name = excluded.mapping_name,
    account_code = excluded.account_code,
    updated_at = now(),
    version = ledger_transaction_mapping.version + 1
where ledger_transaction_mapping.account_code is null;
