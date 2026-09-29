alter table customer add column if not exists owner_name varchar(100);
alter table customer add column if not exists address1 varchar(150);
alter table customer add column if not exists address2 varchar(150);
alter table customer add column if not exists district varchar(80);
alter table customer add column if not exists postal_code varchar(20);
alter table customer add column if not exists address3 text;
alter table customer add column if not exists address4 text;
alter table customer add column if not exists mobile1 varchar(30);
alter table customer add column if not exists mobile2 varchar(30);
alter table customer add column if not exists phone2 varchar(30);
alter table customer add column if not exists fax varchar(30);
alter table customer add column if not exists transaction_condition varchar(20);
alter table customer add column if not exists contact_phone1 varchar(30);
alter table customer add column if not exists contact_phone2 varchar(30);
alter table customer add column if not exists contact_yahoo_id varchar(50);
alter table customer add column if not exists tax_registered boolean not null default false;
alter table customer add column if not exists tax_registration_number varchar(50);
alter table customer add column if not exists tax_registered_date date;
alter table customer add column if not exists tax_invoice_prefix varchar(20);

update customer
set owner_name = coalesce(owner_name, contact_name),
    address1 = coalesce(address1, nullif(address, '')),
    contact_phone1 = coalesce(contact_phone1, phone),
    tax_registration_number = coalesce(tax_registration_number, tax_number)
where owner_name is null
   or address1 is null
   or contact_phone1 is null
   or tax_registration_number is null;

alter table sales_person add column if not exists address varchar(150);
alter table sales_person add column if not exists district varchar(80);
alter table sales_person add column if not exists city varchar(80);
alter table sales_person add column if not exists postal_code varchar(20);
alter table sales_person add column if not exists phone varchar(30);
alter table sales_person add column if not exists commission_percent numeric(9,2) not null default 0;
alter table sales_person add column if not exists collection_target numeric(19,2) not null default 0;
alter table sales_person add column if not exists sales_target numeric(19,2) not null default 0;
alter table sales_person add column if not exists base_salary numeric(19,2) not null default 0;
alter table sales_person add column if not exists target_bonus numeric(19,2) not null default 0;
