using NTQlean.Core;

internal static class GetKeyCommand
{
    public static int Run(string[] args)
    {
        string? dbPath = null;
        var mask = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--db": dbPath = args[++i]; break;
                case "--mask": mask = true; break;
            }
        }

        if (dbPath is null || !File.Exists(dbPath))
        {
            Console.WriteLine("""
                用法: ntqlean-probe get-key --db <账号数据库路径> [--mask]

                从正在运行、已登录的 QQ 进程内存中只读扫描（ReadProcessMemory，
                不注入/不写入/不调试）提取账号数据库 key，并用该数据库文件的
                salt 交叉验证。仅限本机、本人账号使用。

                  --mask   只显示 key 的首尾各 4 个字符
                """);
            return 1;
        }

        var header = DbHeaderInspector.Inspect(dbPath);
        if (header.Format != DbFormatKind.NtqqEncrypted || header.KdfSalt is null)
        {
            ProbeLog.Error("该文件不是 NTQQ 加密数据库（没有可匹配的 salt）。");
            return 1;
        }

        Console.WriteLine($"[INFO] 目标: {ProbeLog.MaskPath(dbPath, InspectDbCommand.CollectAccountIds(dbPath))}");
        Console.WriteLine("[INFO] 扫描 QQ 进程内存（只读）…");
        var result = KeyDumper.DumpKeysForSalt(header.KdfSalt,
            new Progress<string>(m => Console.WriteLine($"  {m}")));
        Console.WriteLine($"[INFO] 扫描 {result.ProcessesScanned} 个进程 / {result.BytesScanned / 1048576.0:F0} MB");

        if (result.Keys.Count == 0)
        {
            Console.WriteLine($"[UNCONFIRMED] 未找到匹配的 key。{result.Note}");
            Console.WriteLine("提示: QQ 必须处于登录状态；刚登录时打开过消息界面更稳妥。");
            return 2;
        }

        foreach (var key in result.Keys)
        {
            var shown = mask
                ? key[..4] + new string('*', 56) + key[^4..]
                : $"x'{key}{Convert.ToHexString(header.KdfSalt).ToLowerInvariant()}'";
            Console.WriteLine($"[INFO] key: {shown}");
        }

        // Validate against the real database (decrypt page 1 only).
        var tempDir = Path.Combine(Path.GetTempPath(), "NTQlean", "keycheck-" + DateTime.Now.Ticks);
        Directory.CreateDirectory(tempDir);
        try
        {
            foreach (var key in result.Keys)
            {
                var probeOut = Path.Combine(tempDir, "probe.plain.db");
                var cfg = new SqlCipherConfig { PageSize = 4096, KdfIterations = 4000, KdfUseSha512 = true, Hmac = HmacAlgorithm.HmacSha1 };
                var r = SqlCipherDecryptor.DecryptCopy(dbPath, probeOut, key, cfg, maxPages: 1);
                if (r.Success)
                {
                    Console.WriteLine(mask
                        ? "[INFO] 验证: 通过（key 未显示）"
                        : "[INFO] 验证: 通过 —— 此 key 可直接用于 analyze / open-db / GUI");
                    return 0;
                }
                Console.WriteLine("[UNCONFIRMED] 验证: 该 key 解密页 1 失败");
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
        return 2;
    }
}
