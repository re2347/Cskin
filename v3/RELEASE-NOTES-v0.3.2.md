# PortableCskin v0.3.2

本版本修复 League Client 英雄选择轮询偶发识别错误和持续覆盖手动选择的问题。

## 修复

- 正确处理合法的 LCU 本地槽位 `cellId=0`，避免本地人马被其他玩家的小丑等英雄覆盖。
- 忽略 `Lobby` 阶段残留的 gameflow 英雄数据。
- gameflow 回退必须明确匹配当前召唤师身份，不再根据单一候选推断本地玩家。
- 新英雄连续检测两次后才同步，过滤单次异常采样。
- 每次实际客户端英雄变化只同步一次，软件中的手动英雄浏览不会被 700 毫秒轮询持续拉回。
- 增加候选、稳定、接受和忽略原因日志，便于定位 LCU 环境差异。

## 发行文件

- `PortableCskin-v0.3.2.zip`：免安装便携版，解压后运行 `PortableCskin.exe`。
- `CskinSetup-v0.3.2.exe`：无 Authenticode 签名安装版，Windows 可能显示“未知发布者”。
- `SHA256SUMS-v0.3.2.txt`：发行文件 SHA-256 校验值。

皮肤文件仍通过授权代理从 GitCode 私有仓库按需下载，发行包不内置 `skins`，也不包含 GitCode 令牌。
