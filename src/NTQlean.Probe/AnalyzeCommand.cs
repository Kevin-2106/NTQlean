using Microsoft.Data.Sqlite;
using NTQlean.Core;

internal static class AnalyzeCommand
{
    public static int Run(string[] args)
    {
        string? dbDir = null, dataDir = null, workspaceDir = null, decryptedDir = null;
        string? key = null;
        var force = false;

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
            }
        }

        if (dataDir is null || !Directory.Exists(dataDir))
        {
            ProbeLog.Error("usage: analyze --data-dir <nt_data> [--db-dir <nt_db> | --decrypted-dir <dir>] [--workspace <dir>] [--key <key>] [--force]");
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
            key ??= Shared.ReadSecret("Enter account database key (input hidden): ");
            if (string.IsNullOrWhiteSpace(key))
            {
                ProbeLog.Error("未提供 key；账号库无法解密。");
                return 2;
            }

            var targets = DecryptService.AccountDbs.Where(n => File.Exists(Path.Combine(dbDir, n))).ToList();
            var missing = DecryptService.AccountDbs.Except(targets).ToList();
            foreach (var m in missing) ProbeLog.Warn($"缺少 {m}（跳过）");

            var outcomes = DecryptService.DecryptAll(workspace, dbDir, key, targets, force, progress);
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

        // Stage 2: build the index.
        ProbeLog.Info("构建索引 …");
        var summary = MediaIndexBuilder.Build(workspace, dataDir, progress);
        ProbeLog.Info($"索引完成: 媒体引用 {summary.MediaRows:N0} 条 " +
                      $"(已解析 {summary.Resolved:N0} / 未解析 {summary.Missing:N0})");
        ProbeLog.Info($"nt_data 文件: {summary.NtFiles:N0}（其中无数据库引用 {summary.OrphanFiles:N0} 个 / " +
                      $"{summary.OrphanBytes / 1024.0 / 1024:F1} MB）");
        ProbeLog.Info($"会话: {ChatCount(workspace):N0}（成功提取群名 {summary.Chats}）");
        foreach (var note in summary.Notes) ProbeLog.Unconfirmed(note);
        return 0;
    }

    private static long ChatCount(Workspace workspace)
    {
        using var conn = NtqSqlite.OpenReadOnly(workspace.IndexPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM chats";
        return cmd.ExecuteScalar() is long l ? l : 0;
    }
}
