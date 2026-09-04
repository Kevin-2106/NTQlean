using System.Collections.Concurrent;

namespace NTQlean.Core;

/// <summary>
/// Locates NTQQ's own thumbnail files for a media item so previews cost no
/// extra disk work: images have Thumb/&lt;md5&gt;_720.jpg, videos Thumb/&lt;md5&gt;_0.png
/// next to the original's month directory.
/// </summary>
public static class ThumbnailLocator
{
    /// <summary>Candidate thumbnail paths ordered by preference (nt_data-relative).</summary>
    public static IEnumerable<string> Candidates(string relPath, string? thumbRel, string fileName)
    {
        if (!string.IsNullOrEmpty(thumbRel))
            yield return thumbRel.Replace('\\', '/');

        var name = Path.GetFileNameWithoutExtension(fileName is { Length: > 0 } ? fileName : relPath);
        if (name.Length == 0) yield break;

        var dir = relPath.Contains('/') ? relPath[..relPath.LastIndexOf('/')] : "";
        if (dir.Contains('/')) dir = dir[..dir.LastIndexOf('/')]; // ..\Thumb from .../Ori/x.jpg
        if (dir.Length > 0)
        {
            yield return $"{dir}/Thumb/{name}_720.jpg";
            yield return $"{dir}/Thumb/{name}_0.png";
            yield return $"{dir}/Thumb/{Path.GetFileName(fileName ?? relPath)}";
        }
    }
}

/// <summary>
/// Small bounded LRU cache keyed by string, safe for concurrent use.
/// </summary>
public sealed class LruCache<TValue>(int capacity)
{
    private readonly ConcurrentDictionary<string, LinkedListNode<(string key, TValue value)>> _map = new();
    private readonly LinkedList<(string key, TValue value)> _lru = new();
    private readonly object _gate = new();
    private readonly int _capacity = Math.Max(1, capacity);

    public bool TryGet(string key, out TValue value)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                value = node.Value.value;
                return true;
            }
        }
        value = default!;
        return false;
    }

    public void Set(string key, TValue value)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                existing.Value = (key, value);
                _lru.Remove(existing);
                _lru.AddFirst(existing);
                return;
            }
            var node = new LinkedListNode<(string, TValue)>((key, value));
            _lru.AddFirst(node);
            _map[key] = node;
            if (_map.Count > _capacity)
            {
                var last = _lru.Last!;
                _lru.RemoveLast();
                _map.TryRemove(last.Value.key, out _);
            }
        }
    }
}
