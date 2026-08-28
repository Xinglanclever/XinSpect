using System.Windows;
using System.Windows.Controls;

namespace XinSpect;

/// <summary>垃圾清理分頁：自持 CleanupService，載入時於背景掃描各分類大小，清理採二次確認。</summary>
public partial class CleanupView : UserControl
{
    private readonly CleanupService _svc = new();
    private bool _loaded;
    private bool _busy;

    public CleanupView()
    {
        InitializeComponent();
        List.ItemsSource = _svc.Categories;
        Loaded += (_, _) => { if (!_loaded) { _loaded = true; _ = ScanAsync(); } };
    }

    private async Task ScanAsync()
    {
        if (_busy) return;
        _busy = true;
        StatusText.Text = "掃描中……";
        await Task.Run(() => _svc.Scan());
        long total = _svc.Categories.Sum(c => c.Size < 0 ? 0 : c.Size);
        TotalText.Text = $"可清理總計約 {Human(total)}";
        StatusText.Text = $"掃描完成 ・ {DateTime.Now:HH:mm:ss}";
        _busy = false;
    }

    private async void Scan_Click(object sender, RoutedEventArgs e) => await ScanAsync();

    private async void Clean_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (!_svc.Categories.Any(c => c.Selected))
        {
            MessageBox.Show("請先勾選要清理的項目。", "垃圾清理", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var ok = MessageBox.Show(
            "確定要清理所選項目？被刪除的暫存檔無法還原（資源回收筒的內容將永久清空）。",
            "垃圾清理", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (ok != MessageBoxResult.OK) return;

        _busy = true;
        StatusText.Text = "清理中……";
        var (_, report) = await Task.Run(() => _svc.Clean());
        long total = _svc.Categories.Sum(c => c.Size < 0 ? 0 : c.Size);
        TotalText.Text = $"可清理總計約 {Human(total)}";
        StatusText.Text = report.Replace("\n", " ");
        _busy = false;
        MessageBox.Show(report, "垃圾清理", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static string Human(long b) =>
        b >= 1L << 30 ? $"{b / 1024.0 / 1024 / 1024:0.00} GB"
        : b >= 1L << 20 ? $"{b / 1024.0 / 1024:0.0} MB"
        : b >= 1L << 10 ? $"{b / 1024.0:0} KB" : $"{b} B";
}
