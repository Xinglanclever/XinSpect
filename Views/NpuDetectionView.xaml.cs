using System.Windows;
using System.Windows.Controls;

namespace XinSpect;

/// <summary>NPU 檢測頁：用 MainViewModel 上那一份 NpuDetectionService。</summary>
/// <remarks>
/// 不可自持一份服務：算力圖（ComputeChartService）讀的是 MainViewModel.NpuDetection，
/// 兩份實例會讓「這頁掃到 NPU、算力圖卻永遠沒有 NPU 長條」。
/// </remarks>
public partial class NpuDetectionView : UserControl
{
    private bool _loaded;

    public NpuDetectionView()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (_loaded) return;
            if (Vm is not { } vm) return;   // 拿不到主檢視模型時不設旗標，下次再試
            _loaded = true;
            await DoRefreshAsync(vm);
        };
    }

    private async Task DoRefreshAsync(MainViewModel vm)
    {
        var svc = vm.NpuDetection;
        await svc.RefreshAsync(vm.Cpu?.Name);

        StatusText.Text = LanguageService.T(svc.Status);

        if (!svc.NpuPresent)
        {
            EmptyCard.Visibility = Visibility.Visible;
            InfoCard.Visibility = Visibility.Collapsed;
            EmptyText.Text = LanguageService.T(svc.Status);
            return;
        }

        EmptyCard.Visibility = Visibility.Collapsed;
        InfoCard.Visibility = Visibility.Visible;

        NameText.Text = svc.NpuName;
        DriverRow.Value = svc.NpuDriver.Length > 0 ? svc.NpuDriver : "—";
        DriverDateRow.Value = svc.NpuDriverDate.Length > 0 ? svc.NpuDriverDate : "—";
        TopsRow.Value = svc.EstimatedTops.Length > 0 ? svc.EstimatedTops : "—";
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) await DoRefreshAsync(vm);
    }

    private MainViewModel? Vm => DataContext as MainViewModel ?? Shell.Vm;
    private async void Bench_Click(object sender, RoutedEventArgs e)
    {
        BenchButton.IsEnabled = false;
        try
        {
            var r = await Task.Run(() => NpuComputeBenchService.Run());
            BenchGflopsRow.Value = r.Gflops.ToString("F1");
            BenchThreadsRow.Value = $"{r.Threads}";
            BenchVectorRow.Value = $"{r.VectorWidth} float";
            BenchTimeRow.Value = $"{r.ElapsedMs:F0} ms";
        }
        catch (Exception ex) { BenchGflopsRow.Value = ex.Message; }
        finally { BenchButton.IsEnabled = true; }
    }
}