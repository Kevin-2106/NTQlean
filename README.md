# NTQlean

面向 Windows NTQQ 的**本地存储分析与清理工具**：只读分析 · 副本解密 · 预演优先 · 回收站式清理。

QQ 用久了会积累几十 GB 的图片/视频缓存和无法通过 QQ 自身清理的残留文件。NTQlean 通过解密本地数据库副本建立媒体索引，
帮你**看清**这些文件是什么、属于哪些会话、哪些已无任何引用（孤儿文件），再按类型/时间/大小/会话圈定范围，
**先预演、后清理**，实际删除的文件进入回收站（可恢复）并生成 manifest 清单。

> **本工具不联网、不上传、无遥测；QQ 原始文件仅以只读方式打开。**

---

## ⚠️ 免责声明 / Disclaimer

**使用本工具前请务必阅读并理解以下条款。继续使用即表示你同意全部条款；如不同意，请立即停止使用并删除本软件。**

1. **非官方工具**：本项目是社区开源工具，与腾讯（QQ / NTQQ 的开发与运营方）**没有任何关联**，
   未获得其授权、支持或认可。QQ、NTQQ 及相关名称与商标归其权利人所有。
2. **仅限本人本机使用**：本工具仅面向你**本人、在你本人的设备上、对你自己的 QQ 账号数据**进行分析与清理。
   严禁用于他人设备、他人账号或任何未经授权的场景。
3. **密钥提取的合规风险**：本工具的密钥提取功能通过对运行中的 QQ 进程进行**只读内存扫描**实现
   （不注入、不 hook、不调试、不写入）。此类行为可能受到 QQ 用户协议的限制或被视为违规，也可能触发安全软件告警。
   是否使用该功能由你自行决定，**相关合规责任由你自行承担**。
4. **不提供担保**：本软件按"现状"（AS-IS）提供，不提供任何明示或默示的担保。尽管清理默认只预演、
   实际删除只进回收站，仍无法完全排除数据丢失或程序异常的可能。**使用前请自行备份重要数据。**
   因使用本工具造成的任何直接或间接损失（包括但不限于数据丢失、账号功能受限等），作者不承担责任。
5. **隐私承诺**：本工具不联网、不上传任何数据、不含遥测；账号密钥仅存在于运行内存，不落盘、不写日志；
   自动化测试从不执行实际清理动作。
6. **许可证**：本项目以 MIT License 发布（见 [LICENSE](LICENSE)）。免责声明与许可证并行生效。

<details>
<summary>English disclaimer (summary)</summary>

NTQlean is an unofficial, community-made tool and is **not affiliated with, authorized, or endorsed by Tencent**.
Use it **only on your own machine and only on your own account**, at your own risk.
Key extraction is done via **read-only memory scanning** of the running QQ process (no injection / hooking / debugging),
which may be restricted by the QQ Terms of Service — compliance is your responsibility.
The software is provided **AS-IS** under the MIT License with no warranty of any kind.
Although cleanup is dry-run by default and real deletion only moves files to the Recycle Bin,
**back up important data before use**. The author accepts no liability for any data loss,
account restrictions, or other consequences.
</details>

---

## 安全模型（不可妥协的设计边界）

