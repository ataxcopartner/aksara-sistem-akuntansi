create temporary table if not exists aksara_default_coa_code_migration (
    old_code varchar(20) primary key,
    new_code varchar(20) not null,
    expected_name varchar(150) not null
) on commit drop;

truncate table aksara_default_coa_code_migration;

insert into aksara_default_coa_code_migration(old_code, new_code, expected_name)
values
    ('1', '10000', 'AKTIVA'),
    ('11', '11000', 'AKTIVA LANCAR'),
    ('111', '11100', 'KAS DAN BANK'),
    ('2', '20000', 'KEWAJIBAN'),
    ('3', '30000', 'MODAL'),
    ('4', '40000', 'PENDAPATAN'),
    ('5', '50000', 'HARGA POKOK PENJUALAN'),
    ('6', '60000', 'BEBAN'),
    ('8', '80000', 'POS DILUAR OPERASI'),
    ('82', '82000', 'PENDAPATAN LAIN'),
    ('83', '83000', 'BEBAN LAIN'),
    ('100.02.09.00', '11302', 'Cek Giro Dagang'),
    ('800.00.00.00', '80000', 'POS DILUAR OPERASI'),
    ('800.02.00.00', '82000', 'PENDAPATAN LAIN'),
    ('800.03.00.00', '83000', 'BEBAN LAIN'),
    ('800.02.01.00', '82101', 'Koreksi Piutang'),
    ('800.03.01.00', '83101', 'Koreksi Utang');

-- Only migrate the known AKSARA defaults. A user-defined account that happens
-- to reuse one of these codes is left untouched when its name does not match.
delete from aksara_default_coa_code_migration migration
where not exists (
          select 1
          from ledger_account source
          where source.code = migration.old_code
            and upper(trim(source.name)) = upper(migration.expected_name)
      )
   or not exists (
          select 1
          from ledger_account target
          where target.code = migration.new_code
            and upper(trim(target.name)) = upper(migration.expected_name)
      );

update ledger_transaction_mapping target
set account_code = migration.new_code,
    updated_at = now(),
    version = target.version + 1
from aksara_default_coa_code_migration migration
where target.account_code = migration.old_code;

update cash_account target
set account_code = migration.new_code,
    updated_at = now(),
    version = target.version + 1
from aksara_default_coa_code_migration migration
where target.account_code = migration.old_code;

update bank_account target
set account_code = migration.new_code,
    updated_at = now(),
    version = target.version + 1
from aksara_default_coa_code_migration migration
where target.account_code = migration.old_code;

update cash_bank_category target
set account_code = migration.new_code,
    updated_at = now(),
    version = target.version + 1
from aksara_default_coa_code_migration migration
where target.account_code = migration.old_code;

update cash_bank_transaction target
set account_code = migration.new_code,
    updated_at = now(),
    version = target.version + 1
from aksara_default_coa_code_migration migration
where target.account_code = migration.old_code;

update cash_bank_transaction_line target
set settlement_account_code = migration.new_code,
    updated_at = now(),
    version = target.version + 1
from aksara_default_coa_code_migration migration
where target.settlement_account_code = migration.old_code;

update cash_bank_transaction_line target
set account_code = migration.new_code,
    updated_at = now(),
    version = target.version + 1
from aksara_default_coa_code_migration migration
where target.account_code = migration.old_code;

update journal_entry_line line
set account_id = replacement.id
from aksara_default_coa_code_migration migration
join ledger_account legacy on legacy.code = migration.old_code
join ledger_account replacement on replacement.code = migration.new_code
where line.account_id = legacy.id;

update initial_setup_ledger_draft draft
set account_id = replacement.id
from aksara_default_coa_code_migration migration
join ledger_account legacy on legacy.code = migration.old_code
join ledger_account replacement on replacement.code = migration.new_code
where draft.account_id = legacy.id;

update ledger_account child
set parent_code = migration.new_code,
    updated_at = now(),
    version = child.version + 1
from aksara_default_coa_code_migration migration
where child.parent_code = migration.old_code;

delete from ledger_account account
using aksara_default_coa_code_migration migration
where account.code = migration.old_code;
