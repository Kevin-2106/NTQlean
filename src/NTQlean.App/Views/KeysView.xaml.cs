using System.IO;
using System.Windows;
using System.Windows.Controls;
using NTQlean.Core;

namespace NTQlean.App.Views;

public partial class KeysView : UserControl
{
    private readonly List<KeyRow> _keyRows = new();

    public KeysView()
    {
        InitializeComponent();
        AppState.LogSink += AppendLog;
        RefreshKeyGrid();
    }

    public void AppendLog(string line)
    {
        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}");
        LogBox.ScrollToEnd();
    }

    private void RefreshKeyGrid(string? selectedDbForManual = null)
    {
        _keyRows.Clear();
        var dbDir = AppState.DbDirBoxText ?? "";
        foreach (var name in DecryptService.AccountDbs)
        {
            var path = Path.Combine(dbDir, name);
            string state, mask = "—";
            if (!File.Exists(path))
            {
                state = "缺失";
            }
            else if (DecryptService.IsPlainSqlite(path))
            {
                state = "未加密";
            }
            else
            {
                var key = FindKeyFor(path);
                if (key is not null)
                {
                    state = "已解密 ✓";
                    mask = key.Length >= 8 ? key[..4] + "…" + key[^4..] : "****";
                }
                else
                {
                    state = "待解密";
                }
            }
            _keyRows.Add(new KeyRow(name, state, mask));
        }
        KeyGrid.ItemsSource = _keyRows.ToList();
    }

    private static string? FindKeyFor(string encryptedDb)
    {
        if (AppState.MemoryKeys is null) return null;
        try
        {
            var salt = KeyDumper.ReadSalt(encryptedDb);
            return AppState.MemoryKeys.TryGetValue(Convert.ToHexString(salt), out var keys)
                ? keys.FirstOrDefault() : null;
        }
        catch
        {
            return null;
        }
    }

    private async void OnDumpKeyClick(object sender, RoutedEventArgs e)
    {
        var dbDir = AppState.DbDirBoxText;
        if (string.IsNullOrEmpty(dbDir) || !Directory.Exists(dbDir))
        {
            MessageBox.Show("请先在「① 数据源」选择有效的 nt_db 目录。", "NTQlean",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var probeDb = DecryptService.AccountDbs
            .Select(n => Path.Combine(dbDir, n))
            .FirstOrDefault(p => File.Exists(p) && !DecryptService.IsPlainSqlite(p));
        if (probeDb is null)
        {
            MessageBox.Show("nt_db 中没有加密的账号数据库。", "NTQlean",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        DumpKeyButton.IsEnabled = false;
        MainWindow.SetStatus("扫描 QQ 进程内存（只读）…");
        try
        {
            var specs = await Task.Run(() => KeyDumper.DumpAllKeyspecs(null, out _, out _));
            var key = await Task.Run(() => ValidateKey(probeDb, specs));
            if (key is null)
            {
                MessageBox.Show("未找到与该账号匹配的 key。\n请确认 QQ 正在运行且已登录该账号。",
                    "NTQlean", MessageBoxButton.OK, MessageBoxImage.Warning);
                MainWindow.SetStatus("未找到 key");
                return;
            }
            AppState.MemoryKeys = specs;
            RefreshKeyGrid();
            MainWindow.SetStatus("key 已提取（仅内存）");
            AppendLog($"提取完成：{specs.Values.Sum(v => v.Count)} 个 keyspec / {specs.Count} 个 salt。");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"提取失败: {ex.Message}", "NTQlean", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            DumpKeyButton.IsEnabled = true;
        }
    }

    private void OnApplyManualKeyClick(object sender, RoutedEventArgs e)
    {
        var key = ManualKeyBox.Text?.Trim();
        if (string.IsNullOrEmpty(key)) return;
        var dbDir = AppState.DbDirBoxText;
        var probeDb = DecryptService.AccountDbs
            .Select(n => Path.Combine(dbDir, n))
            .FirstOrDefault(p => File.Exists(p) && !DecryptService.IsPlainSqlite(p));
        if (probeDb is null) return;

        var salt = KeyDumper.ReadSalt(probeDb);
        var saltHex = Convert.ToHexString(salt);
        var specs = AppState.MemoryKeys is null
            ? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, List<string>>(AppState.MemoryKeys, StringComparer.OrdinalIgnoreCase);
        if (!specs.TryGetValue(saltHex, out var list)) specs[saltHex] = list = new();
        list.Insert(0, key);
        AppState.MemoryKeys = specs;
        RefreshKeyGrid();
        MainWindow.SetStatus("手动 key 已加入（仅内存）");
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
            // ignore
        }
        return null;
    }

    private async void OnBuildIndexClick(object sender, RoutedEventArgs e)
    {
        var force = (sender as FrameworkElement)?.Tag as string == "force";
        var dbDir = AppState.DbDirBoxText;
        var dataDir = AppState.DataDirBoxText;
        var workspaceDir = AppState.WorkspaceBoxText;
        var includeNtMsg = IncludeNtMsgBox.IsChecked == true;

        if (string.IsNullOrEmpty(dataDir) || !Directory.Exists(dataDir))
        {
            MessageBox.Show("nt_data 目录无效。", "NTQlean", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _keyRows.Clear(); // status will be refreshed after build
        SetBuildControlsEnabled(false);
        MainWindow.SetStatus("构建索引中 …");
        try
        {
            var workspace = new Workspace(workspaceDir);
            AppState.Workspace = workspace;
            IProgress<string> progress = new Progress<string>(AppState.WriteLog);

            if (string.IsNullOrEmpty(dbDir) || !Directory.Exists(dbDir))
            {
                AppendLog("nt_db 不可用：尝试复用工作区已有明文副本。");
            }
            else
            {
                if (AppState.MemoryKeys is null)
                {
                    AppendLog("从运行中的 QQ 扫描各库 keyspec（每个库 key 独立）…");
                    var specs = await Task.Run(() => KeyDumper.DumpAllKeyspecs(null, out _, out _));
                    if (specs.Count > 0)
                    {
                        AppState.MemoryKeys = specs;
                        AppendLog($"获得 {specs.Values.Sum(v => v.Count)} 个 keyspec / {specs.Count} 个 salt。");
                    }
                    else
                    {
                        AppendLog("未扫到 keyspec（QQ 未运行？）。只有与所填 key 匹配的库能解密。");
                    }
                }

                var targets = DecryptService.AccountDbs
                    .Where(n => File.Exists(Path.Combine(dbDir, n))).ToList();
                var memoryKeys = CopyMemoryKeys(AppState.MemoryKeys);
                var outcomes = await Task.Run(() => DecryptService.DecryptAll(
                    workspace, dbDir, string.Empty, targets, force, progress, memoryKeys));
                foreach (var o in outcomes)
                {
                    if (o.Error is not null) AppendLog($"[警告] {o.Source}: {o.Error}");
                    else if (o.Decrypted && o.Pages > 0)
                        AppendLog($"{o.Source}: 解密 {o.Pages} 页, HMAC {o.HmacOk}/{o.HmacOk + o.HmacBad}");
                }
            }

            var indexKeys = CopyMemoryKeys(AppState.MemoryKeys);
            var summary = await Task.Run(() =>
            {
                // Optional heavy nt_msg index (per-DB key, D: target for the 13 GB copy).
                string? ntMsgPlain = null;
                if (includeNtMsg &&
                    !string.IsNullOrEmpty(dbDir) && File.Exists(Path.Combine(dbDir, DecryptService.NtMsgDb)))
                {
                    var ntMsgWorkspace = new Workspace("D:\\NTQlean\\nt-msg");
                    progress.Report("解密 nt_msg.db（大库，数分钟）…");
                    var outcomes = DecryptService.DecryptAll(ntMsgWorkspace, dbDir,
                        string.Empty, new[] { DecryptService.NtMsgDb }, force, progress, indexKeys);
                    foreach (var o in outcomes)
                    {
                        if (o.Error is not null) progress.Report($"[警告] nt_msg.db: {o.Error}");
                        else if (o.Decrypted) progress.Report($"nt_msg.db: 解密完成（HMAC {o.HmacOk}）");
                    }
                    var p = ntMsgWorkspace.PlainDbPath(DecryptService.NtMsgDb);
                    if (File.Exists(p)) ntMsgPlain = p;
                }
                return MediaIndexBuilder.Build(workspace, dataDir, progress, ntMsgPlain);
            });
            AppendLog($"索引完成: 媒体 {summary.MediaRows:N0}（已解析 {summary.Resolved:N0} / 未解析 {summary.Missing:N0}），" +
                      $"nt_data 文件 {summary.NtFiles:N0}，孤儿 {summary.OrphanFiles:N0} 个 / {summary.OrphanBytes / 1048576.0:F0} MB");
            foreach (var note in summary.Notes) AppendLog($"[提示] {note}");

            RefreshKeyGrid();
            AppState.NotifyIndexBuilt();
            MainWindow.NavigateTo("select");
            MainWindow.SetStatus("索引就绪");
        }
        catch (Exception ex)
        {
            AppendLog($"[错误] {ex.Message}");
            MessageBox.Show($"索引构建失败: {ex.Message}", "NTQlean", MessageBoxButton.OK, MessageBoxImage.Error);
            MainWindow.SetStatus("索引失败");
        }
        finally
        {
            SetBuildControlsEnabled(true);
        }
    }

    private static IReadOnlyDictionary<string, List<string>>? CopyMemoryKeys(
        IReadOnlyDictionary<string, List<string>>? source)
    {
        return source?.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToList(),
            StringComparer.OrdinalIgnoreCase);
    }

    private void SetBuildControlsEnabled(bool enabled)
    {
        BuildButton.IsEnabled = enabled;
        ForceBuildButton.IsEnabled = enabled;
        DumpKeyButton.IsEnabled = enabled;
        ApplyManualKeyButton.IsEnabled = enabled;
        ManualKeyBox.IsEnabled = enabled;
        IncludeNtMsgBox.IsEnabled = enabled;
    }
}

public sealed record KeyRow(string DbName, string State, string KeyMask);
