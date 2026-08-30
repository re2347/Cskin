import privateSkinIndexData from "./private-skin-index.json";

interface Env {
  DB?: D1Database;
  LICENSE_PEPPER?: string;
  GITCODE_TOKEN?: string;
  GITCODE_OWNER?: string;
  GITCODE_REPOSITORY?: string;
  GITCODE_REF?: string;
  SKIN_REPO_LABEL?: string;
  SUPABASE_SYNC_URL?: string;
  AUTH_SYNC_SECRET?: string;
  ADMIN_API_KEY?: string;
  ADMIN_LOGIN_USERNAME?: string;
  ADMIN_LOGIN_PASSWORD?: string;
  PAYMENT_WEBHOOK_SECRET?: string;
  ENVIRONMENT?: string;
}

interface LicenseRow {
  license_id: string;
  code_hash: string;
  code_prefix: string;
  code_ciphertext: string | null;
  code_iv: string | null;
  plan_days: number;
  status: "unused" | "active" | "expired" | "revoked";
  created_at: number;
  activated_at: number | null;
  expires_at: number | null;
  bound_device_id: string | null;
  created_by: string;
  note: string | null;
  version?: number;
  updated_at?: number | null;
}

interface SyncDeviceRow {
  device_id: string;
  license_id: string;
  public_key_spki: string | null;
  public_key_hash: string;
  first_seen_at: number;
  last_seen_at: number;
  status: string;
  app_version: string | null;
  os_version: string | null;
  unbound_at: number | null;
}

interface SyncLeaseRow {
  lease_id: string;
  license_id: string;
  device_id: string;
  token_hash: string;
  issued_at: number;
  expires_at: number;
  grace_until: number;
  revoked_at: number | null;
  client_version: string | null;
}

interface SyncPayload {
  licenseId: string;
  changedAt: number;
  version: number;
  deleted?: boolean;
  license?: LicenseRow;
  devices?: SyncDeviceRow[];
  leases?: SyncLeaseRow[];
}

interface ActivateBody {
  code?: string;
  deviceId?: string;
  devicePublicKey?: string;
  clientVersion?: string;
  protocolVersion?: string;
  requestId?: string;
  signatureTimestamp?: number;
  signature?: string;
}

interface VerifyBody {
  licenseId?: string;
  deviceId?: string;
  leaseId?: string;
  leaseToken?: string;
  requestId?: string;
  clientVersion?: string;
  signatureTimestamp?: number;
  signature?: string;
}

type PaymentOrderStatus = "pending" | "processing" | "paid" | "delivered" | "expired";

interface PaymentOrderRow {
  order_id: string;
  order_token_hash: string;
  plan_days: number;
  payment_method: "alipay" | "wechat";
  status: PaymentOrderStatus;
  payment_id: string | null;
  license_id: string | null;
  code_ciphertext: string | null;
  code_iv: string | null;
  created_at: number;
  paid_at: number | null;
  delivered_at: number | null;
  expires_at: number;
}

interface CreateOrderBody {
  planDays?: number;
  paymentMethod?: "alipay" | "wechat";
}

interface PaymentWebhookBody {
  orderId?: string;
  paymentId?: string;
  status?: "paid" | "success" | "closed";
  planDays?: number;
}

interface PrivateSkinIndexItem {
  id: number;
  path: string;
  name?: string;
  nameEn?: string;
  blobSha?: string;
}

interface PrivateSkinIndex {
  schema: number;
  repository: string;
  ref: string;
  revision: string;
  generatedAt?: string;
  skins: PrivateSkinIndexItem[];
}

interface PrivateSkinOverride {
  path?: string;
  blobSha?: string;
  deleted?: boolean;
}

interface PrivateSkinCatalogState {
  state_id: number;
  current_revision: string;
  overrides_json: string;
  names_json: string | null;
  last_checked_at: number;
  updated_at: number;
  last_error: string | null;
}

interface SkinNamesPayload {
  zh: Record<string, string>;
  en: Record<string, string>;
}

interface GitCodeCompareFile {
  filename?: string;
  previous_filename?: string;
  status?: string;
  sha?: string;
}

interface GitCodeCompareResponse {
  files?: GitCodeCompareFile[];
  truncated?: boolean;
}

const BUILT_IN_PRIVATE_SKIN_INDEX: PrivateSkinIndex = privateSkinIndexData;

const JSON_HEADERS = {
  "Content-Type": "application/json; charset=utf-8",
  "Cache-Control": "no-store",
};

const MAX_LICENSE_PLAN_DAYS = 36500;
const LEASE_SECONDS = 6 * 60 * 60;
const OFFLINE_GRACE_SECONDS = 24 * 60 * 60;
const MAX_BODY_BYTES = 64 * 1024;
const SIGNATURE_CLOCK_SKEW_SECONDS = 5 * 60;
const REQUEST_NONCE_TTL_SECONDS = 10 * 60;
const DEVICE_KEY_RECOVERY_COOLDOWN_SECONDS = 7 * 24 * 60 * 60;
const PRIVATE_SKIN_REFRESH_SECONDS = 5 * 60;

function nowSeconds(): number {
  return Math.floor(Date.now() / 1000);
}

// Authorization timestamps stay in seconds. Sync timestamps use milliseconds
// so two independent writes in the same second can still be ordered.
function syncNow(): number {
  return Date.now();
}

function isValidLicensePlanDays(value: number): boolean {
  return Number.isSafeInteger(value) && value >= 1 && value <= MAX_LICENSE_PLAN_DAYS;
}

function uuid(): string {
  return crypto.randomUUID();
}

function base64Url(bytes: ArrayBuffer | Uint8Array): string {
  const data = bytes instanceof Uint8Array ? bytes : new Uint8Array(bytes);
  let binary = "";
  for (const byte of data) binary += String.fromCharCode(byte);
  return btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/g, "");
}

function randomToken(byteLength = 32): string {
  const bytes = new Uint8Array(byteLength);
  crypto.getRandomValues(bytes);
  return base64Url(bytes);
}

function base64UrlBytes(value: string): Uint8Array<ArrayBuffer> {
  const normalized = value.replace(/-/g, "+").replace(/_/g, "/") + "=".repeat((4 - (value.length % 4)) % 4);
  const binary = atob(normalized);
  const bytes = new Uint8Array(new ArrayBuffer(binary.length));
  for (let index = 0; index < binary.length; index += 1) bytes[index] = binary.charCodeAt(index);
  return bytes;
}

function base64Bytes(value: string): Uint8Array<ArrayBuffer> {
  const normalized = value.replace(/-/g, "+").replace(/_/g, "/") + "=".repeat((4 - (value.length % 4)) % 4);
  const binary = atob(normalized);
  const bytes = new Uint8Array(new ArrayBuffer(binary.length));
  for (let index = 0; index < binary.length; index += 1) bytes[index] = binary.charCodeAt(index);
  return bytes;
}

function activateSigningPayload(body: ActivateBody): string {
  return [
    "cskin-auth-v2", "POST", "/v1/activate", body.requestId?.trim() || "",
    body.signatureTimestamp ?? "", body.deviceId?.trim() || "", body.devicePublicKey?.trim() || "",
    body.clientVersion || "", body.protocolVersion || "", body.code?.trim() || "",
  ].join("\n");
}

function verifySigningPayload(body: VerifyBody): string {
  return [
    "cskin-auth-v2", "POST", "/v1/verify", body.requestId?.trim() || "",
    body.signatureTimestamp ?? "", body.deviceId?.trim() || "", body.licenseId?.trim() || "",
    body.leaseId?.trim() || "", body.leaseToken?.trim() || "", body.clientVersion || "",
  ].join("\n");
}

async function verifyDeviceSignature(
  publicKeySpki: string | null | undefined,
  signature: string | undefined,
  signatureTimestamp: number | undefined,
  payload: string,
  now: number,
): Promise<boolean> {
  if (!isNonEmptyString(publicKeySpki, 10000) || !isNonEmptyString(signature, 1000)
      || !Number.isSafeInteger(signatureTimestamp)
      || Math.abs(now - (signatureTimestamp as number)) > SIGNATURE_CLOCK_SKEW_SECONDS) return false;
  try {
    const key = await crypto.subtle.importKey(
      "spki",
      base64Bytes(publicKeySpki),
      { name: "ECDSA", namedCurve: "P-256" },
      false,
      ["verify"],
    );
    return await crypto.subtle.verify(
      { name: "ECDSA", hash: "SHA-256" },
      key,
      base64Bytes(signature),
      new TextEncoder().encode(payload),
    );
  } catch {
    return false;
  }
}

function constantTimeEqual(left: string, right: string): boolean {
  const leftBytes = new TextEncoder().encode(left);
  const rightBytes = new TextEncoder().encode(right);
  const length = Math.max(leftBytes.length, rightBytes.length);
  let difference = leftBytes.length ^ rightBytes.length;
  for (let index = 0; index < length; index += 1) {
    difference |= (leftBytes[index] || 0) ^ (rightBytes[index] || 0);
  }
  return difference === 0;
}

async function encryptDeliveryCode(secret: string, code: string): Promise<{ ciphertext: string; iv: string }> {
  const keyDigest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(secret));
  const key = await crypto.subtle.importKey("raw", keyDigest, { name: "AES-GCM" }, false, ["encrypt"]);
  const iv = crypto.getRandomValues(new Uint8Array(12));
  const ciphertext = await crypto.subtle.encrypt({ name: "AES-GCM", iv }, key, new TextEncoder().encode(code));
  return { ciphertext: base64Url(ciphertext), iv: base64Url(iv) };
}

async function decryptDeliveryCode(secret: string, ciphertext: string, iv: string): Promise<string> {
  const keyDigest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(secret));
  const key = await crypto.subtle.importKey("raw", keyDigest, { name: "AES-GCM" }, false, ["decrypt"]);
  const plaintext = await crypto.subtle.decrypt({ name: "AES-GCM", iv: base64UrlBytes(iv) }, key, base64UrlBytes(ciphertext));
  return new TextDecoder().decode(plaintext);
}

function randomCode(): string {
  const alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
  const chars: string[] = [];
  // 28 base32 characters provide 140 bits before formatting separators.
  while (chars.length < 28) {
    const bytes = new Uint8Array(28);
    crypto.getRandomValues(bytes);
    for (const byte of bytes) {
      const limit = 256 - (256 % alphabet.length);
      if (byte >= limit) continue;
      chars.push(alphabet[byte % alphabet.length]);
      if (chars.length === 28) break;
    }
  }
  const groups: string[] = [];
  for (let i = 0; i < chars.length; i += 4) groups.push(chars.slice(i, i + 4).join(""));
  return groups.join("-");
}

function normalizeCode(value: string): string {
  return value.replace(/[^a-z0-9]/gi, "").toUpperCase();
}

function isNonEmptyString(value: unknown, maxLength: number): value is string {
  return typeof value === "string" && value.trim().length > 0 && value.length <= maxLength;
}

async function hmacHex(secret: string, value: string): Promise<string> {
  const key = await crypto.subtle.importKey(
    "raw",
    new TextEncoder().encode(secret),
    { name: "HMAC", hash: "SHA-256" },
    false,
    ["sign"],
  );
  const signature = await crypto.subtle.sign("HMAC", key, new TextEncoder().encode(value));
  return Array.from(new Uint8Array(signature), (byte) => byte.toString(16).padStart(2, "0")).join("");
}

async function sha256Hex(value: string): Promise<string> {
  const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(value));
  return Array.from(new Uint8Array(digest), (byte) => byte.toString(16).padStart(2, "0")).join("");
}

function requestId(request: Request, supplied?: unknown): string {
  if (isNonEmptyString(supplied, 100)) return supplied.trim();
  return request.headers.get("x-request-id")?.slice(0, 100) || uuid();
}

function json(data: unknown, status = 200, headers: Record<string, string> = {}): Response {
  return new Response(JSON.stringify(data), {
    status,
    headers: {
      ...JSON_HEADERS,
      "Access-Control-Allow-Origin": "*",
      "Access-Control-Allow-Headers": "Content-Type, X-Admin-Key, X-Admin-Session, X-Request-Id, X-Cskin-License-Id, X-Cskin-Lease-Id, X-Cskin-Lease-Token, X-Cskin-Device-Id",
      "Access-Control-Allow-Methods": "GET, POST, PATCH, DELETE, OPTIONS",
      ...headers,
    },
  });
}

function corsEmpty(status = 204): Response {
  return new Response(null, {
    status,
    headers: {
      "Access-Control-Allow-Origin": "*",
      "Access-Control-Allow-Headers": "Content-Type, X-Admin-Key, X-Admin-Session, X-Request-Id, X-Cskin-License-Id, X-Cskin-Lease-Id, X-Cskin-Lease-Token, X-Cskin-Device-Id",
      "Access-Control-Allow-Methods": "GET, POST, PATCH, DELETE, OPTIONS",
      "Access-Control-Max-Age": "600",
    },
  });
}

