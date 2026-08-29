PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS licenses (
  license_id TEXT PRIMARY KEY,
  code_hash TEXT NOT NULL UNIQUE,
  code_prefix TEXT NOT NULL,
  code_ciphertext TEXT,
  code_iv TEXT,
  plan_days INTEGER NOT NULL CHECK (plan_days BETWEEN 1 AND 36500),
  status TEXT NOT NULL DEFAULT 'unused'
    CHECK (status IN ('unused', 'active', 'expired', 'revoked')),
  created_at INTEGER NOT NULL,
  activated_at INTEGER,
  expires_at INTEGER,
  bound_device_id TEXT,
  max_devices INTEGER NOT NULL DEFAULT 1 CHECK (max_devices = 1),
  created_by TEXT NOT NULL,
  note TEXT,
  version INTEGER NOT NULL DEFAULT 0,
  updated_at INTEGER NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_licenses_status_expires
  ON licenses(status, expires_at);
CREATE INDEX IF NOT EXISTS idx_licenses_prefix
  ON licenses(code_prefix);

CREATE TABLE IF NOT EXISTS purchase_orders (
  order_id TEXT PRIMARY KEY,
  order_token_hash TEXT NOT NULL UNIQUE,
  plan_days INTEGER NOT NULL CHECK (plan_days IN (1, 7, 30)),
  payment_method TEXT NOT NULL CHECK (payment_method IN ('alipay', 'wechat')),
  status TEXT NOT NULL DEFAULT 'pending'
    CHECK (status IN ('pending', 'processing', 'paid', 'delivered', 'expired')),
  payment_id TEXT UNIQUE,
  license_id TEXT UNIQUE,
  code_ciphertext TEXT,
  code_iv TEXT,
  created_at INTEGER NOT NULL,
  paid_at INTEGER,
  delivered_at INTEGER,
  expires_at INTEGER NOT NULL,
  FOREIGN KEY (license_id) REFERENCES licenses(license_id)
);

CREATE INDEX IF NOT EXISTS idx_purchase_orders_status_expires
  ON purchase_orders(status, expires_at);

CREATE TABLE IF NOT EXISTS devices (
  device_id TEXT NOT NULL,
  license_id TEXT NOT NULL,
  public_key_spki TEXT,
  public_key_hash TEXT NOT NULL,
  first_seen_at INTEGER NOT NULL,
  last_seen_at INTEGER NOT NULL,
  status TEXT NOT NULL DEFAULT 'active'
    CHECK (status IN ('active', 'unbound', 'revoked')),
  app_version TEXT,
  os_version TEXT,
  unbound_at INTEGER,
  PRIMARY KEY (license_id, device_id),
  FOREIGN KEY (license_id) REFERENCES licenses(license_id)
);

CREATE INDEX IF NOT EXISTS idx_devices_license_status
  ON devices(license_id, status);
CREATE UNIQUE INDEX IF NOT EXISTS idx_devices_license_key
  ON devices(license_id, public_key_hash);
CREATE INDEX IF NOT EXISTS idx_devices_last_seen
  ON devices(last_seen_at);

CREATE TABLE IF NOT EXISTS leases (
  lease_id TEXT PRIMARY KEY,
  license_id TEXT NOT NULL,
  device_id TEXT NOT NULL,
  token_hash TEXT NOT NULL UNIQUE,
  issued_at INTEGER NOT NULL,
  expires_at INTEGER NOT NULL,
  grace_until INTEGER NOT NULL,
  revoked_at INTEGER,
  client_version TEXT,
  FOREIGN KEY (license_id, device_id) REFERENCES devices(license_id, device_id)
);

CREATE INDEX IF NOT EXISTS idx_leases_device_active
  ON leases(device_id, revoked_at, expires_at);

CREATE TABLE IF NOT EXISTS sync_outbox (
  license_id TEXT PRIMARY KEY,
  changed_at INTEGER NOT NULL,
  version INTEGER NOT NULL,
  payload_json TEXT NOT NULL,
  attempts INTEGER NOT NULL DEFAULT 0,
  last_error TEXT,
  updated_at INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_sync_outbox_updated_at ON sync_outbox(updated_at);

CREATE TABLE IF NOT EXISTS sync_tombstones (
  license_id TEXT PRIMARY KEY,
  changed_at INTEGER NOT NULL,
  version INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS idx_sync_tombstones_changed_at ON sync_tombstones(changed_at);

CREATE TABLE IF NOT EXISTS admin_users (
  admin_id TEXT PRIMARY KEY,
  access_subject TEXT NOT NULL UNIQUE,
  email TEXT,
  role TEXT NOT NULL DEFAULT 'operator'
    CHECK (role IN ('owner', 'operator', 'viewer')),
  status TEXT NOT NULL DEFAULT 'active'
    CHECK (status IN ('active', 'disabled')),
  created_at INTEGER NOT NULL,
  last_login_at INTEGER
);

CREATE TABLE IF NOT EXISTS verification_logs (
  log_id TEXT PRIMARY KEY,
  request_id TEXT NOT NULL UNIQUE,
  license_id TEXT,
  device_id TEXT,
  event TEXT NOT NULL,
  success INTEGER NOT NULL CHECK (success IN (0, 1)),
  ip_hash TEXT,
  user_agent_hash TEXT,
  created_at INTEGER NOT NULL,
  details_json TEXT
);

CREATE INDEX IF NOT EXISTS idx_logs_license_time
  ON verification_logs(license_id, created_at);
CREATE INDEX IF NOT EXISTS idx_logs_event_time
  ON verification_logs(event, created_at);

CREATE TABLE IF NOT EXISTS request_nonces (
  request_id TEXT PRIMARY KEY,
  seen_at INTEGER NOT NULL,
  expires_at INTEGER NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_request_nonces_expires
  ON request_nonces(expires_at);
