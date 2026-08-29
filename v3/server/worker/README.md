# Cskin License Worker

v3 同一 Worker 还提供受授权租约保护的私有皮肤接口：

- `GET /v1/skins/index`：返回随 Worker 部署的 `privateskin` 路径和名称索引。
- `GET /v1/skins/file?path=...`：校验路径属于索引后，从 GitCode 私有
  `Re2347/skin` 仓库流式返回单个 `.fantome`。

GitCode 访问令牌必须通过 `npx wrangler secret put GITCODE_TOKEN` 配置，禁止
写入 `wrangler.jsonc`、`.dev.vars` 的提交版本或客户端。索引更新流程见
`../../V3-PRIVATE-REPOSITORY.md`。

这是授权服务的 staging Worker，使用 Cloudflare Workers + D1。客户端只连接 `/v1` API；管理员接口额外要求 `X-Admin-Key`，部署到生产时应再由 Cloudflare Access 保护。

## 首次配置

1. 你已经创建的 Cloudflare D1 数据库名称是 `cskin-license-staging-db-20260825`。
2. 打开已经存在的 `wrangler.toml`，取消底部 D1 配置的注释，并把 `database_id` 替换为 D1 页面显示的 ID。若实际数据库名称不同，两个配置文件中的 `database_name` 和命令参数都要改成实际名称。
3. 安装依赖并登录：

```powershell
npm install
npx wrangler login
```

4. 首次执行 schema：

```powershell
npx wrangler d1 execute cskin-license-staging-db-20260825 --remote --file=.\schema.sql
```

如果你是在 Cloudflare 控制台里直接绑定数据库，绑定名称必须是 `DB`，然后仍然需要执行上面的 schema 命令。

已有数据库升级完整密钥查看和过期删除功能时，先应用 D1 migration：

```powershell
npx wrangler d1 migrations apply cskin-license-staging-db-20260825 --remote
```

新生成的激活码会以 `LICENSE_PEPPER` 加密保存，管理员列表只在鉴权请求中解密返回。旧记录如果没有历史密文，无法从哈希反推出完整密钥。管理员可以生成 1-36500 天的自定义时长密钥，通过 `planDays` 筛选库存，使用 `PATCH /v1/admin/licenses/{licenseId}/note` 更新备注，并通过 `POST /v1/admin/licenses/bulk-delete` 批量删除。手动删除接口允许删除未使用、已过期或已撤销的密钥，仍处于 active 状态的密钥会被跳过。Worker 每 5 分钟运行一次 Cron Trigger，自动处理到期清理和 D1/Supabase 同步；同步采用 outbox 重试、删除墓碑以及 `updated_at + version` 的最新状态优先策略，失败不会覆盖另一端较新的数据。管理员仍可通过 `DELETE /v1/admin/licenses/{licenseId}` 立即手动删除。

5. 配置只存在于 Cloudflare 的 Secret：

```powershell
npx wrangler secret put LICENSE_PEPPER
npx wrangler secret put ADMIN_API_KEY
npx wrangler secret put PAYMENT_WEBHOOK_SECRET
npx wrangler secret put AUTH_SYNC_SECRET
```

`LICENSE_PEPPER` 用于哈希激活码和加密订单交付内容，`ADMIN_API_KEY` 只用于 staging 管理接口，`PAYMENT_WEBHOOK_SECRET` 用于验证支付平台回调。不要把这些值写入 `wrangler.toml`、日志或客户端。
`AUTH_SYNC_SECRET` 必须与 Supabase Edge Function 的同名同步密钥值一致（Supabase 平台中使用名称 `AUTH_SYNC_SECRET`），仅用于服务器之间的双向同步。不要把它写入客户端。

管理员后台登录还需要配置两个 Secret：

```powershell
npx wrangler secret put ADMIN_LOGIN_USERNAME
npx wrangler secret put ADMIN_LOGIN_PASSWORD
```

后台打开时会先弹出管理员登录框，Worker 校验账号密码后签发 8 小时会话。页面中的 `ADMIN_API_KEY` 可以勾选“记住此设备”保存到当前浏览器，密码不会保存。

6. 部署：

```powershell
npm run deploy
```

部署命令会更新现有的 `cskin-license-staging` Worker；部署前先运行 `npx wrangler whoami`，确认登录的是账号 `2469416170@qq.com`。

生产环境不要只把 `workers.dev` 地址写进客户端。当前 staging 已绑定自定义域名 `license.re2347.ccwu.cc`；其他环境若域名已在当前 Cloudflare 账号并完成 DNS 配置，可以用 Wrangler 的自定义域名选项部署：

```powershell
npx wrangler deploy --config .\wrangler.toml --domain license.example.com
```

中国大陆网络仍可能无法稳定访问 Cloudflare 边缘。普通 Workers 账号不能直接启用 Cloudflare China Network；应另外准备已备案中国大陆 HTTPS 中继，并把中继地址作为客户端备用端点。仓库中的 `server/auth-relay` 提供了严格的零依赖转发模板，具体限制见其 README 和根目录 `CHINA_AUTH_CONNECTIVITY.md`。中继不应拥有 D1 或 Worker Secret。

## API 冒烟测试

部署后先访问 `/health`。如果 D1 尚未绑定，会明确返回 `DB_NOT_CONFIGURED`，而不是假装服务正常。

管理员生成测试卡（支持 1-36500 天）：

```powershell
Invoke-RestMethod `
  -Uri "https://license.re2347.ccwu.cc/v1/admin/licenses" `
  -Method Post `
  -Headers @{ "X-Admin-Key" = "<只在本地替换>"; "Content-Type" = "application/json" } `
  -Body '{"planDays":1,"count":1,"createdBy":"owner","note":"staging"}'
```

生成结果中的明文卡密只显示一次。客户端接口为 `POST /v1/activate`、`POST /v1/verify` 和 `POST /v1/heartbeat`。

## 自动发放激活码

支付页使用以下订单接口：

- `POST /v1/orders`：创建 1、7 或 30 天订单，返回只在当前页面使用的 `orderToken`。
- `GET /v1/orders/{orderId}?token=...`：轮询订单状态。支付成功后只返回一次明文激活码，随后状态变为 `delivered`。
- `POST /v1/payment/webhook`：支付平台服务端回调。请求头必须带 `X-Payment-Signature`，值为 `HMAC-SHA-256(PAYMENT_WEBHOOK_SECRET, 原始请求体)`。

回调请求体示例：

```json
{
  "orderId": "订单号",
  "paymentId": "支付平台流水号",
  "status": "paid",
  "planDays": 7
}
```

个人收款码本身不会向 Worker 发送可信到账回调，因此不能仅凭扫码页面自动确认付款。要启用真正的自动发码，需要使用支付宝/微信商户支付接口或一个能转发签名回调的支付服务，并将其回调地址设置为 `/v1/payment/webhook`。

Worker 部署完成后，把 `website/payment.js` 中的 `PAYMENT_CONFIG.api` 设置为 Worker 公网地址，支付页才会创建订单并轮询交付状态。
