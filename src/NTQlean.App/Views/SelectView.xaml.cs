using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using NTQlean.Core;

namespace NTQlean.App.Views;

public partial class SelectView : UserControl
{
    private SelectionResult? _selection;
    private CleanupPlan? _plan;
    private readonly HashSet<string> _excludedChats = new(StringComparer.OrdinalIgnoreCase);
    private readonly ThumbService _thumbs = new(256);

    public SelectView()
    {
        InitializeComponent();
        // WAL safety: records for freshly received files may still sit in QQ's
        // -wal files, invisible to the index → new files look like orphans.
        // Default the upper bound to one week ago; the user can change it.
        DateTo.SelectedDate = DateTime.Today.AddDays(-7);
        AppState.IndexBuilt += () => Dispatcher.BeginInvoke(LoadChats);
    }

    private string? IndexPath => AppState.Workspace?.IndexPath;

    private void LoadChats()
    {
        if (IndexPath is null || !File.Exists(IndexPath)) return;
        _excludedChats.IntersectWith(SelectionEngine.QueryChats(IndexPath).Select(c => c.ChatId));
        ChatGrid.ItemsSource = SelectionEngine.QueryChats(IndexPath)
            .Select(c => new ChatRow(c.ChatId, c.DisplayName, c.Items, c.Bytes,
                _excludedChats.Contains(c.ChatId)))
            .ToList();
    }

    private SelectionOptions BuildOptions()
    {
        var kinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (KImage.IsChecked == true) kinds.Add("image");
        if (KVideo.IsChecked == true) kinds.Add("video");
        if (KFile.IsChecked == true) kinds.Add("file");
        if (KPtt.IsChecked == true) kinds.Add("ptt");
        if (KOther.IsChecked == true) kinds.Add("other");

        var confs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (CExact.IsChecked == true) confs.Add("exact");
        if (CStrong.IsChecked == true) confs.Add("strong");
        if (CHeuristic.IsChecked == true) confs.Add("heuristic");
        if (COrphan.IsChecked == true) confs.Add("orphan");

        long? sizeMin = ParseSize(SizeMin.Text, 1);
        long? sizeMax = ParseSize(SizeMax.Text, 1);
        long? from = DateFrom.SelectedDate is { } d1
            ? new DateTimeOffset(d1, TimeZoneInfo.Local.GetUtcOffset(d1)).ToUnixTimeSeconds() : null;
        long? to = DateTo.SelectedDate is { } d2
            ? new DateTimeOffset(d2.AddDays(1), TimeZoneInfo.Local.GetUtcOffset(d2)).ToUnixTimeSeconds() - 1 : null;

        return new SelectionOptions
        {
            Kinds = kinds,
            Confidences = confs,
            IncludedChats = null,
            ExcludedChats = _excludedChats,
            TimeFrom = from,
            TimeTo = to,
            SizeMin = sizeMin,
            SizeMax = sizeMax,
            Expression = string.IsNullOrWhiteSpace(ExprBox.Text) ? null : ExprBox.Text,
            IncludeOrphans = COrphan.IsChecked == true,
            IncludeReferencedOrphans = CNoRefOrphan.IsChecked != true,
        };
    }

