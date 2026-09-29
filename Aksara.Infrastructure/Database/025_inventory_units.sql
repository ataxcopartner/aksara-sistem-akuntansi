create table if not exists inventory_unit (
    name varchar(10) primary key,
    is_system boolean not null default false,
    created_at timestamptz not null default now()
);

insert into inventory_unit(name, is_system)
select value, true
from unnest(array[
    'BIJI','BOTOL','BOX','BUAH','CM','DENAH','DM','DOS','DRUM','DUS','DZ','FILE',
    'FOLIO','FOTO','GMBR','GRAM','GROSS','HARI','HELAI','INCH','JAM','KAMAR','KANTG',
    'KG','KILO','KM','KMPTR','KODI','KOLI','KOMP','KRNG','KTK','KURSI','LEMBA','LUSIN',
    'MEJA','MENIT','MESIN','METER','MILI','MM','MOBIL','MOTOR','ONS','ONZ','ORANG','PACK',
    'PCS','PIECE','PLANO','PLY','PORSI','PPS','RIM','ROL','ROLL','RUMAH','SAK','SET','TAS',
    'TITIK','UNIT','ZAK'
]) as value
on conflict (name) do update set is_system = true;
