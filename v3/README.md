# Cskin Native

轻量化 Windows 原生皮肤选择器，使用本地 Cskin 引擎完成应用，皮肤资源按编号按需缓存。

当前开发版本：**0.3.0**（Supabase 主通道、Cloudflare 备用、无客户端 Git 依赖、按需流式下载、
授权租约保护、覆盖层快速待命、LCU 阶段同步和 WAD 槽位冲突修复）。

私有仓库架构、部署和验证记录见 `V3-PRIVATE-REPOSITORY.md`。

## 使用

1. 运行 `PortableCskin.exe`。
2. 安装器和压缩包包含本地 Cskin 引擎及原生依赖，不需要安装 Git。
3. 按拼音浏览或搜索英雄，选中皮肤后点击“应用皮肤”。首次启动先使用随包索引，授权通过后由 Supabase/Cloudflare 安全资源服务更新私有索引并按需下载皮肤。

程序启动时会检查私有资源服务，运行期间每 10 分钟自动检查一次；服务端最多每 5 分钟检查 GitCode revision。仓库新增或修正皮肤会自动合并到当前列表和本地引擎索引，日常皮肤更新不需要重新发布服务或客户端。网络暂时不可用时会继续使用上一次成功同步的索引和已经校验的本地皮肤缓存。

目录使用国服本地皮肤译名；有炫彩的基础皮肤会在卡片底部显示“炫彩 · 数量”，点击后从二级菜单选择具体炫彩，应用时仍按对应皮肤编号处理。

资源保存在 GitCode 私有仓库 `https://gitcode.com/Re2347/skin`，逻辑名称为
`privateskin`。客户端不会直接连接 GitCode，也不包含 GitCode 令牌；索引和单个
`.fantome` 都由授权代理返回。客户端按 `Supabase → Cloudflare 自定义域 → workers.dev` 做有界故障切换，GitCode 始终是实际文件源。GitCode 令牌只配置在 Supabase 与 Cloudflare 平台 Secret。

程序只使用本地引擎接口，不会自动打开旧网页端。

客户端同步：程序每 700 毫秒读取国服 League Client 的 LCU
`/lol-champ-select/v1/session`，在客户端悬停、锁定或更换英雄时自动切换左侧英雄和皮肤目录；LCU 暂不可用时才回退到本地引擎的 `/api/champion-selection` 接口。

LCU 诊断：运行 `diagnostics\collect-lcu-diagnostic.ps1` 可在 `diagnostics` 目录生成脱敏诊断日志（不会写入令牌）。应用日志会记录 LCU 接口状态码、游戏流阶段、候选队伍数量和最终解析的英雄/皮肤编号。WeGame 下 `LeagueClient\lockfile` 为空时属于正常情况，程序会改用客户端进程参数、PowerShell 或随包 `Engine\wmic.exe` 获取端口和令牌。

英雄同步和皮肤应用是两条独立流程。应用皮肤不会等待 LCU 请求；它使用最近一次轮询到的客户端槽位，若没有有效槽位则按英雄原皮槽应用。轮询期间的资源下载或覆盖层启动失败，也不会阻塞后续英雄同步。

应用时沿用稳定版引擎的原始包流程：将 `.fantome` 复制到引擎缓存、按客户端当前皮肤槽修正 WAD PROP 路径哈希、解压为 mod，再调用 `mkoverlay` 和 `runoverlay`。引擎返回 `armed` 时表示覆盖层已待命，必须进入实际对局后才会显示，程序不会再把空响应当成应用成功。

引擎启动会自动从运行中的 `LeagueClientUx.exe`、客户端 lockfile 和常见安装目录推断游戏目录，并通过 `AATROX_GAME_DIR` 传给引擎；如果客户端启动顺序较晚，首次应用失败会自动重启引擎后重试。

## 构建

```powershell
dotnet publish .\CskinNative.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o .\publish
```

生成可整体复制的压缩包（输出 `PortableCskin.zip` 和同名目录）：

```powershell
.\installer\build-portable.ps1
```

便携包和安装包脚本都会先运行 `installer\build-engine.ps1`，从
`Runtime\Cskin\cskin_engine.py` 重新生成 PyInstaller one-dir 引擎和
`cskin_engine.pyc`，再复制到发布目录；不需要手动运行 PyInstaller，避免
源码和冻结引擎版本不一致。

