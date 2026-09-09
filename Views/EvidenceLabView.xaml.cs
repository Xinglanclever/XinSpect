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
