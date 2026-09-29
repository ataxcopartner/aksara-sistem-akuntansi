update ledger_transaction_mapping
set account_code = case account_code
        when '800.02.01.00' then '82101'
        when '800.03.01.00' then '83101'
        else account_code
    end,
    updated_at = now(),
    version = version + 1
where account_code in ('800.02.01.00', '800.03.01.00');

update cash_account
set account_code = case account_code
        when '800.02.01.00' then '82101'
        when '800.03.01.00' then '83101'
        else account_code
    end,
    updated_at = now(),
    version = version + 1
where account_code in ('800.02.01.00', '800.03.01.00');

update bank_account
set account_code = case account_code
        when '800.02.01.00' then '82101'
        when '800.03.01.00' then '83101'
        else account_code
    end,
    updated_at = now(),
    version = version + 1
where account_code in ('800.02.01.00', '800.03.01.00');

update cash_bank_category
set account_code = case account_code
        when '800.02.01.00' then '82101'
        when '800.03.01.00' then '83101'
        else account_code
    end,
    updated_at = now(),
    version = version + 1
where account_code in ('800.02.01.00', '800.03.01.00');

update cash_bank_transaction
set account_code = case account_code
        when '800.02.01.00' then '82101'
        when '800.03.01.00' then '83101'
        else account_code
    end,
    updated_at = now(),
    version = version + 1
where account_code in ('800.02.01.00', '800.03.01.00');

update cash_bank_transaction_line
set settlement_account_code = case settlement_account_code
        when '800.02.01.00' then '82101'
        when '800.03.01.00' then '83101'
        else settlement_account_code
    end,
    updated_at = now(),
    version = version + 1
where settlement_account_code in ('800.02.01.00', '800.03.01.00');

update cash_bank_transaction_line
set account_code = case account_code
        when '800.02.01.00' then '82101'
        when '800.03.01.00' then '83101'
        else account_code
    end,
    updated_at = now(),
    version = version + 1
where account_code in ('800.02.01.00', '800.03.01.00');

update journal_entry_line line
set account_id = replacement.id
from ledger_account legacy
join ledger_account replacement on replacement.code = case legacy.code
    when '800.02.01.00' then '82101'
    when '800.03.01.00' then '83101'
end
where line.account_id = legacy.id
  and legacy.code in ('800.02.01.00', '800.03.01.00');

update initial_setup_ledger_draft draft
set account_id = replacement.id
from ledger_account legacy
join ledger_account replacement on replacement.code = case legacy.code
    when '800.02.01.00' then '82101'
    when '800.03.01.00' then '83101'
end
where draft.account_id = legacy.id
  and legacy.code in ('800.02.01.00', '800.03.01.00');

update ledger_account
set parent_code = case parent_code
        when '800.00.00.00' then '80000'
        when '800.02.00.00' then '82000'
        when '800.03.00.00' then '83000'
        else parent_code
    end,
    updated_at = now(),
    version = version + 1
where parent_code in ('800.00.00.00', '800.02.00.00', '800.03.00.00');

delete from ledger_account
where code in (
    '800.02.01.00',
    '800.03.01.00',
    '800.02.00.00',
    '800.03.00.00',
    '800.00.00.00'
);
