using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;

namespace NTQlean.Core;

public sealed record NtMsgRefStats(long RowsScanned, long BlobsScanned, long BytesScanned,
    int DistinctHits, long RowsSkipped);

/// <summary>
/// Scans a decrypted nt_msg.db for binary/ASCII md5 references to nt_data files.
/// This answers "is this orphan file actually referenced by a chat message?"
/// without needing a full protobuf decode of column 40800.
///
/// Technique: fingerprint-bucketed multi-pattern search — every candidate md5 is
/// indexed as its 16-byte binary form and 32-char ASCII form under the pattern's
/// first two bytes; each blob byte position costs two dictionary lookups.
/// Work is split across rowid ranges scanned in parallel; corrupt pages (QQ was
/// running during the snapshot) shrink the batch and skip bad rowids instead of
/// aborting.
/// </summary>
public static class NtMsgRefScanner
{
    private sealed class HitInfo
    {
        public int Hits;
        public string? SampleTable;
        public long SampleRowid;
        public long SampleTime;
        public string? SamplePeer;
    }

    public static NtMsgRefStats Scan(
        string plainNtMsgPath,
        SqliteConnection index,
        IReadOnlySet<string> md5Set,
        IProgress<string>? progress = null)
    {
        // ── shared, read-only pattern tables ──
        // ASCII hex fingerprints use the first FOUR bytes (hex pairs only span 256
        // two-byte combos, which would overload buckets); binary uses two bytes.
        var bin = new Dictionary<ushort, List<byte[]>>();
        var ascii = new Dictionary<uint, List<byte[]>>();
        foreach (var md5 in md5Set)
        {
            var bytes = new byte[16];
            for (var i = 0; i < 16; i++)
                bytes[i] = Convert.ToByte(md5.Substring(i * 2, 2), 16);
            Add(bin, Fingerprint2(bytes), bytes);

            var upper = System.Text.Encoding.ASCII.GetBytes(md5.ToUpperInvariant());
            var lower = System.Text.Encoding.ASCII.GetBytes(md5.ToLowerInvariant());
            Add(ascii, Fingerprint4(upper), upper);
            Add(ascii, Fingerprint4(lower), lower);
        }

        var grand = new Dictionary<string, HitInfo>(StringComparer.OrdinalIgnoreCase);
        var grandLock = new object();
        long rowsTotal = 0, blobsTotal = 0, bytesTotal = 0, skippedTotal = 0;
        var workers = Math.Clamp(Environment.ProcessorCount - 2, 1, 12);

        using (var src = NtqSqlite.OpenReadOnly(plainNtMsgPath))
        {
            foreach (var (table, timeCol, peerCol) in MessageTables(src))
            {
                var peerExists = ColumnExists(src, table, peerCol);
                var (lo, hi) = RowIdRange(src, table);
                if (hi <= 0) continue;

                progress?.Report($"nt_msg: 并行扫描 {table}（rowid {lo:N0}–{hi:N0}, {workers} 线程）…");
                var rangeSize = Math.Max(1, (hi - lo + 1) / workers);

                var ranges = new List<(long lo, long hi)>();
                for (var start = lo; start <= hi; start += rangeSize)
                    ranges.Add((start, Math.Min(hi, start + rangeSize - 1)));

                System.Threading.Tasks.Parallel.ForEach(ranges, range =>
                {
                    var hits = new Dictionary<string, HitInfo>(StringComparer.OrdinalIgnoreCase);
                    long rows = 0, blobs = 0, bytes = 0, skipped = 0;
                    try
                    {
                        ScanRange(plainNtMsgPath, table, timeCol, peerCol, peerExists,
                            range.lo, range.hi, bin, ascii, hits,
                            ref rows, ref blobs, ref bytes, ref skipped);
                    }
                    catch
                    {
                        // worker-level failure: keep whatever other workers found
                    }
                    Interlocked.Add(ref rowsTotal, rows);
                    Interlocked.Add(ref blobsTotal, blobs);
                    Interlocked.Add(ref bytesTotal, bytes);
                    Interlocked.Add(ref skippedTotal, skipped);
                    lock (grandLock)
                    {
                        foreach (var (md5, info) in hits)
                        {
                            if (!grand.TryGetValue(md5, out var g)) grand[md5] = g = info;
                            else g.Hits += info.Hits;
                        }
                    }
                    progress?.Report($"nt_msg: 累计 {Interlocked.Read(ref rowsTotal):N0} 行 / " +
                                     $"{Interlocked.Read(ref bytesTotal) / 1048576.0:F0} MB，" +
                                     $"命中 {grand.Count:N0} 个文件，跳坏行 {Interlocked.Read(ref skippedTotal):N0}");
                });
            }
        }

        // ── write nt_refs ──
        Exec(index, "CREATE TABLE IF NOT EXISTS nt_refs(md5 TEXT PRIMARY KEY, hits INTEGER, " +
                    "sample_table TEXT, sample_rowid INTEGER, sample_time INTEGER, sample_peer TEXT)");
        using var tx = index.BeginTransaction();
        var ins = index.CreateCommand();
        ins.CommandText = "INSERT OR REPLACE INTO nt_refs(md5,hits,sample_table,sample_rowid,sample_time,sample_peer) " +
                          "VALUES ($m,$h,$t,$r,$ts,$p)";
        var pm = ins.Parameters.Add("$m", SqliteType.Text);
        var ph = ins.Parameters.Add("$h", SqliteType.Integer);
        var pt = ins.Parameters.Add("$t", SqliteType.Text);
        var pr = ins.Parameters.Add("$r", SqliteType.Integer);
        var pts = ins.Parameters.Add("$ts", SqliteType.Integer);
        var pp = ins.Parameters.Add("$p", SqliteType.Text);
        foreach (var (md5, info) in grand)
        {
            pm.Value = md5.ToUpperInvariant();
            ph.Value = info.Hits;
            pt.Value = (object?)info.SampleTable ?? DBNull.Value;
            pr.Value = info.SampleRowid;
            pts.Value = info.SampleTime;
            pp.Value = (object?)info.SamplePeer ?? DBNull.Value;
            ins.ExecuteNonQuery();
        }
        tx.Commit();

        return new NtMsgRefStats(rowsTotal, blobsTotal, bytesTotal, grand.Count, skippedTotal);
    }