function failure(code: string, message: string, requestIdValue: string, status: number): Response {
  return json({ error: { code, message }, requestId: requestIdValue }, status);
}

async function readJson<T>(request: Request): Promise<T> {
  const contentLength = Number(request.headers.get("content-length") || 0);
  if (contentLength > MAX_BODY_BYTES) throw new Error("REQUEST_TOO_LARGE");
  const text = await request.text();
  if (new TextEncoder().encode(text).byteLength > MAX_BODY_BYTES) throw new Error("REQUEST_TOO_LARGE");
  return JSON.parse(text) as T;
}

async function writeLog(
  env: Env,
  request: Request,
  event: string,
  success: boolean,
  requestIdValue: string,
  licenseId?: string,
  deviceId?: string,
  details?: Record<string, unknown>,
): Promise<void> {
  if (!env.DB) return;
  try {
    const pepper = env.LICENSE_PEPPER;
    const ip = request.headers.get("cf-connecting-ip") || "";
    const userAgent = request.headers.get("user-agent") || "";
    const [ipHash, userAgentHash] = pepper
      ? await Promise.all([hmacHex(pepper, `ip:${ip}`), hmacHex(pepper, `ua:${userAgent}`)])
      : [null, null];
    await env.DB.prepare(
      `INSERT OR IGNORE INTO verification_logs
       (log_id, request_id, license_id, device_id, event, success, ip_hash, user_agent_hash, created_at, details_json)
       VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
    )
      .bind(uuid(), requestIdValue, licenseId || null, deviceId || null, event, success ? 1 : 0, ipHash, userAgentHash, nowSeconds(), details ? JSON.stringify(details) : null)
      .run();
  } catch {
    // Logging must not make an otherwise valid activation fail.
  }
}

async function claimRequestId(db: D1Database, requestIdValue: string, now: number): Promise<boolean> {
  try {
    const result = await db.prepare(
      "INSERT OR IGNORE INTO request_nonces (request_id, seen_at, expires_at) VALUES (?, ?, ?)",
    ).bind(requestIdValue, now, now + REQUEST_NONCE_TTL_SECONDS).run();
    return Boolean(result.meta.changes);
  } catch {
    // A missing migration must fail closed; otherwise replay protection would
    // silently disappear in production.
    return false;
  }
}

function requireDb(env: Env, requestIdValue: string): D1Database | Response {
  return env.DB || failure("DB_NOT_CONFIGURED", "授权数据库尚未绑定到 Worker", requestIdValue, 503);
}

function requirePepper(env: Env, requestIdValue: string): string | Response {
  return env.LICENSE_PEPPER || failure("SERVER_NOT_CONFIGURED", "授权服务尚未完成密钥配置", requestIdValue, 503);
}

function privateRepositoryConfig(env: Env, requestIdValue: string): {
  token: string;
  owner: string;
  repository: string;
  ref: string;
  label: string;
} | Response {
  const token = env.GITCODE_TOKEN?.trim() || "";
  const owner = env.GITCODE_OWNER?.trim() || "";
  const repository = env.GITCODE_REPOSITORY?.trim() || "";
  const ref = env.GITCODE_REF?.trim() || "";
  const label = env.SKIN_REPO_LABEL?.trim() || "";
  const identifier = /^[A-Za-z0-9._-]{1,100}$/;
  if (!token || !identifier.test(owner) || !identifier.test(repository) || !identifier.test(ref)
      || !identifier.test(label)) {
    return failure("SKIN_REPOSITORY_NOT_CONFIGURED", "私有皮肤资源服务尚未完成配置", requestIdValue, 503);
  }
  return { token, owner, repository, ref, label };
}

function isSafeRepositoryPath(value: string, requiredExtension: ".json" | ".fantome"): boolean {
  if (!value || value.length > 500 || value.startsWith("/") || value.includes("\\") || value.includes(":")) return false;
  const parts = value.split("/");
  return parts.length >= 2 && parts.every((part) => part.length > 0 && part !== "." && part !== "..")
    && value.toLowerCase().endsWith(requiredExtension);
}

function isSafeSkinPath(value: string): boolean {
  if (!isSafeRepositoryPath(value, ".fantome") || !value.startsWith("skins/")) return false;
  const parts = value.split("/");
  if (parts.length < 4 || parts.length > 5) return false;
  const fileId = parts.at(-1)?.replace(/\.fantome$/i, "") || "";
  return /^\d+$/.test(fileId) && parts.slice(1, -1).every((part) => /^\d+$/.test(part));
}

function gitCodeRawUrl(config: { owner: string; repository: string; ref: string }, path: string): string {
  const encodedPath = path.split("/").map(encodeURIComponent).join("/");
  return `https://api.gitcode.com/api/v5/repos/${encodeURIComponent(config.owner)}/${encodeURIComponent(config.repository)}/raw/${encodedPath}?ref=${encodeURIComponent(config.ref)}`;
}

async function fetchPrivateRepositoryFile(
  config: { token: string; owner: string; repository: string; ref: string },
  path: string,
): Promise<Response> {
  return fetch(gitCodeRawUrl(config, path), {
    headers: {
      "PRIVATE-TOKEN": config.token,
      "Authorization": `Bearer ${config.token}`,
      "User-Agent": "Cskin-Private-Skin-Worker/3.0",
      "Accept": "application/octet-stream, application/json",
    },
  });
}

function gitCodeApiHeaders(token: string, accept = "application/json"): HeadersInit {
  return {
    "PRIVATE-TOKEN": token,
    "Authorization": `Bearer ${token}`,
    "User-Agent": "Cskin-Private-Skin-Worker/3.0",
    "Accept": accept,
  };
}

async function readBoundedJson<T>(response: Response, maxBytes: number): Promise<T> {
  const declaredLength = Number(response.headers.get("content-length") || 0);
  if (declaredLength > maxBytes) throw new Error("GITCODE_RESPONSE_TOO_LARGE");
  if (!response.body) throw new Error("GITCODE_RESPONSE_EMPTY");
  const reader = response.body.getReader();
  const chunks: Uint8Array[] = [];
  let total = 0;
  try {
    while (true) {
      const { done, value } = await reader.read();
      if (done) break;
      if (!value) continue;
      total += value.byteLength;
      if (total > maxBytes) {
        await reader.cancel("response too large");
        throw new Error("GITCODE_RESPONSE_TOO_LARGE");
      }
      chunks.push(value);
    }
  } finally {
    reader.releaseLock();
  }
  const payload = new Uint8Array(total);
  let offset = 0;
  for (const chunk of chunks) {
    payload.set(chunk, offset);
    offset += chunk.byteLength;
  }
  return JSON.parse(new TextDecoder().decode(payload)) as T;
}

async function fetchGitCodeJson<T>(url: string, token: string): Promise<T> {
  const response = await fetch(url, { headers: gitCodeApiHeaders(token) });
  if (!response.ok) throw new Error(`GITCODE_HTTP_${response.status}`);
  return await readBoundedJson<T>(response, 8 * 1024 * 1024);
}

function gitCodeBranchUrl(config: { owner: string; repository: string; ref: string }): string {
  return `https://api.gitcode.com/api/v5/repos/${encodeURIComponent(config.owner)}/${encodeURIComponent(config.repository)}/branches/${encodeURIComponent(config.ref)}`;
}

function gitCodeCompareUrl(config: { owner: string; repository: string }, from: string, to: string): string {
  return `https://api.gitcode.com/api/v5/repos/${encodeURIComponent(config.owner)}/${encodeURIComponent(config.repository)}/compare/${encodeURIComponent(from)}...${encodeURIComponent(to)}`;
}

function parseRecord<T>(value: string | null | undefined): Record<string, T> {
  if (!value) return {};
  try {
    const parsed = JSON.parse(value);
    return parsed && typeof parsed === "object" && !Array.isArray(parsed) ? parsed as Record<string, T> : {};
  } catch {
    return {};
  }
}

function stringRecord(value: unknown): Record<string, string> {
  if (!value || typeof value !== "object" || Array.isArray(value)) return {};
  return Object.fromEntries(Object.entries(value).filter(([, item]) => typeof item === "string" && item.trim())) as Record<string, string>;
}

function parseSkinNames(value: string | null | undefined): SkinNamesPayload {
  const parsed = parseRecord<unknown>(value);
  const zh = stringRecord(parsed.zh);
  const en = stringRecord(parsed.en);
  if (Object.keys(zh).length === 0 && Object.keys(en).length === 0) return { zh: stringRecord(parsed), en: {} };
  return { zh, en };
}

function skinIdFromPath(path: string): number | null {
  if (!isSafeSkinPath(path)) return null;
  const value = path.split("/").at(-1)?.replace(/\.fantome$/i, "") || "";
  const skinId = Number(value);
  return Number.isSafeInteger(skinId) && skinId > 0 ? skinId : null;
}

async function ensurePrivateSkinCatalogState(db: D1Database): Promise<PrivateSkinCatalogState> {
  const existing = await db.prepare("SELECT * FROM private_skin_catalog_state WHERE state_id = 1").first<PrivateSkinCatalogState>();
  if (existing) return existing;
  const now = nowSeconds();
  await db.prepare(
    "INSERT OR IGNORE INTO private_skin_catalog_state (state_id, current_revision, overrides_json, names_json, last_checked_at, updated_at, last_error) VALUES (1, ?, '{}', NULL, 0, ?, NULL)",
  ).bind(BUILT_IN_PRIVATE_SKIN_INDEX.revision, now).run();
  return (await db.prepare("SELECT * FROM private_skin_catalog_state WHERE state_id = 1").first<PrivateSkinCatalogState>()) || {
    state_id: 1,
    current_revision: BUILT_IN_PRIVATE_SKIN_INDEX.revision,
    overrides_json: "{}",
    names_json: null,
    last_checked_at: 0,
    updated_at: now,
    last_error: null,
  };
}

function buildPrivateSkinIndex(state?: PrivateSkinCatalogState | null): PrivateSkinIndex {
  const skins = new Map<number, PrivateSkinIndexItem>(
    BUILT_IN_PRIVATE_SKIN_INDEX.skins.map((skin) => [skin.id, { ...skin }]),
  );
  const overrides = parseRecord<PrivateSkinOverride>(state?.overrides_json);
  const names = parseSkinNames(state?.names_json);
  for (const [idValue, override] of Object.entries(overrides)) {
    const skinId = Number(idValue);
    if (!Number.isSafeInteger(skinId) || skinId <= 0) continue;
    if (override.deleted) {
      skins.delete(skinId);
      continue;
    }
    const current = skins.get(skinId);
    if (!override.path || !isSafeSkinPath(override.path)) continue;
    skins.set(skinId, {
      id: skinId,
      path: override.path,
      name: names.zh[idValue] || current?.name || "",
      nameEn: names.en[idValue] || current?.nameEn,
      blobSha: override.blobSha || current?.blobSha,
    });
  }
  for (const [idValue, name] of Object.entries(names.zh)) {
    const skin = skins.get(Number(idValue));
    if (skin && name.trim()) skin.name = name.trim();
  }
  for (const [idValue, name] of Object.entries(names.en)) {
    const skin = skins.get(Number(idValue));
    if (skin && name.trim()) skin.nameEn = name.trim();
  }
  return {
    ...BUILT_IN_PRIVATE_SKIN_INDEX,
    revision: state?.current_revision || BUILT_IN_PRIVATE_SKIN_INDEX.revision,
    generatedAt: state?.updated_at ? new Date(state.updated_at * 1000).toISOString() : BUILT_IN_PRIVATE_SKIN_INDEX.generatedAt,
    skins: [...skins.values()].sort((left, right) => left.id - right.id || left.path.localeCompare(right.path)),
  };
}

async function recordCatalogRefreshFailure(db: D1Database, state: PrivateSkinCatalogState, error: string): Promise<void> {
  await db.prepare("UPDATE private_skin_catalog_state SET last_checked_at = ?, last_error = ? WHERE state_id = 1")
    .bind(nowSeconds(), error.slice(0, 500)).run();
  console.error(JSON.stringify({ event: "private_skin_catalog_refresh_failed", revision: state.current_revision, error }));
}

async function refreshPrivateSkinCatalog(env: Env, force = false): Promise<PrivateSkinCatalogState | null> {
  if (!env.DB) return null;
  const config = privateRepositoryConfig(env, uuid());
  if (config instanceof Response) return null;
  let state: PrivateSkinCatalogState;
  try {
    state = await ensurePrivateSkinCatalogState(env.DB);
  } catch (error) {
    console.error(JSON.stringify({ event: "private_skin_catalog_state_unavailable", error: error instanceof Error ? error.message : "unknown" }));
    return null;
  }
  const now = nowSeconds();
  if (!force && now - Number(state.last_checked_at || 0) < PRIVATE_SKIN_REFRESH_SECONDS) return state;
  try {
    const branch = await fetchGitCodeJson<{ commit?: { id?: string } }>(gitCodeBranchUrl(config), config.token);
    const nextRevision = branch.commit?.id?.trim() || "";
    if (!/^[0-9a-f]{40}$/i.test(nextRevision)) throw new Error("GITCODE_REVISION_INVALID");
    if (nextRevision === state.current_revision) {
      await env.DB.prepare("UPDATE private_skin_catalog_state SET last_checked_at = ?, last_error = NULL WHERE state_id = 1")
        .bind(now).run();
      return { ...state, last_checked_at: now, last_error: null };
    }

    const compared = await fetchGitCodeJson<GitCodeCompareResponse>(
      gitCodeCompareUrl(config, state.current_revision, nextRevision),
      config.token,
    );
    if (compared.truncated) throw new Error("GITCODE_COMPARE_TRUNCATED");
    const overrides = parseRecord<PrivateSkinOverride>(state.overrides_json);
    for (const file of compared.files || []) {
      const currentPath = file.filename?.replace(/\\/g, "/") || "";
      const previousPath = file.previous_filename?.replace(/\\/g, "/") || "";
      if (previousPath && previousPath !== currentPath) {
        const previousId = skinIdFromPath(previousPath);
        if (previousId) overrides[String(previousId)] = { deleted: true };
      }
      const skinId = skinIdFromPath(currentPath);
      if (!skinId) continue;
      if (file.status === "removed") overrides[String(skinId)] = { deleted: true };
      else overrides[String(skinId)] = { path: currentPath, blobSha: file.sha || undefined };
    }

    const previousNames = parseSkinNames(state.names_json);
    let namesJson = JSON.stringify(previousNames);
    const [zhResponse, enResponse] = await Promise.all([
      fetchPrivateRepositoryFile(config, "resources/zh/skin_ids.json"),
      fetchPrivateRepositoryFile(config, "resources/en/skin_ids.json"),
    ]);
    const names = { zh: previousNames.zh, en: previousNames.en };
    if (zhResponse.ok) names.zh = await readBoundedJson<Record<string, string>>(zhResponse, 4 * 1024 * 1024);
    else console.warn(JSON.stringify({ event: "private_skin_names_zh_unavailable", status: zhResponse.status }));
    if (enResponse.ok) names.en = await readBoundedJson<Record<string, string>>(enResponse, 4 * 1024 * 1024);
    else console.warn(JSON.stringify({ event: "private_skin_names_en_unavailable", status: enResponse.status }));
    namesJson = JSON.stringify(names);
    const overridesJson = JSON.stringify(overrides);
    await env.DB.prepare(
      "UPDATE private_skin_catalog_state SET current_revision = ?, overrides_json = ?, names_json = ?, last_checked_at = ?, updated_at = ?, last_error = NULL WHERE state_id = 1",
    ).bind(nextRevision, overridesJson, namesJson, now, now).run();
    console.log(JSON.stringify({ event: "private_skin_catalog_refreshed", from: state.current_revision, to: nextRevision, changedFiles: (compared.files || []).length }));
    return { ...state, current_revision: nextRevision, overrides_json: overridesJson, names_json: namesJson, last_checked_at: now, updated_at: now, last_error: null };
  } catch (error) {
    await recordCatalogRefreshFailure(env.DB, state, error instanceof Error ? error.message : "unknown");
    return state;
  }
}

async function authorizeSkinRequest(request: Request, env: Env, requestIdValue: string): Promise<Response | null> {
  const dbOrError = requireDb(env, requestIdValue);
  if (dbOrError instanceof Response) return dbOrError;
  const pepperOrError = requirePepper(env, requestIdValue);
  if (pepperOrError instanceof Response) return pepperOrError;
  const licenseId = request.headers.get("x-cskin-license-id")?.trim() || "";
  const leaseId = request.headers.get("x-cskin-lease-id")?.trim() || "";
  const leaseToken = request.headers.get("x-cskin-lease-token")?.trim() || "";
  const deviceId = request.headers.get("x-cskin-device-id")?.trim() || "";
  if (![licenseId, leaseId, leaseToken, deviceId].every((value) => value.length > 0 && value.length <= 500)) {
    return failure("SKIN_AUTH_REQUIRED", "皮肤资源请求需要有效授权", requestIdValue, 401);
  }

  const tokenHash = await hmacHex(pepperOrError, `lease:${leaseToken}`);
  const row = await dbOrError.prepare(
    `SELECT l.status AS license_status, l.expires_at AS license_expires_at, l.bound_device_id,
            le.device_id AS lease_device_id, le.expires_at AS lease_expires_at, le.revoked_at,
            d.status AS device_status
       FROM leases le
       JOIN licenses l ON l.license_id = le.license_id
       JOIN devices d ON d.device_id = le.device_id AND d.license_id = l.license_id
      WHERE le.lease_id = ? AND le.token_hash = ? AND l.license_id = ?`,
  ).bind(leaseId, tokenHash, licenseId).first<{
    license_status: LicenseRow["status"];
    license_expires_at: number | null;
    bound_device_id: string | null;
    lease_device_id: string;
    lease_expires_at: number;
    revoked_at: number | null;
    device_status: string;
  }>();
  const now = nowSeconds();
  const allowed = row
    && row.license_status === "active"
    && row.license_expires_at !== null
    && row.license_expires_at > now
    && row.lease_expires_at > now
    && row.revoked_at === null
    && row.device_status === "active"
    && row.bound_device_id === deviceId
    && row.lease_device_id === deviceId;
  if (!allowed) {
    await writeLog(env, request, "skin_resource_denied", false, requestIdValue, licenseId, deviceId);
    return failure("SKIN_AUTH_INVALID", "皮肤资源授权已失效，请重新验证密钥", requestIdValue, 403);
  }
  return null;
}

async function loadPrivateSkinIndex(
  env: Env,
  requestIdValue: string,
  refresh = false,
): Promise<{ index: PrivateSkinIndex; config: Exclude<ReturnType<typeof privateRepositoryConfig>, Response> } | Response> {
  const config = privateRepositoryConfig(env, requestIdValue);
  if (config instanceof Response) return config;
  const state = refresh ? await refreshPrivateSkinCatalog(env) : env.DB ? await ensurePrivateSkinCatalogState(env.DB) : null;
  const index = buildPrivateSkinIndex(state);
  if (index.schema !== 1 || index.repository !== config.label || index.ref !== config.ref
      || !Array.isArray(index.skins) || index.skins.length === 0
      || index.skins.some((skin) => !Number.isSafeInteger(skin.id) || skin.id <= 0 || !isSafeSkinPath(skin.path))) {
    return failure("SKIN_INDEX_INVALID", "私有皮肤索引格式无效", requestIdValue, 502);
  }
  return { index, config };
}

async function privateSkinIndex(request: Request, env: Env, requestIdValue: string): Promise<Response> {
  const authorizationError = await authorizeSkinRequest(request, env, requestIdValue);
  if (authorizationError) return authorizationError;
  const loaded = await loadPrivateSkinIndex(env, requestIdValue, true);
  if (loaded instanceof Response) return loaded;
  return json(loaded.index, 200, {
    "Cache-Control": "private, max-age=300",
    "X-Cskin-Revision": loaded.index.revision,
    "X-Cskin-Gateway": "cloudflare",
    "X-Cskin-Upstream": "gitcode",
    "X-Content-Type-Options": "nosniff",
  });
}

async function privateSkinFile(request: Request, env: Env, requestIdValue: string): Promise<Response> {
  const authorizationError = await authorizeSkinRequest(request, env, requestIdValue);
  if (authorizationError) return authorizationError;
  const url = new URL(request.url);
  const skinId = Number(url.searchParams.get("skinId") || 0);
  const legacyPath = url.searchParams.get("path")?.trim() || "";
  const loaded = await loadPrivateSkinIndex(env, requestIdValue);
  if (loaded instanceof Response) return loaded;
  const item = Number.isSafeInteger(skinId) && skinId > 0
    ? loaded.index.skins.find((skin) => skin.id === skinId)
    : loaded.index.skins.find((skin) => skin.path === legacyPath);
  if (!item) {
    return failure("SKIN_NOT_FOUND", "私有皮肤索引中没有该资源", requestIdValue, 404);
  }
  const path = item.path;

  const upstream = await fetchPrivateRepositoryFile(loaded.config, path);
  if (!upstream.ok || !upstream.body) {
    console.error(JSON.stringify({ event: "private_skin_file_upstream_failed", status: upstream.status, requestId: requestIdValue, path }));
    return failure(upstream.status === 404 ? "SKIN_NOT_FOUND" : "SKIN_FILE_UNAVAILABLE", "私有皮肤资源暂时不可用", requestIdValue, upstream.status === 404 ? 404 : 502);
  }
  const fileName = path.split("/").at(-1) || "skin.fantome";
  const headers = new Headers({
    "Content-Type": "application/octet-stream",
    "Content-Disposition": `attachment; filename="${fileName}"`,
    "Cache-Control": "private, no-store",
    "X-Cskin-Revision": loaded.index.revision,
    "X-Cskin-Gateway": "cloudflare",
    "X-Cskin-Upstream": "gitcode",
    "X-Content-Type-Options": "nosniff",
  });
  const contentLength = upstream.headers.get("content-length");
  const etag = upstream.headers.get("etag");
  if (contentLength) headers.set("Content-Length", contentLength);
  if (etag) headers.set("ETag", etag);
  if (item.blobSha) headers.set("X-Cskin-Blob-Sha", item.blobSha);
  return new Response(upstream.body, { status: 200, headers });
}

function syncEndpoint(env: Env, path: string): string | null {
  if (!env.SUPABASE_SYNC_URL || !env.AUTH_SYNC_SECRET) return null;
  return `${env.SUPABASE_SYNC_URL.replace(/\/$/, "")}/${path.replace(/^\//, "")}`;
}

async function readSyncPayload(db: D1Database, licenseId: string, changedAt = 0): Promise<SyncPayload | null> {
  const license = await db.prepare("SELECT * FROM licenses WHERE license_id = ?").bind(licenseId).first<LicenseRow>();
  if (!license) {
    const tombstone = await db.prepare("SELECT changed_at, version FROM sync_tombstones WHERE license_id = ?").bind(licenseId).first<{ changed_at: number; version: number }>();
    return { licenseId, changedAt: Number(tombstone?.changed_at || changedAt), version: Number(tombstone?.version || 0), deleted: true };
  }
  const devices = await db.prepare("SELECT device_id, license_id, public_key_spki, public_key_hash, first_seen_at, last_seen_at, status, app_version, os_version, unbound_at FROM devices WHERE license_id = ?").bind(licenseId).all<SyncDeviceRow>();
  const leases = await db.prepare("SELECT lease_id, license_id, device_id, token_hash, issued_at, expires_at, grace_until, revoked_at, client_version FROM leases WHERE license_id = ?").bind(licenseId).all<SyncLeaseRow>();
  return {
    licenseId,
    changedAt: Math.max(changedAt, Number(license.updated_at || license.created_at || 0)),
    version: Number(license.version || 0),
    license,
    devices: devices.results,
    leases: leases.results,
  };
}

async function enqueueLicenseSync(env: Env, licenseId: string, changedAt = syncNow()): Promise<void> {
  if (!env.DB || !syncEndpoint(env, "internal/sync/apply")) return;
  try {
    const payload = await readSyncPayload(env.DB, licenseId, changedAt);
    if (!payload) return;
    await env.DB.prepare(
      `INSERT INTO sync_outbox (license_id, changed_at, version, payload_json, attempts, last_error, updated_at)
       VALUES (?, ?, ?, ?, 0, NULL, ?)
       ON CONFLICT(license_id) DO UPDATE SET
         changed_at = excluded.changed_at,
         version = excluded.version,
         payload_json = excluded.payload_json,
         attempts = 0,
         last_error = NULL,
         updated_at = excluded.updated_at`,
    ).bind(licenseId, payload.changedAt, payload.version, JSON.stringify(payload), nowSeconds()).run();
  } catch (error) {
    console.error("license sync enqueue failed", licenseId, error instanceof Error ? error.message : "unknown");
  }
}

async function enqueueDeletedLicenseSync(env: Env, licenseId: string, changedAt = syncNow(), version = 0): Promise<void> {
  if (!env.DB || !syncEndpoint(env, "internal/sync/apply")) return;
  try {
    await env.DB.prepare(
      `INSERT INTO sync_tombstones (license_id, changed_at, version) VALUES (?, ?, ?)
       ON CONFLICT(license_id) DO UPDATE SET changed_at = excluded.changed_at, version = excluded.version`,
    ).bind(licenseId, changedAt, version).run();
    const payload: SyncPayload = { licenseId, changedAt, version, deleted: true };
    await env.DB.prepare(
      `INSERT INTO sync_outbox (license_id, changed_at, version, payload_json, attempts, last_error, updated_at)
       VALUES (?, ?, ?, ?, 0, NULL, ?)
       ON CONFLICT(license_id) DO UPDATE SET
         changed_at = excluded.changed_at,
         version = excluded.version,
         payload_json = excluded.payload_json,
         attempts = 0,
         last_error = NULL,
         updated_at = excluded.updated_at`,
    ).bind(licenseId, changedAt, version, JSON.stringify(payload), nowSeconds()).run();
  } catch (error) {
    console.error("deleted license sync enqueue failed", licenseId, error instanceof Error ? error.message : "unknown");
  }
}

async function applySyncPayloadToD1(db: D1Database, payload: SyncPayload): Promise<void> {
  if (payload.deleted || !payload.license) {
    await db.batch([
      db.prepare("DELETE FROM leases WHERE license_id = ?").bind(payload.licenseId),
      db.prepare("DELETE FROM devices WHERE license_id = ?").bind(payload.licenseId),
      db.prepare("DELETE FROM licenses WHERE license_id = ?").bind(payload.licenseId),
      db.prepare(
        `INSERT INTO sync_tombstones (license_id, changed_at, version) VALUES (?, ?, ?)
         ON CONFLICT(license_id) DO UPDATE SET changed_at = excluded.changed_at, version = excluded.version`,
      ).bind(payload.licenseId, payload.changedAt, payload.version),
    ]);
    return;
  }
  const license = payload.license;
  const updatedAt = Number(license.updated_at || payload.changedAt || license.created_at || 0);
  await db.batch([
    db.prepare(
      `INSERT INTO licenses (license_id, code_hash, code_prefix, code_ciphertext, code_iv, plan_days, status, created_at, activated_at, expires_at, bound_device_id, max_devices, created_by, note, version, updated_at)
       VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
       ON CONFLICT(license_id) DO UPDATE SET code_hash=excluded.code_hash, code_prefix=excluded.code_prefix, code_ciphertext=excluded.code_ciphertext, code_iv=excluded.code_iv, plan_days=excluded.plan_days, status=excluded.status, created_at=excluded.created_at, activated_at=excluded.activated_at, expires_at=excluded.expires_at, bound_device_id=excluded.bound_device_id, max_devices=excluded.max_devices, created_by=excluded.created_by, note=excluded.note, version=excluded.version, updated_at=excluded.updated_at`,
    ).bind(license.license_id, license.code_hash, license.code_prefix, license.code_ciphertext, license.code_iv, license.plan_days, license.status, license.created_at, license.activated_at, license.expires_at, license.bound_device_id, 1, license.created_by, license.note, Number(license.version || payload.version || 0), updatedAt),
    db.prepare("DELETE FROM leases WHERE license_id = ?").bind(license.license_id),
    db.prepare("DELETE FROM devices WHERE license_id = ?").bind(license.license_id),
    db.prepare("DELETE FROM sync_tombstones WHERE license_id = ?").bind(license.license_id),
  ]);
  const deviceStatements = (payload.devices || []).map((device) => db.prepare(
    `INSERT INTO devices (device_id, license_id, public_key_spki, public_key_hash, first_seen_at, last_seen_at, status, app_version, os_version, unbound_at)
     VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
  ).bind(device.device_id, device.license_id, device.public_key_spki, device.public_key_hash, device.first_seen_at, device.last_seen_at, device.status, device.app_version, device.os_version, device.unbound_at));
  if (deviceStatements.length) await db.batch(deviceStatements);
  const leaseStatements = (payload.leases || []).map((lease) => db.prepare(
    `INSERT INTO leases (lease_id, license_id, device_id, token_hash, issued_at, expires_at, grace_until, revoked_at, client_version)
     VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)`,
  ).bind(lease.lease_id, lease.license_id, lease.device_id, lease.token_hash, lease.issued_at, lease.expires_at, lease.grace_until, lease.revoked_at, lease.client_version));
  if (leaseStatements.length) await db.batch(leaseStatements);
}

function compareSyncFreshness(local: LicenseRow | null, localTombstone: { changed_at: number; version: number } | null, remote: SyncPayload): -1 | 0 | 1 {
  if (!local && !localTombstone) return 1;
  const localTime = Number(localTombstone?.changed_at || local?.updated_at || local?.created_at || 0);
  const remoteTime = Number(remote.changedAt || remote.license?.updated_at || remote.license?.created_at || 0);
  if (remoteTime !== localTime) return remoteTime > localTime ? 1 : -1;
  const localVersion = Number(localTombstone?.version || local?.version || 0);
  const remoteVersion = Number(remote.version || remote.license?.version || 0);
  return remoteVersion === localVersion ? 0 : remoteVersion > localVersion ? 1 : -1;
}

async function pushSyncPayload(env: Env, payload: SyncPayload): Promise<{ ok: boolean; remote?: SyncPayload; error?: string }> {
  const endpoint = syncEndpoint(env, "internal/sync/apply");
  if (!endpoint) return { ok: false, error: "SUPABASE_SYNC_NOT_CONFIGURED" };
  try {
    const response = await fetch(endpoint, {
      method: "POST",
      headers: { "content-type": "application/json", "x-sync-secret": env.AUTH_SYNC_SECRET || "" },
      body: JSON.stringify(payload),
    });
    const body = await response.json() as { ok?: boolean; action?: string; current?: SyncPayload; error?: string };
    if (response.ok && body.ok && body.action !== "remote_newer") return { ok: true };
    if (response.ok && body.action === "remote_newer" && body.current) return { ok: false, remote: body.current };
    return { ok: false, error: body.error || `HTTP_${response.status}` };
  } catch (error) {
    return { ok: false, error: error instanceof Error ? error.message.slice(0, 200) : "NETWORK_ERROR" };
  }
}

async function processSyncOutbox(env: Env, licenseId?: string): Promise<void> {
  if (!env.DB || !syncEndpoint(env, "internal/sync/apply")) return;
  const statement = licenseId
    ? env.DB.prepare("SELECT license_id, payload_json FROM sync_outbox WHERE license_id = ? LIMIT 1").bind(licenseId)
    : env.DB.prepare("SELECT license_id, payload_json FROM sync_outbox ORDER BY updated_at LIMIT 20");
  const rows = await statement.all<{ license_id: string; payload_json: string }>();
  for (const row of rows.results) {
    let payload: SyncPayload;
    try { payload = JSON.parse(row.payload_json) as SyncPayload; } catch { await env.DB.prepare("DELETE FROM sync_outbox WHERE license_id = ?").bind(row.license_id).run(); continue; }
    const result = await pushSyncPayload(env, payload);
    if (result.ok) {
      await env.DB.prepare("DELETE FROM sync_outbox WHERE license_id = ?").bind(row.license_id).run();
    } else if (result.remote) {
      await applySyncPayloadToD1(env.DB, result.remote);
      await env.DB.prepare("DELETE FROM sync_outbox WHERE license_id = ?").bind(row.license_id).run();
    } else {
      await env.DB.prepare("UPDATE sync_outbox SET attempts = attempts + 1, last_error = ?, updated_at = ? WHERE license_id = ?")
        .bind(result.error || "SYNC_FAILED", nowSeconds(), row.license_id).run();
    }
  }
}

async function reconcileFromSupabase(env: Env): Promise<void> {
  if (!env.DB) return;
  const endpoint = syncEndpoint(env, "internal/sync/export");
  if (!endpoint) return;
  try {
    const response = await fetch(endpoint, { headers: { "x-sync-secret": env.AUTH_SYNC_SECRET || "" } });
    if (!response.ok) throw new Error(`HTTP_${response.status}`);
    const body = await response.json() as { items?: SyncPayload[] };
    for (const remote of body.items || []) {
      const local = await env.DB.prepare("SELECT * FROM licenses WHERE license_id = ?").bind(remote.licenseId).first<LicenseRow>();
      const tombstone = await env.DB.prepare("SELECT changed_at, version FROM sync_tombstones WHERE license_id = ?").bind(remote.licenseId).first<{ changed_at: number; version: number }>();
      const freshness = compareSyncFreshness(local, tombstone || null, remote);
      if (freshness > 0) {
        await applySyncPayloadToD1(env.DB, remote);
      } else if (freshness < 0 && (local || tombstone)) {
        if (local) await enqueueLicenseSync(env, remote.licenseId, Number(local.updated_at || local.created_at || syncNow()));
        else await enqueueDeletedLicenseSync(env, remote.licenseId, Number(tombstone?.changed_at || syncNow()), Number(tombstone?.version || 0));
      }
    }
  } catch (error) {
    console.error("supabase reconciliation failed", error instanceof Error ? error.message : "unknown");
  }
}

async function applySupabaseSync(request: Request, env: Env, requestIdValue: string): Promise<Response> {
  if (!env.AUTH_SYNC_SECRET || !constantTimeEqual(request.headers.get("x-sync-secret") || "", env.AUTH_SYNC_SECRET)) {
    return failure("SYNC_UNAUTHORIZED", "同步请求未授权", requestIdValue, 401);
  }
  const dbOrError = requireDb(env, requestIdValue);
  if (dbOrError instanceof Response) return dbOrError;
  let payload: SyncPayload;
  try {
    payload = await readJson<SyncPayload>(request);
  } catch {
    return failure("SYNC_INVALID_PAYLOAD", "同步数据无效", requestIdValue, 400);
  }
  if (!isNonEmptyString(payload.licenseId, 100) || !Number.isFinite(payload.changedAt) || !Number.isFinite(payload.version)) {
    return failure("SYNC_INVALID_PAYLOAD", "同步数据无效", requestIdValue, 400);
  }
  const local = await dbOrError.prepare("SELECT * FROM licenses WHERE license_id = ?").bind(payload.licenseId).first<LicenseRow>();
  const tombstone = await dbOrError.prepare("SELECT changed_at, version FROM sync_tombstones WHERE license_id = ?").bind(payload.licenseId).first<{ changed_at: number; version: number }>();
  const freshness = compareSyncFreshness(local, tombstone || null, payload);
  if (freshness === 0) {
    return json({ ok: true, action: "equal" });
  }
  if (freshness < 0) {
    return json({ ok: true, action: "d1_newer", current: await readSyncPayload(dbOrError, payload.licenseId) });
  }
  try {
    await applySyncPayloadToD1(dbOrError, payload);
    return json({ ok: true, action: "applied" });
  } catch (error) {
    console.error("supabase sync apply failed", error instanceof Error ? error.message : "unknown");
    return failure("SYNC_APPLY_FAILED", "同步写入失败", requestIdValue, 503);
  }
}

async function markLicenseChanged(env: Env, licenseId: string): Promise<void> {
  if (!env.DB) return;
  const changedAt = syncNow();
  try {
    await env.DB.prepare("UPDATE licenses SET version = version + 1, updated_at = ? WHERE license_id = ?").bind(changedAt, licenseId).run();
  } catch { /* migration may not be applied during local development */ }
  await enqueueLicenseSync(env, licenseId, changedAt);
  // Authorization and resource requests may fail over immediately. Push this
  // license graph before returning so a lease created by either provider is
  // accepted by the other provider without waiting for the five-minute cron.
  await processSyncOutbox(env, licenseId);
}

async function createAdminSessionToken(env: Env, username: string): Promise<string | null> {
  if (!env.ADMIN_API_KEY) return null;
  const payload = base64Url(new TextEncoder().encode(JSON.stringify({ sub: username, exp: nowSeconds() + 8 * 60 * 60 })));
  const signature = await hmacHex(env.ADMIN_API_KEY, `admin-session:${payload}`);
  return `${payload}.${signature}`;
}

async function validAdminSessionToken(token: string, env: Env): Promise<boolean> {
  if (!env.ADMIN_API_KEY) return false;
  const separator = token.lastIndexOf(".");
  if (separator <= 0 || separator === token.length - 1) return false;
  const payload = token.slice(0, separator);
  const signature = token.slice(separator + 1);
  const expected = await hmacHex(env.ADMIN_API_KEY, `admin-session:${payload}`);
  if (!constantTimeEqual(signature, expected)) return false;
  try {
    const parsed = JSON.parse(new TextDecoder().decode(base64UrlBytes(payload))) as { exp?: number };
    return Number.isFinite(parsed.exp) && Number(parsed.exp) > nowSeconds();
  } catch {
    return false;
  }
}

async function adminAuthorized(request: Request, env: Env): Promise<boolean> {
  if (env.ADMIN_API_KEY && constantTimeEqual(request.headers.get("x-admin-key") || "", env.ADMIN_API_KEY)) return true;
  const session = request.headers.get("x-admin-session") || "";
  return session ? validAdminSessionToken(session, env) : false;
}

async function adminLogin(request: Request, env: Env, requestIdValue: string): Promise<Response> {
  if (!env.ADMIN_LOGIN_USERNAME || !env.ADMIN_LOGIN_PASSWORD || !env.ADMIN_API_KEY) {
    return failure("ADMIN_LOGIN_NOT_CONFIGURED", "管理员登录尚未配置", requestIdValue, 503);
  }
  let body: { username?: string; password?: string };
  try {
    body = await readJson(request);
  } catch {
    return failure("INVALID_JSON", "请求体无效", requestIdValue, 400);
  }
  const username = typeof body.username === "string" ? body.username.trim() : "";
  const password = typeof body.password === "string" ? body.password : "";
  const valid = constantTimeEqual(username, env.ADMIN_LOGIN_USERNAME) && constantTimeEqual(password, env.ADMIN_LOGIN_PASSWORD);
  if (!valid) return failure("ADMIN_LOGIN_FAILED", "管理员账号或密码错误", requestIdValue, 401);
  const sessionToken = await createAdminSessionToken(env, username);
  if (!sessionToken) return failure("ADMIN_LOGIN_NOT_CONFIGURED", "管理员登录尚未配置", requestIdValue, 503);
  return json({ ok: true, sessionToken, expiresIn: 8 * 60 * 60, requestId: requestIdValue });
}

async function markExpiredLicenses(db: D1Database, now: number, env?: Env): Promise<string[]> {
  const expired = await db.prepare(
    "SELECT license_id FROM licenses WHERE status IN ('unused', 'active') AND expires_at IS NOT NULL AND expires_at <= ?",
  ).bind(now).all<{ license_id: string }>();
  if (!expired.results.length) return [];
  await db.prepare(
    "UPDATE licenses SET status = 'expired', version = version + 1, updated_at = ? WHERE status IN ('unused', 'active') AND expires_at IS NOT NULL AND expires_at <= ?",
  ).bind(syncNow(), now).run();
  if (env) await Promise.all(expired.results.map((row) => enqueueLicenseSync(env, row.license_id)));
  return expired.results.map((row) => row.license_id);
}

async function deleteLicenseRecords(
  db: D1Database,
  now: number,
  licenseIds?: string[],
  allowRevokedWithoutExpiration = false,
  allowUnused = false,
): Promise<string[]> {
  await markExpiredLicenses(db, now);
  const eligibility = allowUnused
    ? "(status = 'unused' OR status = 'revoked' OR (status = 'expired' AND expires_at IS NOT NULL AND expires_at <= ?))"
    : allowRevokedWithoutExpiration
      ? "(status = 'revoked' OR (status = 'expired' AND expires_at IS NOT NULL AND expires_at <= ?))"
      : "(status IN ('expired', 'revoked') AND expires_at IS NOT NULL AND expires_at <= ?)";
  const idsToCheck = [...new Set(licenseIds || [])];
  const conditions: string[] = [];
  const params: (string | number)[] = [];
  if (idsToCheck.length) {
    conditions.push(`license_id IN (${idsToCheck.map(() => "?").join(", ")})`);
    params.push(...idsToCheck);
  }
  conditions.push(eligibility);
  params.push(now);
  const rows = await db.prepare(`SELECT license_id FROM licenses WHERE ${conditions.join(" AND ")}`)
    .bind(...params).all<{ license_id: string }>();
  const ids = rows.results.map((row) => row.license_id);
  for (let start = 0; start < ids.length; start += 50) {
    const chunk = ids.slice(start, start + 50);
    const placeholders = chunk.map(() => "?").join(", ");
    await db.batch([
      db.prepare(`UPDATE purchase_orders SET license_id = NULL, code_ciphertext = NULL, code_iv = NULL WHERE license_id IN (${placeholders})`).bind(...chunk),
      db.prepare(`DELETE FROM verification_logs WHERE license_id IN (${placeholders})`).bind(...chunk),
      db.prepare(`DELETE FROM leases WHERE license_id IN (${placeholders})`).bind(...chunk),
      db.prepare(`DELETE FROM devices WHERE license_id IN (${placeholders})`).bind(...chunk),
      db.prepare(`DELETE FROM licenses WHERE license_id IN (${placeholders}) AND ${eligibility}`).bind(...chunk, now),
    ]);
  }
  return ids;
}

async function createLease(
  db: D1Database,
  pepper: string,
  license: LicenseRow,
  deviceId: string,
  clientVersion: string | null,
  now: number,
): Promise<{ leaseId: string; leaseToken: string; leaseExpiresAt: number; graceUntil: number }> {
  if (!license.expires_at || license.expires_at <= now) throw new Error("LICENSE_EXPIRED");
  const leaseId = uuid();
  const leaseToken = randomToken();
  const leaseExpiresAt = Math.min(license.expires_at, now + LEASE_SECONDS);
  const graceUntil = Math.min(license.expires_at, now + OFFLINE_GRACE_SECONDS);
  const tokenHash = await hmacHex(pepper, `lease:${leaseToken}`);
  await db.prepare(
    `INSERT INTO leases
     (lease_id, license_id, device_id, token_hash, issued_at, expires_at, grace_until, client_version)
     VALUES (?, ?, ?, ?, ?, ?, ?, ?)`,
  )
    .bind(leaseId, license.license_id, deviceId, tokenHash, now, leaseExpiresAt, graceUntil, clientVersion)
    .run();
  return { leaseId, leaseToken, leaseExpiresAt, graceUntil };
}

async function migrateLegacyDeviceBinding(
  db: D1Database,
  license: LicenseRow,
  nextDeviceId: string,
  publicKeyHash: string,
  now: number,
): Promise<"migrated" | "not_found" | "error"> {
  const previous = await db.prepare(
    "SELECT device_id FROM devices WHERE license_id = ? AND public_key_hash = ? AND status = 'active'",
  ).bind(license.license_id, publicKeyHash).first<{ device_id: string }>();
  if (!previous) return "not_found";
  try {
    // Older releases used SHA-256(public key) as device_id. Proving the same
    // key lets us atomically move that record to the motherboard-derived id.
    await db.batch([
      db.prepare("DELETE FROM leases WHERE license_id = ?").bind(license.license_id),
      db.prepare("UPDATE devices SET device_id = ?, last_seen_at = ?, status = 'active', unbound_at = NULL WHERE license_id = ? AND device_id = ? AND public_key_hash = ?")
        .bind(nextDeviceId, now, license.license_id, previous.device_id, publicKeyHash),
      db.prepare("UPDATE licenses SET bound_device_id = ?, version = version + 1 WHERE license_id = ? AND status = 'active' AND bound_device_id = ?")
        .bind(nextDeviceId, license.license_id, license.bound_device_id),
    ]);
    const migrated = await db.prepare("SELECT bound_device_id FROM licenses WHERE license_id = ?")
      .bind(license.license_id).first<{ bound_device_id: string | null }>();
    return migrated?.bound_device_id === nextDeviceId ? "migrated" : "error";
  } catch {
    return "error";
  }
}

async function canRotateDeviceKey(db: D1Database, licenseId: string, now: number): Promise<boolean> {
  const lastRotation = await db.prepare(
    "SELECT created_at FROM verification_logs WHERE license_id = ? AND event = 'device_key_rotated' AND success = 1 ORDER BY created_at DESC LIMIT 1",
  ).bind(licenseId).first<{ created_at: number }>();
  return !lastRotation || lastRotation.created_at <= now - DEVICE_KEY_RECOVERY_COOLDOWN_SECONDS;
}

function licenseResponse(license: LicenseRow, lease: { leaseId: string; leaseToken: string; leaseExpiresAt: number; graceUntil: number }, now: number) {
  return {
    licenseId: license.license_id,
    planDays: license.plan_days,
    status: license.status,
    activatedAt: license.activated_at,
    expiresAt: license.expires_at,
    leaseId: lease.leaseId,
    leaseToken: lease.leaseToken,
    leaseExpiresAt: lease.leaseExpiresAt,
    graceUntil: lease.graceUntil,
    serverTime: now,
  };
}

async function activate(request: Request, env: Env, requestIdValue: string): Promise<Response> {
  const dbOrError = requireDb(env, requestIdValue);
  if (dbOrError instanceof Response) return dbOrError;
  const pepperOrError = requirePepper(env, requestIdValue);
  if (pepperOrError instanceof Response) return pepperOrError;
  const db = dbOrError;
  const pepper = pepperOrError;

  let body: ActivateBody;
  try {
    body = await readJson<ActivateBody>(request);
  } catch (error) {
    return failure(error instanceof Error && error.message === "REQUEST_TOO_LARGE" ? "REQUEST_TOO_LARGE" : "INVALID_JSON", "请求体无效", requestIdValue, 400);
  }
  if (!isNonEmptyString(body.code, 100) || !isNonEmptyString(body.deviceId, 200)) {
    return failure("INVALID_REQUEST", "需要提供 code 和 deviceId", requestIdValue, 400);
  }
  if (body.protocolVersion !== "2" || !isNonEmptyString(body.requestId, 100)
      || !Number.isSafeInteger(body.signatureTimestamp) || !isNonEmptyString(body.signature, 1000)
      || !isNonEmptyString(body.devicePublicKey, 10000)) {
    return failure("INVALID_SIGNATURE", "客户端签名信息无效，请更新软件后重试", requestIdValue, 401);
  }
  const now = nowSeconds();
  if (!await verifyDeviceSignature(body.devicePublicKey, body.signature, body.signatureTimestamp, activateSigningPayload(body), now)) {
    await writeLog(env, request, "activate", false, requestIdValue, undefined, body.deviceId.trim(), { reason: "invalid_signature" });
    return failure("INVALID_SIGNATURE", "设备签名校验失败，请更新软件后重试", requestIdValue, 401);
  }
  const deviceId = body.deviceId.trim();
  const normalizedCode = normalizeCode(body.code);
  if (normalizedCode.length < 16) return failure("INVALID_CODE", "激活码格式无效", requestIdValue, 400);

  const codeHash = await hmacHex(pepper, `license:${normalizedCode}`);
  let license = await db.prepare("SELECT * FROM licenses WHERE code_hash = ?").bind(codeHash).first<LicenseRow>();
  if (!license) {
    await writeLog(env, request, "activate", false, requestIdValue, undefined, deviceId, { reason: "not_found" });
    return failure("LICENSE_NOT_FOUND", "激活码不存在或已失效", requestIdValue, 404);
  }
  if (!await claimRequestId(db, body.requestId.trim(), now))
    return failure("REPLAYED_REQUEST", "请求已使用，请重试", requestIdValue, 409);

  if (license.status === "revoked") {
    await writeLog(env, request, "activate", false, requestIdValue, license.license_id, deviceId, { reason: "revoked" });
    return failure("LICENSE_REVOKED", "激活码已被撤销", requestIdValue, 403);
  }
  if (license.expires_at && license.expires_at <= now && license.status !== "unused") {
    await db.prepare("UPDATE licenses SET status = 'expired', version = version + 1 WHERE license_id = ?").bind(license.license_id).run();
    await markLicenseChanged(env, license.license_id);
    await writeLog(env, request, "activate", false, requestIdValue, license.license_id, deviceId, { reason: "expired" });
    return failure("LICENSE_EXPIRED", "激活码已过期", requestIdValue, 403);
  }

  if (license.status === "unused") {
    const expiresAt = now + license.plan_days * 24 * 60 * 60;
    const claimed = await db.prepare(
      `UPDATE licenses
       SET status = 'active', bound_device_id = ?, activated_at = ?, expires_at = ?, version = version + 1
       WHERE license_id = ? AND status = 'unused' AND bound_device_id IS NULL`,
    ).bind(deviceId, now, expiresAt, license.license_id).run();
    if (!claimed.meta.changes) {
      license = await db.prepare("SELECT * FROM licenses WHERE license_id = ?").bind(license.license_id).first<LicenseRow>() || license;
    } else {
      license = await db.prepare("SELECT * FROM licenses WHERE license_id = ?").bind(license.license_id).first<LicenseRow>() || license;
    }
  }

  const publicKey = body.devicePublicKey.trim();
  const publicKeyHash = await sha256Hex(publicKey);
  let bindingMigrated = false;
  let deviceKeyRotated = false;
  if (license.status !== "active" || license.bound_device_id !== deviceId) {
    const migration = license.status === "active"
      ? await migrateLegacyDeviceBinding(db, license, deviceId, publicKeyHash, now)
      : "not_found";
    if (migration !== "migrated") {
      await writeLog(env, request, "activate", false, requestIdValue, license.license_id, deviceId, { reason: "bound_other_device" });
      return failure("LICENSE_ALREADY_BOUND", "激活码已绑定其他设备", requestIdValue, 409);
    }
    license = { ...license, bound_device_id: deviceId };
    bindingMigrated = true;
  }

  const existingDevice = await db.prepare("SELECT device_id, public_key_hash FROM devices WHERE device_id = ? AND license_id = ?")
    .bind(deviceId, license.license_id).first<{ device_id: string; public_key_hash: string }>();
  if (existingDevice) {
    if (!constantTimeEqual(existingDevice.public_key_hash, publicKeyHash)) {
      if (!await canRotateDeviceKey(db, license.license_id, now)) {
        await writeLog(env, request, "activate", false, requestIdValue, license.license_id, deviceId, { reason: "device_key_recovery_cooldown" });
        return failure("DEVICE_KEY_RECOVERY_COOLDOWN", "设备密钥刚刚恢复过，请 7 天后重试或联系管理员", requestIdValue, 409);
      }
      await db.batch([
        db.prepare(
          "UPDATE devices SET public_key_spki = ?, public_key_hash = ?, last_seen_at = ?, status = 'active', app_version = ? WHERE device_id = ? AND license_id = ?",
        ).bind(publicKey, publicKeyHash, now, body.clientVersion || null, deviceId, license.license_id),
        db.prepare("UPDATE leases SET revoked_at = ? WHERE license_id = ? AND revoked_at IS NULL").bind(now, license.license_id),
      ]);
      deviceKeyRotated = true;
    } else {
      await db.prepare(
        `UPDATE devices SET public_key_spki = ?, public_key_hash = ?, last_seen_at = ?, status = 'active', app_version = ?
         WHERE device_id = ? AND license_id = ?`,
      ).bind(publicKey, publicKeyHash, now, body.clientVersion || null, deviceId, license.license_id).run();
    }
  } else {
    await db.prepare(
      `INSERT INTO devices (device_id, license_id, public_key_spki, public_key_hash, first_seen_at, last_seen_at, app_version)
       VALUES (?, ?, ?, ?, ?, ?, ?)`,
    ).bind(deviceId, license.license_id, publicKey, publicKeyHash, now, now, body.clientVersion || null).run();
  }

  const lease = await createLease(db, pepper, license, deviceId, body.clientVersion || null, now);
  await markLicenseChanged(env, license.license_id);
  const event = deviceKeyRotated ? "device_key_rotated" : bindingMigrated ? "device_binding_migrated" : "activate";
  await writeLog(env, request, event, true, requestIdValue, license.license_id, deviceId);
  return json({ ok: true, ...licenseResponse(license, lease, now), requestId: requestIdValue });
}

async function verify(request: Request, env: Env, requestIdValue: string, event: "verify" | "heartbeat"): Promise<Response> {
  const dbOrError = requireDb(env, requestIdValue);
  if (dbOrError instanceof Response) return dbOrError;
  const pepperOrError = requirePepper(env, requestIdValue);
  if (pepperOrError instanceof Response) return pepperOrError;
  const db = dbOrError;
  const pepper = pepperOrError;

  let body: VerifyBody;
  try {
    body = await readJson<VerifyBody>(request);
  } catch {
    return failure("INVALID_JSON", "请求体无效", requestIdValue, 400);
  }
  if (!isNonEmptyString(body.licenseId, 100) || !isNonEmptyString(body.deviceId, 200) || !isNonEmptyString(body.leaseId, 100) || !isNonEmptyString(body.leaseToken, 500)
      || !isNonEmptyString(body.requestId, 100) || !Number.isSafeInteger(body.signatureTimestamp) || !isNonEmptyString(body.signature, 1000)) {
    return failure("INVALID_REQUEST", "需要提供 licenseId、deviceId、leaseId 和 leaseToken", requestIdValue, 400);
  }
  const now = nowSeconds();
  const tokenHash = await hmacHex(pepper, `lease:${body.leaseToken}`);
  const row = await db.prepare(
    `SELECT l.*, le.lease_id, le.device_id AS lease_device_id, le.expires_at AS lease_expires_at,
            le.grace_until, le.revoked_at, d.status AS device_status, d.public_key_spki
     FROM leases le
     JOIN licenses l ON l.license_id = le.license_id
     JOIN devices d ON d.device_id = le.device_id AND d.license_id = le.license_id
     WHERE le.lease_id = ? AND le.token_hash = ? AND l.license_id = ?`,
  ).bind(body.leaseId.trim(), tokenHash, body.licenseId.trim()).first<LicenseRow & { lease_id: string; lease_device_id: string; lease_expires_at: number; grace_until: number; revoked_at: number | null; device_status: string; public_key_spki: string | null }>();

  if (!row || row.lease_device_id !== body.deviceId.trim() || row.bound_device_id !== body.deviceId.trim()) {
    await writeLog(env, request, event, false, requestIdValue, body.licenseId, body.deviceId, { reason: "invalid_lease" });
    return failure("INVALID_LEASE", "授权租约无效", requestIdValue, 401);
  }
  if (!await verifyDeviceSignature(row.public_key_spki, body.signature, body.signatureTimestamp, verifySigningPayload(body), now)) {
    await writeLog(env, request, event, false, requestIdValue, body.licenseId, body.deviceId, { reason: "invalid_signature" });
    return failure("INVALID_SIGNATURE", "设备签名校验失败，请更新软件后重试", requestIdValue, 401);
  }
  if (!await claimRequestId(db, body.requestId.trim(), now))
    return failure("REPLAYED_REQUEST", "请求已使用，请重试", requestIdValue, 409);
  if (row.status === "revoked" || row.device_status !== "active") return failure("LICENSE_REVOKED", "授权已撤销", requestIdValue, 403);
  if (!row.expires_at || row.expires_at <= now) {
    await db.prepare("UPDATE licenses SET status = 'expired', version = version + 1 WHERE license_id = ?").bind(row.license_id).run();
    await markLicenseChanged(env, row.license_id);
    return failure("LICENSE_EXPIRED", "授权已过期", requestIdValue, 403);
  }
  if (row.revoked_at || row.grace_until < now) return failure("LEASE_EXPIRED", "本地租约已过期，请重新联网激活", requestIdValue, 401);

  await db.prepare("UPDATE devices SET last_seen_at = ?, app_version = COALESCE(?, app_version) WHERE device_id = ? AND license_id = ?")
    .bind(now, body.clientVersion || null, body.deviceId.trim(), row.license_id).run();
  const license: LicenseRow = {
    license_id: row.license_id,
    code_hash: row.code_hash,
    code_prefix: row.code_prefix,
    code_ciphertext: row.code_ciphertext,
    code_iv: row.code_iv,
    plan_days: row.plan_days,
    status: row.status,
    created_at: row.created_at,
    activated_at: row.activated_at,
    expires_at: row.expires_at,
    bound_device_id: row.bound_device_id,
    created_by: row.created_by,
    note: row.note,
    version: row.version,
    updated_at: row.updated_at,
  };
  const lease = await createLease(db, pepper, license, body.deviceId.trim(), body.clientVersion || null, now);
  await markLicenseChanged(env, license.license_id);
  await writeLog(env, request, event, true, requestIdValue, license.license_id, body.deviceId);
  return json({ ok: true, ...licenseResponse(license, lease, now), requestId: requestIdValue });
}

async function createOrder(request: Request, env: Env, requestIdValue: string): Promise<Response> {
  const dbOrError = requireDb(env, requestIdValue);
  if (dbOrError instanceof Response) return dbOrError;
  const pepperOrError = requirePepper(env, requestIdValue);
  if (pepperOrError instanceof Response) return pepperOrError;

  let body: CreateOrderBody;
  try {
    body = await readJson<CreateOrderBody>(request);
  } catch {
    return failure("INVALID_JSON", "请求体无效", requestIdValue, 400);
  }
  const planDays = Number(body.planDays);
  const paymentMethod = body.paymentMethod;
  if (![1, 7, 30].includes(planDays) || (paymentMethod !== "alipay" && paymentMethod !== "wechat")) {
    return failure("INVALID_ORDER", "需要提供有效的套餐时长和支付方式", requestIdValue, 400);
  }

  const orderId = uuid();
  const orderToken = randomToken(24);
  const orderTokenHash = await hmacHex(pepperOrError, `order:${orderToken}`);
  const now = nowSeconds();
  const expiresAt = now + 30 * 60;
  try {
    await dbOrError.prepare(
      `INSERT INTO purchase_orders
       (order_id, order_token_hash, plan_days, payment_method, status, created_at, expires_at)
       VALUES (?, ?, ?, ?, 'pending', ?, ?)`,
    ).bind(orderId, orderTokenHash, planDays, paymentMethod, now, expiresAt).run();
  } catch {
    return failure("ORDER_CREATE_FAILED", "订单创建失败，请重试", requestIdValue, 500);
  }
  return json({ ok: true, orderId, orderToken, planDays, paymentMethod, status: "pending", expiresAt, requestId: requestIdValue });
}

function publicOrderResponse(order: PaymentOrderRow, requestIdValue: string, code?: string) {
  return {
    ok: true,
    orderId: order.order_id,
    planDays: order.plan_days,
    paymentMethod: order.payment_method,
    status: order.status,
    expiresAt: order.expires_at,
    paidAt: order.paid_at,
    code: code || null,
    codeAvailable: Boolean(code),
    requestId: requestIdValue,
  };
}

async function getOrder(request: Request, env: Env, requestIdValue: string, orderId: string): Promise<Response> {
  const dbOrError = requireDb(env, requestIdValue);
  if (dbOrError instanceof Response) return dbOrError;
  const pepperOrError = requirePepper(env, requestIdValue);
  if (pepperOrError instanceof Response) return pepperOrError;
  const token = new URL(request.url).searchParams.get("token") || "";
  if (!isNonEmptyString(token, 200)) return failure("ORDER_UNAUTHORIZED", "订单凭证无效", requestIdValue, 401);
  const tokenHash = await hmacHex(pepperOrError, `order:${token}`);
  const order = await dbOrError.prepare("SELECT * FROM purchase_orders WHERE order_id = ? AND order_token_hash = ?")
    .bind(orderId, tokenHash).first<PaymentOrderRow>();
  if (!order) return failure("ORDER_NOT_FOUND", "订单不存在或凭证已失效", requestIdValue, 404);

  const now = nowSeconds();
  if (["pending", "processing"].includes(order.status) && order.expires_at <= now) {
    await dbOrError.prepare("UPDATE purchase_orders SET status = 'expired' WHERE order_id = ? AND status IN ('pending', 'processing')")
      .bind(order.order_id).run();
    order.status = "expired";
  }

  if (order.status !== "paid" || !order.code_ciphertext || !order.code_iv) {
    return json(publicOrderResponse(order, requestIdValue));
  }

  const code = await decryptDeliveryCode(pepperOrError, order.code_ciphertext, order.code_iv);
  const claimed = await dbOrError.prepare(
    `UPDATE purchase_orders SET status = 'delivered', delivered_at = ?
     WHERE order_id = ? AND status = 'paid' AND delivered_at IS NULL`,
  ).bind(now, order.order_id).run();
  if (!claimed.meta.changes) {
    order.status = "delivered";
    return json(publicOrderResponse(order, requestIdValue));
  }
  order.status = "delivered";
  order.delivered_at = now;
  await writeLog(env, request, "payment_delivery", true, requestIdValue, order.license_id || undefined, undefined, { orderId: order.order_id });
  return json(publicOrderResponse(order, requestIdValue, code));
}

async function paymentWebhook(request: Request, env: Env, requestIdValue: string): Promise<Response> {
  const dbOrError = requireDb(env, requestIdValue);
  if (dbOrError instanceof Response) return dbOrError;
  const pepperOrError = requirePepper(env, requestIdValue);
  if (pepperOrError instanceof Response) return pepperOrError;
  if (!env.PAYMENT_WEBHOOK_SECRET) return failure("PAYMENT_NOT_CONFIGURED", "支付回调密钥尚未配置", requestIdValue, 503);

  const signature = request.headers.get("x-payment-signature") || "";
  const rawBody = await request.text();
  if (new TextEncoder().encode(rawBody).byteLength > MAX_BODY_BYTES) return failure("REQUEST_TOO_LARGE", "请求体过大", requestIdValue, 413);
  const expectedSignature = await hmacHex(env.PAYMENT_WEBHOOK_SECRET, rawBody);
  if (!constantTimeEqual(signature, expectedSignature)) return failure("INVALID_SIGNATURE", "支付回调签名无效", requestIdValue, 401);

  let body: PaymentWebhookBody;
  try {
    body = JSON.parse(rawBody) as PaymentWebhookBody;
  } catch {
    return failure("INVALID_JSON", "请求体无效", requestIdValue, 400);
  }
  if (!isNonEmptyString(body.orderId, 100) || !isNonEmptyString(body.paymentId, 200)) {
    return failure("INVALID_WEBHOOK", "缺少订单号或支付流水号", requestIdValue, 400);
  }
  const isPaid = body.status === "paid" || body.status === "success";
  const isClosed = body.status === "closed";
  if (!isPaid && !isClosed) return failure("INVALID_WEBHOOK", "不支持的支付状态", requestIdValue, 400);

  const order = await dbOrError.prepare("SELECT * FROM purchase_orders WHERE order_id = ?").bind(body.orderId.trim()).first<PaymentOrderRow>();
  if (!order) return failure("ORDER_NOT_FOUND", "订单不存在", requestIdValue, 404);
  if (body.planDays !== undefined && Number(body.planDays) !== order.plan_days) {
    return failure("ORDER_PLAN_MISMATCH", "回调套餐与订单不一致", requestIdValue, 409);
  }
  if (isClosed) {
    await dbOrError.prepare("UPDATE purchase_orders SET status = 'expired', payment_id = ? WHERE order_id = ? AND status = 'pending'")
      .bind(body.paymentId.trim(), order.order_id).run();
    return json({ ok: true, orderId: order.order_id, status: "expired", requestId: requestIdValue });
  }
  if (order.status === "paid" || order.status === "delivered") {
    return json({ ok: true, orderId: order.order_id, status: order.status, requestId: requestIdValue });
  }
  const claimed = await dbOrError.prepare("UPDATE purchase_orders SET status = 'processing' WHERE order_id = ? AND status = 'pending'")
    .bind(order.order_id).run();
  if (!claimed.meta.changes) return json({ ok: true, orderId: order.order_id, status: order.status, requestId: requestIdValue }, 202);

  const code = randomCode();
  const licenseId = uuid();
  const createdAt = nowSeconds();
  const syncCreatedAt = syncNow();
  const encrypted = await encryptDeliveryCode(pepperOrError, code);
  try {
    await dbOrError.batch([
      dbOrError.prepare(
      `INSERT INTO licenses (license_id, code_hash, code_prefix, code_ciphertext, code_iv, plan_days, created_at, created_by, note, updated_at)
       VALUES (?, ?, ?, ?, ?, ?, ?, 'payment', ?, ?)`,
      ).bind(licenseId, await hmacHex(pepperOrError, `license:${normalizeCode(code)}`), code.slice(0, 4), encrypted.ciphertext, encrypted.iv, order.plan_days, createdAt, `order:${order.order_id}`, syncCreatedAt),
      dbOrError.prepare(
        `UPDATE purchase_orders SET status = 'paid', payment_id = ?, license_id = ?, code_ciphertext = ?, code_iv = ?, paid_at = ?
         WHERE order_id = ? AND status = 'processing'`,
      ).bind(body.paymentId.trim(), licenseId, encrypted.ciphertext, encrypted.iv, nowSeconds(), order.order_id),
    ]);
  } catch {
    await dbOrError.prepare("UPDATE purchase_orders SET status = 'pending' WHERE order_id = ? AND status = 'processing'").bind(order.order_id).run();
    return failure("PAYMENT_DELIVERY_FAILED", "支付已收到，但激活码生成失败，请重试回调", requestIdValue, 500);
  }
  await enqueueLicenseSync(env, licenseId, syncCreatedAt);
  await processSyncOutbox(env);
  await writeLog(env, request, "payment_confirmed", true, requestIdValue, licenseId, undefined, { orderId: order.order_id, paymentId: body.paymentId.trim() });
  return json({ ok: true, orderId: order.order_id, status: "paid", requestId: requestIdValue });
}

async function adminCreate(request: Request, env: Env, requestIdValue: string): Promise<Response> {
  const dbOrError = requireDb(env, requestIdValue);
  if (dbOrError instanceof Response) return dbOrError;
  if (!env.LICENSE_PEPPER) return failure("SERVER_NOT_CONFIGURED", "授权服务尚未完成密钥配置", requestIdValue, 503);
  const db = dbOrError;
  let body: { planDays?: number; count?: number; createdBy?: string; note?: string };
  try {
    body = await readJson(request);
  } catch {
    return failure("INVALID_JSON", "请求体无效", requestIdValue, 400);
  }
  const planDays = Number(body.planDays);
  const count = Math.floor(Number(body.count || 1));
  if (!isValidLicensePlanDays(planDays) || !Number.isInteger(count) || count < 1 || count > 100) {
    return failure("INVALID_REQUEST", `planDays 必须为 1-${MAX_LICENSE_PLAN_DAYS} 的整数，count 范围为 1-100`, requestIdValue, 400);
  }
  const createdBy = isNonEmptyString(body.createdBy, 100) ? body.createdBy.trim() : "admin";
  const note = isNonEmptyString(body.note, 500) ? body.note.trim() : null;
  const createdAt = nowSeconds();
  const syncCreatedAt = syncNow();
  const codes: string[] = [];
  const licenseIds: string[] = [];
  const statements: D1PreparedStatement[] = [];
  for (let i = 0; i < count; i += 1) {
    const code = randomCode();
    codes.push(code);
    const normalized = normalizeCode(code);
    const codeHash = await hmacHex(env.LICENSE_PEPPER, `license:${normalized}`);
    const encrypted = await encryptDeliveryCode(env.LICENSE_PEPPER, code);
    const licenseId = uuid();
    licenseIds.push(licenseId);
    statements.push(db.prepare(
      `INSERT INTO licenses (license_id, code_hash, code_prefix, code_ciphertext, code_iv, plan_days, created_at, created_by, note, updated_at)
       VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
    ).bind(licenseId, codeHash, code.slice(0, 4), encrypted.ciphertext, encrypted.iv, planDays, createdAt, createdBy, note, syncCreatedAt));
  }
  await Promise.all(licenseIds.map((licenseId) => enqueueLicenseSync(env, licenseId, syncCreatedAt)));
  await processSyncOutbox(env);
  try {
    await db.batch(statements);
  } catch {
    return failure("CREATE_LICENSE_FAILED", "激活码生成失败，请重试", requestIdValue, 500);
  }
  await writeLog(env, request, "admin_create_license", true, requestIdValue, undefined, undefined, { count, planDays, createdBy });
  return json({ ok: true, planDays, count, codes, requestId: requestIdValue });
}

