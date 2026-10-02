using Xunit;

namespace XinSpect.Tests;

public sealed class EcamAerTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private const ulong EcamBase = 0xE0000000;

    // ===== 純解碼：MCFG → ECAM 基底 =====

    [Fact]
    public void MCFG_合法表_解出ECAM基底與bus範圍()
    {
        var ecam = AcpiTable.McfgPrimaryEcam(McfgTable());
        Assert.NotNull(ecam);
        Assert.Equal(EcamBase, ecam.Value.Base);
        Assert.Equal(0, ecam.Value.StartBus);
        Assert.Equal(255, ecam.Value.EndBus);
    }

    [Fact]
    public void MCFG_非MCFG簽章_回null不臆測()
    {
        var t = McfgTable();
        t[0] = (byte)'X';
        Assert.Null(AcpiTable.McfgPrimaryEcam(t));
    }

    [Fact]
    public void MCFG_過短無條目_回null()
    {
        Assert.Null(AcpiTable.McfgPrimaryEcam(new byte[44]));
    }

    // ===== 服務層三態：MCFG usermode 可得，擴充組態空間要 MMIO（自家驅動） =====

    [Fact]
    public void 服務_MCFG存在但驅動未載_ECAM事實仍在且掃描標權限不足()
    {
        var facts = EcamAerService.Collect(new NotLoadedMmioReader(), new ListAcpi([McfgTable()]), At);
        var ecam = facts.Single(x => x.Key == "pcieaer.ecam");
        Assert.Equal(FactAvailability.Present, ecam.Availability);
        Assert.Contains("0xE0000000", ecam.Value);

        var scan = facts.Single(x => x.Key == "pcieaer.scan");
        Assert.Equal(FactAvailability.InsufficientPrivilege, scan.Availability);
        Assert.Contains("缺自家核心驅動", scan.UnavailableReason);
        Assert.Equal("", scan.Value);
    }

    [Fact]
    public void 服務_無MCFG_標不適用()
    {
        var facts = EcamAerService.Collect(new NotLoadedMmioReader(), new ListAcpi([]), At);
        Assert.Equal(FactAvailability.NotApplicable, facts.Single(x => x.Key == "pcieaer.ecam").Availability);
    }

    [Fact]
    public void 服務_ACPI來源不可用_標權限不足()
    {
        var facts = EcamAerService.Collect(new NotLoadedMmioReader(), new ListAcpi([], available: false, reason: "列舉失敗"), At);
        var ecam = facts.Single(x => x.Key == "pcieaer.ecam");
        Assert.Equal(FactAvailability.InsufficientPrivilege, ecam.Availability);
        Assert.Contains("列舉失敗", ecam.UnavailableReason);
    }

    [Fact]
    public void 服務_掃到帶AER裝置_解出未修正錯誤()
    {
        // bus 0 dev 2 fn 0：裝置存在、AER 能力在 0x100、未修正狀態 bit14（完成逾時）、可修正 bit0（接收器錯誤）
        var mmio = new EcamFakeMmio(EcamBase, new Dictionary<ulong, byte[]>
        {
            [Dev(0, 2, 0)] = DevicePage(vendorDevice: 0x12348086u, aerOffset: 0x100, uncorr: 0x4000, corr: 0x0001),
        });
        var facts = EcamAerService.Collect(mmio, new ListAcpi([McfgTable()]), At);

        var aer = facts.Single(x => x.Key == "pcieaer.aer.0.2.0");
        Assert.Equal(FactAvailability.Present, aer.Availability);
        Assert.Contains("完成逾時", aer.Value);
        Assert.Contains("接收器錯誤", aer.Value);
        Assert.Contains("0x100", aer.Value);

        var scan = facts.Single(x => x.Key == "pcieaer.scan");
        Assert.Equal(FactAvailability.Present, scan.Availability);
        Assert.Contains("1 個裝置", scan.Value);
        Assert.Contains("1 個帶 AER", scan.Value);
    }

    [Fact]
    public void 服務_AER裝置無錯誤_如實報無錯誤()
    {
        var mmio = new EcamFakeMmio(EcamBase, new Dictionary<ulong, byte[]>
        {
            [Dev(0, 1, 0)] = DevicePage(0x12348086u, 0x100, 0, 0),
        });
        var facts = EcamAerService.Collect(mmio, new ListAcpi([McfgTable()]), At);
        Assert.Contains("無未修正、無可修正", facts.Single(x => x.Key == "pcieaer.aer.0.1.0").Value);
    }

    [Fact]
    public void 服務_無AER能力的裝置_不產生AER事實但計入掃描()
    {
        var mmio = new EcamFakeMmio(EcamBase, new Dictionary<ulong, byte[]>
        {
            [Dev(0, 0, 0)] = DevicePage(0x12348086u, aerOffset: null, 0, 0), // 主機橋：存在但無 AER
        });
        var facts = EcamAerService.Collect(mmio, new ListAcpi([McfgTable()]), At);
        Assert.DoesNotContain(facts, x => x.Key.StartsWith("pcieaer.aer."));
        Assert.Contains("1 個裝置", facts.Single(x => x.Key == "pcieaer.scan").Value);
    }

    [Fact]
    public void 服務_不存在的裝置_依ECAM慣例回全F_不誤報()
    {
        var mmio = new EcamFakeMmio(EcamBase, new Dictionary<ulong, byte[]>());
        var facts = EcamAerService.Collect(mmio, new ListAcpi([McfgTable()]), At);
        Assert.Contains("0 個裝置", facts.Single(x => x.Key == "pcieaer.scan").Value);
    }

    [Fact]
    public void 服務_MMIO讀取失敗_標讀取失敗不假裝掃過()
    {
        var mmio = new NullMmio();
        var facts = EcamAerService.Collect(mmio, new ListAcpi([McfgTable()]), At);
        var scan = facts.Single(x => x.Key == "pcieaer.scan");
        Assert.Equal(FactAvailability.ReadError, scan.Availability);
        Assert.Contains("ECAM", scan.UnavailableReason);
    }

    [Fact]
    public void MCFG_多條目_全部解出且segment如實()
    {
        var t = McfgTable(); // 條目一：segment 0, bus 0-255
        Array.Resize(ref t, 76);
        BitConverter.GetBytes(0xC0000000UL).CopyTo(t, 60); // 條目二：segment 1
        BitConverter.GetBytes((ushort)1).CopyTo(t, 68);
        t[70] = 0; t[71] = 127;

        var entries = AcpiTable.McfgEntries(t);
        Assert.Equal(2, entries.Count);
        Assert.Equal(0, entries[0].SegmentGroup);
        Assert.Equal(0xC0000000UL, entries[1].Base);
        Assert.Equal(1, entries[1].SegmentGroup);
        Assert.Equal((byte)0, entries[1].StartBus);
        Assert.Equal((byte)127, entries[1].EndBus);
    }

    [Fact]
    public void 服務_多segment平台_ECAM事實明說其餘未掃()
    {
        var t = McfgTable();
        Array.Resize(ref t, 76);
        BitConverter.GetBytes(0xC0000000UL).CopyTo(t, 60);
        BitConverter.GetBytes((ushort)1).CopyTo(t, 68);
        t[70] = 0; t[71] = 127;

        var facts = EcamAerService.Collect(new NotLoadedMmioReader(), new ListAcpi([t]), At);
        var ecam = facts.Single(x => x.Key == "pcieaer.ecam");
        Assert.Contains("另有 1 個 segment 未納入掃描", ecam.Value);
    }

    [Fact]
    public void 掃描擴大_bus1裝置納入_上限外如實標未掃()
    {
        var devices = new Dictionary<ulong, byte[]>
        {
            [Dev(0, 0, 0)] = DevicePage(0x1234_8086, 0x100, 0x0000_0040, 0), // bus 0 根埠帶 AER
            [Dev(1, 0, 0)] = DevicePage(0x15D3_8086, 0x100, 0, 0x0000_0001), // bus 1 裝置帶 AER（可修正錯誤）
        };
        var facts = EcamAerService.Collect(new EcamFakeMmio(EcamBase, devices), new ListAcpi([McfgTable(endBus: 255)]), At);

        var bus0 = facts.Single(f => f.Key == "pcieaer.aer.0.0.0");
        var bus1 = facts.Single(f => f.Key == "pcieaer.aer.1.0.0"); // bus 1 也掃到了
        Assert.Contains("AER 0:00.0", bus0.Name);
        Assert.Contains("AER 1:00.0", bus1.Name);
        var scan = facts.Single(f => f.Key == "pcieaer.scan");
        Assert.Contains("bus 0-31", scan.Value);
        Assert.Contains("2 個裝置、2 個帶 AER 能力", scan.Value);
        Assert.Contains("未掃", scan.Value);                      // bus 32-255 超出上限保護
    }

    [Fact]
    public void 掃描中止_失敗點如實記錄且保留已得事實()
    {
        var devices = new Dictionary<ulong, byte[]>
        {
            [Dev(0, 0, 0)] = DevicePage(0x1234_8086, 0x100, 0, 0),
            [Dev(1, 0, 0)] = DevicePage(0x15D3_8086, null, 0, 0),
        };
        var inner = new EcamFakeMmio(EcamBase, devices);
        IMmioReader mmio = new NullProbeAt(inner, EcamBase + (2UL << 20)); // bus 2 的第一個探頭失敗

        var facts = EcamAerService.Collect(mmio, new ListAcpi([McfgTable(endBus: 255)]), At);
        var scan = facts.Single(f => f.Key == "pcieaer.scan");
        Assert.Equal(FactAvailability.ReadError, scan.Availability);
        Assert.Contains("中止於 bus 2", scan.UnavailableReason);
        Assert.Single(facts, f => f.Key == "pcieaer.aer.0.0.0"); // 已得事實保留
    }

    private static ulong Dev(byte bus, byte dev, byte fn) => (ulong)bus << 16 | (ulong)dev << 8 | fn;

    private static byte[] McfgTable(ulong ecamBase = EcamBase, byte startBus = 0, byte endBus = 255)
    {
        var t = new byte[60];
        System.Text.Encoding.ASCII.GetBytes("MCFG").CopyTo(t, 0);
        BitConverter.GetBytes((uint)60).CopyTo(t, 4);
        t[8] = 1; // revision
        BitConverter.GetBytes(ecamBase).CopyTo(t, 44);
        BitConverter.GetBytes((ushort)0).CopyTo(t, 52); // PCI segment group 0
        t[54] = startBus;
        t[55] = endBus;
        return t;
    }

    private static byte[] DevicePage(uint vendorDevice, int? aerOffset, uint uncorr, uint corr)
    {
        var page = new byte[4096];
        BitConverter.GetBytes(vendorDevice).CopyTo(page, 0x00);
        if (aerOffset is { } off)
        {
            // 擴充能力表頭：CapID[15:0]=0x0001(AER) | CapVer[19:16]=1 | NextOffset[31:20]=0（鏈結尾）
            BitConverter.GetBytes(0x00010001u).CopyTo(page, off);
            BitConverter.GetBytes(uncorr).CopyTo(page, off + 0x04); // Uncorrectable Error Status
            BitConverter.GetBytes(corr).CopyTo(page, off + 0x10);   // Correctable Error Status
        }
        return page;
    }

    private sealed class ListAcpi(IReadOnlyList<byte[]> tables, bool available = true, string? reason = null) : IAcpiTableSource
    {
        public bool Available => available;
        public string? UnavailableReason => reason;
        public IReadOnlyList<byte[]> ReadAll() => tables;
    }

    private sealed class NullMmio : IMmioReader
    {
        public bool Available => true;
        public string? UnavailableReason => null;
        public byte[]? ReadBlock(ulong physicalAddress, int length) => null;
    }

    /// <summary>模擬 ECAM：有裝置的頁回內容，無裝置的位址依 PCIe 慣例回全 0xFF（不當成錯誤）。</summary>
    private sealed class EcamFakeMmio(ulong ecamBase, IReadOnlyDictionary<ulong, byte[]> devices) : IMmioReader
    {
        public bool Available => true;
        public string? UnavailableReason => null;

        public byte[]? ReadBlock(ulong address, int length)
        {
            if (address < ecamBase || address - ecamBase > uint.MaxValue) return null;
            ulong off = address - ecamBase;
            var key = ((off >> 20) & 0xFF) << 16 | ((off >> 15) & 0x1F) << 8 | ((off >> 12) & 0x7);
            if (!devices.TryGetValue(key, out var page)) return Enumerable.Repeat((byte)0xFF, length).ToArray();
            int inPage = (int)(off & 0xFFF);
            if (inPage + length > page.Length) return null;
            return page[inPage..(inPage + length)];
        }
    }

    /// <summary>包一層：對特定位址的 0x10 探頭回 null（模擬 ECAM 途中讀取失敗）。</summary>
    private sealed class NullProbeAt(IMmioReader inner, ulong probeAddress) : IMmioReader
    {
        public bool Available => inner.Available;
        public string? UnavailableReason => inner.UnavailableReason;

        public byte[]? ReadBlock(ulong address, int length)
            => address == probeAddress && length == 0x10 ? null : inner.ReadBlock(address, length);
    }
}
