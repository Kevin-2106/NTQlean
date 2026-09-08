# NTQlean Phase 0 — Research Notes

> ⚠️ **免责声明**：本项目为非官方社区工具，与腾讯无关；仅限在本人设备上分析本人账号数据。
> 密钥提取使用只读内存扫描，可能受 QQ 用户协议限制，合规责任由使用者自行承担。
> 完整条款见仓库根目录 README 与 LICENSE。
>
> 阶段目标：验证在 **不注入 QQ、不修改 QQ、不依赖插件框架、严格只读** 的前提下，
> 能否安全读取本机 Windows NTQQ 的本地数据库，并确认其中存在构建存储分析器所需的信息。
>
> 本文件是研究过程记录（含失败尝试）；只写"已确证事实"的结论版见 `ntqq-database.md`。

---

## 1. 研究环境

| 项目 | 值 |
| --- | --- |
| 日期 | 2026-09-05 |
| OS | Windows 10.0.26200 x64（win32） |
| QQ 版本 | **9.9.30.48762**（build 6a73892f），安装于 `C:\Program Files\Tencent\QQNT` |
| wrapper.node | `C:\Program Files\Tencent\QQNT\versions\9.9.30-48762\resources\app\wrapper.node`（101,419,560 字节） |
| 研究期间 QQ 状态 | **正在运行**（多个 QQ.exe 进程） |
| 实现环境 | .NET 8.0.424（正式实现）、Python 3.13（仅一次性研究脚本，位于 `tools/research/`） |

## 2. 数据目录发现（Q1）

本机 Documents 被重定向至 OneDrive（`%USERPROFILE%\OneDrive\文档`），QQ 数据分布在两处：