| 边界 | 说明 |
| --- | --- |
| ❌ 不注入 / 不 hook / 不调试 | 密钥提取只用 `ReadProcessMemory` 只读扫描，不写入对方进程 |
| ❌ 不修改任何 QQ 文件 | 原始数据库 / WAL / 配置仅以只读共享方式打开（`FileShare.Read`） |
| ❌ 不联网 / 不上传 / 无遥测 | 全程离线，没有任何网络代码 |
| ✅ 分析基于副本 | 解密副本与索引只写入工作区（默认 `%LOCALAPPDATA%\NTQlean\`） |
| ✅ 密钥只在内存 | GUI 密码框 / CLI 隐藏输入，不落盘、不写日志、不回显 |
| ✅ 清理默认预演 | 实际清理 = 回收站（可恢复）+ manifest 清单，且仅触碰所配置 `nt_data` 根内的文件 |
| ✅ 双重确认 | 实际清理需要输入确认文字**并**勾选知情声明 |
| ✅ 测试不删文件 | 自动化测试从不执行实际清理动作 |

## 快速开始

要求：Windows 10/11 x64、NTQQ（QQ 9.9.x）。

**方式一（推荐）：直接用 Release 免安装包**——从
[Releases](https://github.com/Kevin-2106/NTQlean/releases) 下载最新的
`NTQlean-v*-win-x64.zip`，解压后双击 `Start-NTQlean.bat`（自包含发布，无需安装 .NET）。

**方式二：从源码构建**（需要 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)）：

```powershell
git clone https://github.com/Kevin-2106/NTQlean.git
cd NTQlean
dotnet build
```

启动 GUI 的两种方式：

```powershell
# 方式一：双击 Start-NTQlean.bat（首次会自动构建）
# 方式二：
dotnet run --project src/NTQlean.App
```

> 首次进入 GUI 后会看到"使用条款与免责声明"（左下角随时可再次打开）。

## GUI 使用说明

整体流程为四步：**① 数据源 → ② 密钥与索引 → ③ 选择与预览 → ④ 清理**。

### ① 数据源

1. 点击 **自动发现**：在标准位置（含 OneDrive 文档重定向与自定义数据根，**不做全盘扫描**）查找 NTQQ 账号。
2. 在账号列表中选中一行，路径会自动填入；也可手动 `浏览…` 修改：
   - `nt_db`：数据库目录（只读）
   - `nt_data`：媒体目录（清理目标根）
   - `工作区`：解密副本与索引的存放处（默认 `%LOCALAPPDATA%\NTQlean\`）
3. QQ 运行时仍可共享读取；如需最可靠的快照，建议先完全退出 QQ。

### ② 密钥与索引

1. **密钥（每个数据库独立）**：
   - 点击 **从运行中的 QQ 提取全部 key**：对运行中的 QQ 进程做只读内存扫描（需 QQ 处于登录状态），
     提取的 key 会用数据库文件内的 salt 交叉验证。**仅限本人本机账号使用。**
   - 或在 CLI 中运行 `get-key` 后**手动粘贴** key（可选路径）。
   - key 只保存在本次运行的内存中，关闭程序即消失。
2. **构建索引**：解密账号数据库副本并建立统一索引
   （覆盖 `files_in_chat` / `rich_media` / `file_assistant` / `group_info` / `emoji`）。
   默认不包含约 13 GB 级的 `nt_msg` 消息大库；勾选后可用于孤儿文件的引用确认，但构建时间明显增加。
   右侧日志面板实时显示进度。

### ③ 选择与预览

- **类型**：图片 / 视频 / 文件 / 语音 / 其他
- **置信度**：`exact`（路径与大小均吻合）/ `strong`（路径吻合但大小不一致）/
  `heuristic`（库内路径已失效，按文件名匹配）/ `orphan`（无任何数据库记录的文件）
- **时间范围**：近 30 天 / 今年 / 去年 / 全部，或自选日期。
  默认截止**一周前**：索引由解密的主库副本构建、未合并 WAL，最近收到的文件可能被误判为孤儿；
  孤儿筛选在没有设置「至」时也始终排除最近一周的文件（CLI 同理，`--to` 可覆盖）。
- **大小范围**（MB）
- **会话范围**：列表中选中会话后点 **排除选中**（等价于 `NOT chat == ...`）
- **高级布尔表达式**（覆盖时间和大小条件）：
  `size >= 10MB AND time < 2025-01-01 AND NOT chat == 123456`
  支持 `AND` / `OR` / `NOT` 与括号；字段：`time`、`size`、`chat`、`kind`、`conf`、`name`、`path`。
- 结果列表支持低开销缩略图预览（复用 NTQQ 自带 Thumb 文件，240px 解码 + LRU 缓存）、
  右键打开文件/所在位置/复制路径、**导出 CSV**。
- **本页所有操作均为预演，不会删除任何文件。**

### ④ 清理

1. **预演报告**：显示将处理的文件数量、总大小与明细，此页面之外不会删除文件。
2. **实际清理（可选）**：需要同时满足两个条件才会执行——
   - 在输入框中输入确认文字 `清理`
   - 勾选 **"我已检查预演报告，理解删除范围与风险，并为重要数据做好了备份"**
3. 执行后文件移入**回收站**（可恢复），并生成 manifest 清单以便追溯；
   仅处理预演命中且位于所配置 `nt_data` 根内的文件。
4. 建议：执行前完全退出 QQ；回收站清空后文件将无法恢复。

## CLI（ntqlean-probe）

```text
ntqlean-probe help                              # 完整帮助
ntqlean-probe discover                          # 定位 NTQQ 数据根 / 账号 / 数据库文件
ntqlean-probe inspect-db <path>                 # 分析单个数据库文件头/格式（无需 key）
ntqlean-probe open-db <path> [--key ...]        # 快照副本 + 解密 + 导出 schema
ntqlean-probe get-key --db <path> [--mask]      # 从运行中的 QQ 只读扫描提取 key（仅限本人本机）
ntqlean-probe analyze --db-dir <nt_db> --data-dir <nt_data> [--key ...]
                                                # 解密账号库并构建索引（[--decrypted-dir] 可复用明文库）