    private static void ScanRange(
        string plainNtMsgPath, string table, string timeCol, string peerCol, bool peerExists,
        long lo, long hi,
        Dictionary<ushort, List<byte[]>> bin, Dictionary<uint, List<byte[]>> ascii,
        Dictionary<string, HitInfo> hits,
        ref long rows, ref long blobs, ref long bytes, ref long skipped)
    {
        const int Batch = 4000;
        var src = NtqSqlite.OpenReadOnly(plainNtMsgPath);
        try
        {
            var last = lo - 1;
            var batch = Batch;
            var select = peerExists
                ? $"SELECT rowid, \"{timeCol}\", \"{peerCol}\", \"40800\" FROM \"{table}\" " +
                  "WHERE rowid > $last AND rowid <= $hi ORDER BY rowid LIMIT $n"
                : $"SELECT rowid, \"{timeCol}\", NULL, \"40800\" FROM \"{table}\" " +
                  "WHERE rowid > $last AND rowid <= $hi ORDER BY rowid LIMIT $n";

            while (last < hi)
            {
                var batchRows = new List<(long rowid, long time, string? peer, object body)>();
                try
                {
                    using (var cmd = src.CreateCommand())
                    {
                        cmd.CommandText = select;
                        cmd.Parameters.AddWithValue("$last", last);
                        cmd.Parameters.AddWithValue("$hi", hi);
                        cmd.Parameters.AddWithValue("$n", batch);
                        using var r = cmd.ExecuteReader();
                        while (r.Read())
                        {
                            var rowid = r.GetInt64(0);
                            last = rowid;
                            long time = !r.IsDBNull(1) && long.TryParse(r.GetValue(1)?.ToString(), out var t) ? t : 0;
                            string? peer = peerExists && !r.IsDBNull(2) ? r.GetValue(2)?.ToString() : null;
                            var body = r.GetValue(3);
                            batchRows.Add((rowid, time, peer, body));
                        }
                    }
                }
                catch (SqliteException)
                {
                    // corrupt page: reconnect, shrink batch, skip the bad rowid at batch=1
                    src.Dispose();
                    src = NtqSqlite.OpenReadOnly(plainNtMsgPath);
                    if (batch == 1)
                    {
                        last += 1;
                        skipped++;
                        batch = 200;
                    }
                    else
                    {
                        batch = Math.Max(1, batch / 4);
                    }
                    continue;
                }

                foreach (var (rowid, time, peer, body) in batchRows)
                {
                    switch (body)
                    {
                        case byte[] blob:
                            blobs++;
                            bytes += blob.Length;
                            ScanBlob(blob, bin, ascii, hits, table, rowid, time, peer);
                            break;
                        case string s:
                            blobs++;
                            bytes += s.Length;
                            ScanBlob(System.Text.Encoding.UTF8.GetBytes(s), bin, ascii, hits, table, rowid, time, peer);
                            break;
                    }
                    rows++;
                }

                if (batchRows.Count == 0) break; // range exhausted
                if (batch < Batch) batch = Math.Min(Batch, batch * 2);
            }
        }
        finally
        {
            src.Dispose();
        }
    }