async function adminList(request: Request, env: Env, requestIdValue: string): Promise<Response> {
  const dbOrError = requireDb(env, requestIdValue);
  if (dbOrError instanceof Response) return dbOrError;
  const pepper = env.LICENSE_PEPPER;
  if (!pepper) return failure("SERVER_NOT_CONFIGURED", "授权服务尚未完成密钥配置", requestIdValue, 503);
  const db = dbOrError;
  await markExpiredLicenses(db, nowSeconds(), env);
  const url = new URL(request.url);
  const page = Math.max(1, Math.min(100000, Number(url.searchParams.get("page") || 1)));
  const pageSize = Math.max(1, Math.min(100, Number(url.searchParams.get("pageSize") || 20)));
  const status = url.searchParams.get("status") || "";
  const rawPlanDays = (url.searchParams.get("planDays") || "").trim();
  const search = (url.searchParams.get("search") || "").trim().slice(0, 100);
  const normalizedSearch = normalizeCode(search);
  const searchRequiresPlaintextFilter = normalizedSearch.length > 4 && normalizedSearch.length < 28;
  const conditions: string[] = [];
  const params: (string | number)[] = [];
  if (["unused", "active", "expired", "revoked"].includes(status)) { conditions.push("status = ?"); params.push(status); }
  if (rawPlanDays) {
    const planDays = Number(rawPlanDays);
    if (!isValidLicensePlanDays(planDays)) return failure("INVALID_FILTER", `套餐天数必须为 1-${MAX_LICENSE_PLAN_DAYS} 的整数`, requestIdValue, 400);
    conditions.push("plan_days = ?");
    params.push(planDays);
  }
  if (normalizedSearch.length >= 28) {
    conditions.push("code_hash = ?");
    params.push(await hmacHex(pepper, `license:${normalizedSearch}`));
  } else if (normalizedSearch.length >= 4) {
    conditions.push("code_prefix = ?");
    params.push(normalizedSearch.slice(0, 4));
  } else if (normalizedSearch) {
    conditions.push("code_prefix LIKE ?");
    params.push(`${normalizedSearch}%`);
  }
  const where = conditions.length ? `WHERE ${conditions.join(" AND ")}` : "";
  const selectSql = `SELECT license_id, code_prefix, code_ciphertext, code_iv, plan_days, status, created_at, activated_at, expires_at, bound_device_id, created_by, note
     FROM licenses ${where} ORDER BY created_at DESC`;
  type AdminLicenseRow = Omit<LicenseRow, "code_hash">;
  const toAdminItem = async (row: AdminLicenseRow) => {
    let code: string | null = null;
    if (row.code_ciphertext && row.code_iv) {
      try {
        code = await decryptDeliveryCode(pepper, row.code_ciphertext, row.code_iv);
      } catch {
        code = null;
      }
    }
    const { code_ciphertext: _ciphertext, code_iv: _iv, ...safeRow } = row;
    return { ...safeRow, code };
  };

  if (searchRequiresPlaintextFilter) {
    const candidateRows = await db.prepare(selectSql).bind(...params).all<AdminLicenseRow>();
    const candidates = await Promise.all(candidateRows.results.map(toAdminItem));
    const filtered = candidates.filter((item) => item.code && normalizeCode(item.code).includes(normalizedSearch));
    const offset = (page - 1) * pageSize;
    return json({ ok: true, page, pageSize, total: filtered.length, items: filtered.slice(offset, offset + pageSize), requestId: requestIdValue });
  }

  const totalRow = await db.prepare(`SELECT COUNT(*) AS total FROM licenses ${where}`).bind(...params).first<{ total: number }>();
  const rows = await db.prepare(`${selectSql} LIMIT ? OFFSET ?`).bind(...params, pageSize, (page - 1) * pageSize).all<AdminLicenseRow>();
  const items = await Promise.all(rows.results.map(toAdminItem));
  return json({ ok: true, page, pageSize, total: Number(totalRow?.total || 0), items, requestId: requestIdValue });
}

