# 配额感知双库认证设计

## 目标

降低 Supabase PostgreSQL 与 Cloudflare D1 的日常读写量，同时维持下列认证优先级和一致性保证：

```text
桌面客户端 -> Supabase Edge Function -> Cloudflare Worker（仅配额或暂时不可用时）
                                       |
                             Cloudflare D1 + 同步出站队列
                                       |
                               Supabase PostgreSQL 恢复后补写
```

Supabase 是正常情况下的权威认证端。Cloudflare 只在 Supabase 已返回限流、配额错误、5xx、超时或网络不可达时才作为备用认证端。卡密不存在、已绑定、已撤销、签名错误、租约无效和已过期均为业务结果，绝不触发备用端请求。

## 当前问题与根因

1. `AuthorizationHeartbeat` 每 30 分钟调用远程 `verify`；两个认证端的 `verify` 当前都会新建 lease、更新设备最后访问时间、更新许可证版本、向另一端同步完整授权图并写成功日志。
2. Cloudflare 的 `*/5` 定时任务每次全量调用 Supabase `/internal/sync/export`。导出会扫描全部许可证，并为每张许可证读取设备和全部租约。
3. 定时任务顺序为清理、拉取 Supabase、推送 outbox。D1 删除过期卡密后，仍可能在同次拉取中从 Supabase 重新导入旧快照，造成重复删除和导入。
4. 管理台每 15 秒读取 Supabase health，即使仅浏览 Cloudflare 管理数据。

## 认证与租约

### 只读心跳

- 心跳和普通 `verify` 始终验证：租约令牌、许可证状态和到期时间、设备绑定状态、设备签名与请求重放。
- 正常且未接近过期的租约只返回当前租约，不创建 lease、不更新许可证版本、不推送同步图、不写成功验证日志，也不写入 `request_nonces`。
- 只读验证继续使用设备签名时间窗。由于重放只会得到相同的只读结果，且不能延长租约或改变授权状态，不需要为每次心跳持久化 nonce；激活、续租、解绑、撤销及其他状态变更继续在写入前声明 request nonce。
- 失败认证仍保留现有安全审计日志。激活、解绑、撤销、续期、删除等实际状态变更也保留审计和同步。

### 有限续租和设备活动时间

- 仅当 `lease.expires_at - now <= 1 小时` 时续租。
- 续租更新当前有效租约，而不是插入另一条 lease；若当前 lease 已被撤销或不存在，返回已有的租约错误。
- `devices.last_seen_at` 最多每 6 小时更新一次。它不参与授权判定，因此不触发许可证版本更新或跨库同步。
- 激活、设备密钥变更、管理员解绑、撤销、延期、删除和过期状态转换仍立即更新许可证 `updated_at` 与 `version`，并同步其完整授权图。

## Supabase -> Cloudflare 增量同步

### 游标

Cloudflare D1 增加一条 `sync_state` 记录，保存 Supabase 增量导出游标：

```text
state_id = "supabase_to_d1"
cursor_changed_at = 最后成功应用的变更秒数
cursor_license_id = 同一秒内最后成功应用的 license_id
```

Supabase `/internal/sync/export` 接受 `changedAt` 和 `licenseId` 游标。它按 `(changed_at, license_id)` 升序导出：

- `licenses.updated_at` 大于游标，或时间相同且 `license_id` 更大；
- `sync_tombstones.changed_at` 大于游标，或时间相同且 `license_id` 更大。

Edge Function 合并、排序并限制结果，返回每一许可证的完整快照或删除墓碑，以及 `nextCursor`。相同游标重放是幂等的；Cloudflare 只有在整页成功应用后才推进本地游标。这样同一秒的大批量操作不会被跳过，也不会在无变更时反复导出历史记录。

### 调度与即时同步

- Cloudflare cron 从 5 分钟改为 30 分钟。无变更时，Supabase 只处理两条有索引的增量查询，而不是扫描整个授权图。
- Supabase 的激活、解绑、延期、撤销、删除与状态转换会保持当前的即时 `syncToCloudflare`。因此正常情况下备用库几乎实时可用；30 分钟增量拉取只负责修复网络失败或漏发。
- 心跳不会调用 `syncToCloudflare`。

## Cloudflare -> Supabase 补写

Cloudflare 在备用认证期间产生的真实状态变更继续写入 D1，并按许可证合并到 `sync_outbox`。每次 cron 的固定顺序是：

```text
1. 清理到期记录并写出 tombstone/outbox
2. 推送 Cloudflare outbox 至 Supabase
3. 从 Supabase 拉取并应用一页增量
4. 再推送一次 outbox，处理拉取过程发现的本地较新记录
```

- Supabase 返回成功后删除 outbox 项；若 Supabase 返回权威的较新快照，则应用该快照并删除本地冲突项。
- 限流、配额或临时不可用时保留 outbox；记录下一次可尝试时间，避免同一变更在配额耗尽期间被高频重试。
- 变更新鲜度由 `changedAt` 优先、`version` 次之比较。删除墓碑使用删除时的版本，禁止使用固定版本号。墓碑较新时优先于旧许可证快照。
- Supabase 与 D1 的 apply 操作继续保持幂等；新游标不替代现有的冲突比较和 outbox 机制。

## 客户端故障转移

`AuthorizationClient` 只在当前 Supabase 端点返回下列结果时尝试 Cloudflare：

- HTTP `429`；
- 明确的配额或限流错误代码；
- HTTP `408`、任意 `5xx`；
- 没有 HTTP 响应的连接、DNS、TLS 或超时错误。

不会故障转移的业务错误包括 `LICENSE_NOT_FOUND`、`INVALID_LEASE`、`LICENSE_ALREADY_BOUND`、`DEVICE_KEY_CHANGED`、`LICENSE_REVOKED`、`LICENSE_EXPIRED`、签名或请求参数错误。Cloudflare 也不再成为 Supabase 的并行验证端。

## 管理台与可观测性

- 列表、统计和自动刷新继续只使用 Cloudflare 管理 API。
- Supabase health 在页面加载、手动刷新以及距上次检查至少 60 秒时获取；不再每 15 秒重复读取。
- Supabase health 与 Cloudflare health 均显示同步待处理数量和最后同步游标/时间，便于识别备用期间积压的补写。

## 数据库与索引

- D1 migration 创建 `sync_state`，并为 outbox 添加 `next_attempt_at`（默认 `0`）。
- Supabase migration 为 `license.licenses(updated_at, license_id)` 和 `license.sync_tombstones(changed_at, license_id)` 建立增量导出索引。
- 现有 `version`、`updated_at` 与 `sync_tombstones` 继续作为冲突处理的真实来源；不向公开 Data API 暴露新表。

## 验证标准

1. 正常心跳可验证授权且不插入 lease、不更新许可证版本、不调用跨库同步。
2. 到期前一小时内的心跳只更新原 lease，并最多每 6 小时更新一次设备活动时间。
3. 数据库中的业务错误不会让桌面端请求 Cloudflare；`429`、`5xx` 和无响应会。
4. 初次增量同步可导入完整数据；后续无变更同步不返回历史数据；同一秒多个变更不丢失。
5. D1 本地删除能先推送 tombstone，不会被旧 Supabase 快照重新导入。
6. Supabase 不可用时 Cloudflare 改动保留在 outbox；Supabase 恢复后自动补写，并按版本和时间保留最新记录。
7. Worker、Edge Function、管理台和桌面端的现有测试均通过，随后进行生产健康检查与一轮真实同步检查。
