import { createClient, type SupabaseClient } from "https://esm.sh/@supabase/supabase-js@2.57.0";
import privateSkinIndexData from "./private-skin-index.json" with { type: "json" };

type Database = SupabaseClient<any, "license">;

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

interface DeviceRow {
  device_id: string;
  license_id: string;
  public_key_spki: string | null;
  public_key_hash: string;
  status: "active" | "unbound" | "revoked";
}

interface LeaseRow {
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

interface PrivateSkinIndexItem {
  id: number;
  path: string;
  name?: string;
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
  overrides_json: Record<string, PrivateSkinOverride>;
  names_json: Record<string, string> | null;
  last_checked_at: number;
  updated_at: number;
  last_error: string | null;
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

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type, x-request-id, x-sync-secret, x-cskin-license-id, x-cskin-lease-id, x-cskin-lease-token, x-cskin-device-id",
  "Access-Control-Allow-Methods": "GET, POST, OPTIONS",
};
const jsonHeaders = {
  ...corsHeaders,
  "Content-Type": "application/json; charset=utf-8",
  "Cache-Control": "no-store",
};

const LEASE_SECONDS = 6 * 60 * 60;
const OFFLINE_GRACE_SECONDS = 24 * 60 * 60;
const MAX_BODY_BYTES = 64 * 1024;
const SIGNATURE_CLOCK_SKEW_SECONDS = 5 * 60;
const REQUEST_NONCE_TTL_SECONDS = 10 * 60;
const DEVICE_KEY_RECOVERY_COOLDOWN_SECONDS = 7 * 24 * 60 * 60;
const SYNC_MAX_ITEMS = 5000;
const PRIVATE_SKIN_REFRESH_SECONDS = 5 * 60;
const CLOUDFLARE_SYNC_URL = Deno.env.get("CLOUDFLARE_SYNC_URL") || "https://license.re2347.ccwu.cc/internal/sync/apply";
const BUILT_IN_PRIVATE_SKIN_INDEX: PrivateSkinIndex = privateSkinIndexData;

let cachedDatabase: Database | null | undefined;

function nowSeconds(): number {
  return Math.floor(Date.now() / 1000);
}

function uuid(): string {
  return crypto.randomUUID();
}

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: jsonHeaders });
}

function failure(code: string, message: string, requestIdValue: string, status: number): Response {
  return json({ error: { code, message }, requestId: requestIdValue }, status);
}

function requestId(request: Request, supplied?: unknown): string {
  if (isNonEmptyString(supplied, 100)) return supplied.trim();
  return request.headers.get("x-request-id")?.slice(0, 100) || uuid();
}

function isNonEmptyString(value: unknown, maxLength: number): value is string {
  return typeof value === "string" && value.trim().length > 0 && value.length <= maxLength;
}

function base64Url(bytes: ArrayBuffer | Uint8Array): string {
  const data = bytes instanceof Uint8Array ? bytes : new Uint8Array(bytes);
  let binary = "";
  for (const byte of data) binary += String.fromCharCode(byte);
  return btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/g, "");
}

function base64UrlBytes(value: string): Uint8Array<ArrayBuffer> {
  const normalized = value.replace(/-/g, "+").replace(/_/g, "/") + "=".repeat((4 - (value.length % 4)) % 4);
  const binary = atob(normalized);
  const bytes = new Uint8Array(new ArrayBuffer(binary.length));
  for (let index = 0; index < binary.length; index += 1) bytes[index] = binary.charCodeAt(index);
  return bytes;
}

function randomToken(byteLength = 32): string {
  return base64Url(crypto.getRandomValues(new Uint8Array(byteLength)));
}

function normalizeCode(value: string): string {
  return value.replace(/[^a-z0-9]/gi, "").toUpperCase();
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

function constantTimeEqual(left: string, right: string): boolean {
  const leftBytes = new TextEncoder().encode(left);
  const rightBytes = new TextEncoder().encode(right);
  const length = Math.max(leftBytes.length, rightBytes.length);
  let difference = leftBytes.length ^ rightBytes.length;
  for (let index = 0; index < length; index += 1) difference |= (leftBytes[index] || 0) ^ (rightBytes[index] || 0);
  return difference === 0;
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
      base64UrlBytes(publicKeySpki),
      { name: "ECDSA", namedCurve: "P-256" },
      false,
      ["verify"],
    );
    return await crypto.subtle.verify(
      { name: "ECDSA", hash: "SHA-256" },
      key,
      base64UrlBytes(signature),
      new TextEncoder().encode(payload),
    );
  } catch {
    return false;
  }
}

async function readJson<T>(request: Request): Promise<T> {
  const contentLength = Number(request.headers.get("content-length") || 0);
  if (contentLength > MAX_BODY_BYTES) throw new Error("REQUEST_TOO_LARGE");
  const text = await request.text();
  if (new TextEncoder().encode(text).byteLength > MAX_BODY_BYTES) throw new Error("REQUEST_TOO_LARGE");
  return JSON.parse(text) as T;
}

function database(): Database | null {
  if (cachedDatabase !== undefined) return cachedDatabase;
  const url = Deno.env.get("SUPABASE_URL") || Deno.env.get("SUPABASE_PROJECT_URL");
  const serviceRoleKey = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY");
  if (!url || !serviceRoleKey) return (cachedDatabase = null);
  return (cachedDatabase = createClient(url, serviceRoleKey, {
    db: { schema: "license" },
    auth: { autoRefreshToken: false, detectSessionInUrl: false, persistSession: false },
  }));
}

function pepper(): string | null {
  const value = Deno.env.get("LICENSE_PEPPER");
  return value && value.length >= 16 ? value : null;
}

