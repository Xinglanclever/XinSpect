using System.Windows;
using System.Windows.Controls;

namespace XinSpect;

public partial class BenchView : UserControl
{
    public BenchView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            UpdateDurationButtons();
            UpdateChessDurationButtons();
            UpdateStressDurationButtons();
            UpdatePiDigitButtons();
            if (Chess is not null) ChessTAll.Content = $"全部核心（{Chess.LogicalCores}）";
        };
    }

    private MainViewModel? Vm => DataContext as MainViewModel;
    private BenchService? Bench => Vm?.Bench;
    private ChessBenchService? Chess => Vm?.Chess;
    private WinsatService? Winsat => Vm?.Winsat;
    private StressTestService? Stress => Vm?.Stress;
    private CacheBenchService? Cache => Vm?.Cache;
    private SuperPiService? SuperPi => Vm?.SuperPi;
    private DiskBenchService? DiskBench => Vm?.DiskBench;

    // ===== 綜合效能測試 =====
    private void Dur10_Click(object sender, RoutedEventArgs e) => SetDuration(10);
    private void Dur30_Click(object sender, RoutedEventArgs e) => SetDuration(30);
    private void Dur60_Click(object sender, RoutedEventArgs e) => SetDuration(60);

    private void SetDuration(int seconds)
    {
        if (Bench is { IsRunning: false } b) { b.DurationSeconds = seconds; UpdateDurationButtons(); }
    }

    private void UpdateDurationButtons()
    {
        int d = Bench?.DurationSeconds ?? 30;
        Dur10.Content = d == 10 ? "✓ 10 秒" : "10 秒";
        Dur30.Content = d == 30 ? "✓ 30 秒" : "30 秒";
        Dur60.Content = d == 60 ? "✓ 60 秒" : "60 秒";
    }

    private void Start_Click(object sender, RoutedEventArgs e) => Bench?.Start();
    private void Stop_Click(object sender, RoutedEventArgs e) => Bench?.Cancel();

    // ===== 象棋跑分 =====
    private void ChessT1_Click(object sender, RoutedEventArgs e) => SetChessThreads(1);
    private void ChessTAll_Click(object sender, RoutedEventArgs e) => SetChessThreads(Chess?.LogicalCores ?? 1);
    private void ChessT64_Click(object sender, RoutedEventArgs e) => SetChessThreads(64);
    private void ChessT128_Click(object sender, RoutedEventArgs e) => SetChessThreads(128);
    private void ChessT256_Click(object sender, RoutedEventArgs e) => SetChessThreads(256);

    private void SetChessThreads(int t)
    {
        if (Chess is { IsRunning: false } c) c.SetThreads(t);   // 繫結會自動反映到自訂輸入框
    }

    private void ChessD5_Click(object sender, RoutedEventArgs e) => SetChessDuration(5);
    private void ChessD10_Click(object sender, RoutedEventArgs e) => SetChessDuration(10);
    private void ChessD30_Click(object sender, RoutedEventArgs e) => SetChessDuration(30);

    private void SetChessDuration(int seconds)
    {
        if (Chess is { IsRunning: false } c) { c.SetDuration(seconds); UpdateChessDurationButtons(); }
    }

    private void UpdateChessDurationButtons()
    {
        int d = Chess?.DurationSeconds ?? 10;
        ChessD5.Content = d == 5 ? "✓ 5 秒" : "5 秒";
        ChessD10.Content = d == 10 ? "✓ 10 秒" : "10 秒";
        ChessD30.Content = d == 30 ? "✓ 30 秒" : "30 秒";
    }

    private void ChessStart_Click(object sender, RoutedEventArgs e) => Chess?.Start();
    private void ChessStop_Click(object sender, RoutedEventArgs e) => Chess?.Cancel();
    private void Fritz_Click(object sender, RoutedEventArgs e) => Chess?.LaunchOriginalFritz();

    // ===== WinSAT =====
    private async void Winsat_Click(object sender, RoutedEventArgs e)
    {
        if (Winsat is { CanRun: true } w) await w.RunFormalAsync();
    }

    // ===== 烤機（穩定度壓力測試） =====
    private void StressD60_Click(object sender, RoutedEventArgs e) => SetStressDuration(60);
    private void StressD300_Click(object sender, RoutedEventArgs e) => SetStressDuration(300);
    private void StressD600_Click(object sender, RoutedEventArgs e) => SetStressDuration(600);
    private void StressDInf_Click(object sender, RoutedEventArgs e) => SetStressDuration(0);

    private void SetStressDuration(int seconds)
    {
        if (Stress is { IsRunning: false } s) { s.SetDuration(seconds); UpdateStressDurationButtons(); }
    }

    private void UpdateStressDurationButtons()
    {
        int d = Stress?.DurationSeconds ?? 300;
        StressD60.Content = d == 60 ? "✓ 1 分鐘" : "1 分鐘";
        StressD300.Content = d == 300 ? "✓ 5 分鐘" : "5 分鐘";
        StressD600.Content = d == 600 ? "✓ 10 分鐘" : "10 分鐘";
        StressDInf.Content = d <= 0 ? "✓ 持續" : "持續";
    }

    private void StressStart_Click(object sender, RoutedEventArgs e) => Stress?.Start();
    private void StressStop_Click(object sender, RoutedEventArgs e) => Stress?.Cancel();

    // ===== 快取 / 記憶體延遲 =====
    private void CacheStart_Click(object sender, RoutedEventArgs e) => Cache?.Start();
    private void CacheStop_Click(object sender, RoutedEventArgs e) => Cache?.Cancel();

    // ===== SuperPI =====
    // 六檔位：10萬 / 50萬 / 100萬 / 1000萬 / 5000萬 / 1億
    private void PiD1_Click(object sender, RoutedEventArgs e) => SetPiDigits(100_000);
    private void PiD2_Click(object sender, RoutedEventArgs e) => SetPiDigits(500_000);
    private void PiD3_Click(object sender, RoutedEventArgs e) => SetPiDigits(1_000_000);
    private void PiD4_Click(object sender, RoutedEventArgs e) => SetPiDigits(10_000_000);
    private void PiD5_Click(object sender, RoutedEventArgs e) => SetPiDigits(50_000_000);
    private void PiD6_Click(object sender, RoutedEventArgs e) => SetPiDigits(100_000_000);

    private void SetPiDigits(int digits)
    {
        if (SuperPi is { IsRunning: false } p) { p.SetDigits(digits); UpdatePiDigitButtons(); }
    }

    private void UpdatePiDigitButtons()
    {
        int d = SuperPi?.Digits ?? 100_000;
        PiD1.Content = d == 100_000 ? "✓ 10 萬位" : "10 萬位";
        PiD2.Content = d == 500_000 ? "✓ 50 萬位" : "50 萬位";
        PiD3.Content = d == 1_000_000 ? "✓ 100 萬位" : "100 萬位";
        PiD4.Content = d == 10_000_000 ? "✓ 1000 萬位" : "1000 萬位";
        PiD5.Content = d == 50_000_000 ? "✓ 5000 萬位" : "5000 萬位";
        PiD6.Content = d == 100_000_000 ? "✓ 1 億位" : "1 億位";
    }

    private void PiStart_Click(object sender, RoutedEventArgs e)
    {
        if (SuperPi is not { IsRunning: false } p) return;

        // 5000萬／1億位極耗時且佔用大量記憶體，開始前先行確認。
        if (p.Digits >= 50_000_000)
        {
            string tier = p.Digits >= 100_000_000 ? "1 億" : "5000 萬";
            var r = System.Windows.MessageBox.Show(
                $"即將計算圓周率至 {p.Digits:#,0} 位（{tier}位）。\n\n" +
                "此檔位極為耗時（視處理器效能，可能需數分鐘至數小時），且運算過程會佔用大量記憶體。\n" +
                "計算期間可隨時按「停止」中止。確定要開始嗎？",
                "SuperPI ・ 高負載確認",
                System.Windows.MessageBoxButton.OKCancel,
                System.Windows.MessageBoxImage.Warning);
            if (r != System.Windows.MessageBoxResult.OK) return;
        }
        p.Start();
    }

    private void PiStop_Click(object sender, RoutedEventArgs e) => SuperPi?.Cancel();

    // ===== 磁碟效能測試 =====
    private void DiskBenchStart_Click(object sender, RoutedEventArgs e) => DiskBench?.Start();
    private void DiskBenchStop_Click(object sender, RoutedEventArgs e) => DiskBench?.Cancel();
}
