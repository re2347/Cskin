ALTER TABLE license.licenses ADD COLUMN IF NOT EXISTS updated_at bigint;

UPDATE license.licenses
SET updated_at = COALESCE(updated_at, created_at, 0)
WHERE updated_at IS NULL;

CREATE TABLE IF NOT EXISTS license.sync_tombstones (
  license_id text primary key,
  changed_at bigint not null,
  version bigint not null default 0
);

CREATE INDEX IF NOT EXISTS idx_license_sync_tombstones_changed_at
  ON license.sync_tombstones(changed_at);

ALTER TABLE license.sync_tombstones ENABLE ROW LEVEL SECURITY;
GRANT SELECT, INSERT, UPDATE, DELETE ON license.sync_tombstones TO service_role;
