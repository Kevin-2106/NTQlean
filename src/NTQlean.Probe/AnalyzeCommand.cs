using Microsoft.Data.Sqlite;
using NTQlean.Core;

internal static class AnalyzeCommand
{
    public static async Task<int> Run(string[] args)
    {
        string? dbDir = null, dataDir = null, workspaceDir = null, decryptedDir = null, ntMsgDir = null;
        string? key = null;
        var force = false;
        var dumpKey = false;
        var includeNtMsg = false;
        IReadOnlyDictionary<string, List<string>>? memoryKeys = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--db-dir": dbDir = args[++i]; break;
                case "--data-dir": dataDir = args[++i]; break;
                case "--workspace": workspaceDir = args[++i]; break;
                case "--decrypted-dir": decryptedDir = args[++i]; break;
                case "--key": key = args[++i]; break;
                case "--force": force = true; break;
                case "--dump-key": dumpKey = true; break;
                case "--include-nt-msg": includeNtMsg = true; break;
                case "--nt-msg-dir": ntMsgDir = args[++i]; break;
            }
        }

        if (dataDir is null || !Directory.Exists(dataDir))
        {
            ProbeLog.Error("usage: analyze --data-dir <nt_data> [--db-dir <nt_db> | --decrypted-dir <dir>] [--workspace <dir>] [--key <key>] [--dump-key] [--force]");
            return 1;
        }

        workspaceDir ??= Workspace.DefaultRoot();
        var workspace = new Workspace(workspaceDir);
        ProbeLog.Info($"工作区: {workspace.Root}");
        var progress = new Progress<string>(m => ProbeLog.Info(m));

        // Stage 1: obtain plain copies.
        if (decryptedDir is not null)
        {
            // Fixture/research mode: plain databases supplied directly.
            foreach (var plain in Directory.EnumerateFiles(decryptedDir, "*.plain.db"))
                File.Copy(plain, Path.Combine(workspace.DecryptedDir, Path.GetFileName(plain)), overwrite: true);
            ProbeLog.Info($"已导入明文库: {Directory.GetFiles(workspace.DecryptedDir, "*.plain.db").Length} 个");
        }
        else
        {
            if (dbDir is null || !Directory.Exists(dbDir))
            {
                ProbeLog.Error("需要 --db-dir（QQ nt_db 目录）或 --decrypted-dir（已有明文库）");
                return 1;
            }
            if (key is null && dumpKey && Directory.Exists(dbDir))
            {
                ProbeLog.Info("从 QQ 进程内存只读扫描 keyspec（一次扫描覆盖所有库）…");
                var specs = KeyDumper.DumpAllKeyspecs(new Progress<string>(m => ProbeLog.Info(m)),
                    out var procs, out var bytes);
                ProbeLog.Info($"扫描完成: {procs} 进程 / {bytes / 1048576.0:F0} MB，" +
                              $"{specs.Sum(kv => kv.Value.Count)} 个 keyspec / {specs.Count} 个 salt");
                memoryKeys = specs;

                // The "account key" = a key validated against the first encrypted DB.
                foreach (var name in DecryptService.AccountDbs)
                {
                    var candidate = Path.Combine(dbDir, name);
                    if (!File.Exists(candidate) || DecryptService.IsPlainSqlite(candidate)) continue;
                    var winner = ValidateKeyFor(candidate, specs);
                    if (winner is not null)
                    {
                        key = winner;
                        ProbeLog.Info($"已验证 {name} 的 key（仅保存在本次进程内存中）。");
                        break;
                    }
                }
                if (key is null)
                {
                    ProbeLog.Error("没有任何 keyspec 能解密账号库（QQ 需已登录目标账号）。");
                    return 2;
                }
            }

            key ??= Shared.ReadSecret("Enter account database key (input hidden, or Ctrl+C to abort; use --dump-key to fetch from QQ memory): ");
            if (string.IsNullOrWhiteSpace(key))
            {
                ProbeLog.Error("未提供 key；账号库无法解密。");
                return 2;
            }

            var targets = DecryptService.AccountDbs.Where(n => File.Exists(Path.Combine(dbDir, n))).ToList();
            var missing = DecryptService.AccountDbs.Except(targets).ToList();
            foreach (var m in missing) ProbeLog.Warn($"缺少 {m}（跳过）");

            var outcomes = DecryptService.DecryptAll(workspace, dbDir, key, targets, force, progress, memoryKeys);
            foreach (var o in outcomes)
            {
                if (o.Error is not null) ProbeLog.Warn($"{o.Source}: {o.Error}");
                else if (o.Decrypted && o.Pages > 0)
                    ProbeLog.Info($"{o.Source}: 解密 {o.Pages} 页, HMAC {o.HmacOk}/{o.HmacOk + o.HmacBad}" +
                                  (o.HmacBad > 0 ? " ⚠" : " ✓"));
                else ProbeLog.Info($"{o.Source}: 复用已有明文库");
            }
            if (outcomes.All(o => o.Error is not null && !o.Decrypted && !File.Exists(workspace.PlainDbPath(o.Source))))
            {
                ProbeLog.Error("没有任何账号库成功解密。");
                return 2;
            }
        }

        // Stage 2b: optional nt_msg decryption (13 GB class) into its own directory.
        string? ntMsgPlain = null;
        if (includeNtMsg && dbDir is not null && File.Exists(Path.Combine(dbDir, DecryptService.NtMsgDb)))
        {
            ntMsgDir ??= Path.Combine("D:\\", "NTQlean", "nt-msg");
            var ntMsgWorkspace = new Workspace(ntMsgDir);
            ProbeLog.Info($"解密 nt_msg.db（大库，数分钟）→ {ntMsgWorkspace.Root} …");
            var ntMsgOutcomes = await Task.Run(() => DecryptService.DecryptAll(
                ntMsgWorkspace, dbDir, key ?? "", new[] { DecryptService.NtMsgDb },
                force, progress, memoryKeys));
            foreach (var o in ntMsgOutcomes)
            {
                if (o.Error is not null) ProbeLog.Warn($"nt_msg.db: {o.Error}");
                else if (o.Decrypted) ProbeLog.Info($"nt_msg.db: 解密完成（HMAC {o.HmacOk}）");
            }
            ntMsgPlain = ntMsgWorkspace.PlainDbPath(DecryptService.NtMsgDb);
            if (!File.Exists(ntMsgPlain)) ntMsgPlain = null;
        }

        // Stage 2: build the index.
        ProbeLog.Info("构建索引 …");
        var summary = await Task.Run(() => MediaIndexBuilder.Build(workspace, dataDir, progress, ntMsgPlain));
        ProbeLog.Info($"索引完成: 媒体引用 {summary.MediaRows:N0} 条 " +
                      $"(已解析 {summary.Resolved:N0} / 未解析 {summary.Missing:N0})");
        ProbeLog.Info($"nt_data 文件: {summary.NtFiles:N0}（其中无数据库引用 {summary.OrphanFiles:N0} 个 / " +
                      $"{summary.OrphanBytes / 1024.0 / 1024:F1} MB）");
        ProbeLog.Info($"会话: {ChatCount(workspace):N0}（成功提取群名 {summary.Chats}）");
        foreach (var note in summary.Notes) ProbeLog.Unconfirmed(note);
        return 0;
    }

    /// <summary>Validates keyspec candidates against a real database (page 1 only).</summary>
    private static string? ValidateKeyFor(string encryptedDb, IReadOnlyDictionary<string, List<string>> specs)
    {
        var salt = KeyDumper.ReadSalt(encryptedDb);
        if (!specs.TryGetValue(Convert.ToHexString(salt), out var keys)) return null;

        var tempDir = Path.Combine(Path.GetTempPath(), "NTQlean", "keycheck-" + DateTime.Now.Ticks);
        Directory.CreateDirectory(tempDir);
        try
        {
            var cfg = new SqlCipherConfig { PageSize = 4096, KdfIterations = 4000, KdfUseSha512 = true, Hmac = HmacAlgorithm.HmacSha1 };
            foreach (var key in keys)
            {
                if (SqlCipherDecryptor.DecryptCopy(encryptedDb, Path.Combine(tempDir, "probe.plain.db"),
                        key, cfg, maxPages: 1).Success)
                    return key;
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
        return null;
    }

    private static long ChatCount(Workspace workspace)
    {
        using var conn = NtqSqlite.OpenReadOnly(workspace.IndexPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM chats";
        return cmd.ExecuteScalar() is long l ? l : 0;
    }
}
