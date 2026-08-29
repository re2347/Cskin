# Cskin v2 diagnosis

There were three separate defects, which looked like one missing-material
problem in the game:

1. The old Git cache was incomplete. The previous client cloned with
   `--filter=blob:none`, so GitCode returned paths but not every blob object.
   The catalog listed skins such as `234021` while `git cat-file` could not
   read them.
2. The old native flow copied a `.fantome` archive without translating the
   package's source skin slot to the slot currently selected by the League
   client. King Viego's PROP record references `skin21`; when the client was
   using the base slot, model/material hashes did not match and the game
   showed a white model or missing purple/black materials.
3. The first v2 frozen-engine package omitted the `xxhash` and `zstandard`
   native `.pyd` modules. That made the engine fail before opening its local
   API on a clean machine, even though an existing installation could appear
   to work from old files.

The v2 repository flow now:

- uses a complete `--no-checkout --depth 1` clone;
- runs `git fsck --connectivity-only` before reusing an existing cache;
- moves an incomplete cache to a timestamped `.repair-*` backup and rebuilds
  it once, then retries the requested package download;
- keeps the old `.fantome -> mkoverlay -> runoverlay` application flow;
- passes the LCU `selectedSkinId` as `targetSkinId` and rewrites only the WAD
  PROP slot hash while copying a package into the engine cache;
- ships the `xxhash` and `zstandard` native modules in both portable and
  installer builds.

Validation performed on 2026-08-28:

- a fresh full shallow clone contains the `234021` blob (3,344 bytes) and has
  no missing connectivity objects;
- the old cache reports missing blob objects for `234021` and `git fetch`
  cannot repair it in place;
- the old `CskinRose` copy contains all nine King Viego packages, confirming
  that the source packages themselves are valid ZIP archives.

Additional clean-machine validation on 2026-08-28:

- `CSKIN_DATA_ROOT` overrides the data root only for an isolated test run;
  normal users keep data beside `PortableCskin.exe`.
- A fresh Debug run with an empty data root on `D:` created `Engine`,
  `skin-repo\.git`, and the bundled asset index without using the existing
  AppData cache.
- Silent installation to `D:\CskinNative-v2-fresh-20260828-1826b` completed
  with exit code 0 and did not auto-start the application.
- `verify-portable.ps1 -Check engine` and `-Check application` both pass under
  Windows PowerShell 5.1; the script is stored as UTF-8 with BOM for that
  host. The release package is version `0.2.1`.

Conclusion: v2 repairs incomplete Git caches automatically, maps each package
to the client's active skin slot, and includes all engine dependencies. The
King Viego source archives are valid; the white-model/material failure was a
slot-hash mismatch in the previous application path, not a damaged archive.

The portable layout is self-contained. `Engine`, `Tools\Git`, `Assets`,
`skin-repo`, engine cache data, settings and authorization all live below the
folder containing `PortableCskin.exe`. Copying that folder migrates the
application and its downloaded resources; Windows DPAPI-protected remembered
license data may still require reactivation under a different Windows user or
computer.

Note: a clean engine may initially log `Catalog loaded: 0 skins` because the
engine catalog contains only packages already copied into its local `skins`
directory. After the first requested package is cached, v2 rebuilds the
catalog and applies it; this initial zero count is not a repository failure.

Material/white-model follow-up:

- The King Viego archives contain a valid `WAD/Viego.wad.client` and `META/info.json`;
  the WAD PROP record points at the package's source slot (`skin21`).
- The previous native flow copied that archive unchanged even when the League
  client was using another Viego slot (often the base slot). The model could
  appear while material and effect references resolved to the wrong hash.
- v2 now passes the LCU `selectedSkinId` as `targetSkinId` and rewrites only
  the PROP slot hash while copying the package into the engine cache. The
  original archive remains untouched, and the rewritten 234021 package was
  validated as a readable ZIP with the expected base-slot hash.

## Application-effect repair (2026-08-29)

LCU synchronization was rechecked and is working: the game-flow response
reported `championId=804`, `selectedSkinId=804000`, and an `InProgress` phase.
The remaining "apply succeeded but the game did not change" issue was in the
overlay application path, not LCU or the remote skin repository:

- Some `.fantome` PROP entries use the champion base-slot hash (`skin0`) even
  when the archive's object name identifies a non-base source slot. The native
  engine only accepted the source-slot hash, so it could copy an unchanged
  archive and never redirect the game lookup to the selected slot.
- The engine treated the absence of `cslol-config.json` after `mkoverlay` as a
  failure. `mod-tools mkoverlay` intentionally writes merged WADs under
  `DATA/FINAL`; `runoverlay --opts:configless` does not require that config
  file.
- The desktop client skipped an apply when only the LCU target slot changed,
  leaving a previous overlay active for the wrong slot.

Changes:

- `Runtime/Cskin/cskin_engine.py` now validates WAD layouts/checksums, accepts
  base/source/target PROP hashes, tries the skin and `baseSkinId` source slots,
  maps even when source and target indices are equal, and logs the request,
  cache path, target slot, `mkoverlay` output, overlay path and runner PID.
- `MainForm.cs` now includes the target skin slot in its duplicate-apply key.
- Rebuilt `Runtime/Cskin/Cskin.exe` and `Runtime/Cskin/cskin_engine.pyc` with
  PyInstaller after the source changes.

Validation on 2026-08-29:

- King Viego `234021` mapped to targets `0`, `1`, `3`, and `21`; each output
  remained a valid WAD/ZIP and used the expected target hash.
- Real `mod-tools.exe mkoverlay` against the installed League game completed
  with exit code 0 and generated `DATA/FINAL/Champions/Viego.wad.client`.
- The source engine API returned `overlayStatus=armed`; its log recorded the
  merged WAD output and a live `runoverlay` PID.
- The clean portable build passed `installer/verify-portable.ps1 -Check all`.
  Version: `0.2.1`; ZIP: `PortableCskin.zip`; size: `93,329,508` bytes;
  SHA-256: `32A0F011476029664DC4F1ADA3E511CCD813AE9979BBAB1FB6DED80463FC6D87`.
  The archive contains `Engine/Cskin.exe`, `Engine/cskin_engine.pyc`,
  `Engine/wmic.exe`, `mod-tools.exe`, `xxhash`, `zstandard`, and bundled Git;
  it contains no `.fantome` files or `Engine/data` cache.

## Portable path repair (2026-08-29, version 0.2.2)

The clean-machine screenshot showed `D:\PortableCskin\Engine` without a
`skins` directory. The older archive was built before the portable-path
repair and could also carry an `app-settings.json` containing an absolute
engine path from another computer. That stale setting could make the client
attach to an external engine, so the current package appeared to apply a
skin while no package was written below the copied folder.

The 0.2.2 build now:

- creates `Engine\skins` during startup, even before the first download;
- ignores a configured engine executable outside the current portable root
  whenever the package contains its bundled `Engine\Cskin.exe`;
- keeps an explicitly selected engine inside the current portable root valid;
- writes repository data to `<package>\skin-repo` and skin archives to
  `<package>\Engine\skins`;
- logs the resolved package root, repository path, engine root, skin ID,
  relative path, final target path and downloaded byte count.

Therefore an empty `Engine\skins` on a clean machine is expected only before
the first successful skin download. After clicking Apply, the application
log must contain `准备下载皮肤` followed by `皮肤下载完成`, with `target=`
under the current package's `Engine\skins`. If those entries are absent, the
remaining cause is repository/network access or an unavailable skin ID, not
reuse of the old computer's path.

The release archive is version `0.2.2`; it intentionally contains no
pre-downloaded `.fantome` files, `Engine\data`, or `Engine\skins` contents.

