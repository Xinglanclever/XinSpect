using Xunit;

namespace XinSpect.Tests;

public sealed class RawRegisterCollectTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void 收集_全部可讀_逐區帶回原始位元組()
    {
        var regions = RawRegisterCollectService.Collect(
            new FakePci(biosCntl: 0x00000022u, smramc: 0x00000010u),
            new FakeAcpi([FakeAcpiTable("BERT", 48)]),
            FakeMsr.From((0x3A, 0x5), (0xC80, 1UL << 30), (0x34, 0x1234)),
            new FakeMmio(new byte[0x88]), At);

        var biosCntl = regions.Single(r => r.Source == "pcicfg:00:1f.0+dc");
        Assert.Equal(FactAvailability.Present, biosCntl.Availability);
        Assert.Equal(0x00000022u, BitConverter.ToUInt32(biosCntl.Bytes!, 0));

        var bert = regions.Single(r => r.Source == "acpi:BERT");
        Assert.Equal(48, bert.Bytes!.Length);
        Assert.Equal((byte)'B', bert.Bytes[0]);

        var fc = regions.Single(r => r.Source == "msr:0x3a");
        Assert.Equal(8, fc.Bytes!.Length);
        Assert.Equal(0x5UL, BitConverter.ToUInt64(fc.Bytes, 0));

        Assert.Equal(8, regions.Count); // 2 PCI + 3 MSR + 1 ACPI + 1 MMIO + 1 MCHBAR（此假件無 0x48 → 不適用區）
    }

    [Fact]
    public void 收集_PCI缺ring0_該區三態其餘照常()
    {
        var regions = RawRegisterCollectService.Collect(
            new FakePciUnavailable(), new FakeAcpi([]), FakeMsr.From(), new NotLoadedMmioReader(), At);

        Assert.All(regions.Where(r => r.Source.StartsWith("pcicfg:")), r =>
        {
            Assert.Equal(FactAvailability.InsufficientPrivilege, r.Availability);
            Assert.NotNull(r.UnavailableReason);
            Assert.Null(r.Bytes);
        });
        var spi = regions.Single(r => r.Source == "mmio:spi:0xfed10000+88");
        Assert.Equal(FactAvailability.InsufficientPrivilege, spi.Availability);
        Assert.Contains("缺自家核心驅動", spi.UnavailableReason);
    }

    [Fact]
    public void 收集_MSR平台未實作_標讀取失敗不補零()
    {
        var regions = RawRegisterCollectService.Collect(
            new FakePci(0x20, 0x10), new FakeAcpi([]), FakeMsr.From((0x3A, 0x5)), new NotLoadedMmioReader(), At);
        var c80 = regions.Single(r => r.Source == "msr:0xc80");
        Assert.Equal(FactAvailability.ReadError, c80.Availability);
        Assert.Null(c80.Bytes);
    }

    [Fact]
    public void 收集後快照_帶版本與時間()
    {
        var regions = RawRegisterCollectService.Collect(
            new FakePci(0x20, 0x10), new FakeAcpi([]), FakeMsr.From(), new NotLoadedMmioReader(), At);
        var snapshot = RawRegisterCollectService.Create("2.1.0.5", regions, At);
        Assert.Equal("2.1.0.5", snapshot.AppVersion);
        Assert.Equal(At, snapshot.TakenAtUtc);
        Assert.Equal(regions.Count, snapshot.Regions.Count);
    }

    private static byte[] FakeAcpiTable(string sig, int len)
    {
        var t = new byte[len];
        System.Text.Encoding.ASCII.GetBytes(sig).CopyTo(t, 0);
        BitConverter.GetBytes((uint)len).CopyTo(t, 4);
        return t;
    }

    private sealed class FakePci(uint? biosCntl, uint? smramc) : IPciConfigReader
    {
        public bool Available => true;
        public string? UnavailableReason => null;
        public uint? ReadDword(byte bus, byte device, byte function, uint register)
            => device == 0x1F && function == 0 && register == 0xDC ? biosCntl
             : device == 0x00 && register == 0x88 ? smramc
             : null;
    }

    private sealed class FakePciUnavailable : IPciConfigReader
    {
        public bool Available => false;
        public string? UnavailableReason => "缺 ring0";
        public uint? ReadDword(byte bus, byte device, byte function, uint register) => null;
    }

    private sealed class FakeAcpi(IReadOnlyList<byte[]> tables) : IAcpiTableSource
    {
        public bool Available => true;
        public string? UnavailableReason => null;
        public IReadOnlyList<byte[]> ReadAll() => tables;
    }

    private sealed class FakeMsr : IKernelMsrReader
    {
        private readonly IReadOnlyDictionary<uint, ulong> _values;
        private FakeMsr(IReadOnlyDictionary<uint, ulong> values) => _values = values;
        public bool Available => true;
        public string? UnavailableReason => null;
        public ulong? ReadMsr(uint index) => _values.TryGetValue(index, out var v) ? v : null;
        public static FakeMsr From(params (uint Msr, ulong Value)[] values) =>
            new(values.ToDictionary(v => v.Msr, v => v.Value));
    }

    // ===== 揮發遮罩 × 差分：收集→Diff 全迴路 =====

    [Fact]
    public void 揮發遮罩_計數器位元組不淹沒差分()
    {
        var pci = new FakePci(0x00000022u, 0x10);
        var acpi = new FakeAcpi([]);
        var mmio = new FakeMmio(new byte[0x88]);
        var before = RawRegisterCollectService.Collect(pci, acpi, FakeMsr.From((0x3A, 5), (0xC80, 0), (0x34, 7)), mmio, At);
        var after = RawRegisterCollectService.Collect(pci, acpi, FakeMsr.From((0x3A, 5), (0xC80, 0), (0x34, 999)), mmio, At);

        var diff = RawRegisterSnapshotService.Diff(before, after);
        Assert.Equal(0, diff.Changed); // SMI 計數器（遮罩）變了，不算變動
        Assert.Contains(diff.Regions, r => r.Source == "msr:0x34" && r.Kind == RawRegionChangeKind.Unchanged);
    }

    [Fact]
    public void 非遮罩位元組變動_逐位元組定位到偏移()
    {
        var acpi = new FakeAcpi([]);
        var msr = FakeMsr.From((0x3A, 5), (0xC80, 0), (0x34, 7));
        var mmio = new FakeMmio(new byte[0x88]);
        var before = RawRegisterCollectService.Collect(new FakePci(0x00000022u, 0x10), acpi, msr, mmio, At);
        var after = RawRegisterCollectService.Collect(new FakePci(0x00000020u, 0x10), acpi, msr, mmio, At); // BLE 關掉

        var diff = RawRegisterSnapshotService.Diff(before, after);
        var change = diff.Regions.Single(r => r.Source == "pcicfg:00:1f.0+dc");
        Assert.Equal(RawRegionChangeKind.Changed, change.Kind);
        Assert.Equal([0], change.ChangedOffsets); // 0x22 vs 0x20：低位元組 bit1（BLE）
    }

    [Fact]
    public void SPI遮罩_HSFSTS狀態位元組變動不算變動_FRAP變動算()
    {
        var pci = new FakePci(0x20, 0x10);
        var acpi = new FakeAcpi([]);
        var msr = FakeMsr.From();
        var block = new byte[0x88];
        var mmio = new MutableMmio(block);
        var before = RawRegisterCollectService.Collect(pci, acpi, msr, mmio, At);
        block[0x04] = 0x01; // FDONE 翻起（遮罩內）
        block[0x50] = 0x02; // FRAP bit1 打開（非遮罩）：BIOS 區變成可寫
        var after = RawRegisterCollectService.Collect(pci, acpi, msr, mmio, At);

        var diff = RawRegisterSnapshotService.Diff(before, after);
        var spi = diff.Regions.Single(r => r.Source == "mmio:spi:0xfed10000+88");
        Assert.Equal(RawRegionChangeKind.Changed, spi.Kind);
        Assert.Equal([0x50], spi.ChangedOffsets);
    }

    private sealed class FakeMmio(byte[]? block) : IMmioReader
    {
        public bool Available => true;
        public string? UnavailableReason => null;
        public byte[]? ReadBlock(ulong physicalAddress, int length) => block;
    }

    [Fact]
    public void 同簽章多張表_來源鍵加後綴不重複_差分不炸()
    {
        var regions = RawRegisterCollectService.Collect(
            new FakePci(0x20, 0x10),
            new FakeAcpi([FakeAcpiTable("SSDT", 64), FakeAcpiTable("SSDT", 80), FakeAcpiTable("DSDT", 100)]),
            FakeMsr.From(), new NotLoadedMmioReader(), At);

        var sources = regions.Select(r => r.Source).Where(s => s.StartsWith("acpi:")).ToList();
        Assert.Equal(3, sources.Count);
        Assert.Equal(sources.Count, sources.Distinct().Count()); // 鍵必須唯一
        Assert.Contains("acpi:SSDT.1", sources);
        Assert.Contains("acpi:SSDT.2", sources);
        Assert.Contains("acpi:DSDT", sources);

        // 差分按來源鍵 ToDictionary 配對：鍵重複會直接丟例外——重跑一次同構快照驗證不炸
        var again = RawRegisterCollectService.Collect(
            new FakePci(0x20, 0x10),
            new FakeAcpi([FakeAcpiTable("SSDT", 64), FakeAcpiTable("SSDT", 80), FakeAcpiTable("DSDT", 100)]),
            FakeMsr.From(), new NotLoadedMmioReader(), At);
        var diff = RawRegisterSnapshotService.Diff(regions, again);
        Assert.Equal(0, diff.Changed);
    }

    private sealed class MutableMmio(byte[] block) : IMmioReader
    {
        public bool Available => true;
        public string? UnavailableReason => null;
        public byte[]? ReadBlock(ulong physicalAddress, int length) => (byte[])block.Clone(); // 每次快照是獨立副本
    }
}
