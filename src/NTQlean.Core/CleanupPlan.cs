using System.Text.Json;
using System.Text.Json.Serialization;

namespace NTQlean.Core;

public sealed record CleanupPlanItem(
    long ItemId, string Kind, string? ChatId, string? ChatName,
    string FileName, string AbsPath, long SizeBytes, long? MsgTime,
    string Confidence, string Source);

public sealed class CleanupPlan
{
    public DateTime CreatedAt { get; init; } = DateTime.Now;
    public string IndexPath { get; init; } = "";
    public string WhereSql { get; init; } = "";
    public string? Expression { get; init; }
    public List<CleanupPlanItem> Items { get; init; } = new();
    public long TotalBytes { get; init; }
    public long MissingCount { get; init; }
    public Dictionary<string, long> ByKind { get; init; } = new();
    public Dictionary<string, long> ByChat { get; init; } = new();
    public Dictionary<string, long> ByConfidence { get; init; } = new();

    public string ToSummaryText()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"=== 预演报告（DRY RUN，未删除任何文件） ===");
        sb.AppendLine($"时间: {CreatedAt:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"条件: {WhereSql}");
        if (!string.IsNullOrEmpty(Expression)) sb.AppendLine($"表达式: {Expression}");
        sb.AppendLine($"命中: {Items.Count:N0} 项 / {TotalBytes / 1024.0 / 1024 / 1024:F2} GB ({TotalBytes:N0} 字节)");
        sb.AppendLine($"（其中文件不存在的条目 {MissingCount:N0} 项，不会包含在实际清理中）");
        sb.AppendLine();
        sb.AppendLine("按类型:");
        foreach (var (k, v) in ByKind.OrderByDescending(kv => kv.Value)) sb.AppendLine($"  {k,-8} {v / 1024.0 / 1024:F1} MB");
        sb.AppendLine("按引用可信度:");
        foreach (var (k, v) in ByConfidence.OrderByDescending(kv => kv.Value))
            sb.AppendLine($"  {ConfidenceLabels.Of(k),-8} {v / 1024.0 / 1024:F1} MB");
        sb.AppendLine($"会话数: {ByChat.Count}");
        return sb.ToString();
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);
    public static CleanupPlan? FromJson(string json) => JsonSerializer.Deserialize<CleanupPlan>(json, JsonOpts);

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, ToJson());
    }

    public static CleanupPlan FromSelection(SelectionResult selection, string indexPath, string? expression)
    {
        var items = selection.Rows
            .Where(r => !string.IsNullOrEmpty(r.AbsPath))
            .Select(r => new CleanupPlanItem(
                r.ItemId, r.Kind, r.ChatId, r.ChatName, r.FileName,
                r.AbsPath, r.ActualSize ?? r.SizeBytes ?? 0, r.MsgTime, r.Confidence, r.Source))
            .ToList();

        return new CleanupPlan
        {
            IndexPath = indexPath,
            WhereSql = selection.WhereSql,
            Expression = expression,
            Items = items,
            TotalBytes = items.Sum(i => i.SizeBytes),
            MissingCount = selection.Rows.Count - items.Count,
            ByKind = selection.ByKind,
            ByChat = selection.ByChat,
            ByConfidence = selection.ByConfidence,
        };
    }
}
