namespace NTQlean.Core;

/// <summary>
/// User-facing names for the confidence ladder. The index, CLI values and JSON
/// reports keep the English tokens (exact/strong/heuristic/orphan/missing) for
/// compatibility; every UI surface translates through this mapping.
/// Ladder by decreasing evidence: 记录吻合 &gt; 路径吻合 &gt; 仅同名 &gt; 未找到;
/// 无主文件 is not a media-record verdict but the per-file "no valid claim" set.
/// </summary>
public static class ConfidenceLabels
{
    public static string Of(string? confidence) => confidence?.ToLowerInvariant() switch
    {
        "exact" => "记录吻合",
        "strong" => "路径吻合",
        "heuristic" => "仅同名",
        "orphan" => "无主文件",
        "missing" => "未找到",
        _ => confidence ?? "",
    };
}
