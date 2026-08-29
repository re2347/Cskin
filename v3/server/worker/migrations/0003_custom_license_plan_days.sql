-- SQLite cannot alter a CHECK constraint in place. Rebuild the license graph
-- while preserving all rows and foreign-key relationships.
CREATE TABLE licenses_new (
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
  version INTEGER NOT NULL DEFAULT 0
);

INSERT INTO licenses_new (
  license_id, code_hash, code_prefix, code_ciphertext, code_iv, plan_days,
  status, created_at, activated_at, expires_at, bound_device_id, max_devices,
  created_by, note, version
)
SELECT
  license_id, code_hash, code_prefix, code_ciphertext, code_iv, plan_days,
  status, created_at, activated_at, expires_at, bound_device_id, max_devices,
  created_by, note, version
FROM licenses;

CREATE TABLE purchase_orders_new (
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
  FOREIGN KEY (license_id) REFERENCES licenses_new(license_id)
);

INSERT INTO purchase_orders_new (
  order_id, order_token_hash, plan_days, payment_method, status, payment_id,
  license_id, code_ciphertext, code_iv, created_at, paid_at, delivered_at, expires_at
)
SELECT
  order_id, order_token_hash, plan_days, payment_method, status, payment_id,
  license_id, code_ciphertext, code_iv, created_at, paid_at, delivered_at, expires_at
FROM purchase_orders;

CREATE TABLE devices_new (
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
  FOREIGN KEY (license_id) REFERENCES licenses_new(license_id)
);

INSERT INTO devices_new (
  device_id, license_id, public_key_spki, public_key_hash, first_seen_at,
  last_seen_at, status, app_version, os_version, unbound_at
)
SELECT
  device_id, license_id, public_key_spki, public_key_hash, first_seen_at,
  last_seen_at, status, app_version, os_version, unbound_at
FROM devices;

CREATE TABLE leases_new (
  lease_id TEXT PRIMARY KEY,
  license_id TEXT NOT NULL,
  device_id TEXT NOT NULL,
  token_hash TEXT NOT NULL UNIQUE,
  issued_at INTEGER NOT NULL,
  expires_at INTEGER NOT NULL,
  grace_until INTEGER NOT NULL,
  revoked_at INTEGER,
  client_version TEXT,
  FOREIGN KEY (license_id, device_id) REFERENCES devices_new(license_id, device_id)
);

INSERT INTO leases_new (
  lease_id, license_id, device_id, token_hash, issued_at, expires_at,
  grace_until, revoked_at, client_version
)
SELECT
  lease_id, license_id, device_id, token_hash, issued_at, expires_at,
  grace_until, revoked_at, client_version
FROM leases;

DROP TABLE leases;
DROP TABLE devices;
DROP TABLE purchase_orders;
DROP TABLE licenses;

ALTER TABLE licenses_new RENAME TO licenses;
ALTER TABLE purchase_orders_new RENAME TO purchase_orders;
ALTER TABLE devices_new RENAME TO devices;
ALTER TABLE leases_new RENAME TO leases;

CREATE INDEX idx_licenses_status_expires ON licenses(status, expires_at);
CREATE INDEX idx_licenses_prefix ON licenses(code_prefix);
CREATE INDEX idx_purchase_orders_status_expires ON purchase_orders(status, expires_at);
CREATE INDEX idx_devices_license_status ON devices(license_id, status);
CREATE UNIQUE INDEX idx_devices_license_key ON devices(license_id, public_key_hash);
CREATE INDEX idx_devices_last_seen ON devices(last_seen_at);
CREATE INDEX idx_leases_device_active ON leases(device_id, revoked_at, expires_at);
