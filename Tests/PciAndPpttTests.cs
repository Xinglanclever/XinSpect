using Xunit;

namespace XinSpect.Tests;

/// <summary>Vol2 AC-017（PPTT）、SB-007..014（PCIe 能力鏈）、SV-011（Server Core）的測試：
/// 合成 4KB 組態空間與合成 ACPI 表驅動，不碰硬體。</summary>
public sealed class PciAndPpttTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 11, 0, 0, 0, TimeSpan.Zero);

    // ── SB-007 能力鏈走訪 ────────────────────────────────────────────────

    [Fact]
    public void 能力鏈_legacy鏈依狀態位元走訪並遮罩低兩位元()
    {
        var page = new byte[256];
        page[0x06] = 0x10;            // Status bit4：有 legacy 能力清單
        page[0x34] = 0x40;            // 第一個能力在 0x40（低兩位元為 0）
        page[0x40] = 0x05;            // MSI
        page[0x41] = 0x50;            // 下一個在 0x50
        page[0x50] = 0x10;            // PCI Express
        page[0x51] = 0x00;            // 結束

        var caps = PciCapabilities.Enumerate(page);

        Assert.Equal(2, caps.Count);
        Assert.Equal(0x05, caps[0].Id);
        Assert.Equal(0x10, caps[1].Id);
        Assert.All(caps, c => Assert.False(c.Extended));
    }

    [Fact]
    public void 能力鏈_無狀態位元時不看0x34()
    {
        var page = new byte[256];
        page[0x34] = 0x40;            // 沒有清單位元 → 這個位元組別有用途
        page[0x40] = 0x05;
        Assert.Empty(PciCapabilities.Enumerate(page));
    }

    [Fact]
    public void 能力鏈_擴充鏈自0x100起並依NextOffset跳躍()
    {
        var page = new byte[4096];
        WriteDword(page, 0x100, 0x0001 | (0x200u << 20));  // AER → next 0x200
        WriteDword(page, 0x200, 0x000Du);                  // ACS → next 0

        var ext = PciCapabilities.Enumerate(page).Where(c => c.Extended).ToList();

        Assert.Equal(2, ext.Count);
        Assert.Equal(0x01, ext[0].Id);
        Assert.Equal(0x0D, ext[1].Id);
        Assert.Equal(0x100, ext[0].Offset);
        Assert.Equal(0x200, ext[1].Offset);
    }

    [Fact]
    public void 能力鏈_防環()
    {
        var page = new byte[4096];
        WriteDword(page, 0x100, 0x0001 | (0x100u << 20));  // 自己指自己
        Assert.Single(PciCapabilities.Enumerate(page), c => c.Extended);
    }

    [Fact]
    public void 能力名稱_未收錄識別碼如實回原始碼()
    {
        Assert.Equal("0x7F", PciCapabilities.Name(0x7F, extended: false));
        Assert.Equal("0x1234", PciCapabilities.Name(0x1234, extended: true));
        Assert.Equal("MSI-X", PciCapabilities.Name(0x11, extended: false));
        Assert.Equal("SR-IOV", PciCapabilities.Name(0x10, extended: true));
    }

    [Fact]
    public void MSIX表大小_為N減一編碼()
    {
        var page = new byte[256];
        page[0x40] = 0x11;
        page[0x42] = 0xFF; page[0x43] = 0x00; // Table Size = 0x00FF → 256 條
        Assert.Equal(256, PciCapabilities.MsixTableSize(page, 0x40));
    }

    [Fact]
    public void SR_IOV_虛擬功能數()
    {
        var page = new byte[4096];
        WriteDword(page, 0x100, 0x0010u);
        WriteU16(page, 0x10C, 1); WriteU16(page, 0x10E, 8); WriteU16(page, 0x110, 4);
        WriteU16(page, 0x114, 2); WriteU16(page, 0x116, 1);

        var vf = PciCapabilities.Sriov(page, 0x100);

        Assert.NotNull(vf);
        Assert.Equal(1, vf!.Value.InitialVfs);
        Assert.Equal(8, vf.Value.TotalVfs);
        Assert.Equal(4, vf.Value.NumVfs);
        Assert.Equal(2, vf.Value.VfOffset);
    }

    [Fact]
    public void ACS_能力位元組合()
    {
        var page = new byte[4096];
        WriteDword(page, 0x100, 0x000Du);
        WriteU16(page, 0x104, 0x001D); // SV+RR+CR+UF
        Assert.Equal("SV+RR+CR+UF", PciCapabilities.AcsFlags(page, 0x100));
    }

    [Fact]
    public void L1SS_支援位元與原值()
    {
        var page = new byte[4096];
        WriteDword(page, 0x100, 0x001Eu);
        WriteDword(page, 0x104, 0x0000001F);
        var l1 = PciCapabilities.L1Pm(page, 0x100);
        Assert.NotNull(l1);
        Assert.Equal(0x1Fu, l1!.Value.Capabilities);
    }

    [Fact]
    public void AER完整欄位_六個暫存器()
    {
        var page = new byte[4096];
        WriteDword(page, 0x100, 0x0001u);
        WriteDword(page, 0x104, 0x0000_0010); // Uncorrectable Status
        WriteDword(page, 0x108, 0x0000_00FF); // Mask
        WriteDword(page, 0x10C, 0x0000_0004); // Severity
        WriteDword(page, 0x110, 0x0000_0001); // Correctable Status
        WriteDword(page, 0x114, 0x0000_2000); // Correctable Mask

        var aer = PciCapabilities.AerFullDecode(page, 0x100);

        Assert.NotNull(aer);
        Assert.Equal(0x10u, aer!.Value.UncorrectableStatus);
        Assert.Equal(0xFFu, aer.Value.UncorrectableMask);
        Assert.Equal(4u, aer.Value.UncorrectableSeverity);
        Assert.Equal(1u, aer.Value.CorrectableStatus);
    }

    [Fact]
    public void 越界的位移一律回null_不硬讀()
    {
        var shortPage = new byte[0x50];
        Assert.Null(PciCapabilities.Sriov(shortPage, 0x100));
        Assert.Null(PciCapabilities.AerFullDecode(shortPage, 0x100));
        Assert.Null(PciCapabilities.L1Pm(shortPage, 0x100));
        Assert.Null(PciCapabilities.VirtualChannel(shortPage, 0x100));
        Assert.Null(PciCapabilities.Dpc(shortPage, 0x100));
        Assert.Equal("", PciCapabilities.AcsFlags(shortPage, 0x100));
    }

    // ── SB-007..014 服務層（注入頁面讀取器） ───────────────────────────

    [Fact]
    public void 服務_能力摘要與各專項摘要_由合成頁面產出()
    {
        var page = BuildDevicePage(withSriov: true, withAcs: true, withAer: true);
        var facts = PciCapabilityFactsService.Collect(null!, AcpiWithMcfg(), At,
            (bus, dev, fn) => dev == 0 && fn == 0 ? page : null);

        Assert.Equal("1", facts.Single(f => f.Key == "sb.pci.cap.count").Value);
        Assert.Contains("MSI-X", facts.Single(f => f.Key == "sb.pci.cap.summary").Value);
        Assert.Contains("TotalVFs 8", facts.Single(f => f.Key == "sb.pci.vf.summary").Value);
        Assert.Contains("SV", facts.Single(f => f.Key == "sb.pci.acs.summary").Value);
        Assert.Contains("0x00000010", facts.Single(f => f.Key == "sb.pcieaer.full.summary").Value);
        Assert.Contains("未附已知修正對照表", facts.Single(f => f.Key == "sb.pci.rev.known").Value);
        // L1SS／DPC／VC 未帶 → 如實說這台沒有
        Assert.Equal(FactAvailability.NotApplicable, facts.Single(f => f.Key == "sb.pci.l1sub.summary").Availability);
        Assert.Equal(FactAvailability.NotApplicable, facts.Single(f => f.Key == "sb.pci.dpc.summary").Availability);
    }

    [Fact]
    public void 服務_無MCFG_九把鍵標NotApplicable不假裝掃過()
    {
        var facts = PciCapabilityFactsService.Collect(null!, new NoAcpi(), At);
        Assert.Equal(9, facts.Count);
        Assert.All(facts, f => Assert.Equal(FactAvailability.NotApplicable, f.Availability));
    }

    // ── AC-017 PPTT ─────────────────────────────────────────────────────

    [Fact]
    public void PPTT_處理器與快取節點解出並與OS數並列()
    {
        var table = BuildPptt(processorCount: 4, cacheKib: 2048);
        var facts = PpttFactsService.Collect(new OneTable(table), At, logicalProcessors: 4);

        Assert.Equal("4", facts.Single(f => f.Key == "ac.pptt.count").Value);
        Assert.Contains("作業系統回報 4 個邏輯處理器", facts.Single(f => f.Key == "ac.pptt.summary").Value);
        Assert.Contains("一致", facts.Single(f => f.Key == "ac.pptt.summary").Value);
        Assert.Contains("2048 KiB", facts.Single(f => f.Key == "ac.pptt.summary").Value);
    }

    [Fact]
    public void PPTT_與OS數不一致時兩邊都列出不下判決()
    {
        var facts = PpttFactsService.Collect(new OneTable(BuildPptt(4, 1024)), At, logicalProcessors: 8);
        string v = facts.Single(f => f.Key == "ac.pptt.summary").Value;
        Assert.Contains("不一致", v);
        Assert.Contains("不挑一邊說另一邊錯", v);
    }

    [Fact]
    public void PPTT_無此表_標NotApplicable並說明是表不存在()
    {
        var facts = PpttFactsService.Collect(new OneTable(null), At, 4);
        Assert.All(facts, f => Assert.Equal(FactAvailability.NotApplicable, f.Availability));
        Assert.Contains("ACPI 6.2+ 才引入", facts[0].UnavailableReason!);
    }

    [Fact]
    public void PPTT_長度不可信的子結構即停()
    {
        var t = new byte[36 + 4];
        "PPTT"u8.CopyTo(t);
        t[36] = 0; t[37] = 1;   // length 1 < 2 → 不可信
        Assert.Empty(AcpiPptt.Structures(t));
    }

    // ── SV-011 Server Core 判定 ────────────────────────────────────────

    [Fact]
    public void ServerCore_三種型別字串()
    {
        Assert.Contains("Server Core（無桌面體驗）", ServerModeFactsService.Collect(At, () => "Server Core")[0].Value);
        Assert.Contains("含桌面體驗", ServerModeFactsService.Collect(At, () => "Server")[0].Value);
        Assert.Contains("用戶端", ServerModeFactsService.Collect(At, () => "Client")[0].Value);
    }

    [Fact]
    public void ServerCore_未收錄型別如實回原始字串()
    {
        string v = ServerModeFactsService.Collect(At, () => "Nano Server")[0].Value;
        Assert.Contains("未收錄的型別（Nano Server）", v);
    }

    [Fact]
    public void ServerCore_登錄值不存在_標NotSupported不推測()
    {
        var facts = ServerModeFactsService.Collect(At, () => null);
        Assert.Equal(FactAvailability.NotSupported, facts[0].Availability);
        Assert.Contains("不推測", facts[0].UnavailableReason!);
    }

    [Fact]
    public void ServerCore_讀取失敗_標ReadError()
    {
        var facts = ServerModeFactsService.Collect(At, () => throw new UnauthorizedAccessException("denied"));
        Assert.Equal(FactAvailability.ReadError, facts[0].Availability);
    }

    // ── 工具 ─────────────────────────────────────────────────────────────

    private sealed class OneTable(byte[]? table) : IAcpiTableSource
    {
        public bool Available => true;
        public string? UnavailableReason => null;
        public IReadOnlyList<byte[]> ReadAll() => table is null ? [] : [table];
    }

    private sealed class NoAcpi : IAcpiTableSource
    {
        public bool Available => true;
        public string? UnavailableReason => null;
        public IReadOnlyList<byte[]> ReadAll() => [];
    }

    private static IAcpiTableSource AcpiWithMcfg()
    {
        var t = new byte[36 + 8 + 16];
        "MCFG"u8.CopyTo(t);
        BitConverter.GetBytes((uint)t.Length).CopyTo(t, 4);
        BitConverter.GetBytes(0xE000_0000ul).CopyTo(t, 44);
        t[54] = 0x00; t[55] = 0xFF;
        return new OneTable(t);
    }

    /// <summary>合成一顆功能的 4KB 組態空間：MSI-X（legacy）＋選配的擴充能力鏈（AER／ACS／SR-IOV 依序串接）。</summary>
    private static byte[] BuildDevicePage(bool withSriov, bool withAcs, bool withAer)
    {
        var page = new byte[4096];
        page[0x00] = 0x86; page[0x01] = 0x80;      // vendor 0x8086
        page[0x06] = 0x10;                          // Status：有 legacy 能力清單
        page[0x08] = 0x02;                          // revision
        page[0x34] = 0x40;
        page[0x40] = 0x11;                          // MSI-X
        page[0x41] = 0x00;
        page[0x42] = 0x0F; page[0x43] = 0x00;      // MSI-X 表 16 條

        var chain = new List<ushort>();
        if (withAer) chain.Add(0x0001);
        if (withAcs) chain.Add(0x000D);
        if (withSriov) chain.Add(0x0010);

        int off = 0x100;
        for (int i = 0; i < chain.Count; i++)
        {
            uint next = i + 1 < chain.Count ? (uint)(off + 0x20) : 0u;
            WriteDword(page, off, chain[i] | (next << 20));
            switch (chain[i])
            {
                case 0x0001: WriteDword(page, off + 0x04, 0x0000_0010); break;   // AER 不可更正狀態
                case 0x000D: WriteU16(page, off + 0x04, 0x0001); break;          // ACS：SV
                case 0x0010:                                                      // SR-IOV：InitialVFs／TotalVFs／NumVFs
                    WriteU16(page, off + 0x0C, 1);
                    WriteU16(page, off + 0x0E, 8);
                    WriteU16(page, off + 0x10, 4);
                    break;
            }
            off += 0x20;
        }
        return page;
    }

    private static byte[] BuildPptt(int processorCount, uint cacheKib)
    {
        int cacheLen = 24;
        var t = new byte[36 + processorCount * 20 + cacheLen];
        System.Text.Encoding.ASCII.GetBytes("PPTT").CopyTo(t, 0);
        BitConverter.GetBytes((uint)t.Length).CopyTo(t, 4);
        int off = 36;
        for (int i = 0; i < processorCount; i++)
        {
            t[off] = 0; t[off + 1] = 20;                                    // Processor，長度 20
            BitConverter.GetBytes(i == 0 ? 0x0009u : 0x000Cu).CopyTo(t, off + 4); // 第一個＝封裝＋葉
            BitConverter.GetBytes((uint)i).CopyTo(t, off + 12);
            off += 20;
        }
        t[off] = 1; t[off + 1] = 24;                                        // Cache，長度 24
        BitConverter.GetBytes(1u).CopyTo(t, off + 4);                       // Size Valid
        BitConverter.GetBytes(cacheKib * 1024).CopyTo(t, off + 12);
        return t;
    }

    private static void WriteDword(byte[] a, int off, uint v) => BitConverter.GetBytes(v).CopyTo(a, off);
    private static void WriteU16(byte[] a, int off, ushort v) => BitConverter.GetBytes(v).CopyTo(a, off);
}
