using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace NTQlean.Core;

public sealed record IndexProfile(
    string DbName,
    string Kind,
    string MsgIdCol = "40001",
    string ChatCol = "40021",
    string ChatTypeCol = "40010",
    string TimeCol = "40050",
    string NameCol = "45402",
    string PathCol = "45403",
    string SizeCol = "45405",
    string ThumbCol = "45404",
    string UuidCol = "45503");

public sealed class IndexBuildSummary
{
    public long MediaRows { get; set; }
    public long Resolved { get; set; }
    public long Missing { get; set; }
    public long NtFiles { get; set; }
    public long OrphanFiles { get; set; }
    public long OrphanBytes { get; set; }
    public long Chats { get; set; }
    public DateTime? Oldest { get; set; }
    public DateTime? Newest { get; set; }
    public List<string> Notes { get; } = new();
}

/// <summary>
/// Builds the unified analysis index (index.db) from decrypted plain database
/// copies plus the nt_data media tree. Read-only with respect to all inputs;
/// only writes inside the workspace.
/// </summary>
public static partial class MediaIndexBuilder
{
    private static readonly IndexProfile[] Profiles =
    {
        new("files_in_chat.db", "received", TimeCol: "40050", ThumbCol: "45404"),
        new("rich_media.db", "file", TimeCol: ""),
        new("file_assistant.db", "download", TimeCol: ""),
    };

    [GeneratedRegex("^[0-9a-fA-F]{32}\\.[A-Za-z0-9]{1,8}$")]
    private static partial Regex Md5NameRegex();

    private static readonly Dictionary<string, string> ExtKind = new(StringComparer.OrdinalIgnoreCase)
    {
        ["jpg"] = "image", ["jpeg"] = "image", ["png"] = "image", ["gif"] = "image",
        ["webp"] = "image", ["bmp"] = "image", ["heic"] = "image",
        ["mp4"] = "video", ["mov"] = "video", ["avi"] = "video", ["mkv"] = "video", ["wmv"] = "video",
        ["amr"] = "ptt", ["wav"] = "ptt", ["slk"] = "ptt", ["m4a"] = "ptt", ["mp3"] = "ptt", ["aac"] = "ptt",
        ["pdf"] = "file", ["doc"] = "file", ["docx"] = "file", ["xls"] = "file", ["xlsx"] = "file",
        ["ppt"] = "file", ["pptx"] = "file", ["zip"] = "file", ["rar"] = "file", ["7z"] = "file",
        ["txt"] = "file", ["exe"] = "file", ["apk"] = "file",
    };

    public static string KindOfExtension(string ext) =>
        ExtKind.TryGetValue(ext.TrimStart('.'), out var k) ? k : "other";