Build and verification result (2026-08-29):

- `installer/verify-portable.ps1 -Check all`: passed (`Result: ok`);
- package executable version: `0.2.2`;
- archive: `PortableCskin.zip` (`90,393,677` bytes);
- SHA-256: `DB42F37893BE291C39859D767810DE65AA0212F1DC6C72DF8A01641265047DDF`;
- archive inspection found zero `.fantome`, `Engine\data`, and
  `Engine\skins` entries, while `Cskin.exe`, `cskin_engine.pyc`, `wmic.exe`,
  `mod-tools.exe`, `xxhash`, `zstandard`, and bundled Git were all present.

Download verification follow-up (2026-08-29):

- `SkinRepository` now verifies `directoryExists`, `fileExists`, and the
  final byte count immediately after moving a downloaded archive;
- a missing or zero-byte target is reported as a failed download (including
  the possibility that cleanup deleted it), so the UI cannot claim a cache
  hit from a stale or false log line;
- rebuilt archive: `PortableCskin.zip` (`90,393,825` bytes), SHA-256
  `1240EC1E2A6796518CD70988F6E3E055425FDA4BD28CEC685685A6A086F9A597`;
- `installer/verify-portable.ps1 -Check all` passed again.

Application-effect repair follow-up (2026-08-29):

- `EngineClient.SetEngineExecutable` now applies the same portable-root
  filtering as startup discovery, so settings cannot route `/api/apply` to an
  old external engine;
- the desktop log now records the apply request (`skinId`, `targetSkinId`,
  engine path/root, game path), HTTP status/body, and confirmed overlay mode;
- the frozen engine records every `mkoverlay` command and uses a bounded
  180-second timeout. This covers clean machines where merging the large
  game WAD takes longer than the former 45-second limit without introducing
  an unbounded wait;
- rebuild result: `installer/verify-portable.ps1 -Check all` passed;
- latest archive: `PortableCskin.zip` (`90,395,548` bytes), SHA-256
  `962F5657DD0EC1A988DA5EEB0C21EE9CF21A03CA3F00D688FC196120CBB7C5D9`;
- version remains `0.2.2`.

For an actual apply, `logs\application.log` must contain `提交皮肤应用` and
`皮肤应用响应确认`; `Engine\data\selector.log` must contain `Apply requested`,
`mkoverlay completed`, and `Overlay armed`. If the first two exist but the
engine entries do not, the client was not talking to the packaged engine and
the log will now show which executable/port was selected.

## Engine diagnostics and cleanup follow-up (2026-08-29)

The new-machine report showed that LCU selection, skin download, and `/api/apply`
could all report success while the game still displayed the original skin. The
desktop client now records the resolved engine port, executable, portable root,
and game directory whenever it confirms an engine. Every apply response,
timeout, HTTP error, invalid JSON response, and marker fallback also captures
the final 8 KB of both engine logs:

- `<portable>\Engine\data\selector.log`;
- `<portable>\Engine\data\injection\overlay.log`.

Missing, locked, or unreadable diagnostic files are recorded explicitly instead
of being silently ignored. The application log also includes the WAD count and
total bytes reported by the engine, so an `armed` response can be distinguished
from a response that only reached the HTTP handler.

The frozen engine now logs the package existence check, extracted mod file
count, complete `mkoverlay` command, output tail, and the generated
`.wad.client` files. It refuses to return `armed` when the overlay directory is
missing or no WAD was generated. `runoverlay` output is written to
`overlay.log`, with its PID, exit code, and log tail recorded before the result
is returned. This makes a clean-machine permission, game-path, missing-tool, or
mod-tools failure visible in the application log.

“退出并清理” stops the engine and removes downloaded repository/cache data,
then deletes the entire `<portable>\logs` directory. No success message is
written after a successful log deletion, so the command no longer recreates
`application.log` during shutdown. If Windows still holds a file, the cleanup
dialog reports the exact path and the log remains available for diagnosis.

This diagnostic revision keeps application version `0.2.2`; the portable
archive must be rebuilt after the engine bytecode is regenerated.

Final build verification for this revision:

- `dotnet publish` completed with 0 errors and 0 warnings;
- `python -m py_compile Runtime/Cskin/cskin_engine.py` passed;
- PyInstaller rebuilt `Engine/Cskin.exe`, and the output directory was copied
  by enumerating each item from `dist/Cskin`;
- `installer/verify-portable.ps1 -Check all`: passed (`Result: ok`);
- an isolated start of the frozen engine created `Engine/data/selector.log`
  and wrote `Catalog loaded: 0 skins`; the test process was then stopped;
- final archive: `PortableCskin.zip` (`93,337,447` bytes), SHA-256
  `ACCAB895F3B90EE5DA6853A2CF30155D46DDFAF834646D62616152BB969796F3`;
- final executable version: `0.2.2.0`.

## Waiting-for-game follow-up (2026-08-29)

The next clean-machine engine log confirmed that the package is being merged:
`mkoverlay` wrote `DATA\\FINAL\\Champions\\Jinx.wad.client` (132,274,537
bytes), and `runoverlay` started with a live PID. Its first status was
`Waiting for league match to start`, which is the same initial state recorded
by the older CskinRose build before it later wrote `Found League` and
`Redirected WAD`. Therefore this status means the runner is waiting for the
actual `League of Legends.exe` process; it is not evidence that the downloaded
archive or WAD merge failed.

The engine now records the game executable existence/size and a bounded
`tasklist` snapshot, then watches `overlay.log` for up to five minutes. Every
status change records the runner PID, exit code, detected game process, and log
tail. A clean-machine report can now distinguish a runner that is still
waiting for the game, one that finds the game and redirects a WAD, and one that
exits with an injection or permission error. The monitor does not keep the
application in an unbounded loop and does not stop the runner when its
diagnostic window expires.

The waiting-for-game changes were rebuilt and verified:

- `installer/verify-portable.ps1 -Check all`: passed (`Result: ok`);
- archive: `PortableCskin.zip` (`93,340,813` bytes), SHA-256
  `CA5C95BED8C10A7408235049707707A68CDC4C270C1159245F3DC95708A583D6`;
- executable version: `0.2.2.0`;
- the archive still contains no `.fantome`, `Engine/data`, or `Engine/skins`
  entries and includes the updated engine, `wmic`, `mod-tools`, `xxhash`, and
  `zstandard` dependencies.

## Injection confirmation and portable DLL resolution (2026-08-29)

The 05:07 clean-machine report narrowed the remaining failure to the stage
after `runoverlay` found `League of Legends.exe`. `mkoverlay` had produced a
valid `DATA/FINAL/Champions/Viktor.wad.client`, but the runner log stopped at
`Found League` and never recorded the old working build's `[DLL] Init done!`
and `Redirected WAD` markers. This is after download, catalog, WAD merge and
LCU selection, so an `armed` response alone was not sufficient evidence that
the game had loaded the overlay.

The engine now:

- prepends its own `Engine/tools` directory to the child `PATH` and records
  the runner/DLL existence and byte size. `runoverlay` therefore cannot load a
  same-named DLL from another installation or from the system PATH;
- waits a bounded 35 seconds after starting `runoverlay` for `Found League`,
  `Init done` and `Redirected WAD`, recording state changes, exit code, game
  PID/path identity and the complete overlay-log tail;
- returns `injectionStatus=redirected`, `found-game`, `timeout` or a concrete
  failure state. A runner that exits or emits an error is no longer reported as
  a successful overlay;
- includes `injectionStatus` in the desktop response and shows a distinct
  status when the runner is still waiting or when DLL redirection was not
  confirmed.

