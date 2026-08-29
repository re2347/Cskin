# PortableCskin v3 私有皮肤仓库方案与实施记录

## 目标

v3 将皮肤源切换到 GitCode 私有仓库 `Re2347/skin`，逻辑名称为
`privateskin`。客户端不再携带 Git，也不直接访问 GitCode；Cloudflare Worker
负责返回索引并按需转发单个 `.fantome` 文件。

版本号：`0.3.0`。

## 已实施架构

```text
PortableCskin 0.3.0
  -> 有效授权租约
  -> GET /v1/skins/index
  -> GET /v1/skins/file?path=skins/.../*.fantome
  -> Cloudflare Worker
  -> GITCODE_TOKEN Worker Secret
  -> GitCode private Re2347/skin main
```

- 客户端请求携带当前授权的 license、lease、lease token 和 device ID。
- Worker 使用 D1 中的租约哈希、设备状态、到期时间和撤销状态进行校验。
- GitCode 令牌只存在于 Worker Secret `GITCODE_TOKEN`，不写入源码、配置、
  日志、安装包或 Git 提交。
- Worker 对下载路径执行白名单校验，只允许索引内的
  `skins/<number>/<number>/[<number>/]<number>.fantome`。
- 客户端将文件流写入 `Engine/skins`，校验 ZIP/Fantome 结构和 SHA-256 后
  才生成 `worker-private-v1` 来源标记并允许应用。
- 网络不可用时保留随包索引、上次 Worker 索引和已校验的皮肤缓存。

## 为什么索引随 Worker 部署

GitCode tree API 实测强制每页最多 100 条，`skin` 当前有 8,968 个唯一皮肤
文件。每次 Worker 动态遍历会产生过多上游请求。另一个限制是
`Re2347/skin` 被 GitCode 标记为镜像仓库，服务端拒绝 Git 推送，因此不能将
生成索引写回该仓库。

v3 使用 `scripts/build-private-skin-index.ps1` 从私有仓库 tree 生成
`src/private-skin-index.json`，索引随 Worker 发布，由 `/v1/skins/index` 返回。
皮肤文件本体仍实时从私有仓库 raw API 流式获取。

更新私有仓库镜像后，维护流程为：

```powershell
git clone --depth 1 --filter=blob:none --sparse https://gitcode.com/Re2347/skin.git .\private-skin
git -C .\private-skin sparse-checkout set resources/zh
.\scripts\build-private-skin-index.ps1 `
  -RepositoryRoot .\private-skin `
  -OutputPath .\src\private-skin-index.json
npm run typecheck
npx wrangler deploy --dry-run
npx wrangler deploy
```

生成器会按皮肤 ID 去重，并在镜像包含冲突路径时优先选择与皮肤 ID 英雄前缀
一致的目录。本次生成结果为 8,968 条，重复 ID 为 0，非法路径为 0。

## Worker 配置

非敏感配置位于 `wrangler.jsonc`：

- `GITCODE_OWNER=Re2347`
- `GITCODE_REPOSITORY=skin`
- `GITCODE_REF=main`
- `SKIN_REPO_LABEL=privateskin`

令牌只通过交互式命令配置：

```powershell
npx wrangler secret put GITCODE_TOKEN
```

不要把令牌放入 `.dev.vars` 后提交，也不要作为命令行参数、URL 查询参数或
客户端常量。

## 部署与验证记录

2026-08-29 已部署 Worker：

- Worker：`cskin-license-staging`
- Version ID：`64df4fec-616b-4dbc-8a51-cbfd55e6c896`
- 自定义域名健康检查：`ok=true`、`privateSkinConfigured=true`
- 未授权索引请求：HTTP 401
- 已授权索引请求：HTTP 200，8,968 条
- 已授权文件请求：`skins/110/110003/110003.fantome`，HTTP 200，2,881 bytes，ZIP 头有效

客户端回归：

- .NET Release：0 警告、0 错误
- Worker TypeScript：通过
- Wrangler dry-run：通过
- 随包索引就绪：0.118 秒
- Worker 索引同步：2.929 秒
- 单皮肤下载：3.161 秒
- 缓存来源：`worker-privateskin`
- 缓存完整性校验：通过

## 后续发布条件

v3 源码和 Worker 已可用，但本次没有生成安装版或压缩包。正式发行前仍应：

1. 更新私有仓库后重新生成 Worker 索引并部署。
2. 运行 `installer/build-portable.ps1` 和 `installer/verify-portable.ps1`。
3. 使用受信任的 Authenticode 证书构建正式安装包。
4. 确认 Worker `/health` 的 `privateSkinConfigured` 和 `databaseReady` 均为 true。
