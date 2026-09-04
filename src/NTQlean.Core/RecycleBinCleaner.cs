using System.Runtime.InteropServices;
using System.Text.Json;

namespace NTQlean.Core;

public sealed record CleanupResultItem(string Path, bool Success, string Error);

public sealed record CleanupResult(IReadOnlyList<CleanupResultItem> Items, string ManifestPath)
{
    public int SuccessCount => Items.Count(i => i.Success);
    public long FreedBytes { get; init; }
}

/// <summary>
/// Executes a cleanup plan by moving files to the Recycle Bin (recoverable),
/// writing an auditable manifest first. This is the ONLY component in NTQlean
/// that deletes anything, and it is deliberately unreachable from the CLI —
/// the GUI must pass an explicit user confirmation gate before calling it.
/// Phase 2 note: all automated tests stop at CleanupPlan (dry run).
/// </summary>
public static class CleanupExecutor
{
    /// <summary>
    /// Moves the planned files to the Windows Recycle Bin.
    /// Safety rails:
    ///  - requireConfirm must be the literal result of an explicit UI confirmation;
    ///  - only files whose abs_path is under one of allowedRoots are touched;
    ///  - every attempted path is recorded in a manifest next to the plan.
    /// </summary>
    public static CleanupResult Apply(CleanupPlan plan, string manifestPath, bool requireConfirm, params string[] allowedRoots)
    {
        if (!requireConfirm)
            throw new InvalidOperationException("回收站清理需要显式的用户确认标志。");

        var roots = allowedRoots
            .Select(r => Path.GetFullPath(r).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar)
            .ToArray();

        var results = new List<CleanupResultItem>();
        long freed = 0;
        foreach (var item in plan.Items)
        {
            if (string.IsNullOrEmpty(item.AbsPath) || !File.Exists(item.AbsPath))
            {
                results.Add(new CleanupResultItem(item.AbsPath, false, "文件不存在或路径未解析"));
                continue;
            }

            var full = Path.GetFullPath(item.AbsPath);
            var allowed = roots.Any(r => full.StartsWith(r, StringComparison.OrdinalIgnoreCase));
            if (!allowed)
            {
                results.Add(new CleanupResultItem(full, false, "路径不在允许的 nt_data 根内（安全护栏拒绝）"));
                continue;
            }

            var ok = MoveToRecycleBin(full, out var error);
            if (ok) freed += item.SizeBytes;
            results.Add(new CleanupResultItem(full, ok, error ?? ""));
        }

        var manifest = new
        {
            created = DateTime.Now,
            planWhere = plan.WhereSql,
            freed,
            items = plan.Items,
            results,
        };
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest,
            new JsonSerializerOptions { WriteIndented = true }));

        return new CleanupResult(results, manifestPath) { FreedBytes = freed };
    }

    private static bool MoveToRecycleBin(string path, out string? error)
    {
        var op = new SHFILEOPSTRUCTW
        {
            hwnd = IntPtr.Zero,
            wFunc = FO_DELETE,
            pFrom = path + "\0\0",
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
        };
        var rc = SHFileOperationW(ref op);
        error = rc == 0 ? null : $"SHFileOperation 失败 (0x{rc:X})" + (op.fAnyOperationsAborted ? "，操作被中止" : "");
        return rc == 0 && !op.fAnyOperationsAborted;
    }

    private const uint FO_DELETE = 3;
    private const ushort FOF_ALLOWUNDO = 0x40;
    private const ushort FOF_NOCONFIRMATION = 0x10;
    private const ushort FOF_SILENT = 0x4;
    private const ushort FOF_NOERRORUI = 0x400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCTW
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public BOOL fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    // BOOL (int) member inside the struct
    private struct BOOL
    {
        public int Value;
        public static implicit operator bool(BOOL b) => b.Value != 0;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperationW(ref SHFILEOPSTRUCTW lpFileOp);
}