The bundled `cslol-dll.dll` and `mod-tools.exe` remain byte-for-byte unchanged
from the known working CskinRose build. The new diagnostics distinguish a
late game start from a permission or anti-cheat injection failure without an
unbounded polling loop. If a clean-machine log still ends at `Found League`,
the remaining cause is the target game's process protection or elevation; the
log now includes the runner state and process identity needed to address it.

## Slow game bootstrap follow-up (2026-08-29)

The 06:31 clean-machine log showed the runner finding `League of Legends.exe`
at 06:32:50, but the 35-second confirmation window ended without `[DLL] Init
done!` or `Redirected WAD`. The game process was gone by the timeout. This is
the client bootstrap/transition window, after the package had already been
downloaded and merged into `Talon.wad.client`; it is not a catalog or LCU
selection failure.

The runner confirmation window is now 120 seconds with a hard deadline. The
engine also records `dll-initializing` when the runner has begun loading the
DLL, recognizes common access/injection error text, and logs the confirmation
start and final process/overlay state. The runner is left alive after a timeout
so a delayed game process can still be redirected, while the HTTP request and
desktop polling remain bounded and cannot enter an endless loop.

Validation for this revision:

- `python -m py_compile Runtime/Cskin/cskin_engine.py` passed;
- the frozen engine was rebuilt from the updated source;
- `installer/verify-portable.ps1 -Check all` passed;
- the portable archive was rebuilt without bundled `.fantome`, `Engine/data`,
  or `Engine/skins` state; those directories are created on first download.

## Frozen-engine rebuild and final package (2026-08-29)

The 06:31 report still came from a frozen engine built before the 120-second
confirmation change. The source file had the new constant, but
`Runtime/Cskin/Cskin.exe` and its top-level bytecode were older; the release
scripts only copied those stale files. This made the new-machine package keep
the old 35-second behavior even though the source had been repaired.

The release pipeline now runs `installer/build-engine.ps1` before either
packaging script. It invokes PyInstaller from `Cskin.spec`, compiles the
current source, enumerates the generated one-dir output, and copies every
generated file into `Runtime/Cskin`, including a fresh top-level
`cskin_engine.pyc`. The package scripts then copy that authoritative tree into
the installer and portable staging directories. The verification script also
now computes its default package path after parameter binding, so
`verify-portable.ps1 -Check all` works without an explicit `-PackageRoot`.

Final validation for this revision:

- `python -m py_compile Runtime/Cskin/cskin_engine.py`: passed;
- `dotnet build CskinNative.csproj -c Release --no-restore`: 0 warnings, 0 errors;
- PyInstaller rebuild: passed; frozen bytecode contains the 120-second
  confirmation window;
- `installer/build-installer.ps1 -UnsignedTestBuild`: passed;
- `installer/build-portable.ps1`: passed;
- `installer/verify-portable.ps1 -Check all`: passed (`Result: ok`, bundled Git
  `2.55.0.windows.5`);
- portable package contains 0 `.fantome` files and no pre-created
  `Engine/data` or `Engine/skins` directories; these are created on first
  download;
- portable archive: `PortableCskin.zip`, `90,407,309` bytes, SHA-256
  `E611C680AE7F52B5A0B4B89D62230BD61EF5027430EC42315C0E77338AD75C86`;
- executable version: `0.2.2` (`AssemblyVersion 0.2.2.0`);
- unsigned local installer: `installer-output/CskinSetup-UNSIGNED-TEST.exe`,
  `99,709,418` bytes, version `0.2.2`.
- local signed test installer: `installer-output/CskinSetup-LOCAL-SIGNED-TEST.exe`,
  `99,717,992` bytes, SHA-256
  `79CA9EFE6B67C20ACFB4A3F9F3CF2FE84D89BD91565B0AF8A49C819902560FD9`,
  version `0.2.2`.

The local unsigned installer is a functional test artifact and must not be
distributed. A signed installer should be generated with the existing
`CSKIN_CODESIGN_CERT_SHA1` release process after the local run is accepted.

## 覆盖层快速待命与 LCU 阶段修复（2026-08-29，版本 0.2.3）

13:19 的实机日志确认本轮皮肤应用成功：`mkoverlay` 生成了
`DATA/FINAL/Champions/Yunara.wad.client`，13:20:36 出现 DLL 初始化，
13:20:41 出现 `[DLL] Init done!` 与 `Redirected WAD`。因此下载、皮肤槽位
映射、WAD 合并和注入链路均正常。

等待时间来自应用接口同步执行 120 秒注入确认：请求在 13:19:14 提交，
直到游戏启动并完成重定向后的 13:20:41 才返回，导致按钮一直显示
“正在应用皮肤”，也看不到以前的“覆盖层已准备，等待进入游戏”。

版本 0.2.3 调整为：

- `runoverlay` 启动并通过 0.5 秒存活检查后立即返回，正常状态为
  `waiting-game`，界面显示“覆盖层已准备 · 等待进入游戏”；
- DLL 初始化、`Redirected WAD`、进程退出和注入错误由最长 5 分钟的后台
  监控继续写入 `Engine/data/selector.log`，不阻塞应用按钮；
- `mkoverlay` 仍保留 180 秒硬超时，后台监控也有固定截止时间，不会形成
  无限循环；
- 英雄选择接口返回空阶段但游戏流已返回 `ChampSelect` 时，将游戏流阶段
  合并到界面，不再错误显示“当前阶段 未知”；
- 引擎日志统一脱敏 `--remoting-auth-token`，分享诊断日志时不再泄露 LCU
  临时认证令牌。

本地验证：

- Python 语法编译通过；
- .NET Release 编译通过，0 警告、0 错误；
- 模拟 `mkoverlay/runoverlay` 流程在 `0.734` 秒返回 `waiting-game`，后台
  监控线程保持独立；
- 模拟日志中的 LCU 令牌已替换为 `<redacted>`；
- 修复 `installer/build-engine.ps1` 的 PyInstaller 输出路径：明确使用
  `v2/dist` 与 `v2/build`，并校验 `Cskin.exe` 必须是本次构建产物，避免
  从仓库上层旧 `dist` 误复制冻结引擎；
- 压缩包内冻结 `Cskin.exe` 隔离应用测试在 `1.713` 秒返回
  `waiting-game`，生成 1 个覆盖 WAD，后台监控已启动，且不存在旧版
  `runoverlay confirmation started` 同步等待；
- `installer/verify-portable.ps1 -Check engine` 与 `-Check application` 均
  通过，随包 Git 版本为 `2.55.0.windows.5`；
- 最终压缩包：`PortableCskin-20260829-133157.zip`，`93,358,476` bytes，
  SHA-256 `1D655E34C2FB5E3DA69B9DB0433EB98DF3BAA7B97620C3D4BC7B69D8252CB6CB`；
- 压缩包内引擎 SHA-256：
  `10264A6AA870B0336F1D6E6DF502F884329614740FB2EB75EFDB0DDB160F4D7F`。

## 0.2.3 安装版导出（2026-08-29）

- 使用修正后的 `installer/build-engine.ps1` 重新构建冻结引擎，随后执行
  `installer/build-installer.ps1 -LocalTestBuild`；
- Inno Setup 6.7.1 编译成功，安装包版本为 `0.2.3`；
- 安装内容的引擎与应用启动验证均通过，随包 Git 为
  `2.55.0.windows.5`；
- 签名后冻结引擎隔离应用测试在 `1.835` 秒返回 `waiting-game`，生成
  1 个覆盖 WAD，后台注入监控正常，未出现旧同步等待逻辑；
- 安装内容包含 0 个 `.fantome`，未预置 `Engine/data` 或
  `Engine/skins`，运行缓存仍在安装目录内按需创建；
