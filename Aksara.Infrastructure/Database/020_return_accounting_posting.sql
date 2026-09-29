-- Derived double-entry journal for sales and purchase returns.
-- Rebuilt from posted return documents so imported CPS data affects reports.

delete from journal_entry
where source_module in ('SALES_RETURN', 'PURCHASE_RETURN');

insert into journal_entry(
    id, document_number, document_year, document_date, memo, source_module,
    source_id, status, created_by_name, computer_name, created_at)
select
    md5('AKSARA:SALES_RETURN:' || sr.id::text)::uuid,
    left('JSRET-' || sr.document_number, 30),
    sr.document_year,
    sr.document_date,
    'Retur penjualan ' || sr.document_number,
    'SALES_RETURN',
    sr.id,
    'POSTED',
    coalesce(nullif(sr.created_by_name, ''), 'CPS/AKSARA'),
    coalesce(nullif(sr.computer_name, ''), 'RETURN-POSTING'),
    coalesce(sr.created_at, now())
from sale_return sr
where sr.status = 'POSTED'
  and (sr.grand_total > 0 or sr.cost_total > 0);

insert into journal_entry_line(journal_entry_id, line_number, account_id, description, debit, credit)
select
    e.id, 1, a.id, 'Pembalik pendapatan retur ' || sr.document_number,
    greatest(sr.subtotal - sr.discount_amount, 0), 0
from journal_entry e
join sale_return sr on sr.id = e.source_id
join ledger_account a on a.code = (select account_code from ledger_transaction_mapping where mapping_code = 'SALES_REVENUE')
where e.source_module = 'SALES_RETURN' and e.status = 'POSTED'
  and greatest(sr.subtotal - sr.discount_amount, 0) > 0;

insert into journal_entry_line(journal_entry_id, line_number, account_id, description, debit, credit)
select
    e.id, 2, a.id, 'Pembalik PPN keluaran retur ' || sr.document_number,
    sr.tax_amount, 0
from journal_entry e
join sale_return sr on sr.id = e.source_id
join ledger_account a on a.code = (select account_code from ledger_transaction_mapping where mapping_code = 'SALES_TAX')
where e.source_module = 'SALES_RETURN' and e.status = 'POSTED'
  and sr.tax_amount > 0;

insert into journal_entry_line(journal_entry_id, line_number, account_id, description, debit, credit)
select
    e.id, 3, a.id, 'Pengurang kas/piutang retur ' || sr.document_number,
    0, sr.grand_total
from journal_entry e
join sale_return sr on sr.id = e.source_id
join sale s on s.id = sr.sale_id
left join cash_account ca on ca.code = s.cash_account_code
join ledger_account a on a.code = case
    when sr.transaction_type = 'CASH'
        then coalesce(ca.account_code, (select account_code from ledger_transaction_mapping where mapping_code = 'CASH_DEFAULT'))
    else (select account_code from ledger_transaction_mapping where mapping_code = 'SALES_RECEIVABLE')
end
where e.source_module = 'SALES_RETURN' and e.status = 'POSTED'
  and sr.grand_total > 0;

insert into journal_entry_line(journal_entry_id, line_number, account_id, description, debit, credit)
select
    e.id, 4, a.id, 'Persediaan masuk retur ' || sr.document_number,
    sr.cost_total, 0
from journal_entry e
join sale_return sr on sr.id = e.source_id
join ledger_account a on a.code = (select account_code from ledger_transaction_mapping where mapping_code = 'INVENTORY')
where e.source_module = 'SALES_RETURN' and e.status = 'POSTED'
  and sr.cost_total > 0;

insert into journal_entry_line(journal_entry_id, line_number, account_id, description, debit, credit)
select
    e.id, 5, a.id, 'Pembalik HPP retur ' || sr.document_number,
    0, sr.cost_total
from journal_entry e
join sale_return sr on sr.id = e.source_id
join ledger_account a on a.code = (select account_code from ledger_transaction_mapping where mapping_code = 'SALES_COGS')
where e.source_module = 'SALES_RETURN' and e.status = 'POSTED'
  and sr.cost_total > 0;

insert into journal_entry(
    id, document_number, document_year, document_date, memo, source_module,
    source_id, status, created_by_name, computer_name, created_at)
select
    md5('AKSARA:PURCHASE_RETURN:' || pr.id::text)::uuid,
    left('JPRET-' || pr.document_number, 30),
    pr.document_year,
    pr.document_date,
    'Retur pembelian ' || pr.document_number,
    'PURCHASE_RETURN',
    pr.id,
    'POSTED',
    coalesce(nullif(pr.created_by_name, ''), 'CPS/AKSARA'),
    coalesce(nullif(pr.computer_name, ''), 'RETURN-POSTING'),
    coalesce(pr.created_at, now())
from purchase_return pr
where pr.status = 'POSTED' and pr.grand_total > 0;

insert into journal_entry_line(journal_entry_id, line_number, account_id, description, debit, credit)
select
    e.id, 1, a.id, 'Pengurang kas/hutang retur ' || pr.document_number,
    pr.grand_total, 0
from journal_entry e
join purchase_return pr on pr.id = e.source_id
join ledger_account a on a.code = case
    when pr.transaction_type = 'CASH'
        then (select account_code from ledger_transaction_mapping where mapping_code = 'CASH_DEFAULT')
    else (select account_code from ledger_transaction_mapping where mapping_code = 'PURCHASE_PAYABLE')
end
where e.source_module = 'PURCHASE_RETURN' and e.status = 'POSTED'
  and pr.grand_total > 0;

insert into journal_entry_line(journal_entry_id, line_number, account_id, description, debit, credit)
select
    e.id, 2, a.id, 'Persediaan keluar retur ' || pr.document_number,
    0, greatest(pr.subtotal - pr.discount_amount, 0)
from journal_entry e
join purchase_return pr on pr.id = e.source_id
join ledger_account a on a.code = (select account_code from ledger_transaction_mapping where mapping_code = 'INVENTORY')
where e.source_module = 'PURCHASE_RETURN' and e.status = 'POSTED'
  and greatest(pr.subtotal - pr.discount_amount, 0) > 0;

insert into journal_entry_line(journal_entry_id, line_number, account_id, description, debit, credit)
select
    e.id, 3, a.id, 'Pembalik PPN masukan retur ' || pr.document_number,
    0, pr.tax_amount
from journal_entry e
join purchase_return pr on pr.id = e.source_id
join ledger_account a on a.code = (select account_code from ledger_transaction_mapping where mapping_code = 'PURCHASE_TAX')
where e.source_module = 'PURCHASE_RETURN' and e.status = 'POSTED'
  and pr.tax_amount > 0;
