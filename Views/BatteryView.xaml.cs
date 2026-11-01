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

    private void Read() => _ = Task.Run(() =>
    {
        // BatteryService 讀的是 root\wmi 的電池類別。整機 WMI 卡住時這條查詢不會回來，
        // 而這裡原本直接在 UI 執行緒上跑（進頁 Loaded 與「重新讀取」都是）——表現就是「點開電池頁，
        // 整個視窗凍住」。讀丢背景並加上界，回到 UI 才碰控制項。
        var (info, timedOut) = WmiGuard.Probe(() => _svc.Read());
        Shell.BeginOnUi(() => Apply(info, timedOut));
    });

    void Apply(BatteryInfo? info, bool timedOut)
    {
        if (info is null || !info.Present)
        {
            EmptyCard.Visibility = Visibility.Visible;
            InfoCard.Visibility = Visibility.Collapsed;
            EmptyText.Text = info is null
                ? (timedOut ? "WMI 無回應（root\\wmi 的電池查詢逾時）——稍後再按「重新讀取」。"
                            : "讀取失敗：系統沒有回報電池資訊。")
                : info.Message;
            _wearPercent = -1;
            UpdateWearBar();
            return;
        }
        EmptyCard.Visibility = Visibility.Collapsed;
        InfoCard.Visibility = Visibility.Visible;

        var b = info;

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

    private async void Report_Click(object sender, RoutedEventArgs e)
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
            if (p is not null)
            {
                await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
            }

            if (File.Exists(outPath))
                Process.Start(new ProcessStartInfo(outPath) { UseShellExecute = true });
            else
                XMsg.Show("報告產生失敗：此環境可能沒有電池，或 powercfg 無法產生報告。",
                    "電池分析", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            XMsg.Show("產生電池報告失敗：" + ex.Message, "電池分析",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