1. **活跃数据根**：`D:\Tencent Files\QQ\Tencent Files\`（自定义嵌套布局，昨日仍有写入）
   - 账号 `554****906`（活跃，nt_msg.db ≈ **13.1 GB**，最后写入时间为研究当日）
   - 账号 `399****582`、`283****067`、`181****916`（历史账号，数 MB 级）
   - 全局目录 `nt_qq\global\nt_db\`（login.db / Registry.db / sign.db / bc_09.db）
2. **旧数据根**：`%USERPROFILE%\OneDrive\文档\Tencent Files\`（2024 年残留，同一账号 554****906）

账号目录布局（两处一致）：

```
Tencent Files\<uin>\nt_qq\
├─ nt_db\     nt_msg.db, rich_media.db, group_info.db, profile_info.db,
│             files_in_chat.db, file_assistant.db, emoji.db, collection.db,
│             misc.db, guild*.db, *_fts.db(本地搜索索引), settings.db ...
│             （附带 -wal / -shm / *.material 辅助文件）
├─ nt_data\   Pic\YYYY-MM\{Ori,Thumb,OriTemp,ThumbTemp}, Video\, File\,
│             Ptt\(语音), Emoji\, avatar\, PhotoWall\ ... （媒体缓存）
└─ nt_temp\
```

注意：`D:\Tencent Files` 根下有一层 `QQ\` 再接 `Tencent Files`，属于自定义数据根配置，
Probe 的发现逻辑已同时支持标准布局与该嵌套布局。绝不进行全盘递归搜索。

## 3. 数据库格式（Q2）——本机实测

### 3.1 文件头结构（1024 字节明文自定义头）

对 nt_msg.db / rich_media.db / group_info.db / profile_info.db / login.db 等 **全部加密库** 实测：

| 偏移 | 内容 | 说明 |
| --- | --- | --- |
| 0..16 | `53 51 4c 69 74 65 20 68 65 61 64 65 72 20 33 00` = **`SQLite header 3\0`** | ⚠️ 伪装 magic：故意把标准 `SQLite format 3` 拼写为 `SQLite header 3`，标准 SQLite/工具因此直接拒绝识别 |
| 16..18 | `04 00` | 大端 **1024**（自称 page size；⚠️ 与实际 SQLCipher 页大小 4096 不符，见 §4） |
| 18..32 | 全 0 | 真 SQLite 头此处的 payload fraction 应为 64/32/32——为 0 证明非 SQLite 文件 |
| 32..40 | `QQ_NT DB` | NTQQ 专用标记 |
| 40..44 | 4 字节小端（login.db=0x23，账号库=0x9d） | 语义未知 |
| 44..~200 | **protobuf**：field2=128 字符 hex 串（账号库；login.db 中为占位符 `ABCDEFG`）、field3=`"1.1.0.1"`、field4=`"HMAC_SHA1"`、field5=varint（实测 1720082221 ≈ 2024-07-04 建库时间） | field2 疑似账号级标识；全部库共享同一值 |
| ~200..1024 | 全 0 填充 | |

**偏移 1024 起**为 SQLCipher 密文区：前 16 字节为 KDF salt。

本例（账号 554****906，全部 nt_db 库相同）：

```
salt = ed3e285ebf54663841311fdfd89c802f（login.db，2024 旧副本与此完全一致）
```

### 3.2 加密参数（本机实测确证，Q2/Q3/Q5）

用公开 login.db 密钥实测（见 §5），**经验证可用**的参数集：

| 参数 | 值 |
| --- | --- |
| 头部 | 前 1024 字节明文，剥离/跳过 |
| cipher | AES-256-CBC，`cipher_page_size = 4096`（由 WAL 头明文字段独立证实） |
| KDF | **PBKDF2-HMAC-SHA512，`kdf_iter = 4000`**，salt = 偏移 1024 处 16 字节 |
| 页布局（page 1） | `[salt 16][密文 4032][IV 16][HMAC-SHA1 20][填充 12]` |
| 页布局（page ≥2） | `[密文 4048][IV 16][HMAC-SHA1 20][填充 12]` |
| HMAC | HMAC-**SHA1**，输入 = 密文‖IV‖页号(4 字节小端)；hmac_key = PBKDF2-SHA512(enc_key, salt⊕0x3a, 2 轮) |

⚠️ **与社区文档的两处出入（本机实测为准）**：

1. QQDecrypt/nt_msg_db_util 文档写 `cipher_hmac_algorithm = HMAC_SHA1`，但同时
   nt_msg_db_util 的 1.decrypt.py 头部注释把 page size 写成 4096 且强调 pragma 顺序；
   另有资料（qq_dump_db）称 PBKDF2-SHA512 + HMAC-SHA1 混合——与实测一致。
2. 解密后的 SQLite 头字节 20（reserved space）= **0x50 (80)**，而实际 IV/HMAC 布局按
   **48**（16+20 对齐 16）工作且 HMAC 校验 4/4 通过。即"声明 80、实际按 48 布局"
   的不一致现象。以 reserve=48 解密、再按头部声明打开均实测可用（`open-db` 全流程）。
   保留为开放问题（见 §10）。

### 3.3 WAL 观察

- `*.db-wal` 文件头为**明文标准 SQLite WAL 格式**：magic `37 7f 06 82`、版本 3007000、
  **page size = 4096**（独立于主库确认了页大小）、checkpoint 序号、salt1/salt2、校验和。
- WAL 帧头（pgno/commit/salt/校验和）同样明文；**帧内页面数据已加密**（高熵）。
- 结论：只解主库文件得到的是"最后 checkpoint 时点"的一致快照；QQ 运行期间
  新写入在 WAL 中，解密副本不包含。要拿到最新数据应在 QQ 完全退出后复制。

### 3.4 未加密的例外

`nt_data\bc_09.db` 为**普通明文 SQLite**（标准 magic、参数齐全），内容为
`beacon_events` 遥测表（本机为 0 行）。对存储分析无价值，但说明并非所有 `.db` 都加密。

## 4. DB Key 获取研究（Q4，Phase 0 核心）

### 4.1 key 的形态

- **账号库**（nt_msg.db 等）：每个账号一个 key，16 字符随机口令形如 `#8xxxxxxxxxxx@uJ`
  （qq-nt-db 逆向观察）；运行时以 SQLCipher 口令方式传入。QQ 进程内存中还能观测到
  `x'<64位hex key><32位hex salt>'` 形式的 raw key + salt 组合（NapNeko/qq_dump_db）。
