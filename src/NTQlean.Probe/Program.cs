using Microsoft.Data.Sqlite;
using NTQlean.Core;
using System.Text;

if (args.Length == 0)
{
    PrintUsage();
    return 0;
}

return args[0] switch
{
    "discover" => DiscoverCommand.Run(args.Skip(1).ToArray()),
    "inspect-db" => InspectDbCommand.Run(args.Skip(1).ToArray()),
    "open-db" => OpenDbCommand.Run(args.Skip(1).ToArray()),
    "test-media" => TestMediaCommand.Run(args.Skip(1).ToArray()),
    "analyze" => await AnalyzeCommand.Run(args.Skip(1).ToArray()),
    "select" => SelectCommand.Run(args.Skip(1).ToArray()),
    "get-key" => GetKeyCommand.Run(args.Skip(1).ToArray()),
    "help" or "--help" or "-h" => PrintUsage(),
    _ => Unknown(args[0]),
};

static int Unknown(string cmd)
{
    ProbeLog.Error($"unknown command '{cmd}'");
    PrintUsage();
    return 1;
}

static int PrintUsage()
{
    Console.WriteLine("""
        ntqlean-probe — NTQQ local storage research & analysis CLI (read-only on QQ files)

        Commands:
          discover                     Locate NTQQ data roots, accounts and database files
          inspect-db <path>            Analyze one database file header / format (no key needed)
          open-db <path> [options]     Snapshot a database copy, decrypt with a key, dump schema
          get-key --db <path>          Extract account DB keys via read-only memory scan of the
                                       running QQ process (own machine & own account ONLY!)
          test-media <plain-db> <nt_data_dir>
                                       Probe media metadata tables in a decrypted copy and
                                       try to map samples to files under nt_data
          analyze                      Decrypt account DBs (needs key) and build the index
          select                       Query the index with time/size/chat criteria (dry run!)

        analyze options:
          --db-dir <nt_db>       Account nt_db directory (QQ originals, read-only)
          --decrypted-dir <dir>  Existing plain databases instead of nt_db (research/fixtures)
          --data-dir <nt_data>   Media directory for file mapping
          --workspace <dir>      Workspace location (default: %LOCALAPPDATA%\NTQlean\workspace-*)
          --key <key>            Account key (prefer interactive entry)
          --force                Re-decrypt even if plain copies exist

        select options:
          --workspace <dir>      Workspace with index.db
          --expr "<expr>"        Boolean expression: size >= 10MB AND time < 2025-01-01 AND NOT chat == 123
          --kind <list>          image,video,file,ptt,other
          --chat / --exclude-chat <ids>   Chat id include/exclude (NOT)
          --from / --to <date>   Time range (yyyy-MM-dd)
          --size-min / --size-max <n>     Size range (supports KB/MB/GB suffix)
          --conf <list>          exact(记录吻合),strong(路径吻合),heuristic(仅同名)
                                 (default: exact,strong)
          --include-orphans      Include unclaimed files (无主文件, cleanup main
                                 target; unreferenced by any valid DB record)
          --json <path>          Also save the dry-run report here

        open-db options:
          --key <key>      Database key (prefer interactive entry; avoids shell history)
          --pages <n>      Decrypt only the first n pages (fast schema recon of huge DBs)
          --out <dir>      Workspace directory (default: %TEMP%\NTQlean\<timestamp>)

        get-key options:
          --db <path>      One encrypted account database (its salt validates candidates)
          --mask           Show only the first/last 4 characters of each key

        Safety: this tool never writes to QQ-owned files. All analysis happens on copies.
        The key is never logged, echoed, or persisted. 'select' is always a dry run.

        DISCLAIMER / 免责声明:
          Unofficial community tool — NOT affiliated with or endorsed by Tencent.
          Use only on your own machine and your own account, at your own risk.
          Memory-scan key extraction may be restricted by the QQ Terms of Service;
          compliance is your responsibility. Provided AS-IS (MIT), no warranty:
          back up important data before use. See README.md and LICENSE for details.
        """);
    return 0;
}

