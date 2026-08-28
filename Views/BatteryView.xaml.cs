using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace XinSpect;

/// <summary>電池分析分頁：自持 BatteryService，載入時讀取；可產生 powercfg 官方報告並開啟。</summary>
public partial class BatteryView : UserControl
{
    private readonly BatteryService _svc = new();
    private bool _loaded;

    public BatteryView()
    {
        InitializeComponent();
        Loaded += (_, _) => { if (!_loaded) { _loaded = true; Read(); } };
        SizeChanged += (_, _) => UpdateWearBar();
    }

    private double _wearPercent;

    private void Read()
    {
        var b = _svc.Read();
        if (!b.Present)
        {
            EmptyCard.Visibility = Visibility.Visible;
            InfoCard.Visibility = Visibility.Collapsed;
            EmptyText.Text = b.Message;
            return;
        }
        EmptyCard.Visibility = Visibility.Collapsed;
        InfoCard.Visibility = Visibility.Visible;

        NameText.Text = b.Name;
        ChargeRow.Value = b.ChargePercent > 0 ? $"{b.ChargePercent}%" : "—";
        StatusRow.Value = b.StatusText;
        ChemRow.Value = b.Chemistry;
        DesignRow.Value = b.DesignText;
        FullRow.Value = b.FullText;
        CycleRow.Value = b.CycleText;

        _wearPercent = b.WearText == "—" ? -1 : b.WearPercent;
        WearText.Text = b.WearText == "—"
            ? "此機型未提供設計／滿充容量，無法計算耗損率。"
            : $"耗損率 {b.WearText}（滿充容量已降至設計容量的 {(b.DesignCapacity > 0 ? 100.0 * b.FullCapacity / b.DesignCapacity : 0):0.0}%）";

        // 依耗損程度上色：<20% 綠、20–40% 橙、>40% 紅
        WearBar.Background = _wearPercent < 0 ? (System.Windows.Media.Brush)FindResource("Surface2Brush")
            : _wearPercent < 20 ? (System.Windows.Media.Brush)FindResource("GoodBrush")
            : _wearPercent < 40 ? (System.Windows.Media.Brush)FindResource("WarningBrush")
            : (System.Windows.Media.Brush)FindResource("CriticalBrush");
        UpdateWearBar();
    }

    private void UpdateWearBar()
    {
        if (_wearPercent < 0 || InfoCard.Visibility != Visibility.Visible) { WearBar.Width = 0; return; }
        double track = WearBar.Parent is FrameworkElement fe ? fe.ActualWidth : 0;
        WearBar.Width = Math.Max(0, track * Math.Min(100, _wearPercent) / 100.0);
    }

    private void Read_Click(object sender, RoutedEventArgs e) => Read();

    private void Report_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string outPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "XinSpect", "battery-report.html");
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);

            var psi = new ProcessStartInfo("powercfg", $"/batteryreport /output \"{outPath}\"")
            {
                CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true,
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(8000);

            if (File.Exists(outPath))
                Process.Start(new ProcessStartInfo(outPath) { UseShellExecute = true });
            else
                MessageBox.Show("報告產生失敗：此環境可能沒有電池，或 powercfg 無法產生報告。",
                    "電池分析", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("產生電池報告失敗：" + ex.Message, "電池分析",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
