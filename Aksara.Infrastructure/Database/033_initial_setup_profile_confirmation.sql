alter table initial_setup
add column if not exists profile_confirmed boolean not null default false;

update initial_setup
set profile_confirmed=true
where status='ACTIVE' or legacy_protected;

create or replace function sync_company_instance_name()
returns trigger language plpgsql as $$
begin
    update company_instance
    set company_name=new.company_name, version=version+1
    where singleton;

    update initial_setup
    set profile_confirmed=true, version=version+1
    where singleton and status='DRAFT' and not legacy_protected;
    return new;
end $$;

drop trigger if exists trg_program_configuration_company_identity on program_configuration;
create trigger trg_program_configuration_company_identity
after update of company_name on program_configuration
for each row execute function sync_company_instance_name();
