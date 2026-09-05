using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using NTQlean.Core;

namespace NTQlean.App.Views;

public partial class SourcesView : UserControl
{
    public SourcesView()
    {
        InitializeComponent();
        WorkspaceBox.Text = Workspace.DefaultRoot();
        SyncPaths();
        DbDirBox.TextChanged += (_, _) => SyncPaths();
        DataDirBox.TextChanged += (_, _) => SyncPaths();
        WorkspaceBox.TextChanged += (_, _) => SyncPaths();
        OnDiscoverClick(this, e: null!); // auto-discover on startup
    }

    private void SyncPaths()
    {
        AppState.DbDirBoxText = DbDirBox.Text;
        AppState.DataDirBoxText = DataDirBox.Text;
        AppState.WorkspaceBoxText = WorkspaceBox.Text;
    }

    private void OnDiscoverClick(object sender, RoutedEventArgs? e)
    {
        var rows = new List<AccountRow>();
        foreach (var root in NtqqDiscovery.Discover())
        {
            foreach (var account in root.Accounts)
                rows.Add(new AccountRow(ProbeLog.MaskId(account.Uin), account.Uin,
                    $"{account.DbTotalBytes / 1048576.0:F0} MB", root.Root,
                    account.NtDbDir ?? "", account.NtDataDir ?? ""));
            if (root.GlobalNtDb is not null)
                rows.Add(new AccountRow("(全局)", "", "", root.Root, root.GlobalNtDb, ""));
        }
        AccountGrid.ItemsSource = rows;
        MainWindow.SetStatus($"发现 {rows.Count} 个条目");
    }

    private void OnAccountSelected(object sender, SelectionChangedEventArgs e)
    {
        if (AccountGrid.SelectedItem is not AccountRow row) return;
        DbDirBox.Text = row.NtDbDir;
        DataDirBox.Text = row.NtDataDir;
        if (row.NtDataDir.Length > 0)
        {
            var stable = "workspace-" + new string(row.NtDataDir.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant()[..24];
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
        SyncPaths();
        MainWindow.NavigateTo("keys");
    }
}

public sealed record AccountRow(
    string MaskedUin, string Uin, string DbSizeText, string Root, string NtDbDir, string NtDataDir);
