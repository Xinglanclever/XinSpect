using System.Windows;
using System.Windows.Controls;

namespace XinSpect;

/// <summary>韌體安全頁：晶片組安全暫存器（BIOS_CNTL/SMRAMC/HFS）、SPI 快閃、ACPI 錯誤表的三態呈現，與深層核心存取開關。</summary>
public partial class FirmwareSecurityView : UserControl
{
    public FirmwareSecurityView() => InitializeComponent();

    private async void DeepAccessEnable_Click(object sender, RoutedEventArgs e) => await RunDeepAccessAsync(sut => sut.Enable());

    private async void DeepAccessDisable_Click(object sender, RoutedEventArgs e) => await RunDeepAccessAsync(sut => sut.Disable());

    /// <summary>重新擷取：不動開關，直接以當下後端重載驅動相依五組事實（啟用後免重啟翻真值；也可手動再取一次）。</summary>
    private async void RefreshEvidence_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        SetEvidenceBusy(true);
        try { await Task.Run(() => StartupSequence.LoadDriverBackedEvidence(vm)); }
        finally { SetEvidenceBusy(false); }
    }

    /// <summary>
    /// 與參考映像比對：使用者選原廠／信任來源的 BIOS 區映像，與快閃可讀面逐塊比對。
    /// 讀檔與比對（秒級 MMIO）在背景跑；結果事實收進證據列（重載驅動相依事實時會如實清空）。
    /// </summary>
    private async void CompareFlash_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "選擇參考映像（BIOS 區）",
            Filter = "映像檔 (*.bin;*.rom;*.img)|*.bin;*.rom;*.img|所有檔案 (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() != true) return;

        SetEvidenceBusy(true);
        try
        {
            var fact = await Task.Run(() =>
                EvidenceCollection.CompareFlashWithReference(System.IO.File.ReadAllBytes(dlg.FileName)));
            vm.EvidenceLab.AddSpiCompareFact(fact);
        }
        catch (Exception ex)
        {
            vm.EvidenceLab.AddSpiCompareFact(new HardwareFact("spi.bios_compare", "韌體安全",
                "BIOS 區比對（vs 參考映像）", "", "", "記憶體映射快閃 vs 使用者提供映像",
                FactTrustLevel.Unknown, false, DateTimeOffset.UtcNow, null,
                FactAvailability.ReadError, $"比對流程失敗：{ex.Message}"));
        }
        finally { SetEvidenceBusy(false); }
    }

    // 憑證產生（RSA 4096）與 SCM 呼叫是秒級動作，放背景跑避免凍結 UI；回報文字經繫結自動更新。
    // 啟用與停用都改變驅動狀態，完成後一律重載：啟用→三態翻真值免重啟；停用→讀不到就如實回到三態，不留舊值冒充。
    private async Task RunDeepAccessAsync(Func<DeepAccessService, DeepAccessStatus> action)
    {
        if (DataContext is not MainViewModel vm) return;
        var sut = vm.EvidenceLab.DeepAccess;
        SetEvidenceBusy(true);
        try
        {
            var status = await Task.Run(() => action(sut));
            // 狀態文字已由 Enable/Disable 組裝完成（程序說明＋目前狀態，各一次）——這裡不再附加 notes
            await Task.Run(() => StartupSequence.LoadDriverBackedEvidence(vm));
        }
        finally { SetEvidenceBusy(false); }
    }

    private void SetEvidenceBusy(bool busy)
    {
        if (FindName("RefreshEvidenceButton") is Button b) b.IsEnabled = !busy;
        if (FindName("CompareFlashButton") is Button cmp) cmp.IsEnabled = !busy;
        if (FindName("DeepAccessEnableButton") is Button en) en.IsEnabled = !busy;
        if (FindName("DeepAccessDisableButton") is Button dis) dis.IsEnabled = !busy;
    }
}
