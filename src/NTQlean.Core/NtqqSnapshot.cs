namespace NTQlean.Core;

public sealed class SnapshotResult
{
    public string SourcePath { get; init; } = "";
    public string CopiedPath { get; init; } = "";
    public List<string> CopiedSidecars { get; init; } = new();
    public List<string> SkippedSidecars { get; init; } = new();
}

/// <summary>
/// Copies database files into the NTQlean workspace. All reads on the original are
/// opened with FileShare.Read; nothing is ever written to QQ-owned paths.
/// </summary>
public static class NtqqSnapshot
{
    /// <summary>
    /// Copies one .db plus its -wal / -shm / *.material sidecars into targetDir.
    /// A stale -wal copied while QQ runs can make the copy inconsistent; callers
    /// should recommend closing QQ first (see research notes).
    /// </summary>
    public static SnapshotResult CopyDatabase(string dbPath, string targetDir, string? targetName = null)
    {
        Directory.CreateDirectory(targetDir);
        var name = targetName ?? Path.GetFileName(dbPath);
        var dest = Path.Combine(targetDir, name);

        try
        {
            CopyWithReadShare(dbPath, dest);
        }
        catch (IOException)
        {
            throw new IOException(
                $"source file is locked by another process (likely the running QQ client): {dbPath}. " +
                "Fully exit QQ and retry.");
        }

        var result = new SnapshotResult { SourcePath = dbPath, CopiedPath = dest };
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            var sidecar = dbPath + suffix;
            if (!File.Exists(sidecar)) continue;
            var sidecarDest = dest + suffix;
            try
            {
                CopyWithReadShare(sidecar, sidecarDest);
                result.CopiedSidecars.Add(suffix);
            }
            catch (Exception)
            {
                result.SkippedSidecars.Add(suffix);
            }
        }

        // NTQQ keeps additional .material sidecars for some databases.
        var prefix = Path.GetFileNameWithoutExtension(dbPath);
        var dir = Path.GetDirectoryName(dbPath)!;
        foreach (var material in Directory.EnumerateFiles(dir, prefix + "*.material"))
        {
            var suffix = Path.GetFileName(material)[prefix.Length..]; // e.g. "-first.material"
            try
            {
                CopyWithReadShare(material, Path.Combine(targetDir, name + suffix));
                result.CopiedSidecars.Add(suffix);
            }
            catch (Exception)
            {
                result.SkippedSidecars.Add(suffix);
            }
        }

        return result;
    }

    private static void CopyWithReadShare(string src, string dst)
    {
        const int BufferSize = 4 * 1024 * 1024;
        using var input = new FileStream(src, FileMode.Open, FileAccess.Read, ReadOnlyFile.PermissiveShare, BufferSize);
        using var output = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize);
        input.CopyTo(output, BufferSize);
    }
}