- 安装包：`installer-output/CskinSetup-LOCAL-SIGNED-TEST.exe`，
  `99,717,944` bytes，SHA-256
  `933727977366AB11BA8BDF16265FFFF8E71F079530E91043F2A4132C6D3C740C`；
- 签名证书为 `Cskin Native Local Test Code Signing`，已嵌入安装包，但
  不是公开受信任的商业代码签名证书，因此其他电脑仍可能显示未知发布者。

## 首次启动资源索引等待修复（2026-08-29，版本 0.2.4）

15:13 的新机日志显示，本地引擎在 `15:13:34` 已经就绪，但首次完整 GitCode
克隆直到 `15:15:14` 才完成。应用动作与启动同步共用了同一个任务，因此界面
持续显示“正在等待本地资源索引完成”。随后同一份日志已经在
`15:15:15` 下载 `804013.fantome` 并成功生成 `Yunara.wad.client`，证明
`C:\其他\LOL\英雄联盟(26)\Game` 中的中文和括号没有破坏参数传递，游戏目录
不是本次等待的原因。

版本 0.2.4 调整为：

- 首次启动先发布随包 `skin_index.json` 的完整路径索引，皮肤列表和按需下载
  不再等待完整 Git 仓库克隆；
- 完整 GitCode 仓库仍在后台同步，用于更新索引和后续离线缓存；
- 后台同步尚未完成时，选中的单个皮肤通过 GitCode Contents API 获取，校验
  HTTP 状态、Base64 编码、声明大小和 ZIP 文件头后再原子落盘；
- API 下载失败且仓库没有正在同步时，才进入原有仓库修复流程，避免首次克隆
  与修复任务同时操作同一目录；
- 应用日志增加 `repositoryReady`、`repositorySyncing` 和实际下载来源，方便区分
  索引等待、API 下载、Git 对象读取和游戏覆盖阶段。

本地全新目录验收：

- 测试根目录位于 `v2/obj`，没有预建 `skin-repo` 或 `Engine/skins`；
- 随包 8968 条路径索引在 `0.116` 秒内发布；
- 完整 Git 同步仍在运行时，`804013.fantome` 通过 Contents API 在
  `0.579` 秒内下载完成并写入新建的 `Engine/skins/804/804010/804013`；
- 下载文件为 `4,649` bytes，声明大小一致且 ZIP 文件头有效；
- .NET Release 构建通过，0 警告、0 错误。
## 2026-08-29 部分复杂皮肤注入后闪退修复

### 现象与定位

- `234043`（王 佛耶戈 / `Revenant Reign Viego (W/SwordSwap)`）能够完成下载、
  `mkoverlay`、DLL 初始化以及三个 WAD 重定向，但游戏在载入阶段直接退出。
- 失败并非 LCU、下载路径或覆盖 DLL 问题。失败局日志在载入阶段中断；同一环境下
  使用其他佛耶戈包能够进入 `GAMESTATE_GAMELOOP`。
- 原包的 `Viego.wad.client` 同时含有 base slot (`skin0`) 和来源 slot
  (`skin43`) 的 PROP 目录条目。旧映射逻辑仍将 `skin43` 条目改成 `skin0`，
  生成两个相同的 WAD 目录哈希；外层目录表与内部资源关系因此冲突，游戏读取时闪退。

### 修复

- `Runtime/Cskin/cskin_engine.py` 在改写前收集 WAD 已有目录哈希。
- 当目标槽哈希已经存在时，保留原包的 base/source 双槽条目，不再制造重复哈希；
  目标槽不存在时仍沿用原有槽位映射，避免回退其他皮肤的修复。
- 引擎会记录 `Slot mapping preserved existing target entry`，用于确认复杂包走了
  兼容路径。

### 验证

- 使用用户实际失败包 `234043.fantome` 对比原包和映射缓存包。
- 修复前：`Viego.wad.client` 的 `skin43` 哈希被改成已有 `skin0` 哈希，产生重复项。
- 修复后实测保留原包 4 个互不重复的目录哈希，冠军 WAD 与原包逐字节一致，
  并通过 WAD 布局和压缩块校验。
- `python -m py_compile` 与真实包回归脚本
  `installer/test-slot-compatibility.py` 均通过。
- 正式冻结引擎已重新构建；本地 `/api/health` 返回 `ok=true, engine=true`。
- 未生成 Debug 免密版、安装包或 ZIP。
## 2026-08-29 0.2.5 部分皮肤闪退：下载源一致性修复

### 最终根因

- 用户提供的最早可用版本位于 `C:\CskinNative`，桌面版本为 `0.1.8`。
- 反汇编旧引擎 `cskin_engine.pyc` 并以同一皮肤包实测后确认，旧版和修复后的
  v2 对 `234043` 都保留原包，不做槽位哈希改写；两版 `mod-tools.exe` 与
  `cslol-dll.dll` 的 SHA-256 也完全一致。
- 真正差异是下载文件。稳定旧仓库 `Re2347/skin` 中的
  `234043.fantome` 为 `1,740,921` bytes、Patch `16.16`、SHA-256
  `8B75E834641FB277193248CBC286CF266F6B8F6C4634180394D02BDE3BE494C6`；
  最新镜像 `Re2347/cloneSkin` 返回 `1,432,418` bytes、Patch `16.17`、
  SHA-256 `D3B8CF744A8277E10126896245ECE0BD73A29CA208C211010471FEA57A529A80`。
- 失败局游戏日志明确记录 `FATAL ERROR. Missing data: 0x0`。错误包的
  `Viego.wad.client` 只有 `442,841` bytes；旧版可用包为 `751,344` bytes。

### 修复

- 保留 `cloneSkin` 用于最新皮肤索引和 Git 后台同步。
- 单个皮肤下载改为稳定仓库 `Re2347/skin` Contents API 优先；稳定仓库没有
  对应文件时才使用 `cloneSkin` 最新镜像。
- 每个下载包新增 `.source.json`，记录缓存格式、来源、Git blob SHA、大小和
  SHA-256；自动应用前会重新计算 SHA-256。
- 没有可信来源标记的旧缓存不再由自动应用直接复用；手动应用会重新下载并替换。
- 下载完成后完整读取 ZIP 中所有 WAD 条目，截断包、损坏包或空 WAD 不再进入引擎。
- 程序版本升至 `0.2.5`，正式 Release 仍要求授权；不恢复 Debug 免密逻辑。
- 根目录继续使用软件自身目录，皮肤、引擎数据、日志和 Git 缓存不会迁移到别处。

### 验证

- `dotnet build -c Release --no-restore`：0 warning，0 error。
- 干净目录首次下载 `234043`：`1.321s`，稳定包 `1,740,921` bytes。
- 预置错误的 `1,432,418` bytes 缓存后再测试：在 `1.487s` 内替换为
  `stable-skin`，blob `7f509625f65a31f557d2036d01d390f0c7947de0`。
- 稳定包槽位回归：4 个 WAD 路径哈希全部唯一，冠军 WAD 保持原包不变且校验通过。
- 稳定包离线 `mkoverlay`：成功生成 `UI`、`Viego`、`Common` 三个 WAD。

## 2026-08-29 0.2.6 王 佛耶戈二次闪退：历史兼容包锁定

### 新日志结论

- 16:08 实机日志确认稳定源的 16.16 包仍会闪退。引擎已成功下载
  `1,740,921` bytes、完成三个 WAD 的 `mkoverlay`、DLL 初始化和全部重定向；
  游戏自己的 `r3dlog` 随后在 `LoadGlobalEffects` 阶段报
  `FATAL ERROR. Missing data: 0x0`。