async function adminUpdateLicenseNote(request: Request, env: Env, requestIdValue: string, licenseId: string): Promise<Response> {
  const dbOrError = requireDb(env, requestIdValue);
  if (dbOrError instanceof Response) return dbOrError;
  let body: { note?: unknown };
  try {
    body = await readJson(request);
  } catch {
    return failure("INVALID_JSON", "请求体无效", requestIdValue, 400);
  }
  if (typeof body.note !== "string" || body.note.length > 500) {
    return failure("INVALID_NOTE", "备注必须是不超过 500 个字符的文本", requestIdValue, 400);
  }
  const db = dbOrError;
  const license = await db.prepare("SELECT license_id FROM licenses WHERE license_id = ?").bind(licenseId).first<{ license_id: string }>();
  if (!license) return failure("LICENSE_NOT_FOUND", "激活码不存在", requestIdValue, 404);
  const note = body.note.trim() || null;
  try {
    await db.prepare("UPDATE licenses SET note = ?, version = version + 1 WHERE license_id = ?").bind(note, licenseId).run();
  } catch {
    return failure("LICENSE_NOTE_UPDATE_FAILED", "备注保存失败，请重试", requestIdValue, 500);
  }
  await markLicenseChanged(env, licenseId);
  await processSyncOutbox(env);
  await writeLog(env, request, "admin_update_license_note", true, requestIdValue, licenseId);
  return json({ ok: true, licenseId, note, requestId: requestIdValue });
}