internal static class Shared
{
    /// <summary>Reads a secret from stdin with echo disabled where a console is available.</summary>
    public static string? ReadSecret(string prompt)
    {
        if (Console.IsInputRedirected)
        {
            // Piped input (e.g. research automation): echo suppression is impossible.
            Console.Out.Write(prompt);
            var line = Console.ReadLine();
            return string.IsNullOrEmpty(line) ? null : line;
        }

        Console.Out.Write(prompt);
        var buf = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) { Console.Out.WriteLine(); break; }
            if (key.Key == ConsoleKey.Backspace && buf.Length > 0) buf.Length--;
            else if (!char.IsControl(key.KeyChar)) buf.Append(key.KeyChar);
        }
        var value = buf.ToString();
        buf.Clear();
        return value.Length == 0 ? null : value;
    }

    public static void PrintHeaderReport(DbHeaderInfo info, IEnumerable<string> maskIds)
    {
        ProbeLog.Info($"Database: {MaskPath(info.Path, maskIds)}");
        ProbeLog.Info($"Size: {info.Size:N0} bytes  modified {info.LastWriteTimeUtc:u}");
        ProbeLog.Info($"Sidecars: wal={info.HasWal} shm={info.HasShm}");
        ProbeLog.Info($"Magic at offset 0: {info.MagicPreviewHex}");

        switch (info.Format)
        {
            case DbFormatKind.PlainSqlite:
                ProbeLog.Info("Format: plain SQLite 3 (no encryption)");
                break;

            case DbFormatKind.NtqqEncrypted:
                ProbeLog.Research("NTQQ custom header detected: fake magic \"SQLite header 3\\0\" at " +
                                  "offset 0 (deliberately NOT the real \"SQLite format 3\\0\"), " +
                                  "QQ_NT DB tag at offset 32");
                ProbeLog.Info($"Possible extra header: {info.HeaderSize} bytes");
                ProbeLog.Research($"Header protobuf: version={info.HeaderProtoVersion} " +
                                  $"hmac={info.HeaderHmacAlgorithm} tag_tail=0x{info.NtTagTail:x}");
                ProbeLog.Unconfirmed($"Header account id (hex, masked): {info.HeaderAccountIdHex ?? "(none)"}");
                if (info.HeaderProtoVarints is { } v)
                    ProbeLog.Unconfirmed($"Header varint field (semantics unknown, plausible Unix time): {v}");
                ProbeLog.Research($"Claimed page size in fake header: {info.ClaimedPageSize} " +
                                  "(may not match the SQLCipher page size)");
                ProbeLog.Unconfirmed("Payload after header appears SQLCipher-encrypted " +
                                     $"(sampled entropy {info.SampledEntropy:F2} bits/byte)");
                ProbeLog.Research("KDF salt: 16 bytes at offset 1024 (collected, not logged)");
                ProbeLog.Info("Encryption: SQLCipher (community parameters: page 4096, kdf_iter 4000, " +
                              "PBKDF2-HMAC-SHA512, HMAC-SHA1)");
                break;

            case DbFormatKind.EncryptedUnknown:
                ProbeLog.Unconfirmed($"Format: unrecognized (entropy {info.SampledEntropy:F2} bits/byte)");
                break;

            case DbFormatKind.Unknown:
            default:
                ProbeLog.Unconfirmed("Format: unknown");
                break;
        }
    }

    public static string MaskPath(string path, IEnumerable<string> ids) =>
        ProbeLog.MaskPath(path, ids);

    public static void DumpSchema(SqliteConnection conn)
    {
        ProbeLog.Info("--- schema ---");

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name, type FROM sqlite_master ORDER BY type, name";
        using var reader = cmd.ExecuteReader();

        var tables = new List<string>();
        var indexes = 0;
        var others = 0;
        while (reader.Read())
        {
            var name = reader.GetString(0);
            var type = reader.GetString(1);
            if (type == "table") tables.Add(name);
            else if (type == "index") indexes++;
            else others++;
        }

        ProbeLog.Info($"Tables: {tables.Count}, indexes: {indexes}, other objects: {others}");

        foreach (var table in tables)
        {
            var cols = new List<string>();
            using (var ci = conn.CreateCommand())
            {
                ci.CommandText = $"PRAGMA table_info(\"{table.Replace("\"", "\"\"")}\")";
                using var r = ci.ExecuteReader();
                while (r.Read())
                    cols.Add($"{r.GetString(1)} {r.GetString(2)}");
            }

            // Cheap row estimate: max(rowid) is a b-tree seek, not a full count.
            long estimate = -1;
            try
            {
                using var est = conn.CreateCommand();
                est.CommandText = $"SELECT max(rowid) FROM \"{table.Replace("\"", "\"\"")}\"";
                if (est.ExecuteScalar() is long l) estimate = l;
            }
            catch
            {
                // virtual tables (fts) or views may not support rowid; skip estimate
            }

            var estText = estimate >= 0 ? $"~{estimate:N0} rows (max rowid)" : "row estimate n/a";
            var colText = string.Join(", ", cols.Take(12));
            if (cols.Count > 12) colText += ", …";
            ProbeLog.Info($"  {table}: {estText}");
            if (cols.Count > 0) ProbeLog.Info($"    columns: {colText}");
        }

        FlagMessageAndMediaTables(tables);
    }

    public static void FlagMessageAndMediaTables(IEnumerable<string> tables)
    {
        var known = new (string Table, string Role)[]
        {
            ("group_msg_table", "group chat messages"),
            ("c2c_msg_table", "private chat messages"),
            ("recent_contact_v3_table", "recent contacts"),
            ("recent_contact_top_table", "pinned chats"),
            ("group_at_me_msg", "@-mentions"),
            ("file_table", "file transfer metadata (rich_media.db)"),
            ("files_in_chat_table", "received media metadata (files_in_chat.db)"),
            ("login_table", "login records (login.db)"),
        };
        var set = new HashSet<string>(tables, StringComparer.OrdinalIgnoreCase);
        foreach (var (table, role) in known)
            if (set.Contains(table))
                ProbeLog.Info($"  message/media table candidate: {table} — {role}");
    }
}