生成正式安装器（需要 Inno Setup 6、Windows SDK 的 `signtool.exe`，以及受信任的 Authenticode 证书）：

```powershell
$env:CSKIN_CODESIGN_CERT_SHA1 = "证书指纹"
.\installer\build-installer.ps1
```

经发布负责人明确接受 Windows“未知发布者”提示后，可显式生成无签名正式安装器：

```powershell
.\installer\build-installer.ps1 -UnsignedReleaseBuild
```

该模式输出正式文件 `installer-output\CskinSetup.exe`，不会签名任何程序文件；它与两个测试构建模式互斥，不能通过重命名测试包代替。

正式构建会同时签名主程序、`Engine\Cskin.exe`、`Engine\tools\mod-tools.exe` 等关键 PE 文件；没有受信任证书时脚本会拒绝生成正式包。

本地功能验证可显式生成未签名测试包：`.\installer\build-installer.ps1 -UnsignedTestBuild`；输出名为 `CskinSetup-UNSIGNED-TEST.exe`，不得分发。

如需在本机验证 Authenticode 签名链路，可运行 `.\installer\setup-local-signing.ps1` 创建并安装当前用户范围的自签名测试证书，然后运行 `.\installer\build-installer.ps1 -LocalTestBuild`；输出名为 `CskinSetup-LOCAL-SIGNED-TEST.exe`。该证书只用于本机测试，其他用户电脑仍会显示未知发布者，不能降低正式发布的安全软件拦截，也不得分发。

正式发布需要从代码签名证书机构购买/申请 Authenticode 证书，或使用 Azure Trusted Signing。将证书机构提供的 `.pfx` 导入“当前用户\个人”证书存储并保留私钥，然后设置其指纹：`$env:CSKIN_CODESIGN_CERT_SHA1 = "证书指纹"`，再运行 `.\installer\build-installer.ps1`。脚本会检查 Code Signing 用途、私钥和受信任证书链，并拒绝自签名证书。Let's Encrypt 等仅用于 HTTPS 的 TLS 证书不能用于软件代码签名；证书私钥不得放入仓库或安装包。

安装器输出到 `installer-output\CskinSetup.exe`，默认用户级安装到 `%LOCALAPPDATA%\Programs\PortableCskin`。安装器不需要管理员权限；程序、授权、日志、索引、皮肤缓存和引擎数据都保存在同一应用根目录，复制整个目录即可迁移。

发布物是标准的多文件 .NET 程序，`Assets` 目录中的索引和图标资源会直接安装，不使用单文件自解压。安装包和压缩包都携带完整的 Cskin one-dir 引擎、`xxhash`/`zstandard` 原生模块、`cslol-dll.dll` 和 `mod-tools.exe`；这些文件不会释放到随机 `_MEI` 临时目录。

### 自包含迁移布局

程序文件可以放在任意可写目录；授权和运行数据根就是 `PortableCskin.exe` 所在目录。旧版本程序目录或 `%LOCALAPPDATA%\CskinNative` 中的 `authorization` 会在首次启动时自动导入，目录中的主要内容如下：

- `Engine`：引擎及全部运行依赖；`Engine\data` 保存引擎日志、下载包和覆盖层；`Engine\skins` 保存已缓存皮肤。
- `Assets`：皮肤索引、中文名称和图标。
- `Assets\skin_worker_index.json`：最近一次成功获取的私有索引；`authorization`、`app-settings.json`、`settings.json`：授权与用户设置。

首次运行后产生的缓存和授权信息都在软件目录。迁移或升级时应复制整个目录；更换硬盘后，只要主板信息保持不变，输入原卡密即可受控恢复设备密钥。更换主板、Windows 用户或手动删除授权目录时，需要管理员重置设备绑定。

资源同步只使用 HTTPS 授权代理 API，不运行 `git clone`、`git fetch`、`git reset` 或 `git cat-file`。

应用不会将完整皮肤库打包进安装文件；只会在选择皮肤时按 `skins/<championId>/.../<skinId>.fantome` 精确缓存。

运行数据保存在软件目录：索引缓存位于 `Assets`，皮肤文件位于 `Engine\skins`，引擎运行目录位于 `Engine`，日志位于 `logs`，授权位于 `authorization`。程序不依赖开发机的用户目录或下载目录。

顶部的“退出并清理”会在确认后停止本地引擎，删除软件目录中的引擎数据和已下载皮肤，但保留授权、设置和私有索引缓存。普通关闭按钮不会自动删除资源。