async function adminBulkDelete(request: Request, env: Env, requestIdValue: string): Promise<Response> {
  const dbOrError = requireDb(env, requestIdValue);
  if (dbOrError instanceof Response) return dbOrError;
  let body: { licenseIds?: unknown };
  try {
    body = await readJson(request);
  } catch {
    return failure("INVALID_JSON", "请求体无效", requestIdValue, 400);
  }
  if (!Array.isArray(body.licenseIds) || body.licenseIds.length < 1 || body.licenseIds.length > 500) {
    return failure("INVALID_REQUEST", "每次可批量删除 1-500 张激活码", requestIdValue, 400);
  }
  const licenseIds = [...new Set(body.licenseIds.map((value) => typeof value === "string" ? value.trim() : ""))];
  if (licenseIds.some((licenseId) => !licenseId || licenseId.length > 100)) {
    return failure("INVALID_REQUEST", "激活码标识无效", requestIdValue, 400);
  }
  const now = nowSeconds();
  let deletedIds: string[];
  try {
    deletedIds = await deleteLicenseRecords(dbOrError, now, licenseIds, true, true);
  } catch {
    return failure("LICENSE_DELETE_FAILED", "批量删除失败，请重试", requestIdValue, 500);
  }
  const deletedIdSet = new Set(deletedIds);
  const skippedIds = licenseIds.filter((licenseId) => !deletedIdSet.has(licenseId));
  await Promise.all(deletedIds.map((licenseId) => enqueueDeletedLicenseSync(env, licenseId, now)));
  await processSyncOutbox(env);
  await writeLog(env, request, "admin_bulk_delete_licenses", true, requestIdValue, undefined, undefined, {
    requestedCount: licenseIds.length,
    deletedCount: deletedIds.length,
    skippedCount: skippedIds.length,
  });
  return json({ ok: true, deletedIds, skippedIds, requestId: requestIdValue });
}