    private static void ScanBlob(byte[] blob,
        Dictionary<ushort, List<byte[]>> bin,
        Dictionary<uint, List<byte[]>> ascii,
        Dictionary<string, HitInfo> hits, string table, long rowid, long time, string? peer)
    {
        if (blob.Length >= 16)
        {
            var binLimit = blob.Length - 16;
            for (var i = 0; i <= binLimit; i++)
            {
                var key = (ushort)((blob[i] << 8) | blob[i + 1]);
                if (!bin.TryGetValue(key, out var list)) continue;
                foreach (var pat in list)
                {
                    if (!MatchesAt(blob, i, pat)) continue;
                    RecordHit(hits, pat, table, rowid, time, peer);
                }
            }
        }

        if (blob.Length >= 32)
        {
            var asciiLimit = blob.Length - 32;
            for (var i = 0; i <= asciiLimit; i++)
            {
                // cheap prefilter: pattern chars are all hex digits [0-9a-fA-F]
                if (!IsHexByte(blob[i])) continue;
                var key = (uint)((blob[i] << 24) | (blob[i + 1] << 16) | (blob[i + 2] << 8) | blob[i + 3]);
                if (!ascii.TryGetValue(key, out var list)) continue;
                foreach (var pat in list)
                {
                    if (!MatchesAt(blob, i, pat)) continue;
                    var binKey = new byte[16];
                    for (var k = 0; k < 16; k++)
                        binKey[k] = Convert.ToByte(System.Text.Encoding.ASCII.GetString(pat, k * 2, 2), 16);
                    RecordHit(hits, binKey, table, rowid, time, peer);
                }
            }
        }
    }

    private static void RecordHit(Dictionary<string, HitInfo> hits, byte[] binKey,
        string table, long rowid, long time, string? peer)
    {
        var sb = new System.Text.StringBuilder(32);
        foreach (var b in binKey) sb.Append(b.ToString("X2"));
        var md5 = sb.ToString();
        if (!hits.TryGetValue(md5, out var info)) hits[md5] = info = new HitInfo();
        info.Hits++;
        if (info.SampleTable is null)
        {
            info.SampleTable = table;
            info.SampleRowid = rowid;
            info.SampleTime = time;
            info.SamplePeer = peer;
        }
    }

    private static bool MatchesAt(byte[] blob, int i, byte[] pat)
    {
        for (var k = 1; k < pat.Length; k++)
            if (blob[i + k] != pat[k]) return false;
        return true;
    }

    private static ushort Fingerprint2(byte[] pat) =>
        (ushort)((pat[0] << 8) | pat[1]);

    private static uint Fingerprint4(byte[] pat) =>
        (uint)((pat[0] << 24) | (pat[1] << 16) | (pat[2] << 8) | pat[3]);

    private static bool IsHexByte(byte b) =>
        b is >= (byte)'0' and <= (byte)'9' or >= (byte)'a' and <= (byte)'f' or >= (byte)'A' and <= (byte)'F';

    private static void Add<TK>(Dictionary<TK, List<byte[]>> dict, TK key, byte[] pattern) where TK : notnull
    {
        if (!dict.TryGetValue(key, out var list)) dict[key] = list = new();
        list.Add(pattern);
    }

    private static IEnumerable<(string Table, string TimeCol, string PeerCol)> MessageTables(SqliteConnection src)
    {
        if (TableExists(src, "group_msg_table"))
            yield return ("group_msg_table", "40050", "40021");
        if (TableExists(src, "c2c_msg_table"))
            yield return ("c2c_msg_table", "40050", "40030");
    }

    private static (long lo, long hi) RowIdRange(SqliteConnection c, string table)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT min(rowid), max(rowid) FROM \"{table}\"";
        using var r = cmd.ExecuteReader();
        if (!r.Read() || r.IsDBNull(0)) return (0, 0);
        return (r.GetInt64(0), r.GetInt64(1));
    }

    private static bool TableExists(SqliteConnection c, string t)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name=$n";
        cmd.Parameters.AddWithValue("$n", t);
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    private static bool ColumnExists(SqliteConnection c, string table, string col)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info(\"{table}\")";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            if (string.Equals(r.GetString(1), col, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