- 15:48 的 16.17 包与 16:08 的 16.16 包触发完全相同的错误；15:49 能进入
  `GAMESTATE_GAMELOOP` 的记录是崩溃后未继续覆盖的游戏重启，不是皮肤成功。
- 因此本次问题不是路径、下载、LCU、槽位映射或 DLL 注入，而是该皮肤后续版本
  与当前 16.17 客户端的全局资源不兼容。

### 最久远版本对比与修复

- 在 `Cskin_v5_Portable` 找到实际旧包：Patch 16.14，`464,785` bytes，
  SHA-256 `6D429D86E7868A81BA0F8672E3DA919788A5314C0D18FF688C1AC599752F52BD`。
- 旧包仅覆盖 `UI.wad.client` 和 `Viego.wad.client`；会崩溃的 16.16/16.17 包
  还加入了 `Common.wad.client`。这与崩溃发生在全局特效加载阶段相符。
- `234043` 现在固定从 GitCode `Re2347/skin` 的历史提交
  `334d6a69f01836a075662aa501aed297d5a3564b` 下载 Patch 16.14 兼容包；
  同时固定校验文件大小和 SHA-256，远端响应异常时不会落盘。
- 缓存来源策略升级为 `source-policy-v2`。已有 16.16/16.17 缓存会自动失效并
  重新下载；兼容源不可用时保留旧文件用于诊断，但禁止应用，避免再次闪退。
- 兼容来源、Git ref、blob SHA、大小和 SHA-256 都写入 `.source.json`，新机与
  已使用过旧缓存的机器走同一套结果。

### 验证

- GitCode 历史提交 API 返回 `464,785` bytes，blob
  `800817a43669259a974946508261d075617e6bb2`，SHA-256 与本机最早版本完全一致。
- 首次下载回归从预置的 16.16 坏缓存开始，`0.932s` 内替换为
  `compat-skin-patch-16.14`，并通过来源、ref、大小和 SHA-256 校验。
- 16.14 包槽位回归通过：`Viego.wad.client` 4 个目录哈希全部唯一，已有目标
  槽位被保留，WAD 布局与压缩块校验通过。
- 16.14 包针对当前 16.17 游戏目录离线 `mkoverlay` 成功，只生成
  `UI.wad.client` 与 `Viego.wad.client`，不再覆盖引发崩溃的全局 `Common` WAD。
- `dotnet build -c Release --no-restore`：0 warning，0 error。
- 干净发布目录 `PortableCskin-0.2.6-test` 的 `application` 和 `engine` 检查均为
  `Result: ok`；包含 0 个 `.fantome`，没有预建 `Engine/data` 或 `Engine/skins`。
- 当前实机目录 `PortableCskin-20260829-133157` 已同步 0.2.6 应用，并已实际将
  `234043` 缓存替换为上述兼容包。该目录不是发行空包，因此通用便携包检查会按
  设计拒绝其中已经按需下载的 `Engine/skins`。
- 停止了两个从旧 `AppData/Local/CskinNative/engine` 残留运行的引擎进程；未删除
  旧目录或其他用户文件，端口 5336 已无旧进程监听。

## 2026-08-29 0.2.7 更正：保留 16.17 包并隔离损坏的 Common 音频库

0.2.6 的整包回退方案已撤销。当前皮肤资源必须以 16.17 为准，回退到 16.14
会丢失后续资源，不能作为正式修复。

- 下载源改为只优先使用 `Re2347/cloneSkin` 的 `main` 分支，不再先取仍停留在
  16.16 的 `Re2347/skin`；缓存格式升级为 `latest-source-v3`，0.2.6 的 16.14
  缓存会自动失效并重新下载。
- 当前 `234043` 16.17 包为 `1,432,418` bytes，SHA-256
  `D3B8CF744A8277E10126896245ECE0BD73A29CA208C211010471FEA57A529A80`，blob
  `2a29d760ed27d6c4d94d50d901fe1f1d3cd5ab66`。
- 包内 `Common.wad.client` 只有一个条目，覆盖游戏已经存在的全局 Wwise 音频
  bank（内容以 `BKHD` 开头）。该文件 SHA-256 为
  `DC07002681762FBDA866C429588DD0AC3F56BC0D2C058DCC5984EEC99D95CC2B`，它使
  16.17 客户端在 `LoadGlobalEffects` 阶段报 `FATAL ERROR. Missing data: 0x0`。
- 引擎仍使用完整的最新 16.17 包；仅在临时注入目录中，且 skinId、文件名与
  上述坏文件哈希全部精确匹配时，隔离这个 Common 音频 WAD。最新的
  `Viego.wad.client` 和 `UI.wad.client` 都保留，不修改下载缓存。
- 如果上游发布修好的 Common 文件导致哈希变化，引擎会保留新文件并记录
  `Compatibility filter kept updated global WAD`，过滤不会永久屏蔽未来修复。
- `PortableCskin-0.2.6-test` 已按要求移入 Windows 回收站，可从回收站恢复；
  没有删除当前工作目录、授权或日志。

### 0.2.7 验证

- 从预置 16.14 缓存开始的下载回归在 `1.317s` 内替换为 `cloneSkin/main` 的
  16.17 包，缓存标记为 `latest-source-v3`、`latest-cloneSkin`，大小、blob 和
  SHA-256 均与远程 API 一致。
- 全局 WAD 单元回归通过：精确匹配的坏 Common 哈希被移除；模拟上游修改任意
  字节后哈希变化，文件被保留；`Viego.wad.client` 与 `UI.wad.client` 始终保留。
- 正式冻结引擎隔离应用 16.17 `234043`：`3.039s` 返回 `waiting-game`，后台监控
  正常启动，覆盖层只生成 2 个 WAD，日志确认
  `Compatibility filter removed incompatible global WAD`。
- `dotnet build -c Release --no-restore`：0 warning，0 error；新发布应用和引擎的
  `verify-portable` 检查均为 `Result: ok`。

## 2026-08-29 0.2.8 展开式 WAD 与 234043 UI 崩溃修复

本次根据 `16:37` 的实际运行日志确认了两个彼此独立的问题。

- `157087` 确实存在于 `Re2347/cloneSkin` 的 `main` 分支，路径为
  `skins/157/157087/157087.fantome`，GitCode Contents API 返回 blob
  `90d42d6b5a33d149f2c773af1f5caf1f7b76f182`、大小 `8,452,658` bytes。
  旧校验器只接受包内直接存在的 `WAD/*.wad.client`，但该包使用合法的展开布局
  `WAD/Yasuo.wad.client/assets/...`，因此被误报为“远程仓库中未找到皮肤”。
- 下载完整性校验现同时识别打包 WAD 和展开式 WAD，并读取所有相关 ZIP 条目以
  触发 CRC 校验；危险的绝对路径和 `..` 路径会被拒绝。
- 引擎遇到展开式包时，先调用随包 `mod-tools import` 生成真实
  `WAD/Yasuo.wad.client`，再将源皮肤槽位映射到 LCU 当前选择槽位，最后执行原有
  `mkoverlay -> runoverlay`。不会依赖系统安装的工具或其他软件目录。
- `234043` 的 16.17 包来自 `cloneSkin/main`，下载来源没有错误。失败局中 DLL 同时
  重定向了 `UI.wad.client` 和 `Viego.wad.client` 后游戏报
  `FATAL ERROR. Missing data: 0x0`；紧接着 `234019` 只重定向 Viego WAD 并正常进入
  `GAMESTATE_GAMELOOP`。因此在已确认损坏的 Common 之外，精确过滤 `234043` 的
  UI 文件 SHA-256 `9353430E53712CBDE0497D7C6AF103E7A0082D8DC6EE4F45820ADCC4D508F37F`。
  过滤仍以皮肤 ID、文件名和 SHA-256 三项同时匹配为条件，上游换成修复文件后会
  自动保留，不影响其他皮肤。