async function adminDeleteLicense(request: Request, env: Env, requestIdValue: string, licenseId: string): Promise<Response> {
  const dbOrError = requireDb(env, requestIdValue);
  if (dbOrError instanceof Response) return dbOrError;
  const db = dbOrError;
  const now = nowSeconds();
  await markExpiredLicenses(db, now, env);
  const license = await db.prepare("SELECT license_id, code_prefix, status, expires_at, version FROM licenses WHERE license_id = ?")
    .bind(licenseId).first<{ license_id: string; code_prefix: string; status: LicenseRow["status"]; expires_at: number | null; version: number }>();
  if (!license) return failure("LICENSE_NOT_FOUND", "激活码不存在", requestIdValue, 404);
  if (license.status !== "unused" && license.status !== "revoked" && (license.status !== "expired" || !license.expires_at || license.expires_at > now)) {
    return failure("LICENSE_NOT_DELETABLE", "只能删除未使用、已过期或已撤销的激活码", requestIdValue, 409);
  }

  try {
    const deletedIds = await deleteLicenseRecords(db, now, [licenseId], true, true);
    if (!deletedIds.includes(licenseId)) return failure("LICENSE_DELETE_CONFLICT", "激活码状态已变化，请刷新后重试", requestIdValue, 409);
    await enqueueDeletedLicenseSync(env, licenseId, now, Number(license.version || 0) + 1);
    await processSyncOutbox(env);
  } catch {
    return failure("LICENSE_DELETE_FAILED", "过期激活码删除失败，请重试", requestIdValue, 500);
  }
  await writeLog(env, request, "admin_delete_license", true, requestIdValue, undefined, undefined, { licenseId, codePrefix: license.code_prefix });
  return json({ ok: true, licenseId, action: "delete", requestId: requestIdValue });
}

