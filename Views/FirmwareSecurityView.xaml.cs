using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace XinSpect;

/// <summary>韌體安全頁：晶片組安全暫存器（BIOS_CNTL/SMRAMC/HFS）、SPI 快閃、ACPI 錯誤表的三態呈現，與深層核心存取開關。</summary>
public partial class FirmwareSecurityView : UserControl
{
    public FirmwareSecurityView() => InitializeComponent();

    public static readonly DependencyProperty RowhammerBusyProperty = DependencyProperty.Register(
        nameof(RowhammerBusy), typeof(bool), typeof(FirmwareSecurityView), new PropertyMetadata(false));
    public bool RowhammerBusy
    {
        get => (bool)GetValue(RowhammerBusyProperty);
        private set => SetValue(RowhammerBusyProperty, value);
    }

    private void RowhammerConsent_IsEnabledChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        // 任一風險開關關閉或本次操作開始，立即清除勾選；重開開關不恢復同意。
        if (sender is CheckBox consent && !consent.IsEnabled) consent.IsChecked = false;
    }

    private bool TryBeginRowhammer(TextBlock resultText, out bool userConsent)
    {
        userConsent = FindName("RowhammerConsentCheckBox") is CheckBox { IsChecked: true };
        if (RowhammerBusy) return false;
        try { RowhammerProbeService.EnsureAdmission(userConsent); }
        catch (InvalidOperationException ex) { resultText.Text = "未執行：" + ex.Message; return false; }
        RowhammerBusy = true;
        // 僅把本次快照交給 worker；完成、失敗或關閉開關後都須重新勾選。
        if (FindName("RowhammerConsentCheckBox") is CheckBox consent) consent.IsChecked = false;
        return true;
    }

    private async void DeepAccessEnable_Click(object sender, RoutedEventArgs e)
    {
        // asInvoker（2026-11-07）之後：非提權啟動時引導「runas 自我提權代理」——
        // 一次 UAC 讓提權實例完成安裝（服務常駐），其後一般權限的主程序即可直讀。
        if (!new System.Security.Principal.WindowsPrincipal(
                System.Security.Principal.WindowsIdentity.GetCurrent())
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
        {
            await RunElevatedDeepAccessAsync();
            return;
        }
        await RunDeepAccessAsync(sut => sut.Enable());
    }

    /// <summary>以 runas 拉起提權代理（--enable-deep-access），輪詢結果檔並呈現；完成後重新掃描狀態。</summary>
    private async Task RunElevatedDeepAccessAsync()
    {
        if (DataContext is not MainViewModel vm) return;
        var resultPath = Path.Combine(Path.GetTempPath(), "XinSpect-deepaccess-result.txt");
        try { File.Delete(resultPath); } catch { }

        SetEvidenceBusy(true);
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = Environment.ProcessPath ?? string.Empty,
                Arguments = "--enable-deep-access",
                UseShellExecute = true,
                Verb = "runas",
            });
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // 1223 = ERROR_CANCELLED：使用者按掉了 UAC——如實說，不算失敗
            Diag.Swallow("FirmwareSecurityView.UacCancelled", ex, "UAC 取消");
            SetEvidenceBusy(false);
            return;
        }

        // 最多等 90 秒（憑證產生＋簽章＋服務安裝要一會兒）；結果檔出現即回報
        string content = "";
        for (int i = 0; i < 90; i++)
        {
            await Task.Delay(1000);
            try { content = File.ReadAllText(resultPath); break; }
            catch { }
        }
        SetEvidenceBusy(false);

        var lines = content.Split('\n', 2);
        var ok = lines.Length > 0 && lines[0].Trim() == "0";
        var detail = lines.Length > 1 ? lines[1] : "逾時未收到提權實例的結果——可稍後按重新掃描更新狀態。";
        if (ok)
            await RunDeepAccessAsync(sut => sut.RefreshStatus());
        Diag.Swallow("FirmwareSecurityView.ElevatedResult", null,
            (ok ? "提權安裝完成" : "提權安裝未完成") + "：" + detail);
    }

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

    /// <summary>兩級風險開關＋逐次同意；GUI 先檢查，背景 public 入口再次檢查。</summary>
    private async void RowhammerRun_Click(object sender, RoutedEventArgs e)
    {
        if (FindName("RowhammerResultText") is not TextBlock resultText ||
            !TryBeginRowhammer(resultText, out bool userConsent)) return;
        resultText.Text = "探測執行中（高頻讀寫 256 MiB，可能數十秒）……";
        try
        {
            var result = await Task.Run(() => RowhammerProbeService.RunConsentedProbe(userConsent));
            resultText.Text = RowhammerProbeService.FormatResult(result);
        }
        catch (Exception ex) { resultText.Text = "探測未完成：" + ex.Message; }
        finally { RowhammerBusy = false; }
    }

    /// <summary>一次明確同意涵蓋本次 10 輪操作；各輪仍重核即時風險開關。</summary>
    private async void RowhammerMulti_Click(object sender, RoutedEventArgs e)
    {
        if (FindName("RowhammerResultText") is not TextBlock resultText ||
            !TryBeginRowhammer(resultText, out bool userConsent)) return;
        resultText.Text = "多輪測試執行中（10 輪 × 256 MiB，可能數分鐘）……";
        try
        {
            var result = await Task.Run(() => RowhammerProbeService.RunMultiRound(userConsent, rounds: 10));
            resultText.Text = RowhammerProbeService.FormatMultiRound(result);
        }
        catch (Exception ex) { resultText.Text = "多輪測試未完成：" + ex.Message; }
        finally { RowhammerBusy = false; }
    }

    /// <summary>WP27 PMU 編程驗證：先呈現原生通路支援狀態；未驗證平台不啟動交易。</summary>
    private async void PmuVerify_Click(object sender, RoutedEventArgs e)
    {
        if (FindName("PmuConsentCheckBox") is not CheckBox consent ||
            FindName("PmuVerifyButton") is not Button run ||
            FindName("PmuResultText") is not TextBlock resultText) return;
        if (consent.IsChecked != true) return;

        var msr = new WinRing0PmuMsrAccess();
        if (!msr.Available)
        {
            resultText.Text = msr.UnavailableReason ?? "PMU 原生編程不可用。";
            return;
        }
        run.IsEnabled = false;
        consent.IsEnabled = false;
        resultText.Text = "編程驗證執行中（3 輪：啟用→工作量→還原）……";
        try
        {
            var rounds = await Task.Run(() => PmuProgrammingService.RunConsentedVerification(
                userConsent: true, msr: msr, rounds: 3));
            int passed = rounds.Count(r => r.EnableReadbackOk && r.CounterActive && r.CleanupOk);
            resultText.Text = PmuProgrammingService.FormatNotice + Environment.NewLine +
                $"通過 {passed}/{rounds.Count} 輪：" + string.Join("；", rounds.Select((r, i) =>
                    $"第 {i + 1} 輪啟用{(r.EnableReadbackOk ? "✓" : "✗")}計數{(r.CounterActive ? "✓" : "✗")}還原{(r.CleanupOk ? "✓" : "✗")}" +
                    $"（0x309：{r.CounterStart}→{r.CounterEnd}）"));
        }
        catch (Exception ex) { resultText.Text = "編程驗證失敗：" + ex.Message; }
        finally { run.IsEnabled = true; consent.IsEnabled = true; }
    }

    private void SetEvidenceBusy(bool busy)
    {
        if (FindName("RefreshEvidenceButton") is Button b) b.IsEnabled = !busy;
        if (FindName("CompareFlashButton") is Button cmp) cmp.IsEnabled = !busy;
        if (FindName("DeepAccessEnableButton") is Button en) en.IsEnabled = !busy;
        if (FindName("DeepAccessDisableButton") is Button dis) dis.IsEnabled = !busy;
    }
}
