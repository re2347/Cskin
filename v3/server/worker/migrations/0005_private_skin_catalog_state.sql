CREATE TABLE IF NOT EXISTS private_skin_catalog_state (
  state_id INTEGER PRIMARY KEY CHECK (state_id = 1),
  current_revision TEXT NOT NULL,
  overrides_json TEXT NOT NULL DEFAULT '{}',
  names_json TEXT,
  last_checked_at INTEGER NOT NULL DEFAULT 0,
  updated_at INTEGER NOT NULL DEFAULT 0,
  last_error TEXT
);
