using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;

namespace XinSpect;

public partial class EvidenceLabView : UserControl
{
    public EvidenceLabView() => InitializeComponent();

    internal async Task RunSmokeAsync()
    {
        if (Vm is not { } vm) throw new InvalidOperationException("找不到主檢視模型。");
        for (int i = 0; i < vm.HardwareEvidence.Sections.Count; i++)
        {
            vm.HardwareEvidence.SelectedSection = i;
            await vm.HardwareEvidence.RefreshAsync();
        }
    }

    private MainViewModel? Vm => DataContext as MainViewModel ?? Shell.Vm;

    private async void SaveSnapshot_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var dlg = new SaveFileDialog
        {
            Title = "建立硬體時間膠囊",
            Filter = "XinSpect 時間膠囊 (*.xinsnapshot)|*.xinsnapshot",
            FileName = $"XinSpect_時間膠囊_{DateTime.Now:yyyyMMdd_HHmmss}.xinsnapshot",
            AddExtension = true,
        };
        if (dlg.ShowDialog() != true) return;
        await vm.EvidenceLab.SaveAsync(vm, dlg.FileName, IncludeSensitive.IsChecked == true);
    }

    private async void CompareSnapshot_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var path = PickSnapshot("選擇要和目前硬體比較的時間膠囊");
        if (path is not null) await vm.EvidenceLab.CompareAsync(vm, path);
    }

    private async void OpenSnapshot_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var path = PickSnapshot("開啟硬體時間膠囊");
        if (path is not null) await vm.EvidenceLab.InspectAsync(path);
    }

    private async void RawSave_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var dlg = new SaveFileDialog
        {
            Title = "建立原始暫存器快照",
            Filter = "XinSpect 原始快照 (*.xinraw)|*.xinraw",
            FileName = $"XinSpect_原始快照_{DateTime.Now:yyyyMMdd_HHmmss}.xinraw",
            AddExtension = true,
        };
        if (dlg.ShowDialog() != true) return;
        await vm.EvidenceLab.SaveRawSnapshotAsync(dlg.FileName);
    }

    private async void RawCompare_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var path = PickRaw("選擇要和目前硬體差分的原始快照");
        if (path is not null) await vm.EvidenceLab.CompareRawAsync(path);
    }

    private async void RawOpen_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var path = PickRaw("開啟原始暫存器快照");
        if (path is not null) await vm.EvidenceLab.InspectRawSnapshotAsync(path);
    }

    private static string? PickRaw(string title)
    {
        var dlg = new OpenFileDialog
        {
            Title = title,
            Filter = "XinSpect 原始快照 (*.xinraw)|*.xinraw|所有檔案 (*.*)|*.*",
            CheckFileExists = true,
        };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    private async void RefreshEvidence_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) await vm.HardwareEvidence.RefreshAsync();
    }

    // 匯出驗機報告:把最近一次「驗機對帳」的結論輸出成純文字單子,供二手交易存證。
    // 還沒跑過驗機對帳時 BuildVerdictReport() 回 null——如實提示,不產生一份空報告。
    private void ExportVerdict_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var report = vm.HardwareEvidence.BuildVerdictReport();
        if (report is null)
        {
            XMsg.Show("還沒有驗機結果。請先把上面的下拉切到「驗機對帳」並按「重新擷取」,再匯出報告。",
                "匯出驗機報告", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dlg = new SaveFileDialog
        {
            Title = "匯出驗機報告",
            Filter = "純文字報告 (*.txt)|*.txt",
            FileName = $"XinSpect_驗機報告_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
            AddExtension = true,
        };
        if (dlg.ShowDialog() != true) return;
        AtomicWrite.AllText(dlg.FileName, report);
    }

    private static string? PickSnapshot(string title)
    {
        var dlg = new OpenFileDialog
        {
            Title = title,
            Filter = "XinSpect 時間膠囊 (*.xinsnapshot)|*.xinsnapshot|所有檔案 (*.*)|*.*",
            CheckFileExists = true,
        };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    // 匯入外部報告：讓使用者選 GPU-Z txt / AIDA64 XML / HWiNFO CSV，
    // 解析後列出讀值。純讀檔，不啟動任何子行程。
    private void ImportReport_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "匯入外部工具的感測器報告",
            Filter = "外部報告 (*.txt;*.xml;*.csv)|*.txt;*.xml;*.csv|所有檔案 (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var result = ExternalReportService.ParseFile(dlg.FileName);
            ExternalReportStatus.Text = $"{result.FileName}（{result.Format}）・{result.Status}";
            ExternalReportRows.ItemsSource = result.Rows.Select(row => new
            {
                row.Tool, row.Field, row.Value, row.Unit,
                MeasuredAtText = row.MeasuredAt?.ToString("MM/dd HH:mm:ss") ?? "",
            }).ToList();
        }
        catch (Exception ex)
        {
            ExternalReportStatus.Text = $"讀取失敗：{ex.Message}";
        }
    }
}