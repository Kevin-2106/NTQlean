# NTQlean

面向 Windows NTQQ 的**本地存储分析与清理工具**。
Phase 0（只读数据库研究）与 Phase 2 核心目标（范围选择 + 预演清理 + WPF UI）已完成；
真实数据的完整流程需要用户提供一次账号数据库 key。

## 安全边界（不可妥协）

- ❌ 不注入 QQ 进程、不 hook、不调试、不读取在线 QQ 进程内存
- ❌ 不修改任何 QQ 文件/数据库/WAL/配置（原始文件仅以只读共享方式打开）
- ❌ 不联网、不上传、无遥测
- ✅ 一切分析基于副本（解密副本与索引仅写入工作区，默认 `%LOCALAPPDATA%\NTQlean\`）
- ✅ key 只存在于内存（GUI 密码框 / CLI 隐藏输入），不落盘、不写日志
- ✅ 清理默认只到**预演（dry-run）**；实际清理 = 回收站（可恢复）+ manifest 清单，
  且仅触碰所配置 nt_data 根内的文件，需要输入确认文字并勾选知情声明
- ✅ 自动化测试从不执行实际清理动作

## 结构

```
src/NTQlean.Core      发现 / 头部识别 / 快照 / SQLCipher 解密 / 媒体索引 / 布尔选择引擎 / 预演与回收站
src/NTQlean.Probe     命令行工具（discover / inspect-db / open-db / analyze / select）
src/NTQlean.App       Phase 2 WPF UI（数据源 → 索引 → 选择 → 预览/清理）
tools/research/       一次性研究脚本（Python）与社区文档存档
docs/                 research-notes.md（过程记录）、ntqq-database.md（确证事实）
```

## 构建

```powershell
dotnet build
```

需要 .NET 8 SDK，Windows x64。GUI 入口：`src/NTQlean.App`。

## GUI 使用（Phase 2）

1. **① 数据源** — 自动发现账号（标准位置，不全盘扫描），确认 nt_db / nt_data / 工作区路径
2. **② 索引** — 输入账号 key（仅内存）→ 解密副本 → 构建统一索引
   （files_in_chat / rich_media / file_assistant / group_info，不含 13GB 级 nt_msg 大库）
3. **③ 选择** — 快速圈定范围：
   - 类型（图片/视频/文件/语音）与置信度（exact/strong/heuristic/orphan）
   - 时间范围（近30天/今年/去年 预设）、大小范围（KB/MB/GB）
   - 会话列表（选中即排除 = NOT）
   - 高级布尔表达式：`size >= 10MB AND time < 2025-01-01 AND NOT chat == 123456`
     （AND / OR / NOT 与括号，索引 SQL 查询，秒级出结果）
   - 结果列表 + 低开销缩略图预览（复用 NTQQ 自带 Thumb 文件，240px 解码 + LRU 缓存）
   - 导出 CSV
4. **④ 清理** — 默认只有预演报告；实际清理需输入确认文字 + 勾选知情声明，
   文件移入**回收站**（可恢复）并生成 manifest

## CLI

```text
ntqlean-probe discover / inspect-db / open-db        # Phase 0 研究（见 docs）
ntqlean-probe analyze  --db-dir <nt_db> --data-dir <nt_data> [--key ...]
                       [--decrypted-dir <已有明文库>]  # 构建索引
ntqlean-probe select   --workspace <dir> [--expr "..."] [--kind ...]
                       [--from ... --to ...] [--size-min 10MB]
                       [--exclude-chat id] [--include-orphans]   # 预演，不删除
```

## 状态

| 能力 | 状态 |
| --- | --- |
| 解密（副本、只读原文件） | ✅ 实测（login.db 公开 key，HMAC 4/4） |
| 媒体索引 / 孤儿分析 / 群名提取 | ✅ 合成数据集成测试通过 |
| 布尔选择（时间/大小/群聊 AND OR NOT） | ✅ CLI 矩阵测试通过，索引级查询 |
| WPF UI | ✅ 启动冒烟通过；完整真实数据流需用户 key |
| 实际清理 | 已实现（回收站 + manifest + 双重确认）；**测试从未执行** |
