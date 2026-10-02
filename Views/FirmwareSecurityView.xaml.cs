using System.Windows;
using System.Windows.Controls;

namespace XinSpect;

/// <summary>韌體安全頁：晶片組安全暫存器（BIOS_CNTL/SMRAMC/HFS）、SPI 快閃、ACPI 錯誤表的三態呈現，與深層核心存取開關。</summary>
public partial class FirmwareSecurityView : UserControl
{
    public FirmwareSecurityView() => InitializeComponent();

    private async void DeepAccessEnable_Click(object sender, RoutedEventArgs e) => await RunDeepAccessAsync(sut => sut.Enable());

    private async void DeepAccessDisable_Click(object sender, RoutedEventArgs e) => await RunDeepAccessAsync(sut => sut.Disable());

    // 憑證產生（RSA 4096）與 SCM 呼叫是秒級動作，放背景跑避免凍結 UI；回報文字經繫結自動更新。
    private async Task RunDeepAccessAsync(Func<DeepAccessService, DeepAccessStatus> action)
    {
        if (DataContext is not MainViewModel vm) return;
        var sut = vm.EvidenceLab.DeepAccess;
        var status = await Task.Run(() => action(sut));
        sut.AppendStatusNotes(status.Notes);
    }
}
