using Microsoft.Data.Sqlite;

namespace NTQlean.Core;

public sealed record DecryptRequest(string SourceDb, string PlainTarget, long? MaxPages = null);

public sealed class DecryptOutcome
{
    public string Source { get; init; } = "";
    public bool Decrypted { get; init; }      // false = plain already existed / reused
    public SqlCipherConfig? Config { get; init; }
    public long Pages { get; init; }
    public long HmacOk { get; init; }
    public long HmacBad { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Orchestrates snapshot → SQLCipher decrypt → plain working copies inside the
/// workspace. The account key is held only in memory for the duration of a run.
/// </summary>
public static class DecryptService
{
    /// <summary>Databases required for the media/storage index.</summary>
    public static readonly string[] AccountDbs =
    {
        "files_in_chat.db", "rich_media.db", "file_assistant.db", "group_info.db", "emoji.db",
    };

    /// <summary>Optional heavy message database (indexed only on request).</summary>
    public const string NtMsgDb = "nt_msg.db";

    public static bool IsPlainSqlite(string path)
    {
        try
        {
            var info = DbHeaderInspector.Inspect(path, readSalt: false);
            return info.Format == DbFormatKind.PlainSqlite;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Decrypts (or reuses existing) plain copies for the requested databases.
    /// Re-decrypts when forceRebuild is set. Falls back to an existing plain copy
    /// when the QQ original is locked (QQ running).
    /// memoryKeys: optional salt→keys map from a QQ process memory scan — used
    /// when a database carries its own key rather than the shared account key.
    /// </summary>
    public static List<DecryptOutcome> DecryptAll(
        Workspace workspace,
        string? ntDbDir,
        string accountKey,
        IEnumerable<string> dbNames,
        bool forceRebuild = false,
        IProgress<string>? progress = null,
        IReadOnlyDictionary<string, List<string>>? memoryKeys = null)
    {
        var outcomes = new List<DecryptOutcome>();
        foreach (var name in dbNames)
        {
            progress?.Report($"处理 {name} …");
            var target = workspace.PlainDbPath(name);
            var source = ntDbDir is null ? null : Path.Combine(ntDbDir, name);

            if (!forceRebuild && File.Exists(target))
            {
                outcomes.Add(new DecryptOutcome { Source = name, Decrypted = false });
                continue;
            }

            if (source is null || !File.Exists(source))
            {
                outcomes.Add(new DecryptOutcome { Source = name, Error = "source database not found" });
                continue;
            }

            if (IsPlainSqlite(source))
            {
                NtqqSnapshot.CopyDatabase(source, workspace.DecryptedDir, Path.GetFileName(target));
                outcomes.Add(new DecryptOutcome { Source = name, Decrypted = true, Pages = -1 });
                continue;
            }

            var result = DecryptOne(source, target, accountKey, memoryKeys);
            if (result.Success)
            {
                outcomes.Add(new DecryptOutcome
                {
                    Source = name,
                    Decrypted = true,
                    Config = result.Config,
                    Pages = result.PagesDecrypted,
                    HmacOk = result.PagesHmacOk,
                    HmacBad = result.PagesHmacBad,
                });
            }
            else
            {
                // Keep a previously built plain copy if we have one.
                if (File.Exists(target))
                {
                    outcomes.Add(new DecryptOutcome { Source = name, Decrypted = false,
                        Error = "decrypt failed; reusing existing plain copy: " + result.Error });
                }
                else
                {
                    outcomes.Add(new DecryptOutcome { Source = name, Error = result.Error });
                }
            }
        }
        return outcomes;
    }

    private static SqlCipherDecryptResult DecryptOne(
        string source, string target, string accountKey,
        IReadOnlyDictionary<string, List<string>>? memoryKeys)
    {
        // snapshot into a temp dir inside the workspace, decrypt, then keep the plain file.
        var tempDir = Path.Combine(Path.GetDirectoryName(target)!, ".tmp-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(tempDir);
            var snap = NtqqSnapshot.CopyDatabase(source, tempDir);

            var header = DbHeaderInspector.Inspect(snap.CopiedPath, readSalt: false);
            var configs = ConfigCandidates(header).ToList();

            // Candidate keys: shared account key first, then keys whose in-memory
            // keyspec salt matches THIS database's own salt (per-DB keys).
            var keys = new List<string>();
            if (!string.IsNullOrEmpty(accountKey)) keys.Add(accountKey);
            var salt = new byte[16];
            using (var fs = new FileStream(snap.CopiedPath, FileMode.Open, FileAccess.Read, ReadOnlyFile.PermissiveShare))
            {
                fs.Seek(DbHeaderInspector.NtqqHeaderSize, SeekOrigin.Begin);
                if (fs.Read(salt) == 16 &&
                    memoryKeys is not null &&
                    memoryKeys.TryGetValue(Convert.ToHexString(salt), out var perDb))
                    keys.AddRange(perDb);
            }

            foreach (var key in keys.Distinct())
            foreach (var config in configs)
            {
                var result = SqlCipherDecryptor.DecryptCopy(snap.CopiedPath, target, key, config, null);
                if (result.Success) return result;
            }
            return new SqlCipherDecryptResult { Success = false, Error = "decryption failed with all known parameter sets" };
        }
        catch (Exception ex)
        {
            return new SqlCipherDecryptResult { Success = false, Error = ex.Message };
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
        }
    }

    internal static IEnumerable<SqlCipherConfig> ConfigCandidates(DbHeaderInfo header)
    {
        var hmacFirst = header.HeaderHmacAlgorithm?.ToUpperInvariant() switch
        {
            "HMAC_SHA256" => HmacAlgorithm.HmacSha256,
            _ => HmacAlgorithm.HmacSha1,
        };
        // Verified NTQQ parameter set first (Phase 0 empirical result).
        yield return new SqlCipherConfig { PageSize = 4096, KdfIterations = 4000, KdfUseSha512 = true, Hmac = hmacFirst };
        yield return new SqlCipherConfig { PageSize = 4096, KdfIterations = 4000, KdfUseSha512 = true, Hmac = hmacFirst == HmacAlgorithm.HmacSha1 ? HmacAlgorithm.HmacSha256 : HmacAlgorithm.HmacSha1 };
    }
}
