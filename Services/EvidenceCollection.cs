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
        finally { (mmio as IDisposable)?.Dispose(); }
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
