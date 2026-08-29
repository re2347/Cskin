ALTER TABLE licenses ADD COLUMN updated_at INTEGER;

UPDATE licenses
SET updated_at = COALESCE(updated_at, created_at, 0)
WHERE updated_at IS NULL;

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
