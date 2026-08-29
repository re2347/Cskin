# Supabase primary auth and private-skin proxy

This directory contains the staging Edge Function and PostgreSQL integration
for the Tokyo project `kzqgphxrghdclkwneqyn`. Supabase PostgreSQL is the default
authorization provider in the desktop client, and the client automatically
falls back to Cloudflare on connection failure,
timeout, invalid response, 408, 429, 5xx, and explicit Supabase/D1 inventory
or lease mismatches (`LICENSE_NOT_FOUND`, `INVALID_LEASE`,
`LICENSE_ALREADY_BOUND`, `DEVICE_KEY_CHANGED`). Revocation, expiration and
signature errors are returned directly and are not hidden by failover. Set
`CSKIN_AUTH_PROVIDER=cloudflare` only to force the emergency Cloudflare-first
override.

The Edge Function implements the same signed `cskin-auth-v2` contract as the
Worker: `POST /v1/activate`, `POST /v1/verify`, `POST /v1/heartbeat`,
`GET /v1/skins/index`, `GET /v1/skins/file?skinId=...`, and `GET /health`.
Skin files are streamed from the private GitCode repository and are never
stored in Supabase Storage. It uses the server-only `SUPABASE_SERVICE_ROLE_KEY` to access
the `license` schema through PostgREST. Row-level security is enabled and the
schema is granted only to `service_role`. The hosted project has the `license`
schema exposed to the Edge Function through its server-only service role.

`supabase/.env.local` is ignored by Git and contains the supplied project URL,
project ref, and publishable key. It intentionally does not contain a
service-role key, database password, or `LICENSE_PEPPER`. The direct
PostgreSQL URL remains a placeholder until a real database password is
provided. The publishable key is not used by the desktop authorization flow.

Cloudflare D1 and PostgreSQL now use controlled dual-write synchronization.
Worker and admin changes are written to a D1 outbox and pushed immediately to
the Edge Function; authentication-side changes are queued as well and failed
deliveries retry every five minutes. Supabase-first
activation and renewal write back to the Worker immediately. Both sides retain
`updated_at + version`, and reconciliation keeps the newer record rather than
overwriting it with stale state. Deletions use tombstones so they cannot return
from an older replica. The shared `AUTH_SYNC_SECRET` must be set to the exact
same server-only value in both platforms; never expose it to the desktop app.

The private catalog state is stored by migration
`20260830010000_private_skin_catalog_state.sql`. At most once every five
minutes the function compares its current revision with GitCode `main` and
applies added, modified, renamed, and removed `.fantome` paths as compact
overrides to the bundled baseline. Ordinary repository updates therefore do
not require a function or client deployment.

For local development, install/authenticate the Supabase CLI and run from the project root:

```powershell
npx supabase@2.116.0 login
npx supabase@2.116.0 link --project-ref kzqgphxrghdclkwneqyn
npx supabase@2.116.0 db reset
npx supabase@2.116.0 functions serve auth --env-file .\supabase\.env.local
```

For a local function test, add `SUPABASE_SERVICE_ROLE_KEY` and the exact same
`LICENSE_PEPPER` used by the existing license inventory to a separate local
env file. Do not put either secret in Git, the desktop client, or
`supabase/.env.local` unless that file remains local-only.

Production migration `20260830010000` and function `auth` were deployed on
2026-08-30. The hosted Edge Function is the default provider; Cloudflare's
custom domain and workers.dev endpoint are bounded fallbacks. All GitCode,
service-role, pepper, and sync values remain platform secrets.