    private static long? ParseSize(string text, long unit)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (!double.TryParse(text, out var v)) throw new FormatException($"非法大小数值: {text}");
        return (long)(v * 1048576 * unit / unit); // MB unit fixed in UI label
    }

    private async void OnApplyFilterClick(object sender, RoutedEventArgs e)
    {
        if (IndexPath is null || !File.Exists(IndexPath))
        {
            MessageBox.Show("请先在「密钥与索引」构建索引。", "NTQlean", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        SelectionOptions options;
        try
        {
            options = BuildOptions();
        }
        catch (FormatException ex)
        {
            MessageBox.Show(ex.Message, "NTQlean", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ApplyFilterButton.IsEnabled = false;
        ExportCsvButton.IsEnabled = false;
        FilterProgress.Visibility = Visibility.Visible;
        ResultStats.Text = "正在查询索引并生成预演…";
        MainWindow.SetBusy(true, "筛选中 …");
        try
        {
            var path = IndexPath;
            (_selection, _plan) = await Task.Run(() =>
            {
                var sel = SelectionEngine.Query(path, options);
                return (sel, CleanupPlan.FromSelection(sel, path, options.Expression));
            });

            ResultGrid.ItemsSource = _selection.Rows.Select(r => new ResultRow(r)).ToList();
            ResultStats.Text = $"命中 {_selection.Rows.Count:N0} 项 / {_selection.TotalBytes / 1073741824.0:F2} GB" +
                               $"（可解析 {_selection.ResolvableCount:N0} 项）";
            AppState.CleanupReport = _plan.ToSummaryText();
            AppState.Plan = _plan;
            AppState.NotifyPlanUpdated();
            MainWindow.SetStatus($"筛选完成: {_selection.Rows.Count:N0} 项");
        }
        catch (SelectionExpression.SyntaxException ex)
        {
            ResultStats.Text = "表达式语法错误";
            MainWindow.SetStatus("筛选失败：表达式语法错误");
            MessageBox.Show($"表达式语法错误: {ex.Message}", "NTQlean", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            ResultStats.Text = "筛选失败";
            MainWindow.SetStatus("筛选失败");
            MessageBox.Show($"筛选失败: {ex.Message}", "NTQlean", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            FilterProgress.Visibility = Visibility.Collapsed;
            ApplyFilterButton.IsEnabled = true;
            ExportCsvButton.IsEnabled = true;
            MainWindow.SetBusy(false);
        }
    }

    private void OnResultSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ResultGrid.SelectedItem is not ResultRow row)
        {
            PreviewImage.Source = null;
            PreviewInfo.Text = "";
            return;
        }
        var root = GetNtDataRoot();
        PreviewInfo.Text =
            $"路径: {row.Row.AbsPath}\n大小: {row.Row.ActualSize ?? row.Row.SizeBytes:N0} 字节\n" +
            $"时间: {row.TimeText}\n置信度: {row.Row.Confidence}\n来源: {row.Row.Source}/{row.Row.SourceTable}";

        var target = row;
        Task.Run(async () =>
        {
            var bmp = await _thumbs.GetAsync(target.Row, root);
            Dispatcher.BeginInvoke(() =>
            {
                if (ResultGrid.SelectedItem is ResultRow cur && cur.ItemId == target.ItemId)
                    PreviewImage.Source = bmp;
            });
        });
    }

    private string? GetNtDataRoot()
    {
        if (IndexPath is null || !File.Exists(IndexPath)) return null;
        try
        {
            using var conn = NtqSqlite.OpenReadOnly(IndexPath);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT value FROM meta WHERE key='nt_data_root'";
            return cmd.ExecuteScalar() as string;
        }
        catch
        {
            return null;
        }
    }

    // ── 右键菜单 ──
    private static ResultRow? MenuRow(object sender) =>
        (sender as MenuItem)?.CommandParameter as ResultRow;

    private void OnOpenFileClick(object sender, RoutedEventArgs e)
    {
        var path = MenuRow(sender)?.Row.AbsPath;
        if (!TryGetExistingFile(path, out var existingPath)) return;

        TryShellAction("打开文件", existingPath, () =>
            Process.Start(new ProcessStartInfo(existingPath) { UseShellExecute = true }));
    }

    private void OnOpenLocationClick(object sender, RoutedEventArgs e)
    {
        var path = MenuRow(sender)?.Row.AbsPath;
        if (!TryGetExistingFile(path, out var existingPath)) return;

        TryShellAction("打开所在位置", existingPath, () =>
            Process.Start(new ProcessStartInfo("explorer.exe")
            {
                UseShellExecute = true,
                ArgumentList = { "/select,", existingPath },
            }));
    }

    private void OnCopyPathClick(object sender, RoutedEventArgs e)
    {
        var path = MenuRow(sender)?.Row.AbsPath;
        if (string.IsNullOrWhiteSpace(path)) return;

        if (TryShellAction("复制完整路径", path, () => Clipboard.SetText(path)))
            MainWindow.SetStatus("路径已复制");
    }

    private static bool TryGetExistingFile(string? path, out string existingPath)
    {
        existingPath = path ?? "";
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) return true;

        MessageBox.Show("文件不存在、已被移动或路径未解析。", "NTQlean",
            MessageBoxButton.OK, MessageBoxImage.Information);
        return false;
    }

    private static bool TryShellAction(string action, string path, Action operation)
    {
        try
        {
            operation();
            return true;
        }
        catch (Exception ex)
        {
            AppState.WriteLog($"[警告] {action}失败: {ex.Message}");
            MessageBox.Show($"{action}失败。\n\n路径: {path}\n原因: {ex.Message}", "NTQlean",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    // ── 会话排除 (= NOT) ──
    private void OnExcludeChatClick(object sender, RoutedEventArgs e)
    {
        foreach (var row in ChatGrid.SelectedItems.Cast<ChatRow>().ToList())
            _excludedChats.Add(row.ChatId);
        RefreshChatStates();
    }

    private void OnUnexcludeChatClick(object sender, RoutedEventArgs e)
    {
        _excludedChats.Clear();
        RefreshChatStates();
    }

    private void RefreshChatStates()
    {
        if (ChatGrid.ItemsSource is IEnumerable<ChatRow> rows)
            ChatGrid.ItemsSource = rows.Select(r => r with { Excluded = _excludedChats.Contains(r.ChatId) }).ToList();
    }

    // ── 时间预设 ──
    private void OnPreset30d(object s, RoutedEventArgs e)
    {
        DateTo.SelectedDate = DateTime.Today;
        DateFrom.SelectedDate = DateTime.Today.AddDays(-30);
    }

    private void OnPresetYear(object s, RoutedEventArgs e)
    {
        DateFrom.SelectedDate = new DateTime(DateTime.Today.Year, 1, 1);
        DateTo.SelectedDate = DateTime.Today;
    }

    private void OnPresetLastYear(object s, RoutedEventArgs e)
    {
        var y = DateTime.Today.Year - 1;
        DateFrom.SelectedDate = new DateTime(y, 1, 1);
        DateTo.SelectedDate = new DateTime(y, 12, 31);
    }

    private void OnPresetAll(object s, RoutedEventArgs e)
    {
        DateFrom.SelectedDate = null;
        DateTo.SelectedDate = null;
    }

    private void OnExportCsvClick(object sender, RoutedEventArgs e)
    {
        if (_selection is null)
        {
            MessageBox.Show("还没有筛选结果。", "NTQlean", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "CSV (*.csv)|*.csv",
            FileName = $"ntqlean-dryrun-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
        };
        if (dlg.ShowDialog() != true) return;

        using var w = new StreamWriter(dlg.FileName);
        w.WriteLine("file_name,kind,chat_id,chat_name,size_bytes,time,confidence,abs_path");
        foreach (var r in _selection.Rows)
            w.WriteLine("\"{0}\",{1},{2},{3},{4},{5},{6},\"{7}\"",
                (r.FileName ?? "").Replace("\"", "\"\""), r.Kind, r.ChatId,
                (r.ChatName ?? "").Replace("\"", "\"\""), r.ActualSize ?? r.SizeBytes ?? 0,
                r.MsgTime is { } t ? DateTimeOffset.FromUnixTimeSeconds(t).LocalDateTime : null,
                r.Confidence, (r.AbsPath ?? "").Replace("\"", "\"\""));
        MainWindow.SetStatus("CSV 已导出");
    }
}

public sealed record ChatRow(
    string ChatId, string? Name, long Items, long Bytes, bool Excluded = false)
{
    public string DisplayName => Name ?? "—";
    public string ItemsText => $"{Items:N0} 项";
    public string SizeText => $"{Bytes / 1048576.0:F0} MB";
    public string ExcludedText => Excluded ? "已排除" : "包含";
}

public sealed class ResultRow
{
    public ResultRow(SelectionRow row) => Row = row;
    public SelectionRow Row { get; }
    public long ItemId => Row.ItemId;
    public string FileName => Path.GetFileName(
        Row.RelPath is { Length: > 0 } ? Row.RelPath : Row.FileName ?? "");
    public string Kind => Row.Kind;
    public string ChatLabel => Row.ChatName is { Length: > 0 }
        ? $"{Row.ChatName} [{Row.ChatId}]"
        : (Row.ChatId is { Length: > 0 } ? Row.ChatId : "—");
    public string SizeText => ((Row.ActualSize ?? Row.SizeBytes ?? 0) / 1048576.0) switch
    {
        var mb when mb >= 1024 => $"{mb / 1024:F2} GB",
        var mb => $"{mb:F1} MB",
    };
    public string TimeText => Row.MsgTime is { } t
        ? DateTimeOffset.FromUnixTimeSeconds(t).LocalDateTime.ToString("yyyy-MM-dd HH:mm")
        : "—";
    public string Confidence => Row.Confidence;
    public string MsgRefsText => Row.Confidence == "orphan"
        ? (Row.MsgRefs > 0 ? $"{Row.MsgRefs} 条" : "0")
        : "—";
}
