alter table supplier
    add column if not exists owner_name varchar(100),
    add column if not exists address1 varchar(150),
    add column if not exists address2 varchar(150),
    add column if not exists district varchar(80),
    add column if not exists postal_code varchar(20),
    add column if not exists address3 text,
    add column if not exists address4 text,
    add column if not exists mobile1 varchar(30),
    add column if not exists mobile2 varchar(30),
    add column if not exists phone2 varchar(30),
    add column if not exists fax varchar(30),
    add column if not exists contact_phone1 varchar(30),
    add column if not exists contact_phone2 varchar(30),
    add column if not exists contact_yahoo_id varchar(50),
    add column if not exists tax_registered boolean not null default false,
    add column if not exists tax_registration_number varchar(50),
    add column if not exists tax_registered_date date;

update supplier
set address1 = coalesce(address1, nullif(address, '')),
    phone2 = coalesce(phone2, nullif(phone, ''))
where address1 is null
   or phone2 is null;
