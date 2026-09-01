import test from "node:test";
import assert from "node:assert/strict";
import {
  compareSyncCursor,
  isAfterSyncCursor,
} from "../src/sync-policy.ts";

test("a cursor does not replay an earlier license in the same second", () => {
  const cursor = { changedAt: 50, licenseId: "LICENSE-B" };
  assert.equal(isAfterSyncCursor(cursor, { changedAt: 50, licenseId: "LICENSE-A" }), false);
  assert.equal(isAfterSyncCursor(cursor, { changedAt: 50, licenseId: "LICENSE-C" }), true);
});

test("a later timestamp always advances the cursor", () => {
  const cursor = { changedAt: 50, licenseId: "LICENSE-Z" };
  assert.equal(compareSyncCursor({ changedAt: 51, licenseId: "LICENSE-A" }, cursor), 1);
  assert.equal(compareSyncCursor({ changedAt: 49, licenseId: "LICENSE-Z" }, cursor), -1);
});
