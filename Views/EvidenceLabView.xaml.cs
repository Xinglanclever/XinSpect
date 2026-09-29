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
}
