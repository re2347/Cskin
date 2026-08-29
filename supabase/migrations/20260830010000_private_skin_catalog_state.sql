create table if not exists license.private_skin_catalog_state (
  state_id integer primary key check (state_id = 1),
  current_revision text not null,
  overrides_json jsonb not null default '{}'::jsonb,
  names_json jsonb,
  last_checked_at bigint not null default 0,
  updated_at bigint not null default 0,
  last_error text
);

alter table license.private_skin_catalog_state enable row level security;
grant select, insert, update, delete on license.private_skin_catalog_state to service_role;
