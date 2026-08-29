# PortableCskin v3 私有皮肤仓库方案与实施记录

## 目标

v3 的唯一皮肤文件源是 GitCode 私有仓库 `Re2347/skin`，逻辑名称为
`privateskin`。客户端不携带 Git，不直接访问 GitCode，也不包含 GitCode
访问令牌。Supabase 是主授权与下载代理，Cloudflare Worker 是备用代理；不使用
R2 或 Supabase Storage。

版本号：`0.3.0`。

## 已实施架构

```text
PortableCskin 0.3.0
  -> 有效授权租约
  -> Supabase Edge Function（主通道，一次）
  -> Cloudflare 自定义域名（备用，一次）
  -> workers.dev（第二备用，一次）
  -> GitCode private Re2347/skin main（唯一文件源）
```

资源接口：

- `GET /v1/skins/index`：返回当前仓库 revision、路径、名称和可用的 blob SHA。
- `GET /v1/skins/file?skinId=<id>`：服务端从索引解析路径并流式转发单个
  `.fantome`，客户端不提交任意 GitCode 路径。
- 两个代理都返回 `X-Cskin-Gateway`、`X-Cskin-Upstream` 和
  `X-Cskin-Revision`；文件响应还可返回 `X-Cskin-Blob-Sha`。

安全与完整性规则：

- 两个平台都用各自数据库中的租约哈希、设备、到期和撤销状态验证资源请求。
- GitCode 令牌只保存在 Cloudflare/Supabase 平台 Secret，不写入源码、日志、
  客户端、安装包或 Git 提交。
- 服务端只允许索引中的
  `skins/<number>/<number>/[<number>/]<number>.fantome`。
- `.fantome` 响应保持流式转发，不在 Worker 或 Edge Function 中整包缓冲。
- 客户端下载到 `.partial`，验证网关、GitCode 上游、revision、blob SHA、
  ZIP/Fantome 结构及 SHA-256 后才原子替换正式缓存。
- 缓存标记为 `private-gateway-v2`。当前索引 revision 或 blob SHA 改变时，
  同一路径旧缓存也会失效并重新下载。

## 有界故障切换

客户端顺序固定为：

```text
Supabase 一次
  -> 仅临时错误时 Cloudflare 自定义域名一次
  -> 仅临时错误时 workers.dev 一次
  -> 通过 SHA-256 校验的本地缓存
  -> 明确失败
```

临时错误仅包括连接失败、超时、无效/副本不一致响应、HTTP 408、429 和 5xx。
HTTP 401/403、撤销、过期和签名错误不会继续切换代理。切换前总会删除当前
`.partial`，不存在无限重试或无限等待。

## 仓库更新无需重新部署

两个代理都内置初始的 8,968 条索引，并在数据库中保存单行动态目录状态。每次
客户端读取索引时最多每五分钟检查一次 GitCode `main` 的最新 revision；
Cloudflare Cron 也每五分钟检查一次。

revision 改变后，服务端调用 GitCode compare API，只把新增、修改、删除和重命名
的 `.fantome` 记录为覆盖项，同时刷新 `resources/zh/skin_ids.json` 名称。更新后的
目录从“内置索引 + 数据库覆盖项”生成。因此日常更新 `Re2347/skin` 后：

- 不需要重新部署客户端。
- 不需要重新部署 Supabase Edge Function。
- 不需要重新部署 Cloudflare Worker。
- 最长等待约五分钟，或由下一次授权索引请求触发检查。

只有仓库历史被强制重写到无法 compare、compare 返回截断结果、索引规则变化，
或要更换仓库/分支时，才需要重新生成基线索引或修改服务端。

## 平台配置

两端非敏感配置均为：

- `GITCODE_OWNER=Re2347`
- `GITCODE_REPOSITORY=skin`
- `GITCODE_REF=main`
- `SKIN_REPO_LABEL=privateskin`

Cloudflare Secret 至少包括 `GITCODE_TOKEN`、`AUTH_SYNC_SECRET`、
`LICENSE_PEPPER` 及管理员 Secret。Supabase Secret 至少包括
`GITCODE_TOKEN`、四个仓库配置项、`AUTH_SYNC_SECRET`、`LICENSE_PEPPER` 和
service-role 配置。只通过平台控制台或交互式 CLI 写入，任何文档都不记录值。

数据库迁移：

