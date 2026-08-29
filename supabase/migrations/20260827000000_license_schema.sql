-- Prepared for the Tokyo Supabase project. This migration is intentionally
-- not applied by this repository change; run it only during a later local or
-- explicitly approved staging test.
create schema if not exists license;

create table if not exists license.licenses (
  license_id text primary key,
  code_hash text not null unique,
  code_prefix text not null,
  code_ciphertext text,
  code_iv text,
  plan_days integer not null check (plan_days between 1 and 36500),
  status text not null default 'unused' check (status in ('unused', 'active', 'expired', 'revoked')),
  created_at bigint not null,
  activated_at bigint,
  expires_at bigint,
  bound_device_id text,
  max_devices integer not null default 1 check (max_devices = 1),
  created_by text not null,
  note text,
  version integer not null default 0
);
create index if not exists idx_license_licenses_status_expires on license.licenses(status, expires_at);
create index if not exists idx_license_licenses_prefix on license.licenses(code_prefix);

create table if not exists license.purchase_orders (
  order_id text primary key,
  order_token_hash text not null unique,
  plan_days integer not null check (plan_days in (1, 7, 30)),
  payment_method text not null check (payment_method in ('alipay', 'wechat')),
  status text not null default 'pending' check (status in ('pending', 'processing', 'paid', 'delivered', 'expired')),
  payment_id text unique,
  license_id text unique references license.licenses(license_id),
  code_ciphertext text,
  code_iv text,
  created_at bigint not null,
  paid_at bigint,
  delivered_at bigint,
  expires_at bigint not null
);
create index if not exists idx_license_orders_status_expires on license.purchase_orders(status, expires_at);

create table if not exists license.devices (
  device_id text not null,
  license_id text not null references license.licenses(license_id),
  public_key_spki text,
  public_key_hash text not null,
  first_seen_at bigint not null,
  last_seen_at bigint not null,
  status text not null default 'active' check (status in ('active', 'unbound', 'revoked')),
  app_version text,
  os_version text,
  unbound_at bigint,
  primary key (license_id, device_id)
);
create index if not exists idx_license_devices_status on license.devices(license_id, status);
create unique index if not exists idx_license_devices_key on license.devices(license_id, public_key_hash);
create index if not exists idx_license_devices_last_seen on license.devices(last_seen_at);

create table if not exists license.leases (
  lease_id text primary key,
  license_id text not null,
  device_id text not null,
  token_hash text not null unique,
  issued_at bigint not null,
  expires_at bigint not null,
  grace_until bigint not null,
  revoked_at bigint,
  client_version text,
  foreign key (license_id, device_id) references license.devices(license_id, device_id)
);
create index if not exists idx_license_leases_device_active on license.leases(device_id, revoked_at, expires_at);

create table if not exists license.admin_users (
  admin_id text primary key,
  access_subject text not null unique,
  email text,
  role text not null default 'operator' check (role in ('owner', 'operator', 'viewer')),
  status text not null default 'active' check (status in ('active', 'disabled')),
  created_at bigint not null,
  last_login_at bigint
);

create table if not exists license.verification_logs (
  log_id text primary key,
  request_id text not null unique,
  license_id text,
  device_id text,
  event text not null,
  success boolean not null,
  ip_hash text,
  user_agent_hash text,
  created_at bigint not null,
  details_json jsonb
);
create index if not exists idx_license_logs_license_time on license.verification_logs(license_id, created_at);
create index if not exists idx_license_logs_event_time on license.verification_logs(event, created_at);

create table if not exists license.request_nonces (
  request_id text primary key,
  seen_at bigint not null,
  expires_at bigint not null
);
create index if not exists idx_license_nonces_expires on license.request_nonces(expires_at);

do $$
declare
  table_name text;
begin
  foreach table_name in array array['licenses', 'purchase_orders', 'devices', 'leases', 'admin_users', 'verification_logs', 'request_nonces'] loop
    execute format('alter table license.%I enable row level security', table_name);
  end loop;
end $$;

-- The Edge Function uses the server-only service_role key. Keep the schema
-- unavailable to anon/authenticated clients while allowing PostgREST access
-- from the function's Supabase client.
grant usage on schema license to service_role;
grant select, insert, update, delete on all tables in schema license to service_role;
alter default privileges in schema license
  grant select, insert, update, delete on tables to service_role;