- **login.db**：20 字符硬编码字符串，公开值 `BD156D6710D54D8782F4`，对所有账号/设备相同。
  本机实测：该字符串**原样存在于 wrapper.node 偏移 0x37f87a4**（静态可见），
  且用它解密本机 login.db **HMAC 4/4 页校验通过**。

### 4.2 方法分类结论

| 类别 | 结论 | 依据 |
| --- | --- | --- |
| A. 本地配置文件获得 | **Not possible**（现有知识） | QQDecrypt 研究与 qq_dump_db 文档均指明 key 仅存在于进程内存；本机 %APPDATA%\Tencent 未发现 key 材料 |
| B. 从账号数据派生 | **Not possible / Unknown** | key 为独立随机口令，无派生证据；未见公开成果 |
| C. 本地 secret/storage | **Not possible**（nt_msg）；login.db 例外：**Possible（已验证）** | login.db key 为二进制内硬编码常量，静态提取即得，无需任何进程操作 |
| D. Windows Credential / DPAPI | **Not possible**（无公开证据） | 未找到任何 DPAPI 包装的 key 存储 |
| E. QQ 日志 / metadata | **Not possible**（无公开证据） | — |
| F. QQ 进程内存 | **Possible（社区已验证）→ NTQlean 不执行**；用户自行离线 dump 后提供 key 可作为 fallback | windows_ntqq_get_key.ps1（INT3 断点注入+调试循环，WriteProcessMemory）、NapNeko/qq_dump_db（纯外部 ReadProcessMemory 扫描）、qq-nt-db（IDA 附加调试）。前两者分别属于"进程修改"与"读取在线进程"，均超出本项目安全边界 |
| G. hook QQ | **Not acceptable for NTQlean** | 与项目目标（zero-injection）直接冲突 |

### 4.3 对 Phase 0 的含义

- **login.db 可以全自动解密**（自动获取硬编码 key + 本地副本解密）——已在本机端到端验证。
- **nt_msg.db / rich_media.db 等账号库：key 需要用户提供**（用户自行通过社区工具
  提取后交互式输入 Probe；Probe 不落地、不记录 key）。一旦提供 key，
  解密→schema→分析管线与 login.db 完全相同（同一套代码路径，参数已实测确认）。
- 社区方法对 **QQ 9.9.32-51246（2026-07-19 验证）** 有效，与本机 9.9.30.48762 同代，
  版本差距小，时效性风险低。

## 5. 本机端到端验证记录（Q3 / Q5）

以下全部在 QQ 运行中完成，且**从未写入任何 QQ 原文件**：

1. `cp` 原始 `login.db`（17,408 B）到研究目录；
2. Python 矩阵探测（`tools/research/decrypt_probe.py`）在 144 组参数组合中唯一定位
   有效组合（页 4096 / iter 4000 / PBKDF2-SHA512 / salt@1024 / 页 1 密文前 16 字节为 salt）；
3. Python 完整解密（`tools/research/full_decrypt.py`）→ `login_plain.db` 被**标准 sqlite3**
   直接打开，读出 `sqlite_master`：`login_table`（4 行，列 1000–1015）、
   `login_misc_data_table`（14 行，列 2000/2001）——与 QQDecrypt 文档列名吻合；
