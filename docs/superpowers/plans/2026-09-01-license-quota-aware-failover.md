# 配额感知双库认证实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 将认证改为低写入心跳、Supabase 优先且仅基础设施故障切换 Cloudflare，并以增量游标同步两套数据库。

**Architecture:** 保留现有 `v1/activate`、`v1/verify`、`v1/heartbeat` 和内部同步端点，在服务端增加只读验证与有限续租分支。D1 保存 Supabase 增量游标，Cloudflare cron 先推送 outbox、再拉取增量、最后再次推送；客户端用错误类型而不是业务错误决定是否切换。

**Tech Stack:** Supabase Edge Function (Deno/TypeScript/PostgREST), Cloudflare Worker + D1 (TypeScript/Wrangler), .NET 8 WinForms, Node `node:test`。

**Spec:** `docs/superpowers/specs/2026-09-01-license-quota-aware-failover-design.md`

## Global Constraints

- Supabase 是正常情况下的权威认证端，Cloudflare 只处理配额、限流、5xx、超时和网络不可达。
- 正常心跳不得插入 lease、更新许可证版本、调用跨库同步或写成功日志。
- 租约剩余不超过 1 小时才续租，且更新原 lease；设备 `last_seen_at` 最多每 6 小时更新一次。
- 增量游标必须按 `(changed_at, license_id)` 排序并在整页成功后推进；删除墓碑优先于旧快照。
- 不读取、输出或提交任何服务密钥；保留工作区中与本任务无关的用户改动。

---

### Task 1: Add executable policy tests

**Files:**
- Create: `supabase/functions/auth/quota-policy.ts`
- Create: `supabase/functions/auth/tests/quota-policy.test.mjs`
- Create: `server/worker/src/sync-policy.ts`
- Create: `server/worker/tests/sync-policy.test.mjs`

**Interfaces:**
- `shouldRenewLease(expiresAt, now, thresholdSeconds): boolean`
- `shouldTouchDevice(lastSeenAt, now, intervalSeconds): boolean`
- `compareCursor(a, b): -1 | 0 | 1`
- `cursorAfter(cursor, changedAt, licenseId): boolean`

- [ ] **Step 1: Write the failing tests**

```js
test("a normal heartbeat does not renew or touch a device", () => {
  assert.equal(shouldRenewLease(10_000, 1_000, 3_600), false);
  assert.equal(shouldTouchDevice(1_000, 1_001, 21_600), false);
});

test("cursor advances deterministically within the same second", () => {
  assert.equal(cursorAfter({ changedAt: 50, licenseId: "B" }, 50, "A"), false);
  assert.equal(cursorAfter({ changedAt: 50, licenseId: "B" }, 50, "C"), true);
});
```
- [ ] **Step 2: Run tests to verify the expected missing-module failures**

Run: `node --test supabase/functions/auth/tests/quota-policy.test.mjs server/worker/tests/sync-policy.test.mjs`

Expected: FAIL because the policy modules do not yet exist.

- [ ] **Step 3: Implement the minimal pure policy functions** with no database or network dependencies.

