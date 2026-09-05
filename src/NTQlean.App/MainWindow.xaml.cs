using System.Windows;
using NTQlean.App.Views;
using Wpf.Ui.Controls;
using UiButton = Wpf.Ui.Controls.Button;

namespace NTQlean.App;

public partial class MainWindow : FluentWindow
{
    private readonly SourcesView _sourcesView = new();
    private readonly KeysView _keysView = new();
    private readonly SelectView _selectView = new();
    private readonly CleanupView _cleanupView = new();

    public MainWindow()
    {
        InitializeComponent();
        PageHost.Content = _sourcesView;
        ShowPage("sources");
        StatusText.Text = "就绪";
        AppState.LogSink = s => Dispatcher.BeginInvoke(() =>
        {
            _keysView.AppendLog(s);
            StatusText.Text = s.Length > 70 ? s[..70] + "…" : s;
        });
    }

    public static void SetStatus(string s)
    {
        var win = Application.Current.MainWindow as MainWindow;
        win?.Dispatcher.BeginInvoke(() => win.StatusText.Text = s);
    }

    public static void NavigateTo(string tag)
    {
        var win = Application.Current.MainWindow as MainWindow;
        win?.Dispatcher.BeginInvoke(() => win.ShowPage(tag));
    }

    private void OnNavButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is UiButton { Tag: string tag }) ShowPage(tag);
    }

    private void ShowPage(string tag)
    {
        var target = tag switch
        {
            "sources" => (UiButton?)NavSources,
            "keys" => (UiButton?)NavKeys,
            "select" => (UiButton?)NavSelect,
            "cleanup" => (UiButton?)NavCleanup,
            _ => null,
        };
        if (target is null) return;

        foreach (var b in new UiButton[] { NavSources, NavKeys, NavSelect, NavCleanup })
            b.Appearance = b == target ? ControlAppearance.Primary : ControlAppearance.Secondary;

        PageHost.Content = tag switch
        {
            "sources" => _sourcesView,
            "keys" => _keysView,
            "select" => _selectView,
            "cleanup" => _cleanupView,
            _ => PageHost.Content,
        };
    }
}