ntqlean-probe select --workspace <dir> [--expr "..."] [--kind ...]
                 [--from ... --to ...] [--size-min 10MB]
                 [--exclude-chat id] [--include-orphans]
                                                # 按条件查询索引（预演，不删除）
```

CLI 全部命令同样遵守安全模型：原文件只读、分析基于副本、key 不落盘不回显、`select` 永远只是预演。

## 工作区里有什么

默认工作区位于 `%LOCALAPPDATA%\NTQlean\`：

- `*.plain.db` — 数据库解密副本（含你的聊天元数据，**请勿分享**；不需要时可整目录删除）
- `index.db` — 媒体索引（选择引擎的查询对象）
- `manifest-*.json` — 每次实际清理的清单（记录了每个文件的去向，便于追溯/恢复）

## 项目结构

```
src/NTQlean.Core      发现 / 头部识别 / 快照 / SQLCipher 解密 / 媒体索引 / 布尔选择引擎 / 预演与回收站
src/NTQlean.Probe     命令行工具（discover / inspect-db / open-db / get-key / analyze / select）
src/NTQlean.App       WPF GUI（数据源 → 索引 → 选择 → 预览/清理）
tools/research/       一次性研究脚本（Python）与社区文档存档
docs/                 research-notes.md（过程记录）、ntqq-database.md（确证事实手册）
```

## 开发与测试

```powershell
dotnet build      # 构建全部三个项目
```

开发期间曾以合成数据（fixtures）做过解密 / 索引 / 选择引擎 / 孤儿分析的集成验证；
仓库当前未附带独立测试工程。注意：任何验证都**不触碰真实 QQ 数据、不执行真实清理**。

## 状态

| 能力 | 状态 |
| --- | --- |
| 数据库解密（副本、只读原文件） | ✅ 实测（login.db 公开 key，HMAC 4/4 页验证） |
| 密钥提取（只读内存扫描 + salt 交叉验证） | ✅ 实测（4 个账号库全部解密） |
| 媒体索引 / 孤儿分析 / 群名提取 | ✅ 开发期合成数据集成验证 |
| 布尔选择（时间/大小/会话 AND OR NOT） | ✅ 开发期 CLI 矩阵验证，索引级查询 |
| WPF GUI 四步流程 | ✅ 真实数据流程已实测 |
| 实际清理 | 已实现（回收站 + manifest + 双重确认）；**开发期验证与自动化流程从不执行真实删除** |

## 许可证

[MIT](LICENSE) © 2026 Kevin-2106。MIT 许可证与上方免责声明并行生效；
在法律允许的范围内，免责声明的条款优先。