async function scheduledCleanup(env: Env): Promise<void> {
  const dbOrError = requireDb(env, uuid());
  if (dbOrError instanceof Response) throw new Error("DB_NOT_CONFIGURED");
  const cleanupNow = nowSeconds();
  await markExpiredLicenses(dbOrError, cleanupNow, env);
  const deletedIds = await deleteLicenseRecords(dbOrError, cleanupNow);
  await Promise.all(deletedIds.map((licenseId) => enqueueDeletedLicenseSync(env, licenseId, nowSeconds())));
  await dbOrError.prepare("DELETE FROM request_nonces WHERE expires_at <= ?").bind(nowSeconds()).run();
  if (deletedIds.length) {
    const request = new Request("https://internal.cskin-license-worker/scheduled/license-cleanup");
    await writeLog(env, request, "scheduled_delete_expired_licenses", true, uuid(), undefined, undefined, {
      deletedCount: deletedIds.length,
    });
    console.log(JSON.stringify({ event: "scheduled_delete_expired_licenses", deletedCount: deletedIds.length }));
  }
}

async function adminLicenseAction(request: Request, env: Env, requestIdValue: string, licenseId: string, action: "revoke" | "reset"): Promise<Response> {
  const dbOrError = requireDb(env, requestIdValue);
  if (dbOrError instanceof Response) return dbOrError;
  const db = dbOrError;
  const license = await db.prepare("SELECT * FROM licenses WHERE license_id = ?").bind(licenseId).first<LicenseRow>();
  if (!license) return failure("LICENSE_NOT_FOUND", "激活码不存在", requestIdValue, 404);
  const now = nowSeconds();
  try {
    if (action === "revoke") {
      await db.batch([
        db.prepare("UPDATE licenses SET status = 'revoked', version = version + 1 WHERE license_id = ?").bind(licenseId),
        db.prepare("UPDATE leases SET revoked_at = ? WHERE license_id = ? AND revoked_at IS NULL").bind(now, licenseId),
        db.prepare("UPDATE devices SET status = 'revoked' WHERE license_id = ? AND status = 'active'").bind(licenseId),
      ]);
    } else {
      await db.batch([
        db.prepare("UPDATE licenses SET status = CASE WHEN expires_at IS NOT NULL AND expires_at <= ? THEN 'expired' ELSE 'unused' END, bound_device_id = NULL, version = version + 1 WHERE license_id = ?").bind(now, licenseId),
        db.prepare("UPDATE leases SET revoked_at = ? WHERE license_id = ? AND revoked_at IS NULL").bind(now, licenseId),
        db.prepare("UPDATE devices SET status = 'unbound', unbound_at = ? WHERE license_id = ? AND status != 'unbound'").bind(now, licenseId),
      ]);
    }
  } catch {
    return failure("LICENSE_ACTION_FAILED", "激活码状态更新失败，请重试", requestIdValue, 500);
  }
  await markLicenseChanged(env, licenseId);
  await processSyncOutbox(env);
  await writeLog(env, request, `admin_${action}_license`, true, requestIdValue, licenseId, undefined);
  return json({ ok: true, licenseId, action, requestId: requestIdValue });
}