- Cloudflare D1：`0005_private_skin_catalog_state.sql`
- Supabase PostgreSQL：`20260830010000_private_skin_catalog_state.sql`

## 2026-08-30 部署与验证

- Supabase Edge Function `auth`：已部署，健康检查 HTTP 200。
- Cloudflare Worker：`cskin-license-staging`。
- Cloudflare Version ID：`020e20f2-ba84-4d50-971e-d19c681f808b`。
- Cloudflare 自定义域：`license.re2347.ccwu.cc` 已写入 `wrangler.jsonc` 并部署。
- 两个数据库迁移均已成功应用，未重建现有授权表。
- 两端健康状态：`privateSkinConfigured=true`、`catalogRefreshReady=true`、
  `catalogLastError=null`。
- 两端未授权索引请求：HTTP 401。
- 同一有效租约访问两端索引：HTTP 200，均为 8,968 条。
- 同一有效租约访问两端文件：HTTP 200，均返回 `upstream=gitcode`。
- `dotnet build -c Release`：0 警告、0 错误。
- Worker TypeScript 与 Wrangler dry-run：通过。
- 隔离首次运行：随包索引 0.122 秒，Supabase 索引同步 3.656 秒，
  下载 `110003.fantome` 2.401 秒、2,881 bytes，ZIP/Fantome 和缓存校验通过。
- 隔离日志确认 `gateway=supabase`、`upstream=gitcode`，证明正常路径优先使用
  Supabase，GitCode 仍是实际文件上游。
- 故障注入将测试进程的 Supabase 地址设为不可达；客户端一次失败后切到
  Cloudflare，自定义域索引和文件下载均成功，最终标记为
  `gateway=cloudflare`、`upstream=gitcode`，无循环重试。
- 授权回归确认无效租约返回 HTTP 401 `INVALID_LEASE`，不会继续切换端点。
- Cloudflare 授权变更现在会立即同步当前 license graph；同步失败时才保留在
  `sync_outbox` 等待定时重试。修复前复现的 Supabase 资源 HTTP 403 已消除，
  Cloudflare 创建的新租约可立即由 Supabase 主通道读取。
- 双库协调使用“远端较新 / 本地较新 / 完全相等”三态比较；状态相等时不再重复
  入队。Supabase 按 lease、device、license 的外键顺序应用同步，避免并发删除
  产生 `LICENSE_GRAPH_CLEAR_FAILED`。
- 真实 revision 转换从 `2d2a331d3bbe0270cf7b666a7735275a9b9b85bc` 比较到
  `a719f468e17993334ac3c59c997efed1b609f668`，D1 自动生成 2 条覆盖记录，
  `last_error=null`；客户端读取到 8,968 条并下载 `86044.fantome` 成功。
- D 盘干净解压验证：338 个文件，压缩包不含授权、日志、引擎数据或皮肤缓存；
  首次同步使用 Supabase，从 GitCode 下载 `86044.fantome` 9,684,313 bytes，
  文件落在解压目录 `Engine\skins`，来源标记、ZIP/Fantome、SHA-256 和二次缓存
  校验均通过。
- D 盘发行 EXE 自动恢复测试授权后进入 `PortableCskin` 主窗口并正常关闭，
  退出码 0。
- 本地签名安装测试：静默安装退出码 0、`skipifsilent` 未启动应用、安装后
  `verify-portable -Check all` 通过、静默卸载退出码 0。

当前网络访问 `workers.dev` 出现超时，但 Cloudflare 自定义域健康且已验证文件
下载；`workers.dev` 仍保留为第三通道，不影响主通道和第一备用通道。

## 当前构建与正式发布条件

- 便携包：`v3/PortableCskin.zip`，79,509,978 bytes，SHA-256
  `20DE412109D1F8BDE12E39B1006B7D0BB5C035C38DE89E235DDBB7FC8D6BD062`。
- 本地签名安装测试包：
  `v3/installer-output/CskinSetup-LOCAL-SIGNED-TEST.exe`，68,548,608 bytes，
  SHA-256 `33D4EF5B6D2C60E611A34BC24C7BF866CD0AF7A51A58E79249C72AD8DB5076EA`。

本地测试安装包使用自签名证书，只用于验证安装链路，不得作为公开 Release。
当前机器没有受信任的 Authenticode 代码签名证书；正式安装版需要提供受信任证书，
或由发布负责人明确决定发布未签名版本。日常皮肤仓库内容更新不需要重新发布软件。
