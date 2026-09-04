using System.IO;
using System.Windows.Media.Imaging;
using NTQlean.Core;

namespace NTQlean.App;

/// <summary>
/// Low-overhead preview: reuses NTQQ's own Thumb files (Thumb/&lt;md5&gt;_720.jpg,
/// Thumb/&lt;md5&gt;_0.png) instead of decoding originals, decodes at thumbnail
/// resolution and caches results in a bounded LRU.
/// </summary>
public sealed class ThumbService
{
    private readonly LruCache<BitmapSource> _cache;

    public ThumbService(int capacity = 256) => _cache = new LruCache<BitmapSource>(capacity);

    public Task<BitmapSource?> GetAsync(SelectionRow row, string? ntDataRoot) => Task.Run(() =>
    {
        // stable cache key
        var key = row.AbsPath is { Length: > 0 } ? row.AbsPath : $"{ntDataRoot}|{row.RelPath}|{row.FileName}";
        if (_cache.TryGet(key, out var cached)) return cached;

        foreach (var path in CandidatePaths(row, ntDataRoot))
        {
            var bmp = TryDecode(path);
            if (bmp is null) continue;
            _cache.Set(key, bmp);
            return bmp;
        }
        return null;
    });

    private static IEnumerable<string> CandidatePaths(SelectionRow row, string? ntRoot)
    {
        var rel = row.RelPath ?? "";
        var name = row.FileName ?? "";
        var ntDataRoot = ntRoot ?? "";

        string Local(string relPath) =>
            Path.Combine(ntDataRoot, relPath.Replace('/', Path.DirectorySeparatorChar));

        foreach (var candidate in ThumbnailLocator.Candidates(rel, row.ThumbRel, name))
        {
            if (Path.IsPathRooted(candidate))
            {
                if (File.Exists(candidate)) yield return candidate;
            }
            else if (ntDataRoot.Length > 0)
            {
                var p = Local(candidate);
                if (File.Exists(p)) yield return p;
            }
        }

        // fall back to the original only for small images (videos would be huge)
        if (row.AbsPath is { Length: > 0 } && File.Exists(row.AbsPath) &&
            row.Kind == "image" &&
            (row.ActualSize ?? row.SizeBytes ?? 0) <= 4 * 1024 * 1024)
            yield return row.AbsPath;
    }

    private static BitmapSource? TryDecode(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.DecodePixelWidth = 240;
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
