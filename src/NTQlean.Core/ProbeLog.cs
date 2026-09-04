namespace NTQlean.Core;

public enum ProbeLevel
{
    Info,
    Warning,
    Research,
    Unconfirmed,
    Blocked,
    Error,
}

public static class ProbeLog
{
    public static void Write(ProbeLevel level, string message)
    {
        var tag = level switch
        {
            ProbeLevel.Info => "INFO",
            ProbeLevel.Warning => "WARNING",
            ProbeLevel.Research => "RESEARCH",
            ProbeLevel.Unconfirmed => "UNCONFIRMED",
            ProbeLevel.Blocked => "BLOCKED",
            ProbeLevel.Error => "ERROR",
            _ => "INFO",
        };
        Console.WriteLine($"[{tag}] {message}");
    }

    public static void Info(string m) => Write(ProbeLevel.Info, m);
    public static void Warn(string m) => Write(ProbeLevel.Warning, m);
    public static void Warning(string m) => Write(ProbeLevel.Warning, m);
    public static void Research(string m) => Write(ProbeLevel.Research, m);
    public static void Unconfirmed(string m) => Write(ProbeLevel.Unconfirmed, m);
    public static void Blocked(string m) => Write(ProbeLevel.Blocked, m);
    public static void Error(string m) => Write(ProbeLevel.Error, m);

    /// <summary>
    /// Masks an account id / uin: 1234567890 -> 123****890.
    /// Anything shorter than 6 chars is fully masked.
    /// </summary>
    public static string MaskId(string? id)
    {
        if (string.IsNullOrEmpty(id)) return "(unknown)";
        if (id.Length < 6) return new string('*', id.Length);
        return id[..3] + "****" + id[^3..];
    }

    /// <summary>
    /// Masks a path segment by replacing the account directory name if it matches one
    /// of the supplied ids; keeps the rest of the path intact.
    /// </summary>
    public static string MaskPath(string path, IEnumerable<string> ids)
    {
        var result = path;
        foreach (var id in ids)
            result = result.Replace(id, MaskId(id));
        return result;
    }

    /// <summary>Short hex preview that never dumps more than the requested bytes.</summary>
    public static string HexPreview(ReadOnlySpan<byte> data, int maxBytes = 32)
    {
        var n = Math.Min(data.Length, maxBytes);
        var sb = new System.Text.StringBuilder(n * 3 + 8);
        for (var i = 0; i < n; i++)
            sb.Append(data[i].ToString("x2")).Append(' ');
        if (data.Length > n) sb.Append("...");
        return sb.ToString();
    }
}
