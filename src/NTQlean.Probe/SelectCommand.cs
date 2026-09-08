using NTQlean.Core;

internal static class SelectCommand
{
    public static int Run(string[] args)
    {
        string? workspaceDir = null, expr = null, jsonOut = null;
        var kinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var includeChats = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var excludeChats = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var confs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "exact", "strong" };
        long? timeFrom = null, timeTo = null, sizeMin = null, sizeMax = null;
        var chatFilterActive = false;
        var includeOrphans = false;
        var includeRefOrphans = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--workspace": workspaceDir = args[++i]; break;
                case "--expr": expr = args[++i]; break;
                case "--kind": kinds.UnionWith(args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries)); break;
                case "--chat": includeChats.UnionWith(args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries)); chatFilterActive = true; break;
                case "--exclude-chat": excludeChats.UnionWith(args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries)); break;
                case "--conf": confs.Clear(); confs.UnionWith(args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries)); break;
                case "--from": timeFrom = ParseDate(args[++i]); break;
                case "--to": timeTo = ParseDate(args[++i]) + 86399; break;
                case "--size-min": sizeMin = ParseSize(args[++i]); break;
                case "--size-max": sizeMax = ParseSize(args[++i]); break;
                case "--include-orphans": includeOrphans = true; break;
                case "--include-referenced-orphans": includeRefOrphans = true; break;
                case "--json": jsonOut = args[++i]; break;
            }
        }

        if (workspaceDir is null || !File.Exists(Path.Combine(workspaceDir, "index.db")))
        {
            ProbeLog.Error("usage: select --workspace <dir> [--expr \"...\"] [--kind image,video] " +
                           "[--chat id1,id2] [--exclude-chat id] [--from 2025-01-01] [--to 2025-12-31] " +
                           "[--size-min 10MB] [--size-max 1GB] [--conf exact,strong,heuristic] [--json out.json]");
            return 1;
        }

        var indexPath = Path.Combine(workspaceDir, "index.db");
        var options = new SelectionOptions
        {
            Kinds = kinds,
            IncludedChats = chatFilterActive ? includeChats : null,
            ExcludedChats = excludeChats,
            Confidences = confs,
            TimeFrom = timeFrom,
            TimeTo = timeTo,
            SizeMin = sizeMin,
            SizeMax = sizeMax,
            Expression = expr,
            IncludeOrphans = includeOrphans,
            IncludeReferencedOrphans = includeRefOrphans,
        };

        var selection = SelectionEngine.Query(indexPath, options);
        var plan = CleanupPlan.FromSelection(selection, indexPath, expr);

        if (includeOrphans && timeTo is null)
            ProbeLog.Info($"孤儿时间上限默认为一周前（{SelectionEngine.DefaultOrphanCutoff():yyyy-MM-dd}）：索引未合并 WAL，" +
                          "最近收到的文件可能被误判为孤儿；用 --to 可显式覆盖。");

        if (includeOrphans && !SelectionEngine.IsNtMsgScanned(indexPath))
            ProbeLog.Unconfirmed("索引未含 nt_msg 引用扫描（analyze --include-nt-msg 后重建）：无法确认孤儿是否仍被聊天记录引用，" +
                                 "结果可能包含聊天中仍可见的文件。");

        Console.WriteLine(plan.ToSummaryText());

        var reportPath = Path.Combine(workspaceDir, "reports",
            $"dryrun-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        plan.Save(reportPath);
        ProbeLog.Info($"预演报告已保存: {reportPath}");
        if (jsonOut is not null)
        {
            plan.Save(jsonOut);
            ProbeLog.Info($"报告副本: {jsonOut}");
        }

        // Largest items preview.
        Console.WriteLine("最大 10 项:");
        foreach (var item in plan.Items.OrderByDescending(x => x.SizeBytes).Take(10))
            Console.WriteLine($"  {item.SizeBytes / 1024.0 / 1024,10:F2} MB  [{item.Kind}/{item.Confidence}] " +
                              $"{Truncate(item.FileName)}  {Mask(item.ChatId, item.ChatName)}");

        Console.WriteLine();
        Console.WriteLine("注意: 这是预演（dry-run）。NTQlean 没有删除任何文件。");
        return 0;
    }

    private static long ParseDate(string text)
    {
        var date = DateTime.ParseExact(text, "yyyy-MM-dd", null);
        return new DateTimeOffset(date, TimeZoneInfo.Local.GetUtcOffset(date)).ToUnixTimeSeconds();
    }

    private static long ParseSize(string text)
    {
        var m = System.Text.RegularExpressions.Regex.Match(text, @"^(\d+(?:\.\d+)?)\s*(KB|MB|GB|B)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success) throw new FormatException($"非法大小: {text}");
        var v = double.Parse(m.Groups[1].Value);
        return (long)(v * (m.Groups[2].Value.ToUpperInvariant() switch
        {
            "KB" => 1024, "MB" => 1024 * 1024, "GB" => 1024L * 1024 * 1024, _ => 1,
        }));
    }

    private static string Truncate(string s) => s.Length <= 48 ? s : s[..45] + "…";
    private static string Mask(string? chat, string? name)
    {
        var label = chat is { Length: > 0 } ? chat : "-";
        if (name is { Length: > 0 }) label += $" ({name})";
        return label.Length <= 40 ? label : label[..37] + "…";
    }
}