4. **C# 正式实现**（`ntqlean-probe open-db`）复现同一结果：
   `Decryption succeeded (4 pages)`，**`HMAC verification: 4/4 pages OK`**，
   schema 输出一致；对 `D:\` 数据根的原始 login.db 快照同样成功。
5. 期间修复的实现缺陷：忘记把每页 IV 赋给 `Aes.IV`（一直用零 IV 解密导致首版失败）——
   已修复并有 debug 预览佐证；PBKDF2 输出与 Python `hashlib.pbkdf2_hmac` 逐字节一致。

失败尝试记录（供未来参考）：

- 首版按"标准 SQLCipher reserve=48"假设 IV 在密文后即 [4048..4064)——这是对的；
  但同轮测试的 reserve=80 组合（按头部声明字节 0x50）解出乱码。IV 位置实测在
  [4048..4064)，HMAC-SHA1 20 字节随后，页尾剩余 12 字节为填充。
- 曾怀疑硬编码 key 随版本轮换，对 wrapper.node 全量扫描 20 字符 ASCII 候选
  （工具：`tools/research/find_login_key.py`，192,158 个候选）——无一独立命中；
  随后以字节直查方式在二进制中找到已知 key 原文（偏移 0x37f87a4），排除轮换假设。
  扫描未命中的原因是候选提取用了**不重叠**正则匹配，位于更长 ASCII 串内部的
  key 没有作为独立候选出现——若需重建该工具应改用滑动窗口。

## 6. Schema 与消息表（Q5 / Q6，基于社区 + 本机格式确证）

本机已验证 schema 读取路径（login.db）。消息库 schema 的读取与解密是同一代码路径，
待用户提供账号 key 后即可运行；表结构以社区近期研究为准（QQ 9.9.3 ~ 9.9.32 一致）：

- `nt_msg.db`：`group_msg_table`（群聊）、`c2c_msg_table`（私聊）、
  `recent_contact_v3_table`、`recent_contact_top_table`、`group_at_me_msg` 等。
- 列名为**数字混淆**：`40001`=msgid、`40027`=本地会话、`40030`=私聊对方、
  `40033`=发送者、`40050`=时间（Unix 秒）、`40058`=日期、`40093`=发送者昵称、
  `40800`=消息体（**protobuf**，群/私聊同构）。
- protobuf 内容已有社区字段图谱（miniyu157/qq-dump 的 proto_maps.py 约 260 个字段；
  QQBackup/nt_msg_db_util 的逐字段文档），Phase 1 按需取用，不自行重推。

## 7. 媒体 metadata 与本地文件关联（Q7 / Q8）

### 7.1 数据库侧（社区字段研究，待 key 验证）

- `rich_media.db` → `file_table`：`45402` 文件名、`45403` 存储路径、`45405` 大小、
  `45503` fileUuid、`45001` elementId、`40001` msgId、`40021` 群号/对方。
- `files_in_chat.db` → `files_in_chat_table`：`45403` 路径、`45404` 缩略图路径、
  `45402` 文件名、`45405` 大小、`40050` 时间、`82302` 是否原图；
  （注意：图片条目 `45403` 常为空——社区观察）。

### 7.2 本地文件侧（本机实测）

`nt_data` 媒体目录按类型分域，文件名为 **32 位 hex（MD5）**，按月分目录：

```
nt_data\Pic\YYYY-MM\Ori\<md5>.jpg      Thumb\<md5>_720.jpg
nt_data\Video\YYYY-MM\Ori\...          Thumb\<md5>_0.png
nt_data\File\...                        nt_data\Ptt\...（语音）  nt_data\Emoji\...
```

### 7.3 关联等级预判

| 等级 | 判据 | 用途 |
| --- | --- | --- |
| Exact | DB 记录 md5 == 文件名 md5 且 size 相等 | 可安全用于删除 |
| Strong | DB 存储路径字段直接命中（`45403`/`45404`）且 size 相等 | 可用于删除（双条件） |
| Heuristic | 仅文件名 + size (+时间窗) 匹配 | **不得默认用于删除** |
| Unknown | 无法关联 | 仅统计 |

Probe 已实现 `test-media` 命令对上述字段抽样验证（对解密副本运行）；
本阶段因账号库 key 未取得，**媒体关联路径已实现但未在真实消息数据上执行**。

## 8. 隐私与安全设计（已在实现中落实）

- 原始 QQ 文件仅以只读共享方式打开；一切分析针对 `%TEMP%\NTQlean\<ts>\` 下的副本。
- key 交互输入不回显；不写入日志/配置/崩溃转储；命令行 `--key` 给出 shell history 警告。
- 日志分级 INFO/WARNING/RESEARCH/UNCONFIRMED/BLOCKED；账号号段自动脱敏（554\*\*\*\*906）；
  头部 hex 标识脱敏；不输出任何消息正文、联系人、票据（login_table 含 A1 票据，只读 schema 不读行）。
- Probe 完全离线，无遥测、无 HTTP。

## 9. 被放弃的方法（安全边界）

| 方法 | 分类 | 来源 |
| --- | --- | --- |
| INT3 断点注入 + WaitForDebugEvent 调试循环 | **Blocked（进程注入/修改）** | QQBackup 官方 windows_ntqq_get_key.ps1 |
| frida hook `nt_sqlite3_key_v2` | **Blocked（注入）** | qq-win-db-key 拓展阅读 |
| IDA 附加 QQ 进程调试 | **Blocked（进程附加）** | Mythologyli/qq-nt-db |
| 外部 ReadProcessMemory 扫描在线 QQ | **越界（本项目不做）**；用户自行操作属个人选择 | NapNeko/qq_dump_db |
| LiteLoaderQQNT-QQCleaner 的 Electron IPC 调用 | **Blocked（插件框架注入）**；仅参考其字段定义 | MisaLiu/LiteLoaderQQNT-QQCleaner |

## 10. 开放问题（进入 Phase 1 前值得确认）

1. SQLite 头 reserved=80 与实际 reserve=48 布局不一致的确切原因（Tencent 私有 fork 行为？）。
   对读取无影响（实测可打开），但精确理解有助于鲁棒性。
2. 头部 protobuf field2（128 hex 字符）的真实语义；login.db 中为 `ABCDEFG` 占位符。
3. 头部偏移 40 的 4 字节小端值（0x23 / 0x9d）语义。
4. 账号库 key 提取的用户流程体验（Phase 1 考虑提供引导文档/命令）。
5. WAL 合并读取（当前解密主库 = 最后 checkpoint 快照；是否需要解析 WAL 帧以获取最新消息）。
6. `*.material` 文件（-first/-last）的用途；它们与主库共享 salt，应为同 key 加密的附属数据。

## 11. 参考资料栈

| 资源 | 用途 | 时效 |
| --- | --- | --- |
| QQBackup/QQDecrypt 文档站 + 仓库 | 全平台解密文档、表结构字段研究、统一解密流程 | Windows NTQQ 9.9.32-51246 验证于 2026-07-19 |
| QQBackup/qq-win-db-key | 分平台 key 提取脚本集（官方 PowerShell 脚本为调试器方案，本项目不采用） | 活跃 |
| QQBackup/nt_msg_db_util | 参考解密器（sqlcipher3 + pragma 顺序）、c2c/group 字段总表 | 活跃（要求 Python ≥3.14） |
| NapNeko/qq_dump_db | 外部内存扫描取 key（MIT）；证实 key 仅存于进程内存 | 近期 |
| Mythologyli/qq-nt-db | IDA 全流程逆向记录；passphrase 形态；kdf_iter=4000 | 2025-03 |
| artiga033/ntdb_unwrap | Rust VFS offset 方案；pragma 参数旁证（HMAC_SHA256/SHA1 双探测） | 2025 |
| miniyu157/qq-dump (proto_maps.py) | 40800 protobuf 字段图谱（约 260 字段） | 2025-12 收录 |
| MisaLiu/LiteLoaderQQNT-QQCleaner | 官方 cleaner 的 metadata 字段参考（不采用其注入机制） | — |
| sqlcipher/sqlcipher 源码 (src/sqlcipher.c) | 页面布局、KDF、HMAC 的权威定义 | master |

（完整本地副本见 `tools/research/community-docs/`，含原文档 CC BY-NC-SA 4.0 授权说明。）

---

## 12. Phase 2 实现记录（2026-09-05）

在 Phase 0 结论（PARTIAL，key 需用户供给）之上完成了存储分析与预演清理的核心能力：

### 已实现

- **统一索引**（`NTQlean.Core.MediaIndexBuilder`）：将解密副本中的
  `files_in_chat_table` / `file_table`（含列签名探测的未知表）归一为 `media` 表，
  附带 `nt_data` 全量文件清单、孤儿分析（无数据库引用文件）、
  群名提取（递归 protobuf 扫描 group_info，启发式、仅展示用）、
  大小单位自动校准（KB vs 字节，依据与实际文件比对）。
- **布尔选择引擎**（`SelectionEngine`）：时间/大小/会话/类型/置信度组合筛选；
  表达式编译器支持 `AND / OR / NOT` 与括号（`size >= 10MB AND time < 2025-01-01
  AND NOT chat == 123456`），编译为索引 SQL；孤儿文件可显式纳入。
- **预演清理**（`CleanupPlan`）：文本 + JSON 报告、按类型/会话/置信度分解、CSV 导出。
- **回收站执行器**（`CleanupExecutor`）：`SHFileOperationW + FOF_ALLOWUNDO`，
  先写 manifest；需要显式确认标志 + nt_data 根白名单；**仅 GUI 可达，测试从不调用**。
- **WPF UI**（`NTQlean.App`）：数据源发现 → key 输入（内存）→ 索引进度 →
  筛选面板（类型/置信度/时间预设/大小/会话排除/表达式）→ 虚拟化结果列表 →
  低开销缩略图预览（复用 NTQQ 自带 `Thumb\*_720.jpg / *_0.png`，240px 解码 + LRU）
  → 预演报告 → 双重确认回收站清理。

### 验证方式（遵守"测试不做实际清理"）

- 合成数据集成测试（`tools/research/make_fixtures.py`）：5 条媒体引用
  （名称引用/路径引用/KB 单位/大小不符/文件缺失）+ 2 个孤儿文件 + 群名提取；
  `analyze` → `select` 表达式矩阵（大小/时间/NOT 会话/OR 组合/排除标志）全部符合预期；
  **预演后文件清单 6/6 原封不动**。
- WPF 启动冒烟：窗口渲染正常（截图留档），无崩溃。
- 真实账号数据的完整 GUI 流程依赖用户 key（见 §4），属预期边界。

### 已知边界 / 下一步

- 索引未覆盖 nt_msg 大库（消息级统计需要时再引入，13 GB 解密约分钟级）。
- emoji.db / file_assistant.db 的表结构未逐列核对，依赖列签名探测，命中率待真库验证。
- GUI 全流程自动化测试未做（需要 UIA 驱动），当前以 CLI 同引擎测试 + 启动冒烟覆盖。

## 13. Key 内存提取（2026-09-05，经用户明确授权）

用户作为本机与账号属主，明确授权读取 QQ 进程内存。实现遵循最小侵入原则：

- **纯外部只读**：`OpenProcess(PROCESS_VM_READ|QUERY_INFORMATION)` +
  `VirtualQueryEx` 枚举提交的私有内存区 + `ReadProcessMemory` 分块扫描；
  不注入、不写入、不挂调试器——目标进程不可能被影响。
- **一次扫描全量 keyspec**：SQLCipher keyspec 在内存中形如
  `x'<64位hex key><32位hex salt>'`；按 salt 分桶收集，因此一次扫描即可覆盖
  该账号打开过的**所有**数据库（实测确认每个库有自己的 keyspec）。
- **交叉验证**：候选 key 必须先通过真实数据库页 1 的解密验证
  （HMAC 布局 + SQLite 头部检查）才会被采用。
- **key 不落盘**：`analyze --dump-key` 与 GUI「提取 key」按钮都把 key
  保持在进程内存中；`get-key` 命令按需显示（支持 --mask）。

### 实测结果（QQ 9.9.30.48762 运行中，账号 554****906）

- 扫描 9 个 QQ 进程 / 约 5.1 GB 内存，数秒完成；
- 4 个账号库（files_in_chat / rich_media / file_assistant / group_info）
  全部解密成功，HMAC 全部页通过；
- 真实索引：**221,229 条媒体引用**（174,076 已解析到本地文件 / 47,153 未解析），
  nt_data 共 257,377 个文件，其中 **154,114 个孤儿文件 / 34.2 GB** 无数据库引用；
  385 个会话，240 个群名成功提取；
- 真实索引上的选择查询即时返回：`size>=100MB AND time<2026-01-01` → 1 项；
  孤儿图片 81,267 项 / 25.5 GB。
- 容错：files_in_chat_table 有 4,384 行（约 2%）因空引用解析失败，已逐行
  跳过并记录 rowid，待后续定位字段（不阻塞分析）。

### 边界重申

此能力仅用于用户本人的账号与数据；提取的 key 仅用于解密本机数据库副本。
自动化测试仍然从不执行实际清理动作。

## 14. 孤儿文件成因分析（2026-09-05，真实索引 154,114 个 / 34.2 GB）

对真实索引做 SQL 分类后的结论（按体积排序）：

| 类别 | 数量 | 体积 | 成因 |
| --- | --- | --- | --- |
| Pic/**Thumb** | 105,081 | **27.0 GB (79%)** | 聊天记录**浏览时按需生成的缩略图缓存**。缩略图不写入 files_in_chat（其 thumbpath 字段大多为空），因此天然无数据库引用。与已引用媒体的登记缩略图（media.thumb_rel）完全匹配数为 **0** ——证实它们是独立的浏览缓存层。删除安全：聊天记录仍在则 QQ 会按需重新生成；记录已删则不再生成 |
| Pic/**Ori** | 17,030 | 5.3 GB | 无引用的原图。可能来源：自己发送的图片（files_in_chat 只记录"接收"）、已删除消息的残留、漫游过期的会话。⚠️ nt_msg.db 未纳入索引，**不能据此认为聊天记录无引用**——清理此类需先理解风险 |
| Emoji | 20,765 | 4.3 GB | 收藏/营销表情包缓存，被未纳入索引的 emoji.db 引用；删除后会自动重新下载 |
| Video | 16,995 | 1.7 GB | 视频缩略图与未完成下载的视频缓存，同图片逻辑 |
| log | 210 | 1.0 GB | 运行日志，可直接清理 |
| avatar | 23,112 | 0.2 GB | 群/好友头像缓存，自动重新下载 |
| Ptt | 5,336 | 0.1 GB | 语音缓存（部分可能已过期） |
| File/其他 | ~4,600 | 0.6 GB | 传输文件缓存 |

**反向验证**：47,153 条"数据库有记录但文件缺失"（confidence=missing）的文件名在磁盘上
出现次数为 **0** ——说明 missing 是"未下载/已删除"，而不是路径映射失败。

**清理建议分级**：log / avatar / Thumb / Emoji ≈ 32.5 GB 属可再生缓存，风险最低；
Pic/Ori 的 5.3 GB 需先索引 nt_msg.db 确认真实引用后再决定；建议 GUI 首轮只勾
orphan + 按类型（image/video 域）与时间范围组合，逐月查看预演报告。

## 15. nt_msg + emoji 索引结果（2026-09-06）

新增：emoji.db 纳入标准索引；nt_msg.db（13.1 GB）按需解密（`--include-nt-msg` /
GUI 勾选，明文副本放 `D:\NTQlean\nt-msg`），其消息体（40800，3.48 GB blob）经
并行指纹扫描建立 md5 引用表（`nt_refs`），孤儿文件据此获得 `ref_count`。

### 实测（账号 554****906）

- nt_msg 解密：3,205,950 页 HMAC 全过；引用扫描 13,568,863 行，跳过 80 个坏行
  （QQ 运行中快照的固有微小不一致）。
- **孤儿重分类（核心结论）**：
  - 全部孤儿中：**16,912 个 / 4.2 GB 被聊天记录消息引用**（默认从清理中排除）；
    真无引用 137,271 个 / 30.1 GB。
  - **Pic/Ori 孤儿：14,627 个 / 4.0 GB 被消息引用；真正无引用仅 1,985 个 / 1.2 GB。**
    —— 证明没有 nt_msg 引用层时清理"Ori 孤儿"会破坏大量在册聊天记录的图片显示。
- emoji：收藏表情 498 个文件（绝对路径列 80012）已划出孤儿；Emoji 域剩余
  ~20,750 个 / 4.3 GB 为营销表情包缓存（market_emoticon_table 无本地路径列），
  删除后会自动重新下载，属安全缓存。

### 工程要点（踩坑记录）

- 内存 keyspec 按 salt 分桶后确认**每个库有独立 key**（含 emoji/nt_msg）。
- 大库扫描三教训：① ascii md5 指纹需 4 字节（hex 对只有 256 种 2 字节组合，桶过载）；
  ② rowid 极稀疏（~1 行/1e11），坏页跳步必须几何级放大，固定 50k 会在同一页死循环；
  ③ 内层事务不可与 Build 外层事务嵌套。