顶部的“切换密钥”会停止当前引擎并返回激活界面；它只清除当前租约和旧的记住密钥，保留设备身份，因此同一台设备可以输入新的激活码。

程序会按英雄记住最近一次选择的皮肤和炫彩。下次启动会恢复选择；只有该皮肤已存在本地缓存时才会自动应用，不会因记忆功能自动下载资源。

顶部“设置”可以配置英雄联盟游戏目录和可选的备用本地引擎。留空时使用安装器自带的 `Engine\Cskin.exe`；路径会保存到程序目录的 `app-settings.json`，并立即重启已选择的引擎生效；清空游戏目录则恢复自动识别。也支持填写国服常见的相对路径 `wegameapps\英雄联盟\Game`，程序会在所有固定磁盘中查找。自动识别优先读取运行中的客户端可执行文件路径，再检查 WeGame、腾讯游戏和 Riot Games 的常见目录，不依赖 WMIC。

保存设置后，程序只会启动已明确选择的引擎文件；未选择备用路径时使用程序目录 `Engine\Cskin.exe`，并以新的 `AATROX_GAME_DIR` 环境重新启动。它不会扫描下载目录或执行其他来源的 `Cskin.exe`。

## 卸载与误报处理

安装器使用 Inno Setup 的标准卸载程序，仅删除本应用的安装目录；压缩包版直接删除其目录即可移除全部程序和缓存。

代码签名和常规安装行为能够降低误报，但不能保证任何安全产品永不报毒。若已签名版本仍被拦截，应保留检测名称、文件 SHA-256、数字签名信息和最小复现包，向对应安全厂商提交误报申诉；不要要求用户关闭防护、添加排除项或使用绕过策略。

## 授权 staging

授权服务代码位于 `server/worker`。Debug 构建默认不启用授权门禁；Release 安装包默认在创建主窗体前显示授权窗口。授权未通过时不会启动引擎或同步皮肤仓库，运行期间每 30 分钟续租验证一次。设备 ID 优先由主板序列号、厂商和型号生成，同一主板重装软件后仍能识别；无法读取主板信息时才回退到设备密钥指纹。远端 Worker 必须先通过 `/health` 检查后再发布 Release 安装包。

中国大陆用户可能无法稳定访问普通 `workers.dev` 域名。当前客户端优先使用 Supabase Tokyo，然后使用 `https://license.re2347.ccwu.cc`，最后才尝试 `workers.dev`。客户端只在连接失败、超时、无效/副本不一致响应、408、429 或 5xx 时切换；401/403、撤销、过期和签名错误不会被备用端点覆盖。详细规则见 [`V3-PRIVATE-REPOSITORY.md`](V3-PRIVATE-REPOSITORY.md)。

授权窗口提供“记住密钥”选项。密钥使用 Windows DPAPI（当前用户范围）加密后保存到软件目录的 `authorization\remembered-license.bin`。只有明确勾选才会在更新或重装后自动恢复；未勾选时不会因残留租约自动进入。进入主界面后，顶部会显示授权到期时间和剩余天数/小时数。

授权管理后台（staging）：https://cskin-license-admin.pages.dev/ 。打开后先输入管理员账号密码（Worker Secret：`ADMIN_LOGIN_USERNAME` / `ADMIN_LOGIN_PASSWORD`），然后可生成预设时长或 1-36500 天的自定义激活码、复制单个/批量结果、按状态或套餐天数筛选、选择后导出 CSV、行内编辑备注、查看完整密钥、撤销激活码、重置设备绑定，并批量删除已过期或已撤销激活码。Worker 每小时整点自动清理过期密钥。`ADMIN_API_KEY` 可勾选“记住此设备”保存到本机浏览器，管理员密码不会保存；历史上未保存密文的旧密钥无法恢复原文。

## Supabase 主认证与资源代理（已上线）

Tokyo 项目 `kzqgphxrghdclkwneqyn` 的 PostgreSQL schema 和 Edge Function 已上线。正常客户端按 `Supabase → Cloudflare 自定义域名 → workers.dev` 自动故障转移；`CSKIN_AUTH_PROVIDER=cloudflare` 只用于紧急运维覆盖。所有 service-role、GitCode 令牌和 `LICENSE_PEPPER` 只通过平台 Secret 注入。