```ts
export function shouldRenewLease(expiresAt: number, now: number, thresholdSeconds = 3_600): boolean {
  return expiresAt - now <= thresholdSeconds;
}
export function shouldTouchDevice(lastSeenAt: number, now: number, intervalSeconds = 21_600): boolean {
  return now - lastSeenAt >= intervalSeconds;
}
```
- [ ] **Step 4: Run the two policy test files**

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add supabase/functions/auth/quota-policy.ts supabase/functions/auth/tests/quota-policy.test.mjs server/worker/src/sync-policy.ts server/worker/tests/sync-policy.test.mjs
git commit -m "test: define quota-aware auth policies"
```

### Task 2: Reduce Supabase authentication writes

**Files:**
- Modify: `supabase/functions/auth/index.ts:claimRequestId, verify, touchLicense, syncToCloudflare`
- Modify: `supabase/functions/auth/tests/quota-policy.test.mjs`

**Interfaces:**
- `verify` uses `shouldRenewLease` and returns the existing lease response for the normal read-only path.
- `touchLicense` and `syncToCloudflare` are called only by actual state-changing paths.

- [ ] **Step 1: Add a test fixture that calls the exported policy helpers for normal and near-expiry verification.**
- [ ] **Step 2: Run the targeted Supabase tests and record the failing renewal expectations.**
- [ ] **Step 3: Move request nonce claiming behind the state-changing/renewal branch, update the existing lease by `lease_id`, and gate `last_seen_at` updates behind a six-hour interval.**

```ts
if (!shouldRenewLease(lease.expires_at, now)) {
  if (shouldTouchDevice(device.last_seen_at, now)) await updateDeviceLastSeen(...);
  return json({ ...leaseResponse(license, lease.lease_id, body.leaseToken, lease.expires_at, lease.grace_until, now), requestId });
}
const nonce = await claimRequestId(db, body.requestId.trim(), now);
const nextLease = await renewLease(db, secret, lease, body.clientVersion, now);
```
- [ ] **Step 4: Keep failure logs, activation logs, and mutation sync; remove successful read-only heartbeat logging and sync.**
- [ ] **Step 5: Run `node --test supabase/functions/auth/tests/*.test.mjs` and `npx supabase@2.116.0 functions serve auth --no-verify-jwt` type/build validation where available.**
- [ ] **Step 6: Commit**

```powershell
git add supabase/functions/auth/index.ts supabase/functions/auth/tests
git commit -m "fix: make license heartbeats read-only"
```

### Task 3: Add Supabase incremental export and D1 checkpoint

**Files:**
- Create: `supabase/migrations/20260901120000_incremental_license_sync.sql`
- Create: `server/worker/migrations/0007_incremental_sync_state.sql`
- Modify: `supabase/functions/auth/index.ts:syncExport`
- Modify: `server/worker/schema.sql`
- Modify: `server/worker/src/index.ts:reconcileFromSupabase, applySyncPayloadToD1`

**Interfaces:**
- `GET /internal/sync/export?changedAt=<seconds>&licenseId=<id>` returns `{ ok, items, nextCursor }`.
- D1 `sync_state(state_id, cursor_changed_at, cursor_license_id)` stores `supabase_to_d1`.

- [ ] **Step 1: Add migration tests that assert both indexes and `sync_state` schema are present.**

```js
assert.match(await readFile(d1Migration, "utf8"), /CREATE TABLE IF NOT EXISTS sync_state/);
assert.match(await readFile(pgMigration, "utf8"), /idx_license_sync_cursor/);
```
- [ ] **Step 2: Run the migration assertions before adding the tables to verify they fail.**
- [ ] **Step 3: Add composite indexes and the D1 checkpoint table; add `next_attempt_at` to `sync_outbox`.**

```sql
CREATE TABLE IF NOT EXISTS sync_state (
  state_id TEXT PRIMARY KEY,
  cursor_changed_at INTEGER NOT NULL DEFAULT 0,
  cursor_license_id TEXT NOT NULL DEFAULT '',
  updated_at INTEGER NOT NULL DEFAULT 0
);
ALTER TABLE sync_outbox ADD COLUMN next_attempt_at INTEGER NOT NULL DEFAULT 0;
```
- [ ] **Step 4: Implement bounded Supabase export using the cursor predicate, merged ascending results, and `nextCursor`; return an empty page without loading device/lease graphs when nothing changed.**

```ts
const cursor = parseSyncCursor(request.url);
const changed = await readChangedLicenseIds(db, cursor, SYNC_MAX_ITEMS);
const items = await Promise.all(changed.map((row) => syncSnapshot(db, row.licenseId, row.changedAt)));
return json({ ok: true, items, nextCursor: cursorFrom(items.at(-1) ?? cursor) });
```
- [ ] **Step 5: Make Worker reconciliation load the checkpoint, apply a page idempotently, and update the checkpoint only after all items succeed.**

```ts
const cursor = await readSyncCursor(env.DB, "supabase_to_d1");
const page = await fetchSyncPage(env, cursor);
for (const remote of page.items) await reconcileOnePayload(env, remote);
await saveSyncCursor(env.DB, "supabase_to_d1", page.nextCursor);
```
- [ ] **Step 6: Run `npx tsc --noEmit` and the Worker test suite.**
- [ ] **Step 7: Commit**

```powershell
git add supabase/migrations/20260901120000_incremental_license_sync.sql supabase/functions/auth/index.ts server/worker/migrations/0007_incremental_sync_state.sql server/worker/schema.sql server/worker/src/index.ts
git commit -m "feat: reconcile licenses with incremental cursor"
```

### Task 4: Correct Cloudflare scheduling and failover classification

**Files:**
- Modify: `server/worker/src/index.ts:processSyncOutbox, reconcileFromSupabase, scheduled`
- Modify: `server/worker/wrangler.toml`
- Modify: `Services/AuthorizationClient.cs:ShouldTryNextEndpoint`
- Create: `Services/AuthorizationFailoverPolicy.cs`
- Create: `tests/AuthorizationFailoverRegression/AuthorizationFailoverRegression.csproj`
- Create: `tests/AuthorizationFailoverRegression/Program.cs`

**Interfaces:**
- Scheduled order is cleanup -> push outbox -> pull delta -> push outbox.
- `AuthorizationFailoverPolicy.ShouldTryNext(HttpStatusCode statusCode, string? errorCode, bool transportFailure)` is consumed by `AuthorizationClient` and tested from the regression executable.

- [ ] **Step 1: Add a .NET regression test for 429, 503, timeout/network, and business-error classification and run it to fail against the current broad fallback.**

```csharp
True(AuthorizationFailoverPolicy.ShouldTryNext(HttpStatusCode.TooManyRequests, null, false), "429 must fail over");
True(AuthorizationFailoverPolicy.ShouldTryNext(HttpStatusCode.ServiceUnavailable, null, false), "503 must fail over");
False(AuthorizationFailoverPolicy.ShouldTryNext(HttpStatusCode.Conflict, "LICENSE_ALREADY_BOUND", false), "binding conflict must not fail over");
```
- [ ] **Step 2: Narrow `ShouldTryNextEndpoint` to transport, 408, 429, 5xx, and explicit quota codes.**

```csharp
return AuthorizationFailoverPolicy.ShouldTryNext(result.StatusCode, result.ErrorCode, result.StatusCode == 0);
```
- [ ] **Step 3: Add `next_attempt_at` backoff filtering to outbox processing and reorder the scheduled operations.**

```ts
await scheduledCleanup(env);
await processSyncOutbox(env);
await reconcileFromSupabase(env);
await processSyncOutbox(env);
```
- [ ] **Step 4: Change the cron expression to `*/30 * * * *` and keep immediate mutation sync.**
- [ ] **Step 5: Run the .NET regression and `npm test` in `server/worker`.**
- [ ] **Step 6: Commit**

```powershell
git add Services/AuthorizationClient.cs server/worker/src/index.ts server/worker/wrangler.toml tests/AuthorizationFailoverRegression
git commit -m "fix: restrict auth failover to infrastructure failures"
```

### Task 5: Throttle admin health polling and update operational docs

**Files:**
- Modify: `server/admin/app.js:SUPABASE_HEALTH_URL,AUTO_REFRESH_MS,loadDashboard`
- Create: `server/admin/health-policy.js`
- Create: `server/admin/health-policy.test.mjs`
- Modify: `server/worker/README.md`
- Modify: `supabase/README.md`

**Interfaces:**
- Health is fetched on initial load, manual refresh, or after a 60-second freshness window; list auto-refresh remains Cloudflare-only.

- [ ] **Step 1: Add a browser-independent test for the health freshness gate.**

```js
assert.equal(shouldRefreshSupabaseHealth(1_000, 950, 60), false);
assert.equal(shouldRefreshSupabaseHealth(1_000, 939, 60), true);
```
- [ ] **Step 2: Run it to fail against the current 15-second direct health polling.**
- [ ] **Step 3: Implement the 60-second health cache and document the cursor/outbox observability fields.**

```js
if (shouldRefreshSupabaseHealth(Date.now(), state.supabaseHealthCheckedAt, 60_000)) {
  await loadSupabaseHealth();
}
```
- [ ] **Step 4: Run the Worker and Supabase tests plus `git diff --check`.**
- [ ] **Step 5: Commit**

```powershell
git add server/admin/app.js server/worker/README.md supabase/README.md
git commit -m "perf: reduce admin health polling"
```

### Task 6: Deploy and verify both providers

**Files:**
- Modify only generated migration history and deployment output if the CLIs require it.

- [ ] **Step 1: Run all local tests, Worker typecheck, and `dotnet build --no-restore` for the existing desktop project.**
- [ ] **Step 2: Run `npx supabase@2.116.0 db push --dry-run` and inspect the exact migration list.**
- [ ] **Step 3: Apply the Supabase migration with `npx supabase@2.116.0 db push`; deploy only the `auth` function.**
- [ ] **Step 4: Run `npx wrangler deploy --dry-run --config .\wrangler.toml`; apply D1 migration remotely and deploy the Worker.**
- [ ] **Step 5: Verify Supabase `/health`, Worker `/health`, sync cursor, outbox count, and one read-only heartbeat request without printing secrets.**
- [ ] **Step 6: Record deployment versions and test output in the final response.**
