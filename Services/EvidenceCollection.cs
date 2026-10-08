namespace XinSpect;

/// <summary>
/// 驅動相依全組事實的<b>單一組合點</b>（V7 WP32）：啟動序列與 CLI 模式共用同一套後端組合——
/// WinRing0（PCI／MSR／I/O 埠／SMBus）＋MMIO 裁決鏈＋Win32 ACPI。改後端組合只改這裡，
/// 兩條入口不會各自漂移。組合本身是特權層黏合，照慣例不在單測覆蓋（測試注入假件打服務層）。
/// </summary>
public static class EvidenceCollection
{
    public static void ReloadInto(EvidenceLabService svc)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool failed = false;
        try
        {
            using var pci = new WinRing0PciConfigReader();
            using var msr = new WinRing0KernelMsrReader();
            using var io = new WinRing0IoPortAccess();
            using var smbusBridge = WinRing0Bridge.Create(); // SMBus 走 I/O 埠；與 readers 共用驅動會話（引用計數）
            ISmbusIo? smbusIo = smbusBridge.IoPortAvailable ? new WinRing0SmbusIo(smbusBridge) : null;
            var mmio = MmioBackendSelector.Select(); // WinRing0 主力 → XsRegProbe 備援 → 帶原因三態
            try
            {
                svc.ReloadDriverBackedFacts(pci, msr, mmio, new Win32AcpiTableSource(), io, smbusIo);
            }
            catch { failed = true; throw; } // 例外上拋給閘門吞噬；遙測記一次
            finally { (mmio as IDisposable)?.Dispose(); }
        }
        finally
        {
            // 自家可觀測性（V7 WP43／A54）：只記耗時與三態/例外計數——預設關閉、匿名、不上傳
            sw.Stop();
            try
            {
                var all = svc.AllFacts;
                SelfTelemetry.Session.RecordScan(sw.Elapsed, all.Count,
                    all.Count(f => f.Availability != FactAvailability.Present), failed ? 1 : 0);
            }
            catch { /* 遙測統計失敗不影響主流程 */ }
        }
    }

    /// <summary>
    /// usermode（不需驅動）事實的<b>單一組合點</b>：目前是儲存可靠性計數器（WMI）。
    /// </summary>
    /// <remarks>
    /// <b>為什麼要另外開一個入口，而不是塞進 <see cref="ReloadInto"/>：</b>
    /// <see cref="ReloadInto"/> 是驅動後端的組合點，而它在啟動路徑上被
    /// <c>DriverEvidenceGate</c> 擋著（已有載入在跑就整批跳過）。把一條**不需要驅動**
    /// 的來源掛進去，等於讓它的有無取決於一個跟它無關的閘門。
    /// <para>
    /// <b>為什麼要有這個入口：</b>這一組事實原本接進了 <c>AllFacts</c> 與報告匯出，
    /// 卻<b>沒有任何地方呼叫載入</b>——所以在真實的 App 與 CLI 裡它永遠是空的，
    /// 而服務層的單元測試（注入假來源）全綠。單元測試測得到服務，測不到「有沒有人接線」；
    /// 把接線本身做成一個有名字的入口，就是為了讓兩條入口（UI／CLI）共用它、不再各自漂移。
    /// </para>
    /// </remarks>
    public static void LoadUsermodeFacts(EvidenceLabService svc, IStorageReliabilitySource? storageReliability = null)
    {
        try { svc.LoadStorageReliability(storageReliability ?? new StorageReliabilityWmiSource()); }
        catch { /* 附加功能：讀不到由 StorageReliabilityFactsService 標三態，不在這裡中斷啟動 */ }
        try { svc.LoadDriverInspection(); }
        catch { /* 附加功能：讀不到由 DriverInspectionFactsService 標三態，不在這裡中斷啟動 */ }
        try { svc.LoadSetupTimeline(); }
        catch { /* 附加功能：讀不到由 SetupApiTimelineService 標三態，不在這裡中斷啟動 */ }
        try { svc.LoadEtlReadback(); }
        catch { /* 附加功能：讀不到由 EtlReadbackService 標三態，不在這裡中斷啟動 */ }
    }

    /// <summary>BIOS 區 vs 參考映像的比對（UI 與 CLI 共用入口）：組合後端後跑一次比對，回結果事實。</summary>
    public static HardwareFact CompareFlashWithReference(byte[] reference)
    {
        using var pci = new WinRing0PciConfigReader();
        var mmio = MmioBackendSelector.Select();
        try
        {
            return SpiFlashCompareService.Compare(pci, mmio, reference, DateTimeOffset.UtcNow);
        }
        finally { (mmio as IDisposable)?.Dispose(); }
    }
}