    public static IndexBuildSummary Build(Workspace workspace, string ntDataDir, IProgress<string>? progress = null, string? ntMsgPlainPath = null)
    {
        var summary = new IndexBuildSummary();

        // Fresh index every build (cheap; deterministic).
        if (File.Exists(workspace.IndexPath)) File.Delete(workspace.IndexPath);
        using var index = CreateIndex(workspace.IndexPath);

        using var tx = index.BeginTransaction();

        Exec(index, "CREATE TABLE chats(chat_id TEXT PRIMARY KEY, chat_type INTEGER, display_name TEXT, name_confident INTEGER)");
        Exec(index, """
            CREATE TABLE media(
                item_id INTEGER PRIMARY KEY,
                source TEXT, source_table TEXT, msg_id INTEGER,
                chat_id TEXT, chat_type INTEGER, kind TEXT,
                file_name TEXT, rel_path TEXT, abs_path TEXT, thumb_rel TEXT,
                size_db INTEGER, size_bytes INTEGER, actual_size INTEGER,
                msg_time INTEGER, time_source TEXT, md5 TEXT, uuid TEXT, confidence TEXT)
            """);
        Exec(index, "CREATE TABLE nt_files(rel_path TEXT PRIMARY KEY, name TEXT, size INTEGER, mtime INTEGER, domain TEXT)");
        Exec(index, "CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT)");
        Exec(index, "CREATE INDEX idx_media_time ON media(msg_time)");
        Exec(index, "CREATE INDEX idx_media_size ON media(actual_size)");
        Exec(index, "CREATE INDEX idx_media_chat ON media(chat_id, kind)");
        Exec(index, "CREATE INDEX idx_media_conf ON media(confidence)");
        Exec(index, "CREATE INDEX idx_ntfiles_name ON nt_files(name)");

        // 1. nt_data file inventory (names only walk; one pass).
        progress?.Report("枚举 nt_data 文件 …");
        var ntIndex = new Dictionary<string, List<(string rel, long size)>>(StringComparer.OrdinalIgnoreCase);
        var ntInsert = index.CreateCommand();
        ntInsert.CommandText = "INSERT OR REPLACE INTO nt_files(rel_path,name,size,mtime,domain) VALUES ($p,$n,$s,$m,$d)";
        var pParam = ntInsert.Parameters.Add("$p", SqliteType.Text);
        var nParam = ntInsert.Parameters.Add("$n", SqliteType.Text);
        var sParam = ntInsert.Parameters.Add("$s", SqliteType.Integer);
        var mParam = ntInsert.Parameters.Add("$m", SqliteType.Integer);
        var dParam = ntInsert.Parameters.Add("$d", SqliteType.Text);
        foreach (var file in SafeEnumerateFiles(ntDataDir))
        {
            var rel = Path.GetRelativePath(ntDataDir, file).Replace('\\', '/');
            var fi = new FileInfo(file);
            var name = Path.GetFileName(file);
            var domain = rel.Contains('/') ? rel[..rel.IndexOf('/')] : "";
            pParam.Value = rel; nParam.Value = name; sParam.Value = fi.Length;
            mParam.Value = ((DateTimeOffset)fi.LastWriteTimeUtc).ToUnixTimeSeconds(); dParam.Value = domain;
            ntInsert.ExecuteNonQuery();

            if (!ntIndex.TryGetValue(name, out var list)) ntIndex[name] = list = new();
            list.Add((rel, fi.Length));
            summary.NtFiles++;
        }

        // 2. media rows from profiled databases.
        foreach (var profile in Profiles)
        {
            progress?.Report($"索引 {profile.DbName} …");
            var plain = workspace.PlainDbPath(profile.DbName);
            if (!File.Exists(plain))
            {
                summary.Notes.Add($"{profile.DbName}: 未提供（跳过）");
                continue;
            }

            using var src = NtqSqlite.OpenReadOnly(plain);
            foreach (var table in ListMediaTables(src))
            {
                var added = IndexTable(src, plain, table, profile, index, ntIndex, ntDataDir, summary);
                summary.MediaRows += added;
            }
        }

        // 2b. emoji favorites (absolute-path references into nt_data\Emoji).
        progress?.Report("索引 emoji.db …");
        try
        {
            IndexEmoji(workspace, index, ntDataDir, summary);
        }
        catch (Exception ex)
        {
            summary.Notes.Add($"emoji.db 索引失败: {ex.Message}");
        }

        // 3. chat display names, best effort from group_info.db.
        progress?.Report("提取群聊名称（尽力而为）…");
        summary.Chats = BuildChatNames(workspace, index);

        // 4. orphan analysis: nt_data files not referenced by any media row.
        progress?.Report("未引用文件（孤儿）分析 …");
        Exec(index, "CREATE TABLE nt_refs(md5 TEXT PRIMARY KEY, hits INTEGER, sample_table TEXT, " +
                    "sample_rowid INTEGER, sample_time INTEGER, sample_peer TEXT)");

        // Optional: cross-reference orphan md5s against decrypted nt_msg bodies.
        var refCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(ntMsgPlainPath) && File.Exists(ntMsgPlainPath))
        {
            var md5Set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in ntIndex.Keys)
            {
                var stem = Path.GetFileNameWithoutExtension(name);
                if (stem.Length == 32 && stem.All(Uri.IsHexDigit)) md5Set.Add(stem.ToUpperInvariant());
            }
            progress?.Report($"nt_msg 引用扫描：候选 md5 {md5Set.Count:N0} 个（大库，需数分钟）…");
            try
            {
                var stats = NtMsgRefScanner.Scan(ntMsgPlainPath, index, md5Set, progress);
                progress?.Report($"nt_msg 扫描完成: {stats.RowsScanned:N0} 行 / {stats.BytesScanned / 1048576.0:F0} MB，" +
                                 $"命中 {stats.DistinctHits:N0} 个文件，跳过坏行 {stats.RowsSkipped:N0}");
                using var cmd = index.CreateCommand();
                cmd.CommandText = "SELECT md5, hits FROM nt_refs";
                using var r = cmd.ExecuteReader();
                while (r.Read()) refCounts[r.GetString(0)] = (int)r.GetInt64(1);
            }
            catch (Exception ex)
            {
                summary.Notes.Add($"nt_msg 引用扫描失败（索引继续，孤儿无引用确认不可用）: {ex.Message}");
            }
        }

        Exec(index, "CREATE TABLE orphan_nt_files(rel_path TEXT PRIMARY KEY, name TEXT, size INTEGER, " +
                    "mtime INTEGER, domain TEXT, ref_count INTEGER)");
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = index.CreateCommand())
        {
            cmd.CommandText = "SELECT rel_path FROM media WHERE rel_path <> ''";
            using var r = cmd.ExecuteReader();
            while (r.Read()) referenced.Add(r.GetString(0));
        }
        using (var cmd = index.CreateCommand())
        {
            cmd.CommandText = "SELECT file_name FROM media WHERE rel_path = '' AND file_name <> ''";
            using var r = cmd.ExecuteReader();
            while (r.Read()) referenced.Add(r.GetString(0));
        }
        long orphanBytes = 0;
        var ins = index.CreateCommand();
        ins.CommandText = "INSERT INTO orphan_nt_files(rel_path,name,size,mtime,domain,ref_count) VALUES ($p,$n,$s,$m,$d,$rc)";
        var p2 = ins.Parameters.Add("$p", SqliteType.Text);
        var n2 = ins.Parameters.Add("$n", SqliteType.Text);
        var s2 = ins.Parameters.Add("$s", SqliteType.Integer);
        var m2 = ins.Parameters.Add("$m", SqliteType.Integer);
        var d2 = ins.Parameters.Add("$d", SqliteType.Text);
        var rc2 = ins.Parameters.Add("$rc", SqliteType.Integer);
        using (var cmd = index.CreateCommand())
        {
            cmd.CommandText = "SELECT rel_path, name, size, mtime, domain FROM nt_files ORDER BY rel_path";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var rel = r.GetString(0);
                var name = r.GetString(1);
                var mtime = r.GetInt64(3);
                var domain = r.GetString(4);
                if (referenced.Contains(rel) || referenced.Contains(name)) continue;
                var sz = r.GetInt64(2);
                orphanBytes += sz;
                var stem = Path.GetFileNameWithoutExtension(name);
                p2.Value = rel; n2.Value = name; s2.Value = sz; m2.Value = mtime; d2.Value = domain;
                rc2.Value = stem.Length == 32 && stem.All(Uri.IsHexDigit)
                    ? refCounts.GetValueOrDefault(stem.ToUpperInvariant())
                    : 0;
                ins.ExecuteNonQuery();
            }
        }
        using (var cnt = index.CreateCommand())
        {
            cnt.CommandText = "SELECT count(*) FROM orphan_nt_files";
            summary.OrphanFiles = cnt.ExecuteScalar() is long o ? o : 0;
        }
        summary.OrphanBytes = orphanBytes;

        using (var meta = index.CreateCommand())
        {
            meta.CommandText = "INSERT OR REPLACE INTO meta(key,value) VALUES ($k,$v)";
            meta.Parameters.AddWithValue("$k", "built_at");
            meta.Parameters.AddWithValue("$v", DateTimeOffset.Now.ToUnixTimeSeconds().ToString());
            meta.ExecuteNonQuery();

            meta.Parameters["$k"].Value = "nt_data_root";
            meta.Parameters["$v"].Value = Path.GetFullPath(ntDataDir);
            meta.ExecuteNonQuery();
        }
        tx.Commit();
        return summary;
    }

    /// <summary>
    /// Indexes emoji favorites: fav_emoji_info_storage_table carries absolute
    /// local paths (80012 = file, 80014 = thumb) plus md5 (80011).
    /// </summary>
    private static void IndexEmoji(Workspace workspace, SqliteConnection index, string ntDataDir, IndexBuildSummary summary)
    {
        var plain = workspace.PlainDbPath("emoji.db");
        if (!File.Exists(plain)) { summary.Notes.Add("emoji.db: 未提供（跳过）"); return; }

        using var src = NtqSqlite.OpenReadOnly(plain);
        if (!TableExists(src, "fav_emoji_info_storage_table"))
        {
            summary.Notes.Add("emoji.db: 没有 fav_emoji_info_storage_table（跳过）");
            return;
        }

        var insert = index.CreateCommand();
        insert.CommandText = """
            INSERT INTO media(source, source_table, msg_id, chat_id, chat_type, kind, file_name, rel_path,
                              abs_path, thumb_rel, size_db, size_bytes, actual_size, msg_time, time_source,
                              md5, uuid, confidence)
            VALUES ('emoji','fav_emoji_info_storage_table',NULL,NULL,NULL,'emoji',$fname,$rpath,
                    $apath,$trel,NULL,$sbytes,$actual,$mtime,'file',$md5,NULL,$conf)
            """;
        var pFname = insert.Parameters.Add("$fname", SqliteType.Text);
        var pRpath = insert.Parameters.Add("$rpath", SqliteType.Text);
        var pApath = insert.Parameters.Add("$apath", SqliteType.Text);
        var pTrel = insert.Parameters.Add("$trel", SqliteType.Text);
        var pSbytes = insert.Parameters.Add("$sbytes", SqliteType.Integer);
        var pActual = insert.Parameters.Add("$actual", SqliteType.Integer);
        var pMtime = insert.Parameters.Add("$mtime", SqliteType.Integer);
        var pMd5 = insert.Parameters.Add("$md5", SqliteType.Text);
        var pConf = insert.Parameters.Add("$conf", SqliteType.Text);

        using var cmd = src.CreateCommand();
        cmd.CommandText = "SELECT \"80011\", \"80012\", \"80014\" FROM \"fav_emoji_info_storage_table\" " +
                          "WHERE \"80012\" IS NOT NULL AND \"80012\" <> ''";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var md5 = r.IsDBNull(0) ? "" : r.GetString(0);
            var abs = r.GetString(1);
            var thumbAbs = r.IsDBNull(2) ? "" : r.GetString(2);

            if (!File.Exists(abs)) continue; // favorite recorded but never downloaded
            var fi = new FileInfo(abs);
            var rel = "";
            var full = Path.GetFullPath(abs);
            var rootFull = Path.GetFullPath(ntDataDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                rel = full[rootFull.Length..].Replace('\\', '/');

            pFname.Value = Path.GetFileName(abs);
            pRpath.Value = rel;
            pApath.Value = full;
            pTrel.Value = string.IsNullOrEmpty(thumbAbs) ? "" : thumbAbs;
            pSbytes.Value = fi.Length;
            pActual.Value = fi.Length;
            pMtime.Value = ((DateTimeOffset)fi.LastWriteTimeUtc).ToUnixTimeSeconds();
            pMd5.Value = string.IsNullOrEmpty(md5) ? DBNull.Value : md5;
            pConf.Value = "exact";
            insert.ExecuteNonQuery();
            summary.MediaRows++;
            summary.Resolved++;
        }
    }

    /// <summary>Tables that look like media metadata (have the 45402/45403/45405 signature).</summary>
    internal static List<string> ListMediaTables(SqliteConnection src)
    {
        var result = new List<string>();
        using var cmd = src.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
        using var r = cmd.ExecuteReader();
        var tables = new List<string>();
        while (r.Read()) tables.Add(r.GetString(0));

        foreach (var table in tables)
        {
            using var ci = src.CreateCommand();
            ci.CommandText = $"PRAGMA table_info(\"{table.Replace("\"", "\"\"")}\")";
            var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var cr = ci.ExecuteReader())
                while (cr.Read()) cols.Add(cr.GetString(1));
            if (cols.Contains("45402") && cols.Contains("45403") && cols.Contains("45405"))
                result.Add(table);
        }
        return result;
    }

    private static long IndexTable(SqliteConnection src, string srcPath, string table, IndexProfile profile,
        SqliteConnection index, Dictionary<string, List<(string rel, long size)>> ntIndex,
        string ntDataDir, IndexBuildSummary summary)
    {
        var q = table.Replace("\"", "\"\"");

        bool HasCol(string col)
        {
            using var ci = src.CreateCommand();
            ci.CommandText = $"PRAGMA table_info(\"{q}\")";
            using var r = ci.ExecuteReader();
            while (r.Read()) if (string.Equals(r.GetString(1), col, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        var hasChat = HasCol(profile.ChatCol);
        var hasChatType = HasCol(profile.ChatTypeCol);
        var hasTime = !string.IsNullOrEmpty(profile.TimeCol) && HasCol(profile.TimeCol);
        var hasUuid = HasCol(profile.UuidCol);
        var hasThumb = HasCol(profile.ThumbCol);
        var hasMsgId = HasCol(profile.MsgIdCol);

        var cols = $"rowid, \"{profile.NameCol}\", \"{profile.PathCol}\", \"{profile.SizeCol}\"";
        if (hasChat) cols += $", \"{profile.ChatCol}\"";
        if (hasChatType) cols += $", \"{profile.ChatTypeCol}\"";
        if (hasTime) cols += $", \"{profile.TimeCol}\"";
        if (hasUuid) cols += $", \"{profile.UuidCol}\"";
        if (hasThumb) cols += $", \"{profile.ThumbCol}\"";
        if (hasMsgId) cols += $", \"{profile.MsgIdCol}\"";

        var insert = index.CreateCommand();
        insert.CommandText = """
            INSERT INTO media(source, source_table, msg_id, chat_id, chat_type, kind, file_name, rel_path,
                              abs_path, thumb_rel, size_db, size_bytes, actual_size, msg_time, time_source,
                              md5, uuid, confidence)
            VALUES ($src,$tbl,$mid,$chat,$ctype,$kind,$fname,$rpath,$apath,$trel,$sdb,$sbytes,$actual,$mtime,$tsrc,$md5,$uuid,$conf)
            """;
        var ps = insert.Parameters;
        ps.Add("$src", SqliteType.Text).Value = profile.Kind;
        ps.Add("$tbl", SqliteType.Text).Value = table;
        var pMid = ps.Add("$mid", SqliteType.Integer);
        var pChat = ps.Add("$chat", SqliteType.Text);
        var pCtype = ps.Add("$ctype", SqliteType.Integer);
        var pKind = ps.Add("$kind", SqliteType.Text);
        var pFname = ps.Add("$fname", SqliteType.Text);
        var pRpath = ps.Add("$rpath", SqliteType.Text);
        var pApath = ps.Add("$apath", SqliteType.Text);
        var pTrel = ps.Add("$trel", SqliteType.Text);
        var pSdb = ps.Add("$sdb", SqliteType.Integer);
        var pSbytes = ps.Add("$sbytes", SqliteType.Integer);
        var pActual = ps.Add("$actual", SqliteType.Integer);
        var pMtime = ps.Add("$mtime", SqliteType.Integer);
        var pTsrc = ps.Add("$tsrc", SqliteType.Text);
        var pMd5 = ps.Add("$md5", SqliteType.Text);
        var pUuid = ps.Add("$uuid", SqliteType.Text);
        var pConf = ps.Add("$conf", SqliteType.Text);

        using var select = src.CreateCommand();
        select.CommandText = $"SELECT {cols} FROM \"{q}\"";
        using var reader = select.ExecuteReader();

        long count = 0;
        long bad = 0;
        while (reader.Read())
        {
            long rowid = 0;
            try
            {
                ProcessRow();
            }
            catch (Exception ex)
            {
                bad++;
                if (summary.Notes.Count < 10)
                    summary.Notes.Add($"{table}: rowid {rowid} 解析失败（已跳过）: {ex.Message}");
            }

            void ProcessRow()
            {
                rowid = reader.GetInt64(0);
            var i = 1;
            string name = reader.IsDBNull(i) ? "" : reader.GetValue(i)?.ToString() ?? ""; i++;
            string relPathInDb = reader.IsDBNull(i) ? "" : reader.GetValue(i)?.ToString() ?? ""; i++;
            long? sizeDb = reader.IsDBNull(i) ? null : Convert.ToInt64(reader.GetValue(i)); i++;
            string? chat = hasChat ? (reader.IsDBNull(i) ? null : reader.GetValue(i)?.ToString()) : null; if (hasChat) i++;
            long? chatType = hasChatType ? (reader.IsDBNull(i) ? null : Convert.ToInt64(reader.GetValue(i))) : null; if (hasChatType) i++;
            long? dbTime = hasTime ? (reader.IsDBNull(i) ? null : Convert.ToInt64(reader.GetValue(i))) : null; if (hasTime) i++;
            string? uuid = hasUuid ? (reader.IsDBNull(i) ? null : reader.GetValue(i)?.ToString()) : null; if (hasUuid) i++;
            string? thumb = hasThumb ? (reader.IsDBNull(i) ? null : reader.GetValue(i)?.ToString()) : null; if (hasThumb) i++;
            long? msgId = hasMsgId ? (reader.IsDBNull(i) ? null : Convert.ToInt64(reader.GetValue(i))) : null; if (hasMsgId) i++;

            if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(relPathInDb)) return;

            // Resolve against nt_data.
            var resolvedRel = "";
            long actualSize = -1;
            long fileMtime = -1;
            var confidence = "missing";

            var candidateRel = relPathInDb?.Trim().Replace('\\', '/') ?? "";
            if (candidateRel.StartsWith("nt_data/", StringComparison.OrdinalIgnoreCase))
                candidateRel = candidateRel["nt_data/".Length..];
            if (candidateRel.Length > 0)
            {
                var abs = Path.Combine(ntDataDir, candidateRel.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(abs))
                {
                    var fi = new FileInfo(abs);
                    resolvedRel = candidateRel;
                    actualSize = fi.Length;
                    fileMtime = ((DateTimeOffset)fi.LastWriteTimeUtc).ToUnixTimeSeconds();
                    confidence = (sizeDb is > 0 && sizeDb != fi.Length) ? "strong" : "exact";
                }
            }

            if (confidence == "missing" && !string.IsNullOrWhiteSpace(name)
                && ntIndex.TryGetValue(name, out var hits))
            {
                (string rel, long size)? pick = hits.Count == 1 ? hits[0] : null;
                pick ??= hits.FirstOrDefault(h => sizeDb is > 0 && h.size == sizeDb);
                pick ??= hits.FirstOrDefault();
                if (pick is { } hit)
                {
                    resolvedRel = hit.rel;
                    actualSize = hit.size;
                    confidence = (sizeDb is > 0 && sizeDb != hit.size) ? "strong" : "exact";
                }
            }

            var kind = KindOfExtension(Path.GetExtension(name is { Length: > 0 } ? name : resolvedRel));
            if (kind == "other" && resolvedRel.Length > 0)
                kind = KindOfExtension(Path.GetExtension(resolvedRel));

            var md5 = "";
            var nameCore = name is { Length: > 0 } ? name : Path.GetFileName(resolvedRel);
            if (Md5NameRegex().IsMatch(nameCore)) md5 = nameCore[..32].ToLowerInvariant();

            // time: db value, else resolved file mtime.
            long msgTime = dbTime ?? (confidence != "missing" ? fileMtime : -1);
            var timeSource = dbTime.HasValue ? "db" : (msgTime > 0 ? "file" : "none");

            // size_bytes estimate: db value; KB-calibrated when the resolved actual
            // is ~1024x the recorded value (files_in_chat records KB per community docs).
            long sizeBytes = sizeDb ?? -1;
            var sdb = sizeDb.GetValueOrDefault();
            if (sizeDb is > 0 && actualSize is > 0)
            {
                if (Math.Abs(sdb - actualSize) <= 1) sizeBytes = actualSize;
                else if (Math.Abs(sdb * 1024 - actualSize) <= 1024) sizeBytes = actualSize;
            }
            else if (sizeDb is > 0)
            {
                sizeBytes = sdb; // cannot calibrate; assume raw
            }

            pMid.Value = msgId ?? (object)DBNull.Value;
            pChat.Value = string.IsNullOrEmpty(chat) ? (object)DBNull.Value : chat;
            pCtype.Value = chatType ?? (object)DBNull.Value;
            pKind.Value = kind;
            pFname.Value = name ?? "";
            pRpath.Value = resolvedRel;
            pApath.Value = resolvedRel.Length > 0
                ? Path.Combine(ntDataDir, resolvedRel.Replace('/', Path.DirectorySeparatorChar))
                : "";
            pTrel.Value = thumb ?? "";
            pSdb.Value = sizeDb ?? (object)DBNull.Value;
            pSbytes.Value = sizeBytes >= 0 ? sizeBytes : (object)DBNull.Value;
            pActual.Value = actualSize >= 0 ? actualSize : (object)DBNull.Value;
            pMtime.Value = msgTime > 0 ? msgTime : (object)DBNull.Value;
            pTsrc.Value = timeSource;
            pMd5.Value = md5.Length > 0 ? md5 : (object)DBNull.Value;
            pUuid.Value = string.IsNullOrEmpty(uuid) ? (object)DBNull.Value : uuid;
            pConf.Value = confidence;

            if (chat is { Length: > 0 })
            {
                var up = index.CreateCommand();
                up.CommandText = "INSERT OR IGNORE INTO chats(chat_id, chat_type) VALUES ($c,$t)";
                up.Parameters.AddWithValue("$c", chat);
                up.Parameters.AddWithValue("$t", chatType ?? (object)DBNull.Value);
                up.ExecuteNonQuery();
            }

            insert.ExecuteNonQuery();
            count++;
            if (confidence != "missing") summary.Resolved++; else summary.Missing++;
            }
        }
        if (bad > 0)
            summary.Notes.Add($"{table}: 共 {bad} 行解析失败被跳过");
        return count;
    }

    /// <summary>
    /// Best-effort chat display names from group_info.db: walks rows, votes for
    /// (group-id → protobuf string field) pairs. Heuristic by design; names carry
    /// a confidence flag and are display-only.
    /// </summary>
    private static long BuildChatNames(Workspace workspace, SqliteConnection index)
    {
        long named = 0;
        var groupInfoPath = workspace.PlainDbPath("group_info.db");
        if (!File.Exists(groupInfoPath)) return 0;

        try
        {
            using var src = NtqSqlite.OpenReadOnly(groupInfoPath);
            var votes = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);

            foreach (var table in ListAllTables(src))
            {
                using var cmd = src.CreateCommand();
                cmd.CommandText = $"SELECT * FROM \"{table.Replace("\"", "\"\"")}\" LIMIT 3000";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    string? groupId = null;
                    var names = new List<string>();
                    for (var c = 0; c < r.FieldCount; c++)
                    {
                        var v = r.GetValue(c);
                        switch (v)
                        {
                            case long l when l is >= 10000 and <= 999999999999:
                                groupId ??= l.ToString();
                                break;
                            case byte[] blob when blob.Length is > 4 and < 8192:
                                ExtractNames(blob, 0, names);
                                break;
                        }
                    }
                    if (groupId is null || names.Count == 0) continue;
                    if (!votes.TryGetValue(groupId, out var tally)) votes[groupId] = tally = new();
                    foreach (var n in names)
                        tally[n] = tally.TryGetValue(n, out var x) ? x + 1 : 1;
                }
            }

            using var up = index.CreateCommand();
            up.CommandText = "UPDATE chats SET display_name=$n, name_confident=0 WHERE chat_id=$c";
            var nParam = up.Parameters.Add("$n", SqliteType.Text);
            var cParam = up.Parameters.Add("$c", SqliteType.Text);

            foreach (var (groupId, tally) in votes)
            {
                if (tally.Count == 0) continue;
                var best = tally.OrderByDescending(kv => kv.Value).First();
                if (best.Value < 1 || best.Key.Length is < 2 or > 64) continue;
                cParam.Value = groupId;
                nParam.Value = best.Key;
                if (up.ExecuteNonQuery() > 0) named++;
            }
        }
        catch
        {
            // names are optional; never fail the index build over them
        }
        return named;
    }

    private static void ExtractNames(byte[] blob, int depth, List<string> into)
    {
        if (depth > 3) return;
        foreach (var f in ProtoWalker.Walk(blob))
        {
            if (f.WireType != 2) continue;
            if (f.ValueSize is < 2 or > 64 && f.ValueSize > 8192) continue;
            var slice = blob.AsSpan(f.ValueOffset, f.ValueSize);
            if (f.ValueSize is >= 2 and <= 64)
            {
                try
                {
                    var s = System.Text.Encoding.UTF8.GetString(slice);
                    if (s.All(ch => !char.IsControl(ch)) && s.Any(char.IsLetterOrDigit))
                        into.Add(s);
                }
                catch { /* not utf8 */ }
            }
            // descend into nested length-delimited payloads (protobuf sub-messages)
            if (f.ValueSize is > 4 and <= 8192 && (slice[0] & 7) is 0 or 2)
            {
                try { ExtractNames(slice.ToArray(), depth + 1, into); } catch { /* malformed */ }
            }
        }
    }

    private static List<string> ListAllTables(SqliteConnection src)
    {
        var tables = new List<string>();
        using var cmd = src.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
        using var r = cmd.ExecuteReader();
        while (r.Read()) tables.Add(r.GetString(0));
        return tables;
    }

    private static IEnumerable<string> SafeEnumerateFiles(string dir)
    {
        var stack = new Stack<string>();
        stack.Push(dir);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            string[] files = Array.Empty<string>();
            string[] subs = Array.Empty<string>();
            try
            {
                files = Directory.GetFiles(current);
                subs = Directory.GetDirectories(current);
            }
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException) { continue; }
            foreach (var f in files) yield return f;
            foreach (var s in subs) stack.Push(s);
        }
    }

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static bool TableExists(SqliteConnection c, string t)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name=$n";
        cmd.Parameters.AddWithValue("$n", t);
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    private static SqliteConnection CreateIndex(string path)
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        conn.Open();
        return conn;
    }
}

public static class NtqSqlite
{
    public static SqliteConnection OpenReadOnly(string path)
    {
        var conn = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly }.ToString());
        conn.Open();
        return conn;
    }
}
