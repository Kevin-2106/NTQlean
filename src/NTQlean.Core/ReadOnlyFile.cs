namespace NTQlean.Core;

/// <summary>
/// Opens QQ-owned files read-only, offering the most permissive sharing possible so
/// a running QQ process can keep its own handles. If QQ holds the file exclusively
/// the open fails and callers should ask the user to close QQ.
/// </summary>
public static class ReadOnlyFile
{
    public const FileShare PermissiveShare = FileShare.ReadWrite | FileShare.Delete;

    public static FileStream Open(string path, long? seekTo = null, int bufferSize = 1 << 20)
    {
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, PermissiveShare, bufferSize);
        if (seekTo is > 0) fs.Seek(seekTo.Value, SeekOrigin.Begin);
        return fs;
    }

    public static byte[] ReadBytes(string path, long offset, int count)
    {
        using var fs = Open(path, offset, count);
        var buf = new byte[count];
        var got = 0;
        while (got < count)
        {
            var n = fs.Read(buf, got, count - got);
            if (n <= 0) break;
            got += n;
        }
        return buf;
    }
}
