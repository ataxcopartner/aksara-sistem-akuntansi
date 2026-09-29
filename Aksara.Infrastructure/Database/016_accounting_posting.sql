-- Accounting posting for legacy and newly entered sales/purchases.
-- The source records remain in sale/purchase; this script only derives their
-- double-entry journal using the account mappings already imported from CPS.

insert into ledger_transaction_mapping(mapping_code, mapping_name, account_code)
select 'PURCHASE_TAX', 'PPN masukan pembelian', '100.07.04.00'
where exists (select 1 from ledger_account where code = '100.07.04.00')
on conflict (mapping_code) do update
set mapping_name = excluded.mapping_name,
    account_code = excluded.account_code,
    updated_at = now(),
    version = ledger_transaction_mapping.version + 1;

create or replace function aksara_rebuild_derived_ledger()
returns void
language plpgsql
as $$
declare
    needs_rebuild boolean;
begin
    -- These two CPS categories were entered as cash-bank categories, but their
    -- meaning is not an income-statement expense.
    update cash_bank_category
    set account_code = '100.08.01.00', updated_at = now(), version = version + 1
    where category_type = 'EXPENSE' and code = 'DM'
      and exists (select 1 from ledger_account where code = '100.08.01.00');

    update cash_bank_category
    set account_code = '301.00.00.00', updated_at = now(), version = version + 1
    where category_type = 'EXPENSE' and code = 'TPV'
      and exists (select 1 from ledger_account where code = '301.00.00.00');

    update cash_bank_transaction
    set account_code = '100.08.01.00', version = version + 1
    where account_code = 'CPS.BIA.DM'
      and exists (select 1 from ledger_account where code = '100.08.01.00');

    update cash_bank_transaction
    set account_code = '301.00.00.00', version = version + 1
    where account_code = 'CPS.BIA.TPV'
      and exists (select 1 from ledger_account where code = '301.00.00.00');

    update cash_bank_transaction_line
    set account_code = '100.08.01.00', version = version + 1
    where account_code = 'CPS.BIA.DM'
      and exists (select 1 from ledger_account where code = '100.08.01.00');

    update cash_bank_transaction_line
    set account_code = '301.00.00.00', version = version + 1
    where account_code = 'CPS.BIA.TPV'
      and exists (select 1 from ledger_account where code = '301.00.00.00');

    update journal_entry_line l
    set account_id = a.id
    from ledger_account old_a, ledger_account a
    where old_a.code = 'CPS.BIA.DM'
      and a.code = '100.08.01.00'
      and l.account_id = old_a.id;

    update journal_entry_line l
    set account_id = a.id
    from ledger_account old_a, ledger_account a
    where old_a.code = 'CPS.BIA.TPV'
      and a.code = '301.00.00.00'
      and l.account_id = old_a.id;

    update ledger_account
    set is_active = false, updated_at = now(), version = version + 1
    where code in ('CPS.BIA.DM', 'CPS.BIA.TPV');

    select
        (select count(*) from sale where status = 'POSTED' and grand_total > 0)
            <> (select count(*) from journal_entry where source_module = 'SALES' and status = 'POSTED')
        or (select count(*) from purchase where status = 'POSTED' and grand_total > 0)
            <> (select count(*) from journal_entry where source_module = 'PURCHASE' and status = 'POSTED')
        or exists (
            select 1
            from journal_entry e
            join journal_entry_line l on l.journal_entry_id = e.id
            where e.source_module in ('SALES', 'PURCHASE') and e.status = 'POSTED'
            group by e.id
            having abs(sum(l.debit) - sum(l.credit)) > 0.01
        )
    into needs_rebuild;

    if not needs_rebuild then
        return;
    end if;

    delete from journal_entry where source_module in ('SALES', 'PURCHASE');

    insert into journal_entry(
        id, document_number, document_year, document_date, memo, source_module,
        source_id, status, created_by_name, computer_name, created_at)
    select
        md5('AKSARA:SALE:' || s.id::text)::uuid,
        left('JSALE-' || s.document_number, 30),
        s.document_year,
        s.document_date,
        'Penjualan ' || s.document_number,
        'SALES',
        s.id,
        'POSTED',
        'CPS/AKSARA',
        'ACCOUNTING-POSTING',
        coalesce(s.created_at, now())
    from sale s
    where s.status = 'POSTED' and s.grand_total > 0;

    -- Sales settlement: cash for cash sales; receivable for credit sales.
    -- A credit down payment is recorded as cash plus the remaining receivable.
    insert into journal_entry_line(journal_entry_id, line_number, account_id, description, debit, credit)
    select
        e.id,
        1,
        a.id,
        'Piutang/kas penjualan ' || s.document_number,
        case when s.transaction_type = 'CASH' then s.grand_total
             when s.down_payment > 0 then s.down_payment
             else s.grand_total end,
        0
    from journal_entry e
    join sale s on s.id = e.source_id
    join ledger_account a on a.code = case
        when s.transaction_type = 'CASH'
            then coalesce(s.cash_account_code, (select account_code from ledger_transaction_mapping where mapping_code = 'CASH_DEFAULT'))
        when s.down_payment > 0
            then (select account_code from ledger_transaction_mapping where mapping_code = 'CASH_DEFAULT')
        else (select account_code from ledger_transaction_mapping where mapping_code = 'SALES_RECEIVABLE')
    end
    where e.source_module = 'SALES' and e.status = 'POSTED';

    insert into journal_entry_line(journal_entry_id, line_number, account_id, description, debit, credit)
    select
        e.id, 2, a.id, 'Sisa piutang penjualan ' || s.document_number, s.balance_due, 0
    from journal_entry e
    join sale s on s.id = e.source_id
    join ledger_account a on a.code = (select account_code from ledger_transaction_mapping where mapping_code = 'SALES_RECEIVABLE')
    where e.source_module = 'SALES' and e.status = 'POSTED'
      and s.transaction_type = 'CREDIT' and s.down_payment > 0 and s.balance_due > 0;

    insert into journal_entry_line(journal_entry_id, line_number, account_id, description, debit, credit)
    select
        e.id, 3, a.id, 'Pendapatan penjualan ' || s.document_number, 0,
        greatest(s.subtotal - s.discount_amount, 0)
    from journal_entry e
    join sale s on s.id = e.source_id
    join ledger_account a on a.code = (select account_code from ledger_transaction_mapping where mapping_code = 'SALES_REVENUE')
    where e.source_module = 'SALES' and e.status = 'POSTED'
      and greatest(s.subtotal - s.discount_amount, 0) > 0;

    insert into journal_entry_line(journal_entry_id, line_number, account_id, description, debit, credit)
    select
        e.id, 4, a.id, 'PPN keluaran ' || s.document_number, 0, s.tax_amount
    from journal_entry e
    join sale s on s.id = e.source_id
    join ledger_account a on a.code = (select account_code from ledger_transaction_mapping where mapping_code = 'SALES_TAX')
    where e.source_module = 'SALES' and e.status = 'POSTED' and s.tax_amount > 0;

    insert into journal_entry_line(journal_entry_id, line_number, account_id, description, debit, credit)
    select
        e.id, 5, a.id, 'Harga pokok penjualan ' || s.document_number, s.cost_total, 0
    from journal_entry e
    join sale s on s.id = e.source_id
    join ledger_account a on a.code = (select account_code from ledger_transaction_mapping where mapping_code = 'SALES_COGS')
    where e.source_module = 'SALES' and e.status = 'POSTED' and s.cost_total > 0;

    insert into journal_entry_line(journal_entry_id, line_number, account_id, description, debit, credit)
    select
        e.id, 6, a.id, 'Pengurangan persediaan ' || s.document_number, 0, s.cost_total
    from journal_entry e
    join sale s on s.id = e.source_id
    join ledger_account a on a.code = (select account_code from ledger_transaction_mapping where mapping_code = 'INVENTORY')
    where e.source_module = 'SALES' and e.status = 'POSTED' and s.cost_total > 0;

    insert into journal_entry(
        id, document_number, document_year, document_date, memo, source_module,
        source_id, status, created_by_name, computer_name, created_at)
    select
        md5('AKSARA:PURCHASE:' || p.id::text)::uuid,
        left('JPUR-' || p.document_number, 30),
        p.document_year,
        p.document_date,
        'Pembelian ' || p.document_number,
        'PURCHASE',
        p.id,
        'POSTED',
        'CPS/AKSARA',
        'ACCOUNTING-POSTING',
        coalesce(p.created_at, now())
    from purchase p
    where p.status = 'POSTED' and p.grand_total > 0;

    insert into journal_entry_line(journal_entry_id, line_number, account_id, description, debit, credit)
    select
        e.id, 1, a.id, 'Persediaan pembelian ' || p.document_number,
        greatest(p.subtotal - p.discount_amount, 0), 0
    from journal_entry e
    join purchase p on p.id = e.source_id
    join ledger_account a on a.code = (select account_code from ledger_transaction_mapping where mapping_code = 'INVENTORY')
    where e.source_module = 'PURCHASE' and e.status = 'POSTED'
      and greatest(p.subtotal - p.discount_amount, 0) > 0;

    insert into journal_entry_line(journal_entry_id, line_number, account_id, description, debit, credit)
    select
        e.id, 2, a.id, 'PPN masukan ' || p.document_number, p.tax_amount, 0
    from journal_entry e
    join purchase p on p.id = e.source_id
    join ledger_account a on a.code = (select account_code from ledger_transaction_mapping where mapping_code = 'PURCHASE_TAX')
    where e.source_module = 'PURCHASE' and e.status = 'POSTED' and p.tax_amount > 0;

    insert into journal_entry_line(journal_entry_id, line_number, account_id, description, debit, credit)
    select
        e.id, 3, a.id, 'Kas/hutang pembelian ' || p.document_number, 0,
        case when p.transaction_type = 'CASH' then p.grand_total
             when p.down_payment > 0 then p.down_payment
             else p.grand_total end
    from journal_entry e
    join purchase p on p.id = e.source_id
    join ledger_account a on a.code = case
        when p.transaction_type = 'CASH'
            then (select account_code from ledger_transaction_mapping where mapping_code = 'CASH_DEFAULT')
        when p.down_payment > 0
            then (select account_code from ledger_transaction_mapping where mapping_code = 'CASH_DEFAULT')
        else (select account_code from ledger_transaction_mapping where mapping_code = 'PURCHASE_PAYABLE')
    end
    where e.source_module = 'PURCHASE' and e.status = 'POSTED';

    insert into journal_entry_line(journal_entry_id, line_number, account_id, description, debit, credit)
    select
        e.id, 4, a.id, 'Sisa hutang pembelian ' || p.document_number, 0, p.balance_due
    from journal_entry e
    join purchase p on p.id = e.source_id
    join ledger_account a on a.code = (select account_code from ledger_transaction_mapping where mapping_code = 'PURCHASE_PAYABLE')
    where e.source_module = 'PURCHASE' and e.status = 'POSTED'
      and p.transaction_type = 'CREDIT' and p.down_payment > 0 and p.balance_due > 0;
end;
$$;

select aksara_rebuild_derived_ledger();
