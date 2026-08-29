CREATE TABLE IF NOT EXISTS request_nonces (
  request_id TEXT PRIMARY KEY,
  seen_at INTEGER NOT NULL,
  expires_at INTEGER NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_request_nonces_expires
  ON request_nonces(expires_at);
