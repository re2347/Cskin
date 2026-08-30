# v3 预览图备用来源与目录同步

## 需求

所有炫彩统一使用相同的预览图降级逻辑，不维护某一个皮肤的硬编码特例：

```text
内存缓存 -> CommunityDragon chromapreview -> CommunityDragon PBE chromapreview
           -> League of Legends Wiki 皮肤页 -> League of Legends Wiki API
           -> 基础皮肤 Data Dragon
```

CommunityDragon 的 `champion-chroma-images/{championId}/{skinId}.png` 在新皮肤发布
后可能暂时返回 404。客户端现在根据 Riot 的皮肤编号和英雄 slug 请求游戏资源目录中
对应的 `skins/skin{skinId % 1000}/chromapreview.png`，路径中的英雄目录强制使用小写，
并在正式 `latest` 尚未同步时尝试 `pbe`。这组资源已验证金克丝海之歌 `222066-222073`
均可返回真实图片。

两条 CommunityDragon 通道都不可用时，客户端先访问 Wiki 的具体皮肤页并解析对应的
炫彩文件，再尝试 MediaWiki API 搜索。Wiki 站点或 API 被 403、验证码、防盗链拦截时，
才显示基础皮肤原画；不会使用相似皮肤图片冒充目标炫彩。

远程目录同时携带中文名称和英文规范名称。英文名称只用于 Wiki 搜索和严格匹配，界面
仍优先显示中文名称；旧版本只保存中文 `names_json` 的服务端状态仍可被新版本读取。

## 目录自动更新

客户端启动时立即同步私有皮肤目录，并每 5 分钟检查 Worker 返回的仓库 revision。
revision 变化后会更新皮肤路径、名称、基础皮肤与炫彩分组，移除仓库中已删除的条目，
同时清空预览图内存缓存，使同一编号的更新图片重新加载。日常只更新 GitCode
皮肤仓库内容时不需要重新安装客户端；只有客户端解析逻辑或程序文件变化才需要发布
新版本。

## 日志

预览解析会记录实际来源和失败原因，例如：

```text
预览图加载成功：skinId=22044 source=communitydragon
Wiki 预览图解析成功：skinId=22044 query=Ocean Song Ashe Ruby
预览图回退基础皮肤：skinId=22044 source=ddragon
```

CommunityDragon、Wiki 皮肤页和 Wiki API 请求均使用有限超时；解析结果按 `skinId`
在进程内缓存，不会访问 GitCode 私有资源。日志会明确标注命中的 URL 和失败通道，
例如：

```text
预览图加载成功：skinId=222070 source=communitydragon-chromapreview url=...
CommunityDragon 炫彩预览图不可用：skinId=222070 url=...
Wiki 皮肤页请求失败：skinId=222070 status=403
预览图回退基础皮肤：skinId=222070 source=ddragon
```

## 2026-08-30 名称元数据补全

- Worker 和 Supabase 在仓库 revision 变化时同步 `resources/zh/skin_ids.json` 与
  `resources/en/skin_ids.json`。
- `/v1/skins/index` 新增可选 `nameEn` 字段；已有客户端可忽略该字段。
- 客户端将 `nameEn` 传给通用 Wiki 回退解析，解决远程新增皮肤只有中文名称时无法
  匹配英文 Wiki 图片标题的问题。
- 未提供英文文件时保留上次可用名称并记录告警，不会阻塞皮肤文件下载或误用相似图片。

## 2026-08-30 炫彩预览图 404/403 修复

- 根因：新炫彩的旧 CDN 地址 `champion-chroma-images/{championId}/{skinId}.png`
  返回 HTTP 404；Wiki API 同时受到 HTTP 403，旧逻辑因此全部回退到基础皮肤原画。
- 修复：增加 CommunityDragon 游戏资源目录的精确 `chromapreview.png` 解析，修正
  英雄目录大小写，并增加 PBE 备用通道。
- 修复：增加 Wiki 具体皮肤页解析，API 不可用时仍可尝试页面中的炫彩文件。
- 验证：金克丝海之歌 `222070` 在主预览地址和基础图均设为无效时，成功加载
  CommunityDragon `skin70/chromapreview.png`；`dotnet build` 和
  `test-preview-fallback.ps1` 均通过。
- 批量验证：`222066-222073` 共 8 个炫彩全部返回独立 `272x304` 图片，结果 `8/8`。
- 备用站点评估：Heimerdinger 被 Cloudflare 拦截，Fandom 请求超时，Locker 依赖
  动态接口，Meraki 公开数据停留在 2025。它们不适合作为新皮肤的客户端实时来源，
  因此没有加入生产链路，避免额外等待、验证码和过期图片。当前采用的是能实测返回
  最新 Riot 游戏资源的 CommunityDragon `latest/PBE` 双通道，再回退 Wiki 与基础图。