### 0.2.8 验证

- 真实首次下载 `157087`：索引 `0.121s` 就绪，GitCode Contents API 下载
  `2.958s`，文件大小 `8,452,658` bytes，来源 `latest-cloneSkin`，blob
  `90d42d6b5a33d149f2c773af1f5caf1f7b76f182`，结果 `ok`。
- 冻结引擎处理真实 `157087`：日志确认执行 `Expanded package imported` 和
  `Imported package slot mapping completed`，`2.869s` 返回 `waiting-game`，覆盖层
  只有 `DATA/FINAL/Champions/Yasuo.wad.client`。
- 冻结引擎处理真实 `234043`：Common 与 UI 的精确坏哈希均被过滤，`1.541s`
  返回 `waiting-game`，覆盖层只有 `DATA/FINAL/Champions/Viego.wad.client`。
- `python -m py_compile` 通过；`dotnet build -c Release --no-restore` 为 0 warning、
  0 error；发布文件的 `verify-portable -Check engine` 与 `-Check application` 均为
  `Result: ok`。
- 当前可运行目录已原位同步为 `0.2.8`，保留授权与设置；发布 EXE 启动成功，发送
  正常关闭请求后退出码为 0，应用和引擎均无残留进程。
- 当前引擎 SHA-256：
  `527ED5A9B00443159B5D57810A226E5815E4F18BF8C686DB37ECDD4157CFB9CA`；应用 DLL
  SHA-256：`A211164D71748EF2749B079F1C9F4C13CFDA767A669B8771FAB4AA4308CF1FBE`。
- 自动测试能验证下载、导入、槽位映射、覆盖层内容和注入等待状态；`234043` 是否
  已完全消除游戏内崩溃仍需进行一次实际对局确认。

## 2026-08-29 0.2.9 Viego PROP 兼容与 Yasuo HUD 槽位修复

`0.2.8` 的实际对局日志进一步确认：

- `234043` 已单独过滤 Common 与 UI，覆盖层只重定向
  `Champions/Viego.wad.client`，游戏仍在加载阶段报
  `FATAL ERROR. Missing data: 0x0`，因此剩余问题确定在 Viego WAD 本身。
- 包内 Viego WAD 只有 4 个 PROP 条目。四个路径哈希在当前 16.17 游戏 WAD 中
  全部存在，但包内内容与当前游戏版本全部不同；它用旧 Skin43 主配置和两组旧动画
  配置覆盖当前客户端。可正常运行的 `234020` 只包含一个基础槽位映射条目。
- 对 SHA-256 为
  `76C9B6D83E092C7724567E1AE8209204A35186FBC965CC9F6DCE8B66F2965CA7`
  的 `234043` Viego WAD，现只保留基础槽位哈希 `4EDF655BF6F3D880`，让 Skin43
  主配置和动画继续使用当前游戏 WAD 的版本。上游文件哈希变化后不再修剪。
- `157087` 的模型已在实际对局中生效，但 HUD 仍显示原皮头像。包内
  `Yasuo_Circle.tex` 与当前游戏原皮内容完全相同，真正的 Skin87 头像位于
  `Yasuo_Circle_87.tex`。该包的展开路径还额外带有 `assets/hematite` 前缀，
  `mod-tools` 会将它导入为游戏不会读取的另一组 WAD 哈希；仅替换文件内容仍会显示
  原皮头像。引擎现会在展开包导入前把 `_87` HUD 资源复制为基础槽位名称，并将
  路径规范为游戏实际读取的 `assets/Characters/.../Yasuo_Circle.tex`，随后再执行
  `mod-tools import`。
- 新增 `installer/test-expanded-hud-compatibility.py`。真实 `157087.fantome` 经
  别名和 `mod-tools import` 后，基础 HUD 条目的 SHA-256 为
  `F4231C0DE9D3CA10D85E423E4E8AF78270EA2AEE103BFE8A900622F4957A4950`，
  与 `_87` 头像一致，并不同于当前游戏原皮头像
  `FF586ACC62C18BE2A9CD2FF8F9F0702096117A0E369F8660F8C6254900F56C79`。
- 源码 `py_compile` 通过，`dotnet build -c Release --no-restore` 为 0 warning、
  0 error。冻结引擎真实包回归：`157087` 在 3.920 秒内导入展开包并生成单个
  Yasuo 覆盖 WAD；`234043` 在 2.243 秒内过滤 UI、修剪 Viego WAD 并生成单个
  Viego 覆盖 WAD；两者均返回 `waiting-game` 且没有旧同步阻塞逻辑。
- 发布目录的 `verify-portable -Check engine` 与 `-Check application` 均为
  `Result: ok`。当前工作目录 `PortableCskin-20260829-133157` 已原位同步为
  `0.2.9`，授权、设置、日志、仓库、皮肤缓存和引擎数据均保留；应用启动后正常
  关闭，退出码 0，应用、引擎及覆盖进程均无残留。
- 最终应用 DLL SHA-256：
  `DE4D5E95D1E4C0252DBAF0B3F409A874FD3F7DD563040803BCFBEFDD8DC43A3D`；
  最终引擎 SHA-256：
  `3A67272BC44AECC1EE96070282EB5FA14070340DCE93BE22C6EF97527F0B49C6`。
  本轮未生成 ZIP 或安装包。`234043` 的自动测试已覆盖包过滤、WAD 修剪、合并与
  注入待命；是否完全消除实际游戏加载崩溃仍需一次真实对局确认。

## 2026-08-29 0.2.10 Viego 换剑与 Yasuo HUD 双槽位修复

`0.2.9` 的实际游戏结果确认佛耶戈已经不再崩溃，但上一版兼容修剪范围过大；
亚索头像则只覆盖了客户端未实际使用的一个 HUD 资源变体。

- `17:30:09` 的 Yasuo 游戏日志完整进入 `GAMESTATE_GAMELOOP`，引擎日志确认新版
  HUD 别名、WAD 合并和重定向均执行成功；截图中的 HUD 仍显示接近空白的原皮资源。
- `17:31:15` 的 Viego 游戏日志也完整进入 `GAMESTATE_GAMELOOP`，确认 `0.2.9`
  已解决 `FATAL ERROR. Missing data: 0x0`；但 `Ctrl+5` 无法切换武器。
- 重新解析 `cloneSkin/main` 当前 `234043.fantome` 后确认，Viego WAD 的四项分别为：
  `26162BD67C7EB042 = Animations/Skin0.bin`、
  `50AA7AC5B646F513 = Animations/Skin43.bin`、
  `4EDF655BF6F3D880 = Skins/Skin0.bin`、
  `12BCB8373C9B2D16 = Skins/Skin43.bin`。`0.2.9` 将前三项中的两个动画配置也删除，
  因而同时删除了包内 `CTRL5_Base/Assassin/Mage/Support/Marksman/Tank/Fighter`
  换剑动画状态。
- Viego WAD 现在保留两个动画配置和基础槽位配置，仅排除导致 16.17 加载崩溃的
  旧 `Skins/Skin43.bin` 主配置。UI WAD 不再整包删除：只移除全局 UI 注册表哈希
  `704B0292633A00A7`，保留专属剑状态界面哈希 `F221D11BC2E0CAE6`。损坏的 Common
  音频 WAD 仍按皮肤 ID、文件名和 SHA-256 精确隔离。
- `Yasuo_Circle_87.tex` 已实际解码验证为有效的 `128x128` DXT5 头像，并非空文件。
  当前腾讯客户端 HUD 仍会读取 `Yasuo_Square.tex`，而包内该文件是原皮头像。
  展开包导入前现将 `_87` 头像同时写入游戏真实的 Circle 和 Square 路径，避免不同
  HUD 布局继续回退到原皮资源。