function privateRepositoryConfig(): { token: string; owner: string; repository: string; ref: string; label: string } | null {
  const token = Deno.env.get("GITCODE_TOKEN")?.trim() || "";
  const owner = Deno.env.get("GITCODE_OWNER")?.trim() || "";
  const repository = Deno.env.get("GITCODE_REPOSITORY")?.trim() || "";
  const ref = Deno.env.get("GITCODE_REF")?.trim() || "";
  const label = Deno.env.get("SKIN_REPO_LABEL")?.trim() || "";
  const identifier = /^[A-Za-z0-9._-]{1,100}$/;
  return token && identifier.test(owner) && identifier.test(repository) && identifier.test(ref) && identifier.test(label)
    ? { token, owner, repository, ref, label }
    : null;
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

function skinIdFromPath(path: string): number | null {
  if (!isSafeSkinPath(path)) return null;
  const skinId = Number(path.split("/").at(-1)?.replace(/\.fantome$/i, "") || 0);
  return Number.isSafeInteger(skinId) && skinId > 0 ? skinId : null;
}

function gitCodeRawUrl(config: { owner: string; repository: string; ref: string }, path: string): string {
  const encodedPath = path.split("/").map(encodeURIComponent).join("/");
  return `https://api.gitcode.com/api/v5/repos/${encodeURIComponent(config.owner)}/${encodeURIComponent(config.repository)}/raw/${encodedPath}?ref=${encodeURIComponent(config.ref)}`;
}

function gitCodeHeaders(config: { token: string }, accept = "application/json"): HeadersInit {
  return {
    "PRIVATE-TOKEN": config.token,
    "Authorization": `Bearer ${config.token}`,
    "User-Agent": "Cskin-Private-Skin-Supabase/3.0",
    "Accept": accept,
  };
}

async function fetchPrivateRepositoryFile(
  config: { token: string; owner: string; repository: string; ref: string },
  path: string,
): Promise<Response> {
  return await fetch(gitCodeRawUrl(config, path), { headers: gitCodeHeaders(config, "application/octet-stream, application/json") });
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

async function fetchGitCodeJson<T>(url: string, config: { token: string }): Promise<T> {
  const response = await fetch(url, { headers: gitCodeHeaders(config) });
  if (!response.ok) throw new Error(`GITCODE_HTTP_${response.status}`);
  return await readBoundedJson<T>(response, 8 * 1024 * 1024);
}

function gitCodeBranchUrl(config: { owner: string; repository: string; ref: string }): string {
  return `https://api.gitcode.com/api/v5/repos/${encodeURIComponent(config.owner)}/${encodeURIComponent(config.repository)}/branches/${encodeURIComponent(config.ref)}`;
}

function gitCodeCompareUrl(config: { owner: string; repository: string }, from: string, to: string): string {
  return `https://api.gitcode.com/api/v5/repos/${encodeURIComponent(config.owner)}/${encodeURIComponent(config.repository)}/compare/${encodeURIComponent(from)}...${encodeURIComponent(to)}`;
}

async function ensurePrivateSkinCatalogState(db: Database): Promise<PrivateSkinCatalogState> {
  const existing = await db.from("private_skin_catalog_state").select("*").eq("state_id", 1).maybeSingle<PrivateSkinCatalogState>();
  if (existing.error) throw existing.error;
  if (existing.data) return existing.data;
  const now = nowSeconds();
  const inserted = await db.from("private_skin_catalog_state").insert({
    state_id: 1,
    current_revision: BUILT_IN_PRIVATE_SKIN_INDEX.revision,
    overrides_json: {},
    names_json: null,
    last_checked_at: 0,
    updated_at: now,
    last_error: null,
  }).select("*").single<PrivateSkinCatalogState>();
  if (inserted.error || !inserted.data) throw inserted.error || new Error("SKIN_CATALOG_STATE_CREATE_FAILED");
  return inserted.data;
}

function buildPrivateSkinIndex(state?: PrivateSkinCatalogState | null): PrivateSkinIndex {
  const skins = new Map<number, PrivateSkinIndexItem>(
    BUILT_IN_PRIVATE_SKIN_INDEX.skins.map((skin) => [skin.id, { ...skin }]),
  );
  const overrides = state?.overrides_json || {};
  const names = state?.names_json || {};
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
      name: names[idValue] || current?.name || "",
      blobSha: override.blobSha || current?.blobSha,
    });
  }
  for (const [idValue, name] of Object.entries(names)) {
    const skin = skins.get(Number(idValue));
    if (skin && typeof name === "string" && name.trim()) skin.name = name.trim();
  }
  return {
    ...BUILT_IN_PRIVATE_SKIN_INDEX,
    revision: state?.current_revision || BUILT_IN_PRIVATE_SKIN_INDEX.revision,
    generatedAt: state?.updated_at ? new Date(state.updated_at * 1000).toISOString() : BUILT_IN_PRIVATE_SKIN_INDEX.generatedAt,
    skins: [...skins.values()].sort((left, right) => left.id - right.id || left.path.localeCompare(right.path)),
  };
}

async function recordCatalogRefreshFailure(db: Database, state: PrivateSkinCatalogState, error: string): Promise<void> {
  await db.from("private_skin_catalog_state").update({
    last_checked_at: nowSeconds(),
    last_error: error.slice(0, 500),
  }).eq("state_id", 1);
  console.error(JSON.stringify({ event: "private_skin_catalog_refresh_failed", revision: state.current_revision, error }));
}

