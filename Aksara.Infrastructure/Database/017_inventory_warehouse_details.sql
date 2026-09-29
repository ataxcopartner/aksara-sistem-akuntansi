alter table warehouse add column if not exists address1 varchar(150);
alter table warehouse add column if not exists address2 varchar(150);
alter table warehouse add column if not exists district varchar(80);
alter table warehouse add column if not exists city varchar(80);
alter table warehouse add column if not exists postal_code varchar(20);
alter table warehouse add column if not exists phone1 varchar(30);
alter table warehouse add column if not exists phone2 varchar(30);
alter table warehouse add column if not exists fax varchar(30);
