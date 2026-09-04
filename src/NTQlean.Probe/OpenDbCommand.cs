using Microsoft.Data.Sqlite;
using NTQlean.Core;

internal static class OpenDbCommand
{
    private const string KnownLoginDbKey = "BD156D6710D54D8782F4";

    public static int Run(string[] args)
    {
        string? path = null;
        string? key = null;
        long? maxPages = null;
        string? outDir = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--key":
                    if (i + 1 >= args.Length)
                    {
                        ProbeLog.Error("--key requires a value");
                        return 1;
                    }
                    key = args[++i];
                    ProbeLog.Warning("key passed on the command line is visible in shell history; " +
                                     "prefer interactive entry (omit --key)");
                    break;
                case "--pages":
                    if (i + 1 < args.Length && long.TryParse(args[++i], out var p))
                        maxPages = p;
                    break;
                case "--out":
                    if (i + 1 < args.Length) outDir = args[++i];
                    break;
                default:
                    if (path is null) path = args[i];
                    break;
            }
        }

        if (path is null || !File.Exists(path))
        {
            ProbeLog.Error("usage: ntqlean-probe open-db <path> [--key <key>] [--pages <n>] [--out <dir>]");
            return 1;
        }

        var header = DbHeaderInspector.Inspect(path);
        if (header.Format == DbFormatKind.PlainSqlite)
        {
            ProbeLog.Info("Target is a plain SQLite file; opening a copy read-only (no key needed)");
            outDir ??= DefaultWorkspace();
            var snap = NtqqSnapshot.CopyDatabase(path, outDir);
            using var conn = OpenReadOnly(snap.CopiedPath);
            Shared.DumpSchema(conn);
            return 0;
        }

        if (header.Format != DbFormatKind.NtqqEncrypted)
        {
            ProbeLog.Unconfirmed("Target is not recognized as an NTQQ encrypted database; refusing to guess");
            return 1;
        }

        // login.db uses a public hardcoded key; try it automatically first.
        var fileName = Path.GetFileName(path);
        var isLoginDb = fileName.StartsWith("login", StringComparison.OrdinalIgnoreCase);
        var attempts = new List<(string Key, string Origin)>();
        if (isLoginDb)
            attempts.Add((KnownLoginDbKey, "public hardcoded login.db key"));
        if (key is not null)
            attempts.Add((key, "user-supplied key"));
        if (key is null)
        {
            var entered = Shared.ReadSecret("Enter database key (input hidden): ");
            if (entered is not null) attempts.Add((entered, "interactively supplied key"));
        }

        outDir ??= DefaultWorkspace();
        ProbeLog.Info($"Workspace: {outDir}");
        var snapshot = NtqqSnapshot.CopyDatabase(path, outDir);
        ProbeLog.Info($"Snapshot created: {Path.GetFileName(snapshot.CopiedPath)} " +
                      $"(sidecars copied: [{string.Join(", ", snapshot.CopiedSidecars)}], " +
                      $"skipped: [{string.Join(", ", snapshot.SkippedSidecars)}])");
        ProbeLog.Research("Decrypting page-by-page (the NTQQ 1024-byte header is skipped by the " +
                          "decryptor itself) into a plain SQLite copy");

        var outputPath = Path.Combine(outDir, Path.GetFileNameWithoutExtension(path) + ".plain.db");

        foreach (var (attemptKey, origin) in attempts)
        {
            foreach (var config in ConfigCandidates(header))
            {
                ProbeLog.Research($"Trying config: page={config.PageSize} kdf_iter={config.KdfIterations} " +
                                  $"kdf={(config.KdfUseSha512 ? "SHA512" : "SHA1")} " +
                                  $"hmac={config.Hmac} — key origin: {origin}");

                var result = SqlCipherDecryptor.DecryptCopy(
                    snapshot.CopiedPath,
                    outputPath,
                    attemptKey,
                    config,
                    maxPages);

                if (!result.Success)
                {
                    ProbeLog.Unconfirmed($"failed: {result.FailureReason}");
                    continue;
                }

                ProbeLog.Info($"Decryption succeeded ({result.PagesDecrypted:N0} pages, " +
                              $"page={result.Config.PageSize}, kdf_iter={result.Config.KdfIterations}, " +
                              $"rawKey={result.RawKeyMode}{(result.Truncated ? ", TRUNCATED (research mode)" : "")})");
                if (result.PagesHmacBad == 0)
                    ProbeLog.Info($"HMAC verification: {result.PagesHmacOk:N0}/" +
                                  $"{result.PagesHmacOk + result.PagesHmacBad:N0} pages OK — " +
                                  "key and parameters fully confirmed");
                else
                    ProbeLog.Unconfirmed($"HMAC verification: {result.PagesHmacOk:N0} ok / " +
                                         $"{result.PagesHmacBad:N0} mismatch (pgno endianness or " +
                                         "HMAC variant difference; data still decrypted)");

                try
                {
                    using var conn = OpenReadOnly(result.OutputPath);
                    Shared.DumpSchema(conn);
                }
                catch (Exception ex)
                {
                    ProbeLog.Error($"decrypted copy could not be opened as SQLite: {ex.Message}");
                    return 1;
                }

                ProbeLog.Info($"Plain copy kept for research: {result.OutputPath}");
                ProbeLog.Info("Original QQ files were only ever opened for shared read; nothing written.");
                return 0;
            }
        }

        ProbeLog.Error("All key/parameter attempts failed. " +
                       "If this is an account database, its key is per-account: obtain it outside " +
                       "this tool (see docs/research-notes.md) and re-run with interactive entry.");
        return 2;
    }

    private static IEnumerable<SqlCipherConfig> ConfigCandidates(DbHeaderInfo header)
    {
        var hmacFirst = header.HeaderHmacAlgorithm?.ToUpperInvariant() switch
        {
            "HMAC_SHA256" => HmacAlgorithm.HmacSha256,
            _ => HmacAlgorithm.HmacSha1,
        };
        var hmacSecond = hmacFirst == HmacAlgorithm.HmacSha1 ? HmacAlgorithm.HmacSha256 : HmacAlgorithm.HmacSha1;

        // Community-verified NTQQ parameters first (page 4096 per QQDecrypt; page 1024
        // as claimed by the fake header), then SQLCipher v4 defaults, then v3-era params.
        yield return new SqlCipherConfig { PageSize = 4096, KdfIterations = 4000, KdfUseSha512 = true, Hmac = hmacFirst };
        yield return new SqlCipherConfig { PageSize = 1024, KdfIterations = 4000, KdfUseSha512 = true, Hmac = hmacFirst };
        yield return new SqlCipherConfig { PageSize = 4096, KdfIterations = 4000, KdfUseSha512 = true, Hmac = hmacSecond };
        yield return new SqlCipherConfig { PageSize = 1024, KdfIterations = 4000, KdfUseSha512 = true, Hmac = hmacSecond };
        yield return new SqlCipherConfig { PageSize = 4096, KdfIterations = 256000, KdfUseSha512 = true, Hmac = HmacAlgorithm.HmacSha512 };
        yield return new SqlCipherConfig { PageSize = 4096, KdfIterations = 64000, KdfUseSha512 = false, Hmac = HmacAlgorithm.HmacSha1 };
        yield return new SqlCipherConfig { PageSize = 1024, KdfIterations = 64000, KdfUseSha512 = false, Hmac = HmacAlgorithm.HmacSha1 };
    }

    internal static SqliteConnection OpenReadOnly(string path)
    {
        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
        };
        var conn = new SqliteConnection(csb.ToString());
        conn.Open();
        return conn;
    }

    private static string DefaultWorkspace()
    {
        var dir = Path.Combine(Path.GetTempPath(), "NTQlean",
                               DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