async function refreshPrivateSkinCatalog(db: Database, force = false): Promise<PrivateSkinCatalogState> {
  const config = privateRepositoryConfig();
  if (!config) throw new Error("SKIN_REPOSITORY_NOT_CONFIGURED");
  const state = await ensurePrivateSkinCatalogState(db);
  const now = nowSeconds();
  if (!force && now - Number(state.last_checked_at || 0) < PRIVATE_SKIN_REFRESH_SECONDS) return state;
  try {
    const branch = await fetchGitCodeJson<{ commit?: { id?: string } }>(gitCodeBranchUrl(config), config);
    const nextRevision = branch.commit?.id?.trim() || "";
    if (!/^[0-9a-f]{40}$/i.test(nextRevision)) throw new Error("GITCODE_REVISION_INVALID");
    if (nextRevision === state.current_revision) {
      const updated = { ...state, last_checked_at: now, last_error: null };
      const result = await db.from("private_skin_catalog_state").update({ last_checked_at: now, last_error: null }).eq("state_id", 1);
      if (result.error) throw result.error;
      return updated;
    }

    const compared = await fetchGitCodeJson<GitCodeCompareResponse>(
      gitCodeCompareUrl(config, state.current_revision, nextRevision),
      config,
    );
    if (compared.truncated) throw new Error("GITCODE_COMPARE_TRUNCATED");
    const overrides: Record<string, PrivateSkinOverride> = { ...(state.overrides_json || {}) };
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

    let names = state.names_json;
    const namesResponse = await fetchPrivateRepositoryFile(config, "resources/zh/skin_ids.json");
    if (namesResponse.ok) {
      names = await readBoundedJson<Record<string, string>>(namesResponse, 4 * 1024 * 1024);
    }
    const updated: PrivateSkinCatalogState = {
      ...state,
      current_revision: nextRevision,
      overrides_json: overrides,
      names_json: names,
      last_checked_at: now,
      updated_at: now,
      last_error: null,
    };
    const saved = await db.from("private_skin_catalog_state").update(updated).eq("state_id", 1);
    if (saved.error) throw saved.error;
    console.log(JSON.stringify({ event: "private_skin_catalog_refreshed", from: state.current_revision, to: nextRevision, changedFiles: (compared.files || []).length }));
    return updated;
  } catch (error) {
    await recordCatalogRefreshFailure(db, state, error instanceof Error ? error.message : "unknown");
    return state;
  }
}

async function authorizeSkinRequest(request: Request, requestIdValue: string): Promise<{ db: Database; config: NonNullable<ReturnType<typeof privateRepositoryConfig>> } | Response> {
  const db = database();
  const secret = pepper();
  const config = privateRepositoryConfig();
  if (!db || !secret) return failure("DB_NOT_CONFIGURED", "Supabase 授权服务尚未完成配置", requestIdValue, 503);
  if (!config) return failure("SKIN_REPOSITORY_NOT_CONFIGURED", "私有皮肤资源服务尚未完成配置", requestIdValue, 503);
  const licenseId = request.headers.get("x-cskin-license-id")?.trim() || "";
  const leaseId = request.headers.get("x-cskin-lease-id")?.trim() || "";
  const leaseToken = request.headers.get("x-cskin-lease-token")?.trim() || "";
  const deviceId = request.headers.get("x-cskin-device-id")?.trim() || "";
  if (![licenseId, leaseId, leaseToken, deviceId].every((value) => value.length > 0 && value.length <= 500)) {
    return failure("SKIN_AUTH_REQUIRED", "皮肤资源请求需要有效授权", requestIdValue, 401);
  }
  const tokenHash = await hmacHex(secret, `lease:${leaseToken}`);
  const [license, lease, device] = await Promise.all([
    db.from("licenses").select("status, expires_at, bound_device_id").eq("license_id", licenseId).maybeSingle<{ status: LicenseRow["status"]; expires_at: number | null; bound_device_id: string | null }>(),
    db.from("leases").select("device_id, expires_at, revoked_at").eq("lease_id", leaseId).eq("license_id", licenseId).eq("token_hash", tokenHash).maybeSingle<{ device_id: string; expires_at: number; revoked_at: number | null }>(),
    db.from("devices").select("status").eq("license_id", licenseId).eq("device_id", deviceId).maybeSingle<{ status: string }>(),
  ]);
  if (license.error || lease.error || device.error) return failure("SKIN_AUTH_UNAVAILABLE", "皮肤资源授权校验暂时不可用", requestIdValue, 503);
  const now = nowSeconds();
  const allowed = license.data?.status === "active"
    && license.data.expires_at !== null
    && license.data.expires_at > now
    && license.data.bound_device_id === deviceId
    && lease.data?.device_id === deviceId
    && lease.data.expires_at > now
    && lease.data.revoked_at === null
    && device.data?.status === "active";
  if (!allowed) {
    await writeLog(db, secret, request, "skin_resource_denied", false, requestIdValue, licenseId, deviceId);
    return failure("SKIN_AUTH_INVALID", "皮肤资源授权已失效，请重新验证密钥", requestIdValue, 403);
  }
  return { db, config };
}

async function privateSkinIndex(request: Request, requestIdValue: string): Promise<Response> {
  const authorization = await authorizeSkinRequest(request, requestIdValue);
  if (authorization instanceof Response) return authorization;
  const state = await refreshPrivateSkinCatalog(authorization.db);
  const index = buildPrivateSkinIndex(state);
  return new Response(JSON.stringify(index), {
    status: 200,
    headers: {
      ...corsHeaders,
      "Content-Type": "application/json; charset=utf-8",
      "Cache-Control": "private, max-age=300",
      "X-Cskin-Revision": index.revision,
      "X-Cskin-Gateway": "supabase",
      "X-Cskin-Upstream": "gitcode",
      "X-Content-Type-Options": "nosniff",
    },
  });
}

