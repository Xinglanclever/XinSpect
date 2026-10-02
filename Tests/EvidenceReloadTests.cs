using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 事實重載（深層存取啟用後免重啟翻真值）的契約：ReloadDriverBackedFacts 以注入假件驗證——
/// 五組驅動相依事實整批「替換」而非附加（差分按來源鍵 ToDictionary，鍵重複直接炸）、
/// 讀得到時從三態翻成真值、可重複呼叫不累積。StartupSequence 的真實後端黏合層不在單測範圍（測試不碰核心）。
/// </summary>
public sealed class EvidenceReloadTests
{
    [Fact]
    public void 重載驅動相依事實_整批替換_鍵不重複且讀得到者翻真值()
    {
        var svc = new EvidenceLabService();
        svc.LoadChipsetSecurity(new DeniedPci());
        svc.LoadSpiFlash(new DeniedPci(), new DeniedMmio());
        svc.LoadPlatformSecurity(new DeniedMsr());
        svc.LoadMchbar(new DeniedPci(), new DeniedMmio());
        svc.LoadPcieAer(new DeniedMmio(), new EmptyAcpi());

        svc.ReloadDriverBackedFacts(new ReadingPci(), new DeniedMsr(), new DeniedMmio(), new EmptyAcpi(), new DeniedIoPort());

        var names = svc.FirmwareSecurityRows.Select(r => r.Name).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
        var bios = Assert.Single(svc.FirmwareSecurityRows, r => r.Name == "BIOS 寫入保護");
        Assert.False(bios.IsUnavailable);
        Assert.StartsWith("最強保護", bios.ValueText);
    }

    [Fact]
    public void 重載可重複呼叫_列數不累積()
    {
        var svc = new EvidenceLabService();
        svc.LoadChipsetSecurity(new DeniedPci());

        svc.ReloadDriverBackedFacts(new ReadingPci(), new DeniedMsr(), new DeniedMmio(), new EmptyAcpi(), new DeniedIoPort());
        int afterFirst = svc.FirmwareSecurityRows.Count;
        svc.ReloadDriverBackedFacts(new ReadingPci(), new DeniedMsr(), new DeniedMmio(), new EmptyAcpi(), new DeniedIoPort());

        Assert.Equal(afterFirst, svc.FirmwareSecurityRows.Count);
    }

    [Fact]
    public void 重載不動ACPI表清單_該來源另由LoadAcpi管理()
    {
        var svc = new EvidenceLabService();
        svc.LoadAcpi(new EmptyAcpi());
        var acpiBefore = svc.AcpiFacts;

        svc.ReloadDriverBackedFacts(new ReadingPci(), new DeniedMsr(), new DeniedMmio(), new EmptyAcpi(), new DeniedIoPort());

        Assert.Same(acpiBefore, svc.AcpiFacts);
    }

    // ===== 假件：先全三態、後可讀，模擬「啟用深層存取前後」 =====

    private sealed class DeniedPci : IPciConfigReader
    {
        public bool Available => false;
        public string? UnavailableReason => "WinRing0 未載入（測試假件）";
        public uint? ReadDword(byte bus, byte device, byte function, uint register) => null;
    }

    private sealed class DeniedMmio : IMmioReader
    {
        public bool Available => false;
        public string? UnavailableReason => "自家驅動未載入（測試假件）";
        public byte[]? ReadBlock(ulong physicalAddress, int length) => null;
    }

    private sealed class DeniedMsr : IKernelMsrReader
    {
        public bool Available => false;
        public string? UnavailableReason => "缺 ring0（測試假件）";
        public ulong? ReadMsr(uint index) => null;
    }

    private sealed class EmptyAcpi : IAcpiTableSource
    {
        public bool Available => false;
        public string? UnavailableReason => "列舉失敗（測試假件）";
        public IReadOnlyList<byte[]> ReadAll() => [];
    }

    private sealed class DeniedIoPort : IIoPortAccess
    {
        public bool Available => false;
        public string? UnavailableReason => "缺 I/O 埠存取（測試假件）";
        public byte? InByte(uint port) => null;
        public bool OutByte(uint port, byte value) => false;
    }

    /// <summary>可讀 PCI：BIOS_CNTL=SMM_BWP+BLE、SMRAMC=D_LCK、0:16.0 給 Intel HECI vendor。</summary>
    private sealed class ReadingPci : IPciConfigReader
    {
        public bool Available => true;
        public string? UnavailableReason => null;
        public uint? ReadDword(byte bus, byte device, byte function, uint register) => (bus, device, function, register) switch
        {
            (0, 31, 0, 0xDC) => 0x22,          // SMM_BWP=1、BLE=1 →「最強保護」
            (0, 0, 0, 0x88) => 0x10,           // D_LCK=1
            (0, 22, 0, 0x00) => 0x0001_8086,   // HECI vendor=Intel
            (0, 22, 0, 0x40) => 0x0000_0900,   // HFSTS1：working_state=0、fw_init
            _ => 0xFFFF_FFFF,
        };
    }
}
