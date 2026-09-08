using System.IO;
using System.Reflection;
using System.Windows;
using Wpf.Ui.Controls;

namespace NTQlean.App;

public partial class AboutWindow : FluentWindow
{
    /// <summary>Marker file under %LOCALAPPDATA%\NTQlean that records disclaimer acceptance.</summary>
    private static string AgreementPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NTQlean", "disclaimer-agreed.txt");

    public bool Agreed { get; private set; }

    public AboutWindow()
    {
        InitializeComponent();
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $"版本 {version?.ToString(3) ?? "dev"} · MIT License · 非官方社区工具，与腾讯无关";
    }

    private void OnAgreeClick(object sender, RoutedEventArgs e)
    {
        Agreed = true;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AgreementPath)!);
            File.WriteAllText(AgreementPath, $"agreed {DateTime.UtcNow:u}\n");
        }
        catch (IOException)
        {
            // Acceptance is best-effort persistence; failing to write must not block usage.
        }
        Close();
    }

    /// <summary>Shows the disclaimer on first launch (per machine). Manual opens never re-write the marker.</summary>
    public static void ShowOnFirstRun(Window owner)
    {
        try
        {
            if (File.Exists(AgreementPath)) return;
        }
        catch (IOException)
        {
            // Unreadable marker: fall through and show the disclaimer.
        }

        var window = new AboutWindow { Owner = owner };
        window.ShowDialog();
    }
}