验证结果：

- Python 编译以及 Viego WAD、Yasuo HUD 源级回归全部通过。修复后的 UI WAD 只有
  专属剑状态条目；Viego WAD 恰好保留上述三个安全条目；Circle 与 Square 导入
  条目内容都与 `_87` 头像一致。
- 冻结引擎处理 GitCode 当前 `157087`：`4.152s` 返回 `waiting-game`，生成一个
  Yasuo 覆盖 WAD；处理当前 `234043`：`3.628s` 返回 `waiting-game`，生成
  `UI.wad.client` 和 `Viego.wad.client` 两个覆盖 WAD，没有旧同步阻塞逻辑。
- `dotnet build -c Release --no-restore` 为 0 warning、0 error；发布目录与当前便携
  目录的 `verify-portable -Check engine/application` 均为 `Result: ok`。
- `PortableCskin-20260829-133157` 已原位同步为 `0.2.10`；授权与设置保留。应用启动
  后正常关闭，退出码 0，应用、引擎和覆盖进程均无残留。
- 最终应用 DLL SHA-256：
  `E6C3625B39A36253D16172828A5288033E6EE74FBED110B3C5034BF521E476D9`；
  最终引擎 SHA-256：
  `6818F86FE05B8228CB9E2F99F8FD347FA8266B11F20B76FA35101D0882544304`。
  本轮未生成 ZIP 或安装包。HUD 显示和 `Ctrl+5` 交互仍需各进行一次实际对局确认。

## 2026-08-29 0.2.11 HUD 空白与佛耶戈加载崩溃证据化修复

本轮针对用户提供的 `17:46-17:49` 游戏日志完成了基于证据的收紧：

- 用户日志中 `157009` 和 `157087` 均成功进入 `GAMESTATE_GAMELOOP`，但两局都出现
  空 HUD；两局都经过 `Circle -> Square` 通用别名。因此该通用别名已撤销。展开包现在
  只把带源槽位的头像写入真实 `Yasuo_Circle.tex`，不再把 Circle 内容复制到
  `Yasuo_Square.tex`。Square 是独立资源，保持包原有内容并由游戏自行决定是否读取。
- 用户日志中 `234043` 的 `runoverlay` 已成功重定向 `UI.wad.client` 和
  `Champions/Viego.wad.client`，但游戏在 `LoadGlobalEffects` 后报告
  `FATAL ERROR. Missing data: 0x0`。因此本轮将已知坏 Viego WAD 收紧为仅保留：
  `26162BD67C7EB042`（`Animations/Skin0.bin`）和
  `4EDF655BF6F3D880`（`Skins/Skin0.bin`）；删除 `Animations/Skin43.bin`，并继续删除
  `Skins/Skin43.bin`。Common 仍按完整 SHA-256 精确隔离，UI 仅保留专属剑状态条目
  `F221D11BC2E0CAE6`。
- 引擎新增 WAD 证据日志。每次应用会记录原始游戏 WAD、导入后的 mod WAD、兼容性处理
  后 WAD 和最终覆盖 WAD 的存在性、文件大小、条目数量、索引 SHA-256，以及关注条目的
  路径、哈希、压缩/解压大小、内容 SHA-256、前 24 字节和解码结果。日志还会输出四个
  阶段的 `WAD evidence comparison`，明确指出条目是缺失、保留还是被改变。
- `mod-tools` 现在分别记录 stdout、stderr 和退出码；应用时如果游戏进程已经运行会拒绝
  重建覆盖层并写出进程快照，避免在 WAD 被占用时二次应用导致误判。
- 后台覆盖监控会在游戏进程结束或 runner 退出后自动扫描最新 `GameLogs/*_r3dlog.txt`，
  记录英雄、客户端 SkinID、是否进入 `GAMESTATE_GAMELOOP`、最后加载阶段、是否出现
  `FATAL ERROR`、错误原文、日志绝对路径和大小。没有新游戏日志时也会明确记录
  `logFound=False`，不再把旧日志当作本次结果。

验证结果：

- Python 源码与回归脚本编译通过。
- 使用 GitCode `main` 当前 `234043.fantome`（1,432,418 bytes）运行
  `test-global-wad-compatibility.py`：Common 精确删除，UI 保留 1 条专属状态，Viego
  WAD 保留 2 条基础配置，WAD 校验通过。
- 使用 GitCode `main` 当前 `157087.fantome`（8,452,658 bytes）运行
  `test-expanded-hud-compatibility.py`：只生成 Circle 别名，Square 未被写入目标路径，
  `mod-tools import` 后 Circle 内容与 Skin87 源头像一致。
- 冻结引擎真实包回归：`234043` 在 `3.251s` 返回 `waiting-game`，生成 UI 和 Viego
  两个覆盖 WAD；`157087` 在 `3.921s` 返回 `waiting-game`，经 `mod-tools import`
  生成单个 Yasuo 覆盖 WAD。两项均启动后台诊断且没有旧同步阻塞逻辑。
- 用用户本次崩溃的真实 `17:48:55 r3dlog` 验证自动结果分析：正确输出
  `result=fatal`、`champion=Viego`、`enteredGameLoop=False`、
  `FATAL ERROR. Missing data: 0x0` 及最后加载阶段。
- `PortableCskin-20260829-133157` 已原位同步到 `0.2.11`，授权、设置等用户数据保留；
  `verify-portable -Check engine/application` 均为 `Result: ok`。最终应用 DLL SHA-256：
  `3C39D1DF65A623CD7C71B1A8C4E1E37213D21176FE68FDB50CC85C18FBD6C059`；
  最终引擎 SHA-256：
  `8D6B4838CA0D8845D8D94A5FC499C5AD41C8898D29F07E179580141D941B4A7A`。
- 版本提升为 `0.2.11`。本轮未生成安装包或压缩包；需在实际游戏中确认 Viego 的进入
  游戏结果和目标 HUD，新的 `selector.log` 会提供完整证据链。

## 2026-08-29 0.2.12 Yasuo PROP HUD 命名空间与 Viego 换剑动画恢复

本轮依据用户提供的 `18:12` 引擎日志、游戏日志和 WAD 展开结果修复两个仍未解决的问题：

- 亚索 `157087` 的 `.fantome` 包只包含 `Yasuo_Circle.tex`、
  `Yasuo_Circle_87.tex` 和 `Yasuo_Square.tex`。包内 Skin0/Skin87 的 PROP 记录明确引用
  `assets/hematite/Characters/Yasuo/HUD/...` 哈希。旧的展开 HUD 映射只把文件名改成
  `assets/Characters/...`，没有同步修改 PROP 引用，因此最终 WAD 中出现了“PROP 指向
  hematite 哈希、纹理却落在 canonical 哈希”的悬空引用，客户端表现为空 HUD。
- 对游戏 WAD 的实际扫描显示，哈希表中列出的多数 Yasuo `.dds` 名称并不存在于当前
  `Yasuo.wad.client`，因此排除了“缺少 DDS 缓存”这一方向。`Circle_87.tex` 已解码为
  有效的 `128x128 DXT5` 纹理，源文件本身不是空材质。
- `0.2.12` 的 `_alias_expanded_hud_archive` 保留目标条目的原始命名空间，尤其保持
  `assets/hematite/...`，让 PROP 哈希和纹理哈希一致；新增 PROP 诊断会在原始包、导入
  包、兼容处理包和最终覆盖包记录 `sourcePresent`、引用次数、`targetHash` 与
  `targetPresent`，若再次悬空可以直接从日志定位。
