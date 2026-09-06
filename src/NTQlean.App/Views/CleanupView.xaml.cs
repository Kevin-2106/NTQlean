using System.IO;
using System.Windows;
using System.Windows.Controls;
using NTQlean.Core;

namespace NTQlean.App.Views;

public partial class CleanupView : UserControl
{
    public CleanupView()
    {
        InitializeComponent();
        ReportBox.Text = "（尚无预演结果 —— 请先在「选择与预览」应用筛选）";
        AppState.PlanUpdated += () => Dispatcher.BeginInvoke(() =>
        {
            ReportBox.Text = AppState.CleanupReport ?? ReportBox.Text;
            var plan = AppState.Plan;
            PlanSummaryText.Text = plan is null
                ? "尚无预演结果"
                : $"{plan.Items.Count:N0} 个文件 · {plan.TotalBytes / 1073741824.0:F2} GB · 等待人工确认";
        });
    }

    private void OnExecuteCleanupClick(object sender, RoutedEventArgs e)
    {
        var plan = AppState.Plan;
        if (plan is null || plan.Items.Count == 0)
        {
            MessageBox.Show("没有待清理的预演结果。请先在「选择与预览」应用筛选。", "NTQlean",
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
            $"将把 {plan.Items.Count:N0} 个文件（{plan.TotalBytes / 1073741824.0:F2} GB）移入回收站。\n" +
            "继续吗？（文件可在回收站还原）", "确认清理", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (ok != MessageBoxResult.Yes) return;

        var workspace = AppState.Workspace
                        ?? new Workspace(Path.Combine(Path.GetTempPath(), "NTQlean"));
        var manifest = Path.Combine(workspace.ReportsDir,
            $"cleanup-manifest-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        var result = CleanupExecutor.Apply(plan, manifest, requireConfirm: true,
            allowedRoots: AppState.DataDirBoxText);

        MessageBox.Show(
            $"完成: {result.SuccessCount}/{result.Items.Count} 个文件已移入回收站。\n" +
            $"释放 {result.FreedBytes / 1073741824.0:F2} GB。\n清单: {result.ManifestPath}",
            "NTQlean", MessageBoxButton.OK, MessageBoxImage.Information);
        AppState.WriteLog($"[清理] 成功 {result.SuccessCount}/{result.Items.Count}，清单 {result.ManifestPath}");
        MainWindow.SetStatus("清理完成（回收站）");
    }
}