async function adminStats(env: Env, requestIdValue: string): Promise<Response> {
  const dbOrError = requireDb(env, requestIdValue);
  if (dbOrError instanceof Response) return dbOrError;
  const db = dbOrError;
  await markExpiredLicenses(db, nowSeconds(), env);
  const rows = await db.prepare("SELECT status, COUNT(*) AS count FROM licenses GROUP BY status").all<{ status: string; count: number }>();
  const stats: Record<string, number> = { unused: 0, active: 0, expired: 0, revoked: 0 };
  for (const row of rows.results) stats[row.status] = Number(row.count);
  const devices = await db.prepare("SELECT COUNT(*) AS count FROM devices WHERE status = 'active'").first<{ count: number }>();
  return json({ ok: true, licenses: stats, activeDevices: Number(devices?.count || 0), requestId: requestIdValue });
}

async function health(env: Env, requestIdValue: string): Promise<Response> {
  let databaseReady = false;
  let catalogRevision = BUILT_IN_PRIVATE_SKIN_INDEX.revision;
  let catalogLastError: string | null = null;
  if (env.DB) {
    try {
      await env.DB.prepare("SELECT 1 AS ok").first();
      databaseReady = true;
      const state = await ensurePrivateSkinCatalogState(env.DB);
      catalogRevision = state.current_revision;
      catalogLastError = state.last_error;
    } catch {
      databaseReady = false;
    }
  }
  const ready = databaseReady && Boolean(env.LICENSE_PEPPER);
  return json({
    ok: ready,
    service: "cskin-license-worker",
    environment: env.ENVIRONMENT || "unknown",
    databaseConfigured: Boolean(env.DB),
    databaseReady,
    pepperConfigured: Boolean(env.LICENSE_PEPPER),
    adminKeyConfigured: Boolean(env.ADMIN_API_KEY),
    adminLoginConfigured: Boolean(env.ADMIN_LOGIN_USERNAME && env.ADMIN_LOGIN_PASSWORD),
    paymentWebhookConfigured: Boolean(env.PAYMENT_WEBHOOK_SECRET),
    supabaseSyncConfigured: Boolean(env.SUPABASE_SYNC_URL && env.AUTH_SYNC_SECRET),
    privateSkinConfigured: Boolean(env.GITCODE_TOKEN && env.GITCODE_OWNER && env.GITCODE_REPOSITORY && env.GITCODE_REF && env.SKIN_REPO_LABEL),
    catalogRevision,
    catalogRefreshReady: databaseReady && catalogLastError === null,
    catalogLastError,
    requestId: requestIdValue,
  }, ready ? 200 : 503);
}

export default {
  async scheduled(controller: ScheduledController, env: Env): Promise<void> {
    try {
      await scheduledCleanup(env);
      await reconcileFromSupabase(env);
      await processSyncOutbox(env);
      await refreshPrivateSkinCatalog(env, true);
    } catch (error) {
      console.error("scheduled license cleanup failed", error instanceof Error ? error.message : "unknown");
      controller.noRetry();
    }
  },
  async fetch(request: Request, env: Env): Promise<Response> {
    const url = new URL(request.url);
    const id = requestId(request);
    if (request.method === "OPTIONS") return corsEmpty();
    if (request.method === "GET" && (url.pathname === "/" || url.pathname === "/health")) {
      return url.pathname === "/health" ? health(env, id) : json({ ok: true, service: "cskin-license-worker", environment: env.ENVIRONMENT || "unknown", requestId: id });
    }

    if (request.method === "POST" && url.pathname === "/v1/admin/login") return adminLogin(request, env, id);

    const adminPath = url.pathname.startsWith("/v1/admin/");
    if (adminPath && !(await adminAuthorized(request, env))) {
      return failure(env.ADMIN_API_KEY ? "ADMIN_UNAUTHORIZED" : "ADMIN_NOT_CONFIGURED", "管理员接口未授权", id, env.ADMIN_API_KEY ? 401 : 503);
    }

    try {
      if (request.method === "GET" && url.pathname === "/v1/skins/index") return privateSkinIndex(request, env, id);
      if (request.method === "GET" && url.pathname === "/v1/skins/file") return privateSkinFile(request, env, id);
      if (request.method === "POST" && url.pathname === "/v1/activate") return activate(request, env, id);
      if (request.method === "POST" && url.pathname === "/v1/verify") return verify(request, env, id, "verify");
      if (request.method === "POST" && url.pathname === "/v1/heartbeat") return verify(request, env, id, "heartbeat");
      if (request.method === "POST" && url.pathname === "/v1/orders") return createOrder(request, env, id);
      if (request.method === "POST" && url.pathname === "/v1/payment/webhook") return paymentWebhook(request, env, id);
      if (request.method === "POST" && url.pathname === "/internal/sync/apply") return applySupabaseSync(request, env, id);
      const orderMatch = url.pathname.match(/^\/v1\/orders\/([^/]+)$/);
      if (request.method === "GET" && orderMatch) return getOrder(request, env, id, decodeURIComponent(orderMatch[1]));
      if (request.method === "POST" && url.pathname === "/v1/admin/licenses") return adminCreate(request, env, id);
      if (request.method === "GET" && url.pathname === "/v1/admin/licenses") return adminList(request, env, id);
      if (request.method === "POST" && url.pathname === "/v1/admin/licenses/bulk-delete") return adminBulkDelete(request, env, id);
      if (request.method === "GET" && url.pathname === "/v1/admin/stats") return adminStats(env, id);

      const actionMatch = url.pathname.match(/^\/v1\/admin\/licenses\/([^/]+)\/(revoke|reset-device)$/);
      if (request.method === "POST" && actionMatch) return adminLicenseAction(request, env, id, decodeURIComponent(actionMatch[1]), actionMatch[2] === "revoke" ? "revoke" : "reset");
      const noteMatch = url.pathname.match(/^\/v1\/admin\/licenses\/([^/]+)\/note$/);
      if (request.method === "PATCH" && noteMatch) return adminUpdateLicenseNote(request, env, id, decodeURIComponent(noteMatch[1]));
      const deleteMatch = url.pathname.match(/^\/v1\/admin\/licenses\/([^/]+)$/);
      if (request.method === "DELETE" && deleteMatch) return adminDeleteLicense(request, env, id, decodeURIComponent(deleteMatch[1]));
      return failure("NOT_FOUND", "接口不存在", id, 404);
    } catch (error) {
      console.error("worker request failed", error instanceof Error ? error.message : "unknown");
      return failure("INTERNAL_ERROR", "服务暂时不可用，请稍后重试", id, 500);
    }
  },
};
