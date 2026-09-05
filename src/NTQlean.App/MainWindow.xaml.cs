using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using NTQlean.Core;

namespace NTQlean.App;

public partial class MainWindow : Window
{
    private Workspace? _workspace;
    private SelectionResult? _selection;
    private CleanupPlan? _plan;
    private readonly HashSet<string> _excludedChats = new(StringComparer.OrdinalIgnoreCase);
    private readonly ThumbService _thumbs = new(256);

    public MainWindow()
    {
        InitializeComponent();
        WorkspaceBox.Text = Workspace.DefaultRoot();
        StatusText.Text = "就绪";
    }

    private void SetStatus(string s) => StatusText.Text = s;

    private void Log(string line)
    {
        Dispatcher.BeginInvoke(() =>
        {
            IndexLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}");
            IndexLog.ScrollToEnd();
        });
    }

    // ═══════════ ① 数据源 ═══════════

    private void OnDiscoverClick(object sender, RoutedEventArgs e)
    {
        AccountList.Items.Clear();
        var roots = NtqqDiscovery.Discover();
        foreach (var root in roots)
        {
            foreach (var account in root.Accounts)
            {
                AccountList.Items.Add(new AccountEntry(
                    $"{ProbeLog.MaskId(account.Uin)}  —  {account.DbTotalBytes / 1024.0 / 1024:F0} MB  @ {root.Root}",
                    account.NtDbDir ?? "", account.NtDataDir ?? ""));
            }
            if (root.GlobalNtDb is not null)
                AccountList.Items.Add(new AccountEntry(
                    $"(全局) login.db 等 @ {root.Root}", root.GlobalNtDb, ""));
        }
        if (AccountList.Items.Count == 0)
            MessageBox.Show("未在标准位置发现 NTQQ 数据目录。请手动填写 nt_db / nt_data 路径。",
                "NTQlean", MessageBoxButton.OK, MessageBoxImage.Information);
        SetStatus($"发现 {AccountList.Items.Count} 个条目");
    }

    private void OnAccountSelected(object sender, SelectionChangedEventArgs e)
    {
        if (AccountList.SelectedItem is not AccountEntry entry) return;
        DbDirBox.Text = entry.NtDbDir;
        DataDirBox.Text = entry.NtDataDir;
        if (entry.NtDataDir.Length > 0 && Directory.Exists(Path.GetDirectoryName(entry.NtDataDir)))
        {
            // workspace per account root, stable name (no timestamp) so reopening works
            var stable = "workspace-" + string.Concat(entry.NtDataDir.Where(char.IsLetterOrDigit)).ToLowerInvariant()[..24];
            WorkspaceBox.Text = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NTQlean", stable);
        }
    }

    private void OnBrowseDbClick(object s, RoutedEventArgs e) => PickDir(DbDirBox);
    private void OnBrowseDataClick(object s, RoutedEventArgs e) => PickDir(DataDirBox);
    private void OnBrowseWorkspaceClick(object s, RoutedEventArgs e) => PickDir(WorkspaceBox);

    private static void PickDir(TextBox box)
    {
        var dlg = new OpenFileDialog
        {
            CheckFileExists = false,
            FileName = "选择此目录",
            InitialDirectory = Directory.Exists(box.Text) ? box.Text : null,
        };
        if (dlg.ShowDialog() == true)
            box.Text = Path.GetDirectoryName(dlg.FileName) ?? box.Text;
    }

    private void OnGoIndexClick(object sender, RoutedEventArgs e)
    {
        if (!Directory.Exists(DbDirBox.Text) && Directory.Exists(DataDirBox.Text))
        {
            // allow missing nt_db only when plain copies already exist in workspace
        }
        Tabs.SelectedIndex = 1;
    }

    // ═══════════ ② 索引 ═══════════

    private async void OnDumpKeyClick(object sender, RoutedEventArgs e)
    {
        var dbDir = DbDirBox.Text;
        if (!Directory.Exists(dbDir))
        {
            MessageBox.Show("请先在「① 数据源」选择有效的 nt_db 目录。", "NTQlean",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string? probeDb = DecryptService.AccountDbs
            .Select(n => Path.Combine(dbDir, n))
            .FirstOrDefault(p => File.Exists(p) && !DecryptService.IsPlainSqlite(p));
        if (probeDb is null)
        {
            MessageBox.Show("nt_db 中没有加密的账号数据库。", "NTQlean",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        DumpKeyButton.IsEnabled = false;
        SetStatus("扫描 QQ 进程内存（只读）…");
        try
        {
            var specs = await Task.Run(() => KeyDumper.DumpAllKeyspecs(null, out _, out _));
            var key = await Task.Run(() => ValidateKey(probeDb, specs));
            if (key is null)
            {
                MessageBox.Show("未找到与该账号匹配的 key。\n请确认 QQ 正在运行且已登录该账号。",
                    "NTQlean", MessageBoxButton.OK, MessageBoxImage.Warning);
                SetStatus("未找到 key");
                return;
            }
            KeyBox.Password = key; // stays in memory only
            SetStatus("key 已提取（仅内存）");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"提取失败: {ex.Message}", "NTQlean", MessageBoxButton.OK, MessageBoxImage.Error);
            SetStatus("提取失败");
        }
        finally
        {
            DumpKeyButton.IsEnabled = true;
        }
    }

    private static string? ValidateKey(string encryptedDb, IReadOnlyDictionary<string, List<string>> specs)
    {
        try
        {
            var salt = KeyDumper.ReadSalt(encryptedDb);
            if (!specs.TryGetValue(Convert.ToHexString(salt), out var keys)) return null;
            var tempDir = Path.Combine(Path.GetTempPath(), "NTQlean", "keycheck-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(tempDir);
            try
            {
                var cfg = new SqlCipherConfig { PageSize = 4096, KdfIterations = 4000, KdfUseSha512 = true, Hmac = HmacAlgorithm.HmacSha1 };
                foreach (var key in keys)
                {
                    if (SqlCipherDecryptor.DecryptCopy(encryptedDb,
                            Path.Combine(tempDir, "probe.plain.db"), key, cfg, maxPages: 1).Success)
                        return key;
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
        catch
        {
            // fall through
        }
        return null;
    }

    private async void OnBuildIndexClick(object sender, RoutedEventArgs e)
    {
        var force = (sender as Button)?.Tag as string == "force";
        var dataDir = DataDirBox.Text;
        var dbDir = DbDirBox.Text;

        if (!Directory.Exists(dataDir))
        {
            MessageBox.Show("nt_data 目录无效。", "NTQlean", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!Directory.Exists(dbDir) && !Directory.Exists(Path.Combine(dataDir, "..", "nt_db")))
        {
            var existing = Directory.Exists(WorkspaceBox.Text) &&
                           Directory.EnumerateFiles(Path.Combine(WorkspaceBox.Text, "decrypted"), "*.plain.db").Any();
            if (!existing)
            {
                MessageBox.Show("nt_db 目录无效，且工作区没有已有的明文副本。", "NTQlean",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        var key = KeyBox.Password;
        if (string.IsNullOrWhiteSpace(key) && Directory.Exists(dbDir) &&
            DecryptService.AccountDbs.Any(n => File.Exists(Path.Combine(dbDir, n)) &&
                                               !DecryptService.IsPlainSqlite(Path.Combine(dbDir, n))))
        {
            MessageBox.Show("账号数据库已加密，需要 key 才能解密。\n" +
                            "key 可通过社区工具获取（见 docs/research-notes.md）；NTQlean 自身不读取 QQ 进程。\n" +
                            "key 只保留在内存中。", "需要 key", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _workspace = new Workspace(WorkspaceBox.Text);
        BuildIndexButton.IsEnabled = false;
        SetStatus("构建索引中 …");

        try
        {
            var progress = new Progress<string>(Log);
            if (!Directory.Exists(dbDir))
            {
                Log("nt_db 不可用：尝试复用工作区已有明文副本。");
            }
            else
            {
                var targets = DecryptService.AccountDbs
                    .Where(n => File.Exists(Path.Combine(dbDir, n))).ToList();
                var outcomes = await Task.Run(() =>
                    DecryptService.DecryptAll(_workspace, dbDir, key, targets, force, progress));
                foreach (var o in outcomes)
                {
                    if (o.Error is not null) Log($"[警告] {o.Source}: {o.Error}");
                    else if (o.Decrypted && o.Pages > 0)
                        Log($"{o.Source}: 解密 {o.Pages} 页, HMAC {o.HmacOk}/{o.HmacOk + o.HmacBad}");
                }
            }

            var summary = await Task.Run(() => MediaIndexBuilder.Build(_workspace, dataDir, progress));
            Log($"索引完成: 媒体 {summary.MediaRows:N0}（已解析 {summary.Resolved:N0} / 未解析 {summary.Missing:N0}），" +
                $"nt_data 文件 {summary.NtFiles:N0}，孤儿 {summary.OrphanFiles:N0} 个 / {summary.OrphanBytes / 1048576.0:F0} MB");

            LoadChats();
            Tabs.SelectedIndex = 2;
            SetStatus("索引就绪");
        }
        catch (Exception ex)
        {
            Log($"[错误] {ex.Message}");
            MessageBox.Show($"索引构建失败: {ex.Message}", "NTQlean", MessageBoxButton.OK, MessageBoxImage.Error);
            SetStatus("索引失败");
        }
        finally
        {
            BuildIndexButton.IsEnabled = true;
        }
    }

    private void LoadChats()
    {
        if (_workspace is null) return;
        ChatList.Items.Clear();
        foreach (var chat in SelectionEngine.QueryChats(_workspace.IndexPath))
        {
            var label = chat.DisplayName is { Length: > 0 }
                ? $"{chat.DisplayName}  [{chat.ChatId}]  {chat.Bytes / 1048576.0:F0} MB"
                : $"{chat.ChatId}  {chat.Bytes / 1048576.0:F0} MB";
            ChatList.Items.Add(new ChatEntry(chat.ChatId,
                (_excludedChats.Contains(chat.ChatId) ? "[已排除] " : "") + label));
        }
    }

    // ═══════════ ③ 选择 ═══════════

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

        long? sizeMin = ParseSize(SizeMin.Text, Unit(SizeMinUnit));
        long? sizeMax = ParseSize(SizeMax.Text, Unit(SizeMaxUnit));
        long? from = DateFrom.SelectedDate is { } d1
            ? new DateTimeOffset(d1, TimeZoneInfo.Local.GetUtcOffset(d1)).ToUnixTimeSeconds() : null;
        long? to = DateTo.SelectedDate is { } d2
            ? new DateTimeOffset(d2.AddDays(1), TimeZoneInfo.Local.GetUtcOffset(d2)).ToUnixTimeSeconds() - 1 : null;

        return new SelectionOptions
        {
            Kinds = kinds,
            Confidences = confs,
            IncludedChats = null, // chat selection is expressed via exclusion (NOT) below
            ExcludedChats = _excludedChats,
            TimeFrom = from,
            TimeTo = to,
            SizeMin = sizeMin,
            SizeMax = sizeMax,
            Expression = string.IsNullOrWhiteSpace(ExprBox.Text) ? null : ExprBox.Text,
            IncludeOrphans = COrphan.IsChecked == true,
        };
    }

    private static string Unit(ComboBox cb) => (cb.SelectedItem as ComboBoxItem)?.Content as string ?? "MB";

    private static long? ParseSize(string text, string unit)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (!double.TryParse(text, out var v)) throw new FormatException($"非法大小数值: {text}");
        return (long)(v * (unit.ToUpperInvariant() switch { "KB" => 1024, "MB" => 1048576, _ => 1073741824 }));
    }

    private async void OnApplyFilterClick(object sender, RoutedEventArgs e)
    {
        if (_workspace is null || !File.Exists(_workspace.IndexPath))
        {
            MessageBox.Show("请先在「② 索引」构建索引。", "NTQlean", MessageBoxButton.OK, MessageBoxImage.Warning);
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

        SetStatus("筛选中 …");
        try
        {
            _selection = await Task.Run(() => SelectionEngine.Query(_workspace!.IndexPath, options));
            _plan = CleanupPlan.FromSelection(_selection, _workspace.IndexPath, options.Expression);

            ResultList.ItemsSource = _selection.Rows.Select(r => new ResultRow(r)).ToList();
            ResultStats.Text = $"命中 {_selection.Rows.Count:N0} 项 / {_selection.TotalBytes / 1073741824.0:F2} GB" +
                               $"（可解析 {_selection.ResolvableCount:N0} 项）";
            CleanupReportBox.Text = _plan.ToSummaryText();
            SetStatus($"筛选完成: {_selection.Rows.Count:N0} 项");
        }
        catch (SelectionExpression.SyntaxException ex)
        {
            MessageBox.Show($"表达式语法错误: {ex.Message}", "NTQlean", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"筛选失败: {ex.Message}", "NTQlean", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnResultSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ResultList.SelectedItem is not ResultRow row)
        {
            PreviewImage.Source = null;
            PreviewInfo.Text = "";
            return;
        }
        var root = GetNtDataRoot();
        PreviewInfo.Text =
            $"路径: {row.Row.AbsPath}\n大小: {row.Row.ActualSize ?? row.Row.SizeBytes:N0} 字节\n" +
            $"时间: {row.TimeText}\n置信度: {row.Row.Confidence}\n来源: {row.Row.Source}/{row.Row.SourceTable}\n" +
            $"msgId: {row.Row.MsgId?.ToString() ?? "-"}  md5: {row.Row.Md5 ?? "-"}";

        // low-cost preview: NTQQ ships its own Thumb files; decode lazily at 240px
        var target = row;
        Task.Run(async () =>
        {
            var bmp = await _thumbs.GetAsync(target.Row, root);
            Dispatcher.BeginInvoke(() =>
            {
                if (ResultList.SelectedItem is ResultRow cur && cur.ItemId == target.ItemId)
                    PreviewImage.Source = bmp;
            });
        });
    }

    private string? GetNtDataRoot()
    {
        if (_workspace is null || !File.Exists(_workspace.IndexPath)) return null;
        try
        {
            using var conn = NtqSqlite.OpenReadOnly(_workspace.IndexPath);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT value FROM meta WHERE key='nt_data_root'";
            return cmd.ExecuteScalar() as string;
        }
        catch
        {
            return null;
        }
    }

    private void OnExportCsvClick(object sender, RoutedEventArgs e)
    {
        if (_selection is null)
        {
            MessageBox.Show("还没有筛选结果。", "NTQlean", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dlg = new SaveFileDialog
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
        SetStatus("CSV 已导出");
    }

    // chat exclusion (= NOT)
    private void OnExcludeChatClick(object sender, RoutedEventArgs e)
    {
        foreach (var item in ChatList.SelectedItems.Cast<ChatEntry>().ToList())
            _excludedChats.Add(item.ChatId);
        LoadChats();
    }

    private void OnUnexcludeChatClick(object sender, RoutedEventArgs e)
    {
        _excludedChats.Clear();
        LoadChats();
    }

    private void OnQuickExcludeClick(object sender, RoutedEventArgs e) => OnExcludeChatClick(sender, e);

    // time presets
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

    // ═══════════ ④ 清理 ═══════════

    private void OnExecuteCleanupClick(object sender, RoutedEventArgs e)
    {
        if (_plan is null || _plan.Items.Count == 0)
        {
            MessageBox.Show("没有待清理的预演结果。请先在「③ 选择」应用筛选。", "NTQlean",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (ConfirmBox.Text != "清理" || UnderstandBox.IsChecked != true)
        {
            MessageBox.Show("请先查看预演报告，输入确认文字并勾选知情声明。", "NTQlean",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var ok = MessageBox.Show(
            $"将把 {_plan.Items.Count:N0} 个文件（{_plan.TotalBytes / 1073741824.0:F2} GB）移入回收站。\n" +
            "继续吗？（文件可在回收站还原）", "确认清理", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (ok != MessageBoxResult.Yes) return;

        var manifest = Path.Combine(_workspace!.ReportsDir,
            $"cleanup-manifest-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        var result = CleanupExecutor.Apply(_plan, manifest, requireConfirm: true, allowedRoots: DataDirBox.Text);
        MessageBox.Show(
            $"完成: {result.SuccessCount}/{result.Items.Count} 个文件已移入回收站。\n" +
            $"释放 {result.FreedBytes / 1073741824.0:F2} GB。\n清单: {result.ManifestPath}",
            "NTQlean", MessageBoxButton.OK, MessageBoxImage.Information);
        Log($"[清理] 成功 {result.SuccessCount}/{result.Items.Count}，清单 {result.ManifestPath}");
        SetStatus("清理完成（回收站）");
    }

    private sealed record AccountEntry(string Label, string NtDbDir, string NtDataDir);

    private sealed record ChatEntry(string ChatId, string Label);

    private sealed class ResultRow
    {
        public ResultRow(SelectionRow row) => Row = row;
        public SelectionRow Row { get; }
        public long ItemId => Row.ItemId;
        public string FileName => Path.GetFileName(Row.RelPath is { Length: > 0 } ? Row.RelPath : Row.FileName ?? "");
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
    }
}
