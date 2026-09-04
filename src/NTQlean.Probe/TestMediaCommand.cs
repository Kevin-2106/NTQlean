using Microsoft.Data.Sqlite;
using NTQlean.Core;

internal static class TestMediaCommand
{
    // Column numbers per QQDecrypt/nt_msg_db_util field research.
    private const int ColFileName = 45402;
    private const int ColFilePath = 45403;
    private const int ColFileSize = 45405;

    public static int Run(string[] args)
    {
        if (args.Length < 2)
        {
            ProbeLog.Error("usage: ntqlean-probe test-media <plain-db> <nt_data_dir> [--samples <n>]");
            return 1;
        }

        var dbPath = args[0];
        var ntDataDir = args[1];
        var samples = 3;
        for (var i = 2; i < args.Length - 1; i++)
            if (args[i] == "--samples" && int.TryParse(args[i + 1], out var n))
                samples = Math.Max(1, n);

        if (!File.Exists(dbPath))
        {
            ProbeLog.Error($"plain database not found: {dbPath}");
            return 1;
        }
        if (!Directory.Exists(ntDataDir))
        {
            ProbeLog.Error($"nt_data directory not found: {ntDataDir}");
            return 1;
        }

        using var conn = OpenDbCommand.OpenReadOnly(dbPath);
        var tables = new List<string>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
            using var r = cmd.ExecuteReader();
            while (r.Read()) tables.Add(r.GetString(0));
        }

        var mediaTables = new (string Table, string Kind, string PathCol)[]
        {
            ("files_in_chat_table", "received media (files_in_chat.db)", $"\"{ColFilePath}\""),
            ("file_table", "file transfers (rich_media.db)", $"\"{ColFilePath}\""),
        };

        var tested = 0;
        foreach (var (table, kind, pathCol) in mediaTables)
        {
            if (!tables.Contains(table, StringComparer.OrdinalIgnoreCase))
                continue;

            ProbeLog.Info($"Media metadata table found: {table} — {kind}");
            var found = TrySampleTable(conn, table, pathCol, ColFileName, ColFileSize, ntDataDir, samples);
            tested += found ? 1 : 0;
        }

        if (tested == 0)
        {
            ProbeLog.Unconfirmed("no media path columns sampled; either the tables are absent or " +
                                 "column numbering changed — dump schema with 'open-db' to verify");
            return 1;
        }

        return 0;
    }

    private static bool TrySampleTable(
        SqliteConnection conn, string table, string pathCol, int nameCol, int sizeCol,
        string ntDataDir, int samples)
    {
        // Phase 0 sampling: tiny LIMIT-based queries only; no full scans.
        var sql = $"""
            SELECT rowid, {pathCol}, "{nameCol}", "{sizeCol}"
            FROM "{table}"
            WHERE {pathCol} IS NOT NULL AND {pathCol} <> ''
            ORDER BY rowid DESC
            LIMIT {samples}
            """;

        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            using var r = cmd.ExecuteReader();

            var any = false;
            while (r.Read())
            {
                any = true;
                var relPath = r.IsDBNull(1) ? "" : r.GetString(1);
                var fileName = r.IsDBNull(2) ? "" : r.GetString(2);
                var fileSize = r.IsDBNull(3) ? -1L : Convert.ToInt64(r.GetValue(3));

                ProbeLog.Info($"  sample rowid={r.GetInt64(0)}: name={TruncateForLog(fileName)} " +
                              $"size={fileSize} path={TruncateForLog(relPath)}");
                ClassifyMapping(relPath, fileName, fileSize, ntDataDir);
            }
            return any;
        }
        catch (Exception ex)
        {
            ProbeLog.Unconfirmed($"sampling {table} failed: {ex.Message}");
            return false;
        }
    }

    private static void ClassifyMapping(string relPath, string fileName, long fileSize, string ntDataDir)
    {
        // Level 1: path stored in the DB is directly usable (Strong when the file exists).
        var candidates = new List<(string Path, string Method)>
        {
            (Path.Combine(ntDataDir, relPath.Replace('/', Path.DirectorySeparatorChar)), "db path"),
        };

        // Level 2: filename search under nt_data (Heuristic — do not use for deletion).
        if (!string.IsNullOrEmpty(fileName))
            candidates.Add((FindUnderNtData(ntDataDir, fileName), "filename search"));

        foreach (var (candidate, method) in candidates)
        {
            if (candidate is null || !File.Exists(candidate)) continue;
            var actual = new FileInfo(candidate).Length;
            var sizeMatches = fileSize <= 0 || actual == fileSize;
            var level = method == "db path" && sizeMatches ? "Exact"
                      : method == "db path" ? "Strong"
                      : sizeMatches ? "Heuristic" : "Unknown";

            ProbeLog.Info($"    match[{level}] via {method}: {TruncateForLog(candidate)} " +
                          $"({actual:N0} bytes{(sizeMatches ? ", size matches" : ", SIZE MISMATCH")})");
            return;
        }

        ProbeLog.Unconfirmed($"    no local file mapped for this sample " +
                             $"(db path and filename lookup both missed)");
        if (fileSize > 0)
            ProbeLog.Research("    note: image entries often have empty path columns per community " +
                              "research; size/filename heuristics are the fallback mapping");
    }

    private static string? FindUnderNtData(string ntDataDir, string fileName)
    {
        try
        {
            var exact = Directory.EnumerateFiles(ntDataDir, fileName, SearchOption.AllDirectories).FirstOrDefault();
            return exact;
        }
        catch
        {
            return null;
        }
    }

    private static string TruncateForLog(string s)
    {
        s = s.Replace('\r', ' ').Replace('\n', ' ');
        return s.Length <= 80 ? s : s[..77] + "…";
    }
}
