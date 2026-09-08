using System.IO;
using System.Windows.Media.Imaging;
using NTQlean.Core;

namespace NTQlean.App;

/// <summary>
/// Preview loader for the single selected row: decodes images from the
/// original file at display resolution (NTQQ thumbs are only 240px-class),
/// falls back to NTQQ's own Thumb files, and pulls video frames from the
/// file itself via the OS decoder. Results cached in a small LRU — full
/// resolution bitmaps are memory-heavy, so the capacity is low.
/// </summary>
public sealed class ThumbService
{
    private const int DecodeWidth = 1024;
    private readonly LruCache<BitmapSource> _cache;

    public ThumbService(int capacity = 16) => _cache = new LruCache<BitmapSource>(capacity);

    public async Task<BitmapSource?> GetAsync(SelectionRow row, string? ntDataRoot)
    {
        // stable cache key
        var key = row.AbsPath is { Length: > 0 } ? row.AbsPath : $"{ntDataRoot}|{row.RelPath}|{row.FileName}";
        if (_cache.TryGet(key, out var cached)) return cached;

        BitmapSource? bmp = null;
        foreach (var path in CandidatePaths(row, ntDataRoot))
        {
            bmp = TryDecode(path);
            if (bmp is not null) break;
        }

        // Videos stored outside nt_data (filerecv / save-as) have no NTQQ
        // cover file — grab a frame with the OS decoder instead.
        if (bmp is null && row.Kind == "video" &&
            row.AbsPath is { Length: > 0 } && File.Exists(row.AbsPath))
            bmp = await VideoFrameGrabber.GrabAsync(row.AbsPath);

        if (bmp is not null) _cache.Set(key, bmp);
        return bmp;
    }

    private static IEnumerable<string> CandidatePaths(SelectionRow row, string? ntRoot)
    {
        var rel = row.RelPath ?? "";
        var name = row.FileName ?? "";
        var ntDataRoot = ntRoot ?? "";

        // Images: the original decodes to display resolution — thumbs are too
        // blurry for a large pane and there is no reason to cap by file size
        // (DecodePixelWidth bounds memory regardless of the source).
        if (row.Kind == "image" &&
            row.AbsPath is { Length: > 0 } && File.Exists(row.AbsPath))
            yield return row.AbsPath;

        foreach (var candidate in ThumbnailLocator.Candidates(rel, row.ThumbRel, name))
        {
            if (Path.IsPathRooted(candidate))
            {
                if (File.Exists(candidate)) yield return candidate;
            }
            else if (ntDataRoot.Length > 0)
            {
                var p = Path.Combine(ntDataRoot, candidate.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(p)) yield return p;
            }
        }
    }

    private static BitmapSource? TryDecode(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.DecodePixelWidth = DecodeWidth;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = fs;
            bmp.EndInit();
            bmp.Freeze(); // cross-thread safety
            return bmp;
        }
        catch
        {
            return null; // not an image, locked, or decode failure
        }
    }
}
