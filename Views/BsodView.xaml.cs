using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace XinSpect;

/// <summary>藍屏分析分頁：自持 BsodService，載入時掃描並顯示傾印判讀結果。</summary>
public partial class BsodView : UserControl
{
    private readonly BsodService _svc = new();
    private bool _loaded;

    public BsodView()
    {
        InitializeComponent();
        Loaded += (_, _) => { if (!_loaded) { _loaded = true; Scan(); } };
    }

    private void Scan()
    {
        _svc.Scan();
        List.ItemsSource = null;
        List.ItemsSource = _svc.Rows;
        StatusText.Text = _svc.Status;
    }

    private void Scan_Click(object sender, RoutedEventArgs e) => Scan();

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string dir = Path.Combine(Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows", "Minidump");
            if (Directory.Exists(dir))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
            else
                MessageBox.Show("傾印資料夾不存在。", "藍屏分析", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch { /* 忽略 */ }
    }
}