async function privateSkinFile(request: Request, requestIdValue: string): Promise<Response> {
  const authorization = await authorizeSkinRequest(request, requestIdValue);
  if (authorization instanceof Response) return authorization;
  const url = new URL(request.url);
  const skinId = Number(url.searchParams.get("skinId") || 0);
  const legacyPath = url.searchParams.get("path")?.trim() || "";
  const state = await ensurePrivateSkinCatalogState(authorization.db);
  const index = buildPrivateSkinIndex(state);
  const item = Number.isSafeInteger(skinId) && skinId > 0
    ? index.skins.find((skin) => skin.id === skinId)
    : index.skins.find((skin) => skin.path === legacyPath);
  if (!item) return failure("SKIN_NOT_FOUND", "私有皮肤索引中没有该资源", requestIdValue, 404);
  const upstream = await fetchPrivateRepositoryFile(authorization.config, item.path);
  if (!upstream.ok || !upstream.body) {
    console.error(JSON.stringify({ event: "private_skin_file_upstream_failed", status: upstream.status, requestId: requestIdValue, skinId: item.id }));
    return failure(upstream.status === 404 ? "SKIN_NOT_FOUND" : "SKIN_FILE_UNAVAILABLE", "私有皮肤资源暂时不可用", requestIdValue, upstream.status === 404 ? 404 : 502);
  }
  const fileName = item.path.split("/").at(-1) || "skin.fantome";
  const headers = new Headers({
    ...corsHeaders,
    "Content-Type": "application/octet-stream",
    "Content-Disposition": `attachment; filename="${fileName}"`,
    "Cache-Control": "private, no-store",
    "X-Cskin-Revision": index.revision,
    "X-Cskin-Gateway": "supabase",
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

interface SyncPayload {
  licenseId: string;
  changedAt: number;
  version: number;
  deleted?: boolean;
  license?: LicenseRow;
  devices?: DeviceRow[];
  leases?: LeaseRow[];
}

function syncAuthorized(request: Request): boolean {
  const secret = Deno.env.get("AUTH_SYNC_SECRET");
  return Boolean(secret && secret.length >= 32 && constantTimeEqual(request.headers.get("x-sync-secret") || "", secret));
}

async function syncSnapshot(db: Database, licenseId: string, changedAt = 0): Promise<SyncPayload> {
  const licenseResult = await db.from("licenses").select("*").eq("license_id", licenseId).maybeSingle<LicenseRow>();
  if (licenseResult.error) throw licenseResult.error;
  if (!licenseResult.data) {
    const tombstone = await db.from("sync_tombstones").select("license_id, changed_at, version").eq("license_id", licenseId).maybeSingle<{ license_id: string; changed_at: number; version: number }>();
    return { licenseId, changedAt: Number(tombstone.data?.changed_at || changedAt), version: Number(tombstone.data?.version || 0), deleted: true };
  }
  const [devices, leases] = await Promise.all([
    db.from("devices").select("*").eq("license_id", licenseId),
    db.from("leases").select("*").eq("license_id", licenseId),
  ]);
  if (devices.error) throw devices.error;
  if (leases.error) throw leases.error;
  const license = licenseResult.data;
  return {
    licenseId,
    changedAt: Math.max(Number(changedAt), Number(license.updated_at || license.created_at || 0)),
    version: Number(license.version || 0),
    license,
    devices: devices.data || [],
    leases: leases.data || [],
  };
}

function compareSyncFreshness(existing: LicenseRow | null, existingTombstone: { changed_at: number; version: number } | null, incoming: SyncPayload): -1 | 0 | 1 {
  const localTime = Number(existingTombstone?.changed_at || existing?.updated_at || existing?.created_at || 0);
  const localVersion = Number(existingTombstone?.version || existing?.version || 0);
  const incomingTime = Number(incoming.changedAt || incoming.license?.updated_at || incoming.license?.created_at || 0);
  if (incomingTime !== localTime) return incomingTime > localTime ? 1 : -1;
  const incomingVersion = Number(incoming.version || incoming.license?.version || 0);
  return incomingVersion === localVersion ? 0 : incomingVersion > localVersion ? 1 : -1;
}

async function applySyncPayload(db: Database, payload: SyncPayload): Promise<Response> {
  const existingResult = await db.from("licenses").select("*").eq("license_id", payload.licenseId).maybeSingle<LicenseRow>();
  if (existingResult.error) return json({ ok: false, error: "LICENSE_READ_FAILED" }, 503);
  const tombstoneResult = await db.from("sync_tombstones").select("changed_at, version").eq("license_id", payload.licenseId).maybeSingle<{ changed_at: number; version: number }>();
  if (tombstoneResult.error && tombstoneResult.error.code !== "PGRST116") return json({ ok: false, error: "TOMBSTONE_READ_FAILED" }, 503);
  const freshness = compareSyncFreshness(existingResult.data, tombstoneResult.data, payload);
  if (freshness === 0) {
    return json({ ok: true, action: "equal" });
  }
  if (freshness < 0) {
    return json({ ok: true, action: "remote_newer", current: await syncSnapshot(db, payload.licenseId) });
  }
  if (payload.deleted || !payload.license) {
    // Respect the lease -> device -> license foreign-key order. Concurrent
    // deletes can race in PostgREST and leave an otherwise valid sync queued.
    const leases = await db.from("leases").delete().eq("license_id", payload.licenseId);
    if (leases.error) return json({ ok: false, error: "LICENSE_DELETE_SYNC_FAILED" }, 503);
    const devices = await db.from("devices").delete().eq("license_id", payload.licenseId);
    if (devices.error) return json({ ok: false, error: "LICENSE_DELETE_SYNC_FAILED" }, 503);
    const license = await db.from("licenses").delete().eq("license_id", payload.licenseId);
    if (license.error) return json({ ok: false, error: "LICENSE_DELETE_SYNC_FAILED" }, 503);
    const tombstone = await db.from("sync_tombstones").upsert({
      license_id: payload.licenseId,
      changed_at: payload.changedAt,
      version: payload.version,
    });
    if (tombstone.error) return json({ ok: false, error: "LICENSE_DELETE_SYNC_FAILED" }, 503);
    return json({ ok: true, action: "applied" });
  }
  const license = { ...payload.license, updated_at: Number(payload.license.updated_at || payload.changedAt) };
  const licenseResult = await db.from("licenses").upsert(license, { onConflict: "license_id" });
  if (licenseResult.error) return json({ ok: false, error: "LICENSE_UPSERT_FAILED" }, 503);
  const leases = await db.from("leases").delete().eq("license_id", payload.licenseId);
  if (leases.error) return json({ ok: false, error: "LICENSE_GRAPH_CLEAR_FAILED" }, 503);
  const devices = await db.from("devices").delete().eq("license_id", payload.licenseId);
  if (devices.error) return json({ ok: false, error: "LICENSE_GRAPH_CLEAR_FAILED" }, 503);
  const tombstone = await db.from("sync_tombstones").delete().eq("license_id", payload.licenseId);
  if (tombstone.error) return json({ ok: false, error: "LICENSE_GRAPH_CLEAR_FAILED" }, 503);
  if (payload.devices?.length) {
    const result = await db.from("devices").insert(payload.devices);
    if (result.error) return json({ ok: false, error: "DEVICE_UPSERT_FAILED" }, 503);
  }
  if (payload.leases?.length) {
    const result = await db.from("leases").insert(payload.leases);
    if (result.error) return json({ ok: false, error: "LEASE_UPSERT_FAILED" }, 503);
  }
  return json({ ok: true, action: "applied" });
}

async function syncExport(db: Database): Promise<Response> {
  const licenses = await db.from("licenses").select("*").limit(SYNC_MAX_ITEMS);
  if (licenses.error) return json({ ok: false, error: "LICENSE_EXPORT_FAILED" }, 503);
  const items: SyncPayload[] = [];
  for (const license of licenses.data || []) items.push(await syncSnapshot(db, license.license_id));
  const tombstones = await db.from("sync_tombstones").select("license_id, changed_at, version").order("changed_at", { ascending: true }).limit(SYNC_MAX_ITEMS);
  if (tombstones.error) return json({ ok: false, error: "TOMBSTONE_EXPORT_FAILED" }, 503);
  for (const tombstone of tombstones.data || []) items.push({ licenseId: tombstone.license_id, changedAt: Number(tombstone.changed_at), version: Number(tombstone.version), deleted: true });
  return json({ ok: true, items });
}

async function touchLicense(db: Database, licenseId: string): Promise<void> {
  const current = await db.from("licenses").select("version").eq("license_id", licenseId).maybeSingle<{ version: number | null }>();
  const nextVersion = Number(current.data?.version || 0) + 1;
  await db.from("licenses").update({ version: nextVersion, updated_at: Date.now() }).eq("license_id", licenseId);
}

async function syncToCloudflare(db: Database, licenseId: string): Promise<void> {
  const secret = Deno.env.get("AUTH_SYNC_SECRET");
  if (!secret || secret.length < 32) return;
  try {
    const payload = await syncSnapshot(db, licenseId);
    const response = await fetch(CLOUDFLARE_SYNC_URL, {
      method: "POST",
      headers: { "content-type": "application/json", "x-sync-secret": secret },
      body: JSON.stringify(payload),
    });
    if (!response.ok) console.error("cloudflare sync rejected", response.status);
  } catch (error) {
    console.error("cloudflare sync failed", error instanceof Error ? error.message : "unknown");
  }
}

async function claimRequestId(db: Database, requestIdValue: string, now: number): Promise<"claimed" | "replayed" | "error"> {
  const { error } = await db.from("request_nonces").insert({
    request_id: requestIdValue,
    seen_at: now,
    expires_at: now + REQUEST_NONCE_TTL_SECONDS,
  });
  if (!error) return "claimed";
  if (error.code === "23505") return "replayed";
  return "error";
}

async function writeLog(
  db: Database,
  secret: string,
  request: Request,
  event: string,
  success: boolean,
  requestIdValue: string,
  licenseId?: string,
  deviceId?: string,
  details?: Record<string, unknown>,
): Promise<void> {
  try {
    const ip = request.headers.get("x-forwarded-for")?.split(",")[0]?.trim() || "";
    const userAgent = request.headers.get("user-agent") || "";
    const [ipHash, userAgentHash] = await Promise.all([
      hmacHex(secret, `ip:${ip}`),
      hmacHex(secret, `ua:${userAgent}`),
    ]);
    await db.from("verification_logs").insert({
      log_id: uuid(),
      request_id: requestIdValue,
      license_id: licenseId || null,
      device_id: deviceId || null,
      event,
      success,
      ip_hash: ipHash,
      user_agent_hash: userAgentHash,
      created_at: nowSeconds(),
      details_json: details || null,
    });
  } catch {
    // Logging is best effort and must not turn a valid authorization into an error.
  }
}

function leaseResponse(license: LicenseRow, leaseId: string, leaseToken: string, leaseExpiresAt: number, graceUntil: number, now: number) {
  return {
    ok: true,
    licenseId: license.license_id,
    planDays: license.plan_days,
    status: license.status,
    activatedAt: license.activated_at,
    expiresAt: license.expires_at,
    leaseId,
    leaseToken,
    leaseExpiresAt,
    graceUntil,
    serverTime: now,
  };
}

async function createLease(db: Database, secret: string, license: LicenseRow, deviceId: string, clientVersion: string | null, now: number) {
  if (!license.expires_at || license.expires_at <= now) throw new Error("LICENSE_EXPIRED");
  const leaseId = uuid();
  const leaseToken = randomToken();
  const leaseExpiresAt = Math.min(license.expires_at, now + LEASE_SECONDS);
  const graceUntil = Math.min(license.expires_at, now + OFFLINE_GRACE_SECONDS);
  const tokenHash = await hmacHex(secret, `lease:${leaseToken}`);
  const { error } = await db.from("leases").insert({
    lease_id: leaseId,
    license_id: license.license_id,
    device_id: deviceId,
    token_hash: tokenHash,
    issued_at: now,
    expires_at: leaseExpiresAt,
    grace_until: graceUntil,
    client_version: clientVersion,
  });
  if (error) throw error;
  return { leaseId, leaseToken, leaseExpiresAt, graceUntil };
}

async function migrateLegacyDeviceBinding(db: Database, license: LicenseRow, nextDeviceId: string, publicKeyHash: string, now: number): Promise<"migrated" | "not_found" | "error"> {
  const previous = await db.from("devices").select("device_id").eq("license_id", license.license_id).eq("public_key_hash", publicKeyHash).eq("status", "active").maybeSingle<{ device_id: string }>();
  if (previous.error) return "error";
  if (!previous.data) return "not_found";
  const leases = await db.from("leases").delete().eq("license_id", license.license_id);
  if (leases.error) return "error";
  const device = await db.from("devices").update({ device_id: nextDeviceId, last_seen_at: now, status: "active", unbound_at: null }).eq("license_id", license.license_id).eq("device_id", previous.data.device_id).eq("public_key_hash", publicKeyHash);
  if (device.error) return "error";
  const licenseUpdate = await db.from("licenses").update({ bound_device_id: nextDeviceId, version: (license.version ?? 0) + 1 }).eq("license_id", license.license_id).eq("status", "active").eq("bound_device_id", license.bound_device_id);
  if (licenseUpdate.error) return "error";
  const refreshed = await db.from("licenses").select("bound_device_id").eq("license_id", license.license_id).maybeSingle<{ bound_device_id: string | null }>();
  if (refreshed.data?.bound_device_id === nextDeviceId) await touchLicense(db, license.license_id);
  return refreshed.data?.bound_device_id === nextDeviceId ? "migrated" : "error";
}

async function canRotateDeviceKey(db: Database, licenseId: string, now: number): Promise<boolean> {
  const latest = await db.from("verification_logs").select("created_at").eq("license_id", licenseId).eq("event", "device_key_rotated").eq("success", true).order("created_at", { ascending: false }).limit(1).maybeSingle<{ created_at: number }>();
  return !latest.data || latest.data.created_at <= now - DEVICE_KEY_RECOVERY_COOLDOWN_SECONDS;
}

async function activate(request: Request, requestIdValue: string): Promise<Response> {
  const db = database();
  const secret = pepper();
  if (!db) return failure("DB_NOT_CONFIGURED", "Supabase 数据库尚未配置 service_role 密钥", requestIdValue, 503);
  if (!secret) return failure("SERVER_NOT_CONFIGURED", "Supabase 授权服务尚未配置 LICENSE_PEPPER", requestIdValue, 503);

  let body: ActivateBody;
  try {
    body = await readJson<ActivateBody>(request);
  } catch (error) {
    return failure(error instanceof Error && error.message === "REQUEST_TOO_LARGE" ? "REQUEST_TOO_LARGE" : "INVALID_JSON", "请求体无效", requestIdValue, 400);
  }
  if (!isNonEmptyString(body.code, 100) || !isNonEmptyString(body.deviceId, 200))
    return failure("INVALID_REQUEST", "需要提供 code 和 deviceId", requestIdValue, 400);
  if (body.protocolVersion !== "2" || !isNonEmptyString(body.requestId, 100)
      || !Number.isSafeInteger(body.signatureTimestamp) || !isNonEmptyString(body.signature, 1000)
      || !isNonEmptyString(body.devicePublicKey, 10000))
    return failure("INVALID_SIGNATURE", "客户端签名信息无效，请更新软件后重试", requestIdValue, 401);

  const now = nowSeconds();
  if (!await verifyDeviceSignature(body.devicePublicKey, body.signature, body.signatureTimestamp, activateSigningPayload(body), now)) {
    await writeLog(db, secret, request, "activate", false, requestIdValue, undefined, body.deviceId, { reason: "invalid_signature" });
    return failure("INVALID_SIGNATURE", "设备签名校验失败，请更新软件后重试", requestIdValue, 401);
  }

  const deviceId = body.deviceId.trim();
  const normalizedCode = normalizeCode(body.code);
  if (normalizedCode.length < 16) return failure("INVALID_CODE", "激活码格式无效", requestIdValue, 400);
  const codeHash = await hmacHex(secret, `license:${normalizedCode}`);
  let { data: license, error } = await db.from("licenses").select("*").eq("code_hash", codeHash).maybeSingle<LicenseRow>();
  if (error) return failure("DATABASE_ERROR", "授权数据库查询失败，请稍后重试", requestIdValue, 503);
  if (!license) {
    await writeLog(db, secret, request, "activate", false, requestIdValue, undefined, deviceId, { reason: "not_found" });
    return failure("LICENSE_NOT_FOUND", "激活码不存在或已失效", requestIdValue, 404);
  }
  const nonce = await claimRequestId(db, body.requestId.trim(), now);
  if (nonce === "replayed") return failure("REPLAYED_REQUEST", "请求已使用，请重试", requestIdValue, 409);
  if (nonce === "error") return failure("DATABASE_ERROR", "请求状态保存失败，请稍后重试", requestIdValue, 503);
  if (license.status === "revoked") return failure("LICENSE_REVOKED", "激活码已被撤销", requestIdValue, 403);
  if (license.expires_at && license.expires_at <= now && license.status !== "unused") {
    await db.from("licenses").update({ status: "expired", version: (license.version ?? 0) + 1 }).eq("license_id", license.license_id);
    await touchLicense(db, license.license_id);
    await syncToCloudflare(db, license.license_id);
    return failure("LICENSE_EXPIRED", "激活码已过期", requestIdValue, 403);
  }

  if (license.status === "unused") {
    const expiresAt = now + license.plan_days * 24 * 60 * 60;
    const claimed = await db.from("licenses").update({ status: "active", bound_device_id: deviceId, activated_at: now, expires_at: expiresAt, version: (license.version ?? 0) + 1 }).eq("license_id", license.license_id).eq("status", "unused").is("bound_device_id", null).select("*").maybeSingle<LicenseRow>();
    if (claimed.error) return failure("DATABASE_ERROR", "授权状态更新失败，请稍后重试", requestIdValue, 503);
    if (claimed.data) license = claimed.data;
    else {
      const refreshed = await db.from("licenses").select("*").eq("license_id", license.license_id).single<LicenseRow>();
      if (refreshed.error || !refreshed.data) return failure("DATABASE_ERROR", "授权状态读取失败，请稍后重试", requestIdValue, 503);
      license = refreshed.data;
    }
  }
  const publicKey = body.devicePublicKey.trim();
  const publicKeyHash = await sha256Hex(publicKey);
  let bindingMigrated = false;
  let deviceKeyRotated = false;
  if (license.status !== "active" || license.bound_device_id !== deviceId) {
    const migration = license.status === "active" ? await migrateLegacyDeviceBinding(db, license, deviceId, publicKeyHash, now) : "not_found";
    if (migration !== "migrated") {
      await writeLog(db, secret, request, "activate", false, requestIdValue, license.license_id, deviceId, { reason: "bound_other_device" });
      return failure("LICENSE_ALREADY_BOUND", "激活码已绑定其他设备", requestIdValue, 409);
    }
    license = { ...license, bound_device_id: deviceId };
    bindingMigrated = true;
  }

  const existing = await db.from("devices").select("device_id, license_id, public_key_spki, public_key_hash, status").eq("device_id", deviceId).eq("license_id", license.license_id).maybeSingle<DeviceRow>();
  if (existing.error) return failure("DATABASE_ERROR", "设备信息读取失败，请稍后重试", requestIdValue, 503);
  if (existing.data && !constantTimeEqual(existing.data.public_key_hash, publicKeyHash)) {
    if (!await canRotateDeviceKey(db, license.license_id, now)) {
      await writeLog(db, secret, request, "activate", false, requestIdValue, license.license_id, deviceId, { reason: "device_key_recovery_cooldown" });
      return failure("DEVICE_KEY_RECOVERY_COOLDOWN", "设备密钥刚刚恢复过，请 7 天后重试或联系管理员", requestIdValue, 409);
    }
    const [deviceUpdate, leases] = await Promise.all([
      db.from("devices").update({ public_key_spki: publicKey, public_key_hash: publicKeyHash, last_seen_at: now, status: "active", app_version: body.clientVersion || null }).eq("device_id", deviceId).eq("license_id", license.license_id),
      db.from("leases").update({ revoked_at: now }).eq("license_id", license.license_id).is("revoked_at", null),
    ]);
    if (deviceUpdate.error || leases.error) return failure("DATABASE_ERROR", "设备密钥更新失败，请稍后重试", requestIdValue, 503);
    deviceKeyRotated = true;
  }
  const devicePayload = { device_id: deviceId, license_id: license.license_id, public_key_spki: publicKey, public_key_hash: publicKeyHash, first_seen_at: now, last_seen_at: now, status: "active", app_version: body.clientVersion || null };
  const deviceResult = existing.data && !deviceKeyRotated
    ? await db.from("devices").update({ public_key_spki: publicKey, public_key_hash: publicKeyHash, last_seen_at: now, status: "active", app_version: body.clientVersion || null }).eq("device_id", deviceId).eq("license_id", license.license_id)
    : existing.data ? { error: null } : await db.from("devices").insert(devicePayload);
  if (deviceResult.error) return failure("DATABASE_ERROR", "设备信息保存失败，请稍后重试", requestIdValue, 503);

  try {
    const lease = await createLease(db, secret, license, deviceId, body.clientVersion || null, now);
    await touchLicense(db, license.license_id);
    await syncToCloudflare(db, license.license_id);
    const event = deviceKeyRotated ? "device_key_rotated" : bindingMigrated ? "device_binding_migrated" : "activate";
    await writeLog(db, secret, request, event, true, requestIdValue, license.license_id, deviceId);
    return json({ ...leaseResponse(license, lease.leaseId, lease.leaseToken, lease.leaseExpiresAt, lease.graceUntil, now), requestId: requestIdValue });
  } catch {
    return failure("LEASE_CREATE_FAILED", "授权租约创建失败，请重试", requestIdValue, 503);
  }
}

async function verify(request: Request, requestIdValue: string, event: "verify" | "heartbeat"): Promise<Response> {
  const db = database();
  const secret = pepper();
  if (!db) return failure("DB_NOT_CONFIGURED", "Supabase 数据库尚未配置 service_role 密钥", requestIdValue, 503);
  if (!secret) return failure("SERVER_NOT_CONFIGURED", "Supabase 授权服务尚未配置 LICENSE_PEPPER", requestIdValue, 503);

  let body: VerifyBody;
  try {
    body = await readJson<VerifyBody>(request);
  } catch {
    return failure("INVALID_JSON", "请求体无效", requestIdValue, 400);
  }
  if (!isNonEmptyString(body.licenseId, 100) || !isNonEmptyString(body.deviceId, 200)
      || !isNonEmptyString(body.leaseId, 100) || !isNonEmptyString(body.leaseToken, 500)
      || !isNonEmptyString(body.requestId, 100) || !Number.isSafeInteger(body.signatureTimestamp)
      || !isNonEmptyString(body.signature, 1000))
    return failure("INVALID_REQUEST", "需要提供 licenseId、deviceId、leaseId 和 leaseToken", requestIdValue, 400);

  const now = nowSeconds();
  const tokenHash = await hmacHex(secret, `lease:${body.leaseToken}`);
  const leaseResult = await db.from("leases").select("*").eq("lease_id", body.leaseId.trim()).eq("token_hash", tokenHash).eq("license_id", body.licenseId.trim()).maybeSingle<LeaseRow>();
  if (leaseResult.error || !leaseResult.data) {
    await writeLog(db, secret, request, event, false, requestIdValue, body.licenseId, body.deviceId, { reason: "invalid_lease" });
    return failure("INVALID_LEASE", "授权租约无效", requestIdValue, 401);
  }
  const lease = leaseResult.data;
  const [licenseResult, deviceResult] = await Promise.all([
    db.from("licenses").select("*").eq("license_id", lease.license_id).maybeSingle<LicenseRow>(),
    db.from("devices").select("device_id, license_id, public_key_spki, public_key_hash, status").eq("device_id", lease.device_id).eq("license_id", lease.license_id).maybeSingle<DeviceRow>(),
  ]);
  const license = licenseResult.data;
  const device = deviceResult.data;
  if (licenseResult.error || deviceResult.error || !license || !device
      || lease.device_id !== body.deviceId.trim() || license.bound_device_id !== body.deviceId.trim()) {
    await writeLog(db, secret, request, event, false, requestIdValue, body.licenseId, body.deviceId, { reason: "invalid_lease" });
    return failure("INVALID_LEASE", "授权租约无效", requestIdValue, 401);
  }
  if (!await verifyDeviceSignature(device.public_key_spki, body.signature, body.signatureTimestamp, verifySigningPayload(body), now)) {
    await writeLog(db, secret, request, event, false, requestIdValue, body.licenseId, body.deviceId, { reason: "invalid_signature" });
    return failure("INVALID_SIGNATURE", "设备签名校验失败，请更新软件后重试", requestIdValue, 401);
  }
  const nonce = await claimRequestId(db, body.requestId.trim(), now);
  if (nonce === "replayed") return failure("REPLAYED_REQUEST", "请求已使用，请重试", requestIdValue, 409);
  if (nonce === "error") return failure("DATABASE_ERROR", "请求状态保存失败，请稍后重试", requestIdValue, 503);
  if (license.status === "revoked" || device.status !== "active" || lease.revoked_at) return failure("LICENSE_REVOKED", "授权已撤销", requestIdValue, 403);
  if (!license.expires_at || license.expires_at <= now) {
    await db.from("licenses").update({ status: "expired", version: (license.version ?? 0) + 1 }).eq("license_id", license.license_id);
    await touchLicense(db, license.license_id);
    await syncToCloudflare(db, license.license_id);
    return failure("LICENSE_EXPIRED", "授权已过期", requestIdValue, 403);
  }
  if (lease.grace_until < now) return failure("LEASE_EXPIRED", "本地租约已过期，请重新联网激活", requestIdValue, 401);

  const update = await db.from("devices").update({ last_seen_at: now, app_version: body.clientVersion || null }).eq("device_id", body.deviceId.trim()).eq("license_id", license.license_id);
  if (update.error) return failure("DATABASE_ERROR", "设备状态更新失败，请稍后重试", requestIdValue, 503);
  try {
  const nextLease = await createLease(db, secret, license, body.deviceId.trim(), body.clientVersion || null, now);
  await touchLicense(db, license.license_id);
  await syncToCloudflare(db, license.license_id);
    await writeLog(db, secret, request, event, true, requestIdValue, license.license_id, body.deviceId);
    return json({ ...leaseResponse(license, nextLease.leaseId, nextLease.leaseToken, nextLease.leaseExpiresAt, nextLease.graceUntil, now), requestId: requestIdValue });
  } catch {
    return failure("LEASE_CREATE_FAILED", "授权租约创建失败，请重试", requestIdValue, 503);
  }
}

async function health(requestIdValue: string): Promise<Response> {
  const db = database();
  const secret = pepper();
  const privateSkinConfigured = privateRepositoryConfig() !== null;
  let databaseReady = false;
  let catalogRevision = BUILT_IN_PRIVATE_SKIN_INDEX.revision;
  let catalogRefreshReady = false;
  let catalogLastError: string | null = null;
  if (db) {
    const result = await db.from("licenses").select("license_id", { head: true, count: "exact" }).limit(1);
    databaseReady = !result.error;
    if (databaseReady) {
      try {
        const state = await ensurePrivateSkinCatalogState(db);
        catalogRevision = state.current_revision;
        catalogLastError = state.last_error;
        catalogRefreshReady = privateSkinConfigured && state.last_error === null;
      } catch (error) {
        catalogLastError = error instanceof Error ? error.message.slice(0, 500) : "catalog state unavailable";
      }
    }
  }
  const ready = Boolean(db && secret && databaseReady && privateSkinConfigured && catalogRefreshReady);
  return json({
    ok: ready,
    service: "cskin-license-supabase",
    environment: Deno.env.get("ENVIRONMENT") || "local-only",
    deployed: true,
    databaseConfigured: Boolean(db),
    databaseReady,
    pepperConfigured: Boolean(secret),
    syncConfigured: Boolean(Deno.env.get("AUTH_SYNC_SECRET")),
    privateSkinConfigured,
    catalogRevision,
    catalogRefreshReady,
    catalogLastError,
    requestId: requestIdValue,
  }, ready ? 200 : 503);
}

Deno.serve(async (request) => {
  if (request.method === "OPTIONS") return new Response(null, { status: 204, headers: corsHeaders });
  const url = new URL(request.url);
  const path = url.pathname.replace(/\/+$/, "");
  const id = requestId(request);
  if (request.method === "GET" && (path.endsWith("/health") || path.endsWith("/auth"))) return health(id);
  try {
    if (request.method === "GET" && path.endsWith("/v1/skins/index")) return await privateSkinIndex(request, id);
    if (request.method === "GET" && path.endsWith("/v1/skins/file")) return await privateSkinFile(request, id);
    if (path.endsWith("/internal/sync/export")) {
      if (request.method !== "GET" || !syncAuthorized(request)) return failure("SYNC_UNAUTHORIZED", "同步请求未授权", id, 401);
      const db = database();
      if (!db) return failure("DB_NOT_CONFIGURED", "Supabase 数据库尚未配置", id, 503);
      return await syncExport(db);
    }
    if (path.endsWith("/internal/sync/apply")) {
      if (request.method !== "POST" || !syncAuthorized(request)) return failure("SYNC_UNAUTHORIZED", "同步请求未授权", id, 401);
      const db = database();
      if (!db) return failure("DB_NOT_CONFIGURED", "Supabase 数据库尚未配置", id, 503);
      const payload = await readJson<SyncPayload>(request);
      if (!isNonEmptyString(payload.licenseId, 100) || !Number.isFinite(payload.changedAt) || !Number.isFinite(payload.version)) return failure("SYNC_INVALID_PAYLOAD", "同步数据无效", id, 400);
      return await applySyncPayload(db, payload);
    }
    if (request.method === "POST" && path.endsWith("/v1/activate")) return await activate(request, id);
    if (request.method === "POST" && path.endsWith("/v1/verify")) return await verify(request, id, "verify");
    if (request.method === "POST" && path.endsWith("/v1/heartbeat")) return await verify(request, id, "heartbeat");
    return failure("NOT_FOUND", "请求路径不存在", id, 404);
  } catch (error) {
    console.error(JSON.stringify({
      event: "request_failed",
      requestId: id,
      path,
      error: error instanceof Error ? error.message : "unknown",
    }));
    return failure("INTERNAL_ERROR", "授权服务内部错误，请稍后重试", id, 500);
  }
});
