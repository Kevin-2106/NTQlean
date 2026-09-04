# NTQlean

面向 Windows NTQQ 的**本地存储分析与清理工具**（计划）。
当前处于 **Phase 0 / Database Research PoC**：只读研究，无任何清理功能。

## 安全边界（不可妥协）

- ❌ 不注入 QQ 进程、不 hook、不调试、不读取在线 QQ 进程内存
- ❌ 不修改任何 QQ 文件/数据库/WAL/配置（原始文件仅以只读共享方式打开）
- ❌ 不联网、不上传、无遥测
- ✅ 一切分析基于副本（`%TEMP%\NTQlean\<timestamp>\`）
- ✅ 数据库 key 交互式输入、不回显、不落盘

研究结论：**PARTIAL — zero-injection 架构可行**，账号库 key 需用户一次性提供。
详见 [docs/research-notes.md](docs/research-notes.md) 与
[docs/ntqq-database.md](docs/ntqq-database.md)。

## 结构

```
src/NTQlean.Core      数据目录发现 / 头部识别 / 快照复制 / SQLCipher 解密 / protobuf 扫描
src/NTQlean.Probe     Phase 0 命令行研究工具
tools/research/       一次性研究脚本（Python）与社区文档存档（tools/research/community-docs/）
docs/                 research-notes.md（过程记录）、ntqq-database.md（确证事实）
samples/              脱敏样本（不含任何真实数据）
```

## 构建

```powershell
dotnet build
```

需要 .NET 8 SDK，Windows x64。

## 用法

```text
ntqlean-probe discover                # 发现数据根/账号/数据库并做格式分类
ntqlean-probe inspect-db <db 路径>     # 分析单个数据库文件头（无需 key）
ntqlean-probe open-db <db 路径>        # 副本快照 → 解密 → schema 报告
ntqlean-probe test-media <明文库> <nt_data 目录>
                                       # 对解密副本抽样验证媒体 ↔ 本地文件映射
```

`open-db` 示例：

```powershell
# login.db：硬编码公开 key，全自动
dotnet run --project src/NTQlean.Probe -- open-db "D:\Tencent Files\QQ\Tencent Files\nt_qq\global\nt_db\login.db"

# 账号库：交互式输入 key（不回显）。key 需用户自行通过社区工具提取，
# 本项目不提供、不执行任何针对 QQ 进程的提取操作。
dotnet run --project src/NTQlean.Probe -- open-db "...\nt_db\nt_msg.db" --pages 512
```

`--pages <n>` 只解密前 n 页（快速侦察超大库的 schema，不解全库）。

## 当前状态（Phase 0 结论：PARTIAL）

| 问题 | 结论 |
| --- | --- |
| 数据库在哪、什么格式 | ✅ 1024 字节伪头（`SQLite header 3\0`/`QQ_NT DB`/protobuf）+ SQLCipher（页 4096、PBKDF2-SHA512×4000、AES-256-CBC、HMAC-SHA1） |
| 能否只读稳定读取副本 | ✅ 端到端验证（login.db，HMAC 4/4 页通过，schema 可读） |
| key 获取 | login.db：✅ 自动（公开硬编码常量）；账号库：⚠️ 需用户交互提供（进程级提取方法均越界，本项目不做） |
| schema / 消息 / 媒体分析 | 代码路径已就绪并经 login.db 验证；消息库待 key 后即可运行 |
