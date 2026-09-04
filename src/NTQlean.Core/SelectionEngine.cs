using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace NTQlean.Core;

public sealed record SelectionOptions
{
    /// <summary>Confidence levels to include. Default: exact + strong (deletion-safe).</summary>
    public HashSet<string> Confidences { get; init; } = new(StringComparer.OrdinalIgnoreCase) { "exact", "strong" };

    /// <summary>Kinds to include; empty = all.</summary>
    public HashSet<string> Kinds { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Chats to include (null = all); chats listed in ExcludedChats are removed.</summary>
    public HashSet<string>? IncludedChats { get; init; }
    public HashSet<string> ExcludedChats { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Unix seconds bounds (inclusive); null = unbounded.</summary>
    public long? TimeFrom { get; init; }
    public long? TimeTo { get; init; }

    /// <summary>Size bounds in bytes (inclusive).</summary>
    public long? SizeMin { get; init; }
    public long? SizeMax { get; init; }

    /// <summary>
    /// Advanced boolean expression over time/size/chat/kind/conf/name/path
    /// supporting AND OR NOT and parentheses. When set, supersedes the
    /// time/size fields above (kind/chat/confidence sets still apply).
    /// Example: size >= 10MB AND time < 2025-01-01 AND NOT chat == '123456'
    /// </summary>
    public string? Expression { get; init; }

    /// <summary>
    /// Include unreferenced (orphan) nt_data files. These have confidence
    /// "orphan" and no chat; they are only ever cleaned up with explicit
    /// user opt-in.
    /// </summary>
    public bool IncludeOrphans { get; init; }

    public bool IsValidForDeletion => Confidences.All(c =>
        c.Equals("exact", StringComparison.OrdinalIgnoreCase) ||
        c.Equals("strong", StringComparison.OrdinalIgnoreCase));
}

public sealed record SelectionRow(
    long ItemId, string Kind, string ChatId, string? ChatName, long? ChatType,
    string FileName, string RelPath, string AbsPath, string ThumbRel,
    long? SizeDb, long? SizeBytes, long? ActualSize,
    long? MsgTime, string TimeSource, string? Md5, string? Uuid,
    string Confidence, string Source, string SourceTable, long? MsgId);

public sealed class SelectionResult
{
    public List<SelectionRow> Rows { get; init; } = new();
    public long TotalBytes { get; init; }
    public long ResolvableCount { get; init; }
    public long ResolvableBytes { get; init; }
    public Dictionary<string, long> ByKind { get; init; } = new();
    public Dictionary<string, long> ByChat { get; init; } = new();
    public Dictionary<string, long> ByConfidence { get; init; } = new();
    public string WhereSql { get; init; } = "";
}

/// <summary>
/// Executes selection criteria against the workspace index and produces dry-run
/// reports. This class never deletes anything.
/// </summary>
public static class SelectionEngine
{
    public static SelectionResult Query(string indexPath, SelectionOptions options)
    {
        var where = BuildWhere(options);
        using var conn = NtqSqlite.OpenReadOnly(indexPath);
        conn.Open();

        var rows = new List<SelectionRow>();
        var byKind = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var byChat = new Dictionary<string, long>(StringComparer.Ordinal);
        var byConf = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        long total = 0, resolvable = 0, resolvableBytes = 0;

        void Accumulate(SelectionRow row)
        {
            var size = row.ActualSize ?? row.SizeBytes ?? 0;
            total += size;
            if (!string.IsNullOrEmpty(row.AbsPath))
            {
                resolvable++;
                resolvableBytes += size;
            }
            byKind[row.Kind] = byKind.GetValueOrDefault(row.Kind) + size;
            var chatLabel = row.ChatName is { Length: > 0 } ? $"{row.ChatId} ({row.ChatName})" : (row.ChatId is { Length: > 0 } ? row.ChatId : "(无会话)");
            byChat[chatLabel] = byChat.GetValueOrDefault(chatLabel) + size;
            byConf[row.Confidence] = byConf.GetValueOrDefault(row.Confidence) + size;
            rows.Add(row);
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"""
                SELECT m.item_id, m.kind, m.chat_id, c.display_name, m.chat_type,
                       m.file_name, m.rel_path, m.abs_path, m.thumb_rel,
                       m.size_db, m.size_bytes, m.actual_size,
                       m.msg_time, m.time_source, m.md5, m.uuid,
                       m.confidence, m.source, m.source_table, m.msg_id
                FROM media m LEFT JOIN chats c ON c.chat_id = m.chat_id
                WHERE {where}
                ORDER BY COALESCE(m.actual_size, m.size_bytes, 0) DESC
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var row = new SelectionRow(
                    r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? "" : r.GetString(2),
                    r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetInt64(4),
                    r.GetString(5), r.GetString(6), r.GetString(7), r.GetString(8),
                    r.IsDBNull(9) ? null : r.GetInt64(9), r.IsDBNull(10) ? null : r.GetInt64(10),
                    r.IsDBNull(11) ? null : r.GetInt64(11), r.IsDBNull(12) ? null : r.GetInt64(12),
                    r.GetString(13), r.IsDBNull(14) ? null : r.GetString(14),
                    r.IsDBNull(15) ? null : r.GetString(15), r.GetString(16), r.GetString(17),
                    r.GetString(18), r.IsDBNull(19) ? null : r.GetInt64(19));
                Accumulate(row);
            }
        }

        // Orphan (unreferenced) files, when explicitly included. They carry
        // confidence "orphan" and no chat; the executor's allowlist still applies.
        if (options.IncludeOrphans)
        {
            var orphanWhere = new List<string>();
            if (options.Kinds.Count > 0)
                orphanWhere.Add($"domain IN ({QuoteList(options.Kinds.Select(KindToDomain))})");
            if (options.TimeFrom is { } of) orphanWhere.Add($"mtime >= {of}");
            if (options.TimeTo is { } ot) orphanWhere.Add($"mtime <= {ot}");
            if (options.SizeMin is { } omin) orphanWhere.Add($"size >= {omin}");
            if (options.SizeMax is { } omax) orphanWhere.Add($"size <= {omax}");

            var ntRoot = GetMeta(conn, "nt_data_root") ?? "";
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT rel_path, name, size, mtime, domain FROM orphan_nt_files " +
                (orphanWhere.Count > 0 ? "WHERE " + string.Join(" AND ", orphanWhere) : "") +
                " ORDER BY size DESC";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var rel = r.GetString(0);
                var name = r.GetString(1);
                var size = r.GetInt64(2);
                var mtime = r.GetInt64(3);
                var domain = r.GetString(4);
                var kind = DomainToKind(domain);
                var abs = string.IsNullOrEmpty(ntRoot) ? "" : Path.Combine(ntRoot, rel.Replace('/', Path.DirectorySeparatorChar));
                var row = new SelectionRow(
                    -1L - rows.Count, kind, "", null, null,
                    name, rel, File.Exists(abs) ? abs : "", "",
                    size, size, File.Exists(abs) ? size : null,
                    mtime, "file", null, null,
                    "orphan", "orphan", "orphan_nt_files", null);
                Accumulate(row);
            }
        }

        return new SelectionResult
        {
            Rows = rows,
            TotalBytes = total,
            ResolvableCount = resolvable,
            ResolvableBytes = resolvableBytes,
            ByKind = byKind,
            ByChat = byChat,
            ByConfidence = byConf,
            WhereSql = where,
        };
    }

    private static string? GetMeta(SqliteConnection conn, string key)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT value FROM meta WHERE key = $k";
            cmd.Parameters.AddWithValue("$k", key);
            return cmd.ExecuteScalar() as string;
        }
        catch
        {
            return null;
        }
    }

    internal static string DomainToKind(string domain) => domain.ToUpperInvariant() switch
    {
        "PIC" => "image",
        "VIDEO" => "video",
        "FILE" => "file",
        "PTT" => "ptt",
        _ => "other",
    };

    internal static string KindToDomain(string kind) => kind.ToLowerInvariant() switch
    {
        "image" => "Pic",
        "video" => "Video",
        "file" => "File",
        "ptt" => "Ptt",
        _ => "",
    };

    public sealed record ChatInfo(string ChatId, string? DisplayName, long Items, long Bytes);

    /// <summary>Chat list with media footprint for the GUI picker.</summary>
    public static List<ChatInfo> QueryChats(string indexPath)
    {
        var list = new List<ChatInfo>();
        using var conn = NtqSqlite.OpenReadOnly(indexPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT c.chat_id, c.display_name, COUNT(m.item_id),
                   COALESCE(SUM(COALESCE(m.actual_size, m.size_bytes, 0)), 0)
            FROM chats c LEFT JOIN media m ON m.chat_id = c.chat_id
            GROUP BY c.chat_id, c.display_name
            ORDER BY 4 DESC
            """;
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new ChatInfo(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetInt64(2), r.GetInt64(3)));
        return list;
    }

    public static string BuildWhere(SelectionOptions o)
    {
        const string Time = "COALESCE(m.msg_time, 0)";
        const string Size = "COALESCE(m.actual_size, m.size_bytes, 0)";
        const string Chat = "COALESCE(m.chat_id, '')";

        var parts = new List<string>();

        if (o.Kinds.Count > 0)
            parts.Add($"m.kind IN ({QuoteList(o.Kinds)})");
        if (o.Confidences.Count > 0)
            parts.Add($"m.confidence IN ({QuoteList(o.Confidences)})");

        if (o.IncludedChats is { } include)
        {
            if (include.Count == 0) parts.Add("1 = 0"); // chat filter active, nothing selected
            else parts.Add($"{Chat} IN ({QuoteList(include)})");
        }
        if (o.ExcludedChats.Count > 0)
            parts.Add($"{Chat} NOT IN ({QuoteList(o.ExcludedChats)})");

        if (o.TimeFrom is { } from) parts.Add($"{Time} >= {from}");
        if (o.TimeTo is { } to) parts.Add($"{Time} <= {to}");
        if (o.SizeMin is { } min) parts.Add($"{Size} >= {min}");
        if (o.SizeMax is { } max) parts.Add($"{Size} <= {max}");

        if (!string.IsNullOrWhiteSpace(o.Expression))
            parts.Add("(" + SelectionExpression.ToSql(o.Expression) + ")");

        return parts.Count == 0 ? "1 = 1" : string.Join(" AND ", parts);
    }

    private static string QuoteList(IEnumerable<string> values) =>
        string.Join(", ", values.Select(v => "'" + v.Replace("'", "''") + "'"));
}

/// <summary>
/// Compiles a small boolean selection expression into SQL.
/// Grammar:  expr  := andExpr (OR andExpr)*
///           andExpr := notExpr (AND notExpr)*
///           notExpr := NOT notExpr | primary
///           primary := '(' expr ')' | ident op value
/// idents: time size chat kind conf name path
/// values: number [KB|MB|GB], yyyy-MM-dd date, 'quoted string', bareword
/// </summary>
public static class SelectionExpression
{
    private static readonly Dictionary<string, string> Idents = new(StringComparer.OrdinalIgnoreCase)
    {
        ["time"] = "COALESCE(m.msg_time, 0)",
        ["size"] = "COALESCE(m.actual_size, m.size_bytes, 0)",
        ["chat"] = "COALESCE(m.chat_id, '')",
        ["kind"] = "m.kind",
        ["conf"] = "m.confidence",
        ["confidence"] = "m.confidence",
        ["name"] = "m.file_name",
        ["path"] = "m.rel_path",
    };

    public static string ToSql(string expression) =>
        new Parser(expression).Parse();

    private sealed class Parser
    {
        private static readonly System.Text.RegularExpressions.Regex DateRegex =
            new(@"^\d{4}-\d{2}-\d{2}$", System.Text.RegularExpressions.RegexOptions.Compiled);

        private readonly List<(string text, string type)> _tokens;
        private int _pos;

        public Parser(string input)
        {
            _tokens = Tokenize(input);
        }

        private (string text, string type)? Peek() =>
            _pos < _tokens.Count ? _tokens[_pos] : null;

        private (string text, string type)? Next() =>
            _pos < _tokens.Count ? _tokens[_pos++] : null;

        public string Parse()
        {
            if (_tokens.Count == 0) throw new SyntaxException("表达式为空");
            var sql = ParseOr();
            if (_pos != _tokens.Count)
                throw new SyntaxException($"多余的输入: '{_tokens[_pos].text}'");
            return sql;
        }

        private string ParseOr()
        {
            var parts = new List<string> { ParseAnd() };
            while (Peek() is { type: "kw", text: "OR" })
            {
                _pos++;
                parts.Add(ParseAnd());
            }
            return parts.Count == 1 ? parts[0] : "(" + string.Join(" OR ", parts) + ")";
        }

        private string ParseAnd()
        {
            var parts = new List<string> { ParseNot() };
            while (Peek() is { type: "kw", text: "AND" })
            {
                _pos++;
                parts.Add(ParseNot());
            }
            return parts.Count == 1 ? parts[0] : "(" + string.Join(" AND ", parts) + ")";
        }

        private string ParseNot()
        {
            if (Peek() is { type: "kw", text: "NOT" })
            {
                _pos++;
                return "NOT " + ParseNot();
            }
            return ParsePrimary();
        }

        private string ParsePrimary()
        {
            if (Peek() is { type: "punct", text: "(" })
            {
                _pos++;
                var inner = ParseOr();
                if (Peek() is not { type: "punct", text: ")" })
                    throw new SyntaxException("缺少右括号");
                _pos++;
                return "(" + inner + ")";
            }

            var identTok = Peek() ?? throw new SyntaxException("表达式意外结束");
            if (identTok.type != "ident")
                throw new SyntaxException($"非法标识符: '{identTok.text}'");
            _pos++;
            if (!Idents.TryGetValue(identTok.text, out var column))
                throw new SyntaxException($"未知字段: '{identTok.text}'（可用: {string.Join(", ", Idents.Keys)}）");

            var opTok = Next() ?? throw new SyntaxException("缺少比较运算符");
            var sqlOp = opTok.text switch
            {
                ">" => ">", "<" => "<", ">=" => ">=", "<=" => "<=",
                "=" or "==" => "=", "!=" or "<>" => "<>",
                _ => throw new SyntaxException($"非法运算符: '{opTok.text}'"),
            };

            var valTok = Next() ?? throw new SyntaxException("缺少比较值");
            return $"{column} {sqlOp} {SqlValue(identTok.text, valTok, sqlOp)}";
        }

        private static string SqlValue(string ident, (string text, string type) token, string op)
        {
            if (ident.Equals("time", StringComparison.OrdinalIgnoreCase))
            {
                var seconds = token.type == "date"
                    ? ParseDate(token.text)
                    : long.TryParse(token.text, out var s) ? s
                    : throw new SyntaxException($"时间值应为 yyyy-MM-dd 或 Unix 秒: '{token.text}'");
                return seconds.ToString(CultureInfo.InvariantCulture);
            }

            if (ident.Equals("size", StringComparison.OrdinalIgnoreCase))
            {
                var m = System.Text.RegularExpressions.Regex.Match(token.text, @"^(\d+(?:\.\d+)?)\s*(KB|MB|GB|B|)$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (!m.Success) throw new SyntaxException($"非法大小值: '{token.text}'");
                var number = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                var mult = m.Groups[2].Value.ToUpperInvariant() switch
                {
                    "KB" => 1024L, "MB" => 1024L * 1024, "GB" => 1024L * 1024 * 1024, _ => 1L,
                };
                return ((long)(number * mult)).ToString(CultureInfo.InvariantCulture);
            }

            // string-typed idents
            return "'" + token.text.Replace("'", "''") + "'";
        }

        private static long ParseDate(string text)
        {
            if (!DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var date))
                throw new SyntaxException($"日期格式应为 yyyy-MM-dd: '{text}'");
            return new DateTimeOffset(date, TimeZoneInfo.Local.GetUtcOffset(date)).ToUnixTimeSeconds();
        }

        private static List<(string text, string type)> Tokenize(string input)
        {
            var tokens = new List<(string, string)>();
            int i = 0;
            while (i < input.Length)
            {
                var ch = input[i];
                if (char.IsWhiteSpace(ch)) { i++; continue; }

                if (ch == '\'' || ch == '"')
                {
                    var end = input.IndexOf(ch, i + 1);
                    if (end < 0) throw new SyntaxException("引号未闭合");
                    tokens.Add((input[(i + 1)..end], "str"));
                    i = end + 1;
                    continue;
                }

                if (ch == '(' || ch == ')') { tokens.Add((ch.ToString(), "punct")); i++; continue; }

                if (ch is '<' or '>' or '!' or '=')
                {
                    if (i + 1 < input.Length && (input[i + 1] == '=' || (ch != '=' && input[i + 1] == ch)))
                    {
                        tokens.Add((input.Substring(i, 2), "op")); i += 2;
                    }
                    else if (ch == '=' || ch == '<' || ch == '>')
                    {
                        tokens.Add((ch.ToString(), "op")); i++;
                    }
                    else throw new SyntaxException($"非法字符: '{ch}'");
                    continue;
                }

                if (char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' || ch == '.')
                {
                    var start = i;
                    while (i < input.Length && (char.IsLetterOrDigit(input[i]) || input[i] is '-' or '_' or '.'))
                        i++;
                    var word = input[start..i];
                    var type = word.ToUpperInvariant() is "AND" or "OR" or "NOT" ? "kw"
                             : DateRegex.IsMatch(word) ? "date"
                             : char.IsLetter(word[0]) ? "ident" : "num";
                    tokens.Add((word, type));
                    continue;
                }

                throw new SyntaxException($"非法字符: '{ch}'");
            }
            return tokens;
        }
    }

    public sealed class SyntaxException(string message) : Exception(message)
    {
    }
}
