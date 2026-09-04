using System.IO;

namespace NTQlean.Core;

public sealed class NtqqAccount
{
    public string Uin { get; init; } = "";
    public string AccountRoot { get; init; } = "";  // ...\Tencent Files\<uin>
    public string? NtDir { get; init; }             // ...\nt_qq\<uin>\nt_qq or similar container
    public string? NtDbDir { get; init; }           // nt_db directory holding *.db
    public string? NtDataDir { get; init; }         // nt_data directory holding media
    public long DbTotalBytes { get; init; }
}

public sealed class NtqqDataRoot
{
    public string Root { get; init; } = "";         // e.g. ...\Documents\Tencent Files
    public string Source { get; init; } = "";       // why we think this is a data root
    public List<NtqqAccount> Accounts { get; init; } = new();
    public string? GlobalNtDb { get; init; }        // global nt_qq/global/nt_db if present
}

/// <summary>
/// Read-only discovery of NTQQ data locations. Only well-known candidate paths are
/// probed; never a filesystem-wide search.
/// </summary>
public static class NtqqDiscovery
{
    public static IReadOnlyList<NtqqDataRoot> Discover()
    {
        var roots = new List<NtqqDataRoot>();

        foreach (var docs in CandidateDocumentsDirs())
        {
            var tf = Path.Combine(docs, "Tencent Files");
            AddRootOrNested(roots, tf, $"Known location: Documents ({docs})");
        }

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
            var tf = Path.Combine(drive.Name, "Tencent Files");
            AddRootOrNested(roots, tf, $"Drive-root convention: {drive.Name}Tencent Files");
        }

        return roots;
    }

    /// <summary>
    /// Some installs configure a custom data root such as
    /// D:\Tencent Files\QQ\Tencent Files (nested). Accept either the given path as
    /// root or the one-level nesting below it.
    /// </summary>
    private static void AddRootOrNested(List<NtqqDataRoot> roots, string tf, string source)
    {
        if (!Directory.Exists(tf)) return;
        if (roots.Any(r => PathEquals(r.Root, tf))) return;

        var nested = Path.Combine(tf, "QQ", "Tencent Files");
        if (LooksLikeDataRoot(nested))
        {
            if (!roots.Any(r => PathEquals(r.Root, nested)))
                roots.Add(BuildRoot(nested, source + " (nested: <root>\\QQ\\Tencent Files)"));
            return;
        }

        if (LooksLikeDataRoot(tf))
            roots.Add(BuildRoot(tf, source));
    }

    private static bool LooksLikeDataRoot(string dir)
    {
        try
        {
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                var name = Path.GetFileName(sub);
                if (name.Equals("nt_qq", StringComparison.OrdinalIgnoreCase)) return true;
                if (name.Length is >= 5 and <= 12 && name.All(char.IsAsciiDigit)
                    && (Directory.Exists(Path.Combine(sub, "nt_qq")) || Directory.Exists(Path.Combine(sub, "nt_db"))))
                    return true;
            }
        }
        catch (IOException)
        {
            return false;
        }
        return false;
    }

    private static IEnumerable<string> CandidateDocumentsDirs()
    {
        // .NET reads the redirected Documents location from the user registry
        // (covers OneDrive redirection without spawning a shell).
        var myDocs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (!string.IsNullOrEmpty(myDocs)) yield return myDocs;

        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents");
    }

    private static NtqqDataRoot BuildRoot(string root, string source)
    {
        var accounts = new List<NtqqAccount>();
        string? globalNtDb = null;

        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(dir);

            // Global folder layout: Tencent Files\nt_qq\global\nt_db
            if (name.Equals("nt_qq", StringComparison.OrdinalIgnoreCase))
            {
                var globalDb = Path.Combine(dir, "global", "nt_db");
                if (Directory.Exists(globalDb)) globalNtDb = globalDb;
                continue;
            }

            if (!IsLikelyUin(name)) continue;

            var ntDb = FindFirstExistingDir(dir, "nt_qq/nt_db", "nt_db");
            var ntData = FindFirstExistingDir(dir, "nt_qq/nt_data", "nt_data");
            if (ntDb is null && ntData is null) continue;

            long total = 0;
            if (ntDb is not null)
                foreach (var f in Directory.EnumerateFiles(ntDb, "*.db"))
                    total += new FileInfo(f).Length;

            accounts.Add(new NtqqAccount
            {
                Uin = name,
                AccountRoot = dir,
                NtDbDir = ntDb,
                NtDataDir = ntData,
                DbTotalBytes = total,
            });
        }

        accounts.Sort((a, b) => b.DbTotalBytes.CompareTo(a.DbTotalBytes));
        return new NtqqDataRoot { Root = root, Source = source, Accounts = accounts, GlobalNtDb = globalNtDb };
    }

    private static string? FindFirstExistingDir(string baseDir, params string[] relative)
    {
        foreach (var rel in relative)
        {
            var candidate = Path.Combine(baseDir, rel.Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static bool IsLikelyUin(string name) =>
        name.Length is >= 5 and <= 12 && name.All(char.IsAsciiDigit);

    private static bool PathEquals(string a, string b) =>
        string.Equals(
            Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>Finds the NTQQ installation directory (Program Files probing only).</summary>
    public static string? FindInstallDir()
    {
        string[] candidates =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tencent", "QQNT"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Tencent", "QQNT"),
        };
        return candidates.FirstOrDefault(Directory.Exists);
    }
}