- 佛耶戈王形态 `234043` 在 `0.2.11` 规则中同时删除了动画
  `50AA7AC5B646F513 = data/characters/viego/animations/skin43.bin` 和主配置
  `12BCB8373C9B2D16 = data/characters/viego/skins/skin43.bin`。历史 `0.2.10` 实机日志
  已证明保留 Skin43 动画时可以进入游戏并支持换剑，当前规则因此过度删除。`0.2.12`
  恢复 `50AA7AC5B646F513`，继续删除已确认会触发 `FATAL ERROR. Missing data: 0x0` 的
  `12BCB8373C9B2D16`，并保留基础槽位 `26162BD67C7EB042`、`4EDF655BF6F3D880`。
  Common WAD 仍按已知 SHA-256 精确过滤，UI WAD 仅删除旧全局注册表并保留专属换剑
  控制器。

验证结果：

- `python -m py_compile`：引擎和两个兼容性回归脚本通过。
- `test-global-wad-compatibility.py`：`234043` 修复后恰好保留三个预期条目，Common
  坏条目被删除，专属换剑 UI 保留，Skin43 动画保留，旧主配置删除。
- `test-expanded-hud-compatibility.py`：`157087` 导入后保留 hematite 命名空间，
  `Circle`/`Circle_87`/`Square` 目标存在，canonical Circle 别名不再生成，导入后的
  Circle 内容与 Skin87 源纹理一致。
- 冻结引擎隔离 apply 回归通过：`157087` 在 `4.500s` 返回 `waiting-game`，通过
  `mod-tools import` 并生成 1 个 Yasuo 覆盖 WAD；`234043` 在 `2.708s` 返回
  `waiting-game`，过滤 Common、修剪 UI/Viego 并生成 2 个覆盖 WAD。两项均启动后台
  监控且未触发旧的同步等待逻辑。
- `PortableCskin-20260829-133157` 已同步应用版本 `0.2.12` 和新的冻结引擎；便携版
  `verify-portable.ps1 -Check engine` 与 `-Check application` 均返回 `Result: ok`。
- 当前尚未在用户实际对局中验证 `Ctrl+5` 和 HUD 显示；上述为源级、WAD 结构及
  `mod-tools import` 验证。若仍有异常，请提供本轮生成的 `Engine/data/selector.log`
  与对应 `GameLogs/*_r3dlog.txt`，其中新增诊断会显示具体悬空引用或缺失条目。

## 2026-08-29 0.2.13 Yasuo HUD 安全回退与 Viego 未响应隔离

依据用户 `18:54-18:58` 日志，本轮确认 `0.2.12` 仍未达到实际可用状态：

- Yasuo `157087` 的日志显示引擎确实生成并注入了覆盖 WAD，且
  `assets/characters/yasuo/hud/yasuo_circle.tex` 内容已变为 Skin87；游戏也进入了
  `GAMESTATE_GAMELOOP`，但客户端仍显示空 HUD。这排除了下载失败和注入失败，说明该
  版本的 HUD 替换与腾讯客户端布局仍不兼容。`0.2.13` 对 Yasuo 展开包跳过 HUD 槽位
  映射，保持原始游戏 WAD 头像，优先保证可用性。
- Viego `234043` 日志明确记录：Common/UI/Viego 覆盖层注入成功，但随后
  `FATAL ERROR. Missing data: 0x0`；`234994` 日志同样注入成功并进入游戏，说明王形态
  资源组合仍会触发客户端加载故障，不能继续把“注入成功”当作“游戏安全”。`0.2.13`
  在引擎和桌面端双重阻止所有 `234xxx` 非原皮应用，记录
  `reason=client-unresponsive-in-game`，并且不启动 `runoverlay`；用户界面直接提示
  已停用，避免再次导致客户端未响应。待有经过当前客户端实机验证的新包后再解除隔离。
- 默认英雄从暗裔剑魔 `266` 改为暗黑元首辛德拉 `134`；选择记忆 schema 提升为 `2`，
  因此旧版本保存的默认英雄不会把新默认值覆盖掉，用户之后主动选择的英雄仍会继续记忆。

验证结果：

- `18:54` Yasuo 日志的证据：注入成功、`enteredGameLoop=True`，但 HUD 仍为空，因此
  本轮改为原皮 HUD 回退。
- `18:57` Viego 日志的证据：`result=fatal`、`enteredGameLoop=False`、
  `FATAL ERROR. Missing data: 0x0`；本轮加入应用前隔离，覆盖层不会再被启动。
- 版本号提升为 `0.2.13`。本轮源码改动尚未推送到远程仓库；推送前需先完成编译和
  本地安全回归，避免把未验证的包发布给其他设备。

## 2026-08-29 0.2.13.1 HUD 通用原皮回退与旧引擎包纠正

用户补充要求：所有皮肤的 HUD 在资源不存在、为空或格式无效时都必须回退原皮头像，
不能只针对 Yasuo。实现调整如下：

- 展开式 Fantome 在导入前扫描所有 `HUD/*.tex`。空文件或非 `TEX` 资源会被移除并记录
  `HUD resource fallback`，缺少该覆盖资源后客户端自然读取游戏原皮头像。
- Yasuo 已知 HUD 即使字节有效仍会在腾讯客户端显示空白，因此该英雄的展开包直接移除
  HUD 覆盖，统一走原皮回退；模型和技能资源不受影响。
- 用户 `18:54-18:58` 日志还证明旧便携目录仍在运行 `0.2.11`/`0.2.12` 引擎规则：
  日志同时出现旧的 Yasuo canonical 映射和 Viego 旧修剪结果。重新构建时会同步新的
  冻结引擎，避免源码已修复但便携目录继续使用旧二进制。
- 本地变更已提交为 Git commit `4bd7814`。已配置远程
  `https://gitcode.com/Re2347/Cskin.git` 并尝试推送，但 GitCode 在 15 秒内未完成
  连接/认证，已主动终止该次推送；未进行无限等待。源码和本地提交仍完整保留，待网络
  或 GitCode 凭据可用后可继续 `git push origin master`。

## 2026-08-29 0.2.14 解除佛耶戈皮肤整体禁用

用户要求恢复佛耶戈皮肤的正常应用流程。本轮移除了桌面端和引擎入口处对所有
`234xxx` 非原皮的安全隔离：选择佛耶戈皮肤时不再直接拒绝请求，也不再在应用前提示
“皮肤已停用”，引擎会继续执行下载、导入和覆盖层启动流程。

针对 `234043` 王佛耶戈仍保留已验证的 WAD 兼容过滤，只精确移除会导致当前客户端
`FATAL ERROR. Missing data: 0x0` 的已知条目，不改变“允许应用”的行为。应用结果和游戏
进程诊断日志继续保留，便于实际对局出现问题时定位。

版本号提升为 `0.2.14`。本轮先完成源码编译验证，未自动生成安装包或压缩包。

## 2026-08-30 0.2.14.1 旧便携目录同步

用户截图仍显示“佛耶戈皮肤已暂时停用”。进程路径诊断确认实际启动的是
`PortableCskin-20260829-133157`，宿主和程序集版本均为 `0.2.13.0`，并非本轮的
`0.2.14.0`；因此截图中的提示来自旧程序文件，不是当前源码或新引擎的逻辑。

本轮停止该目录下的 Cskin 宿主和引擎进程，将 `publish-final` 的 `0.2.14.0` 宿主、
冻结引擎及资源同步回原目录。同步过程没有删除 `Engine\\data`、`Engine\\skins`、日志或
其他用户数据；重新启动该目录的 `PortableCskin.exe` 后，佛耶戈皮肤会进入正常应用流程。
