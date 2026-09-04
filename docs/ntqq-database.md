# NTQQ 数据库事实手册（Windows）

> 只记录**已验证**的事实；推测一律降级到 Likely / Unknown。
> 验证环境：Windows 10.0.26200 x64，QQ (NTQQ) 9.9.30.48762，2026-09-05。

## Confirmed（本机实测确证）

### 文件位置
- 安装目录：`<Program Files>\Tencent\QQNT`；核心 native 模块为
  `versions\<版本>\resources\app\wrapper.node`。
- 数据根（本机）：`D:\Tencent Files\QQ\Tencent Files\`（自定义嵌套布局）与
  OneDrive 重定向的 `...\文档\Tencent Files\`（旧数据）。
- 账号数据：`<数据根>\<uin>\nt_qq\{nt_db, nt_data, nt_temp}`；
  全局数据：`<数据根>\nt_qq\global\{nt_db, ...}`。
- `nt_db\` 内每库附带 `-wal`、`-shm`；部分库有 `*.db-first.material` /
  `*.db-last.material` 附属文件。

### 文件格式（nt_db 下全部加密库）
- 每文件以 **1024 字节明文自定义头**开始：
  - 偏移 0：伪 magic `SQLite header 3\0`（注意：不是标准的 `SQLite format 3`，
    标准工具因此拒绝识别——这是有意设计）；
  - 偏移 16：2 字节大端"自称页大小"= 1024（**与真实页大小 4096 不符**，勿信）；
  - 偏移 32：`QQ_NT DB` 标记；偏移 40：4 字节小端（login.db=0x23、账号库=0x9d，语义未知）；
  - 偏移 44 起：protobuf（field2=128 hex 字符标识、field3=`1.1.0.1`、
    field4=`HMAC_SHA1`、field5=varint 时间戳）；其余零填充。
- 偏移 1024 起：SQLCipher v4 风格密文，首 16 字节为 KDF salt。
- SQLCipher 参数（login.db 实测、HMAC 4/4 页验证）：
  - `cipher_page_size = 4096`（并与 WAL 明文头中的页大小字段相互印证）；
  - KDF = **PBKDF2-HMAC-SHA512，4000 轮**；
  - 页面加密 = **AES-256-CBC**，每页随机 IV；
  - 页 1 布局 `[salt16][密文 4032][IV16][HMAC-SHA1 20][pad12]`；
    页 ≥2 布局 `[密文 4048][IV16][HMAC-SHA1 20][pad12]`；
  - 页 HMAC = **HMAC-SHA1**（密文‖IV‖页号 4 字节小端）；
    hmac_key = PBKDF2-SHA512(enc_key, salt⊕0x3a, **2 轮**)；
- ⚠️ 已知不一致：解密后 SQLite 头的 reserved-space 字节为 0x50(80)，而实际
  IV/HMAC 布局按 48 工作；按上述布局解密 + 直接以标准 SQLite 打开均实测可行。

### 密钥
- **login.db**：key 为客户端内**硬编码公开常量** `BD156D6710D54D8782F4`
  （20 字符；本机 wrapper.node 偏移 0x37f87a4 处原文可见），
  本机实测可直接解密 → `login_table`（列 1000–1015）、`login_misc_data_table`（列 2000/2001）。
  ⚠️ login_table 含登录票据类敏感数据，工具只读 schema，不输出行内容。
- **账号库**（nt_msg.db 等）：**每账号独立 key**，16 字符随机口令；仅存在于
  QQ 进程内存；无公开的纯本地文件获取方法。

### 运行行为
- QQ 运行时对部分库（实测 login.db）持有字节区间锁：单次顺序 Read 可能在锁边界
  处截断；以最大共享模式打开仍可完整读取（快照复制实测成功）。
- WAL 头与帧头为明文标准结构；帧内页面数据已加密。

### 例外
- `nt_data\bc_09.db` 为普通明文 SQLite（beacon_events 遥测，本机 0 行）。

## Likely（高置信，未在本机完整验证）

- 消息库（nt_msg.db）schema：`group_msg_table` / `c2c_msg_table` 等，
  列名为数字混淆：`40001`=msgid、`40030`=私聊对方、`40033`=发送者、
  `40050`=Unix 秒时间、`40800`=protobuf 消息体（QQ 9.9.3→9.9.32 社区一致）。
- 媒体元数据：`rich_media.db.file_table`（45402 文件名 / 45403 路径 / 45405 大小 /
  45503 fileUuid / 45001 elementId）；`files_in_chat.db.files_in_chat_table`
  （45403 路径 / 45404 缩略图 / 45405 大小 / 40050 时间 / 82302 原图标记）；
  图片条目的 45403 常为空。
- 媒体本地命名：`nt_data\{Pic,Video}\YYYY-MM\Ori\<32位hex即MD5>.<ext>`
  （本机文件名形态实测；"hex 即 MD5"来自社区一致性结论）。
- 账号库 key 提取在 QQ 9.9.32-51246（2026-07-19）上仍可行（社区验证），
  与本机 9.9.30.48762 同代。

## Unknown

- 头部 protobuf field2（128 hex 字符）与偏移 40 的 4 字节值的确切语义。
- reserved=80（头声明）与 reserve=48（实际布局）不一致的机制解释。
- `*.material` 附属文件的内部结构（与主库同 salt，推测同 key 加密）。
- 头部 hex 标识是否随账号变化（本机全部账号库同值；login.db 为 `ABCDEFG` 占位）。
- 账号库 key 是否会在 QQ 版本升级时轮换（历史上未观察到）。
