using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// PCIe 設定空間存取方法的宣告與對帳（U10 第三圈 PC-0001）：
/// 兩條路徑的可用性要分開講，0:0.0 那一格要真的比，單邊不叫對帳。
/// </summary>
public class PcieConfigAccessFactsTests
{
    private sealed class FakePci(bool available, uint? id = null, string? why = null) : IPciConfigReader
    {
        public int Reads { get; private set; }
        public bool Available => available;
        public string? UnavailableReason => why;

        public uint? ReadDword(byte bus, byte device, byte function, uint register)
        {
            Reads++;
            return id;
        }
    }

    private sealed class FakeMmio(bool available, uint? id = null, string? why = null) : IMmioReader
    {
        public bool Available => available;
        public string? UnavailableReason => why;
        public string? BackendName => "fake";
        public string? LastFailReason => null;

        public byte[]? ReadBlock(ulong physicalAddress, int length)
        {
            if (id is not { } v) return null;
            var b = new byte[length];
            BinaryPrimitives.WriteUInt32LittleEndian(b, v);
            return b;
        }
    }

    private sealed class FakeAcpi(params byte[][] tables) : IAcpiTableSource
    {
        public bool Available => true;
        public string? UnavailableReason => null;
        public IReadOnlyList<byte[]> ReadAll() => tables.ToList();
    }

    private sealed class DeadAcpi(string why) : IAcpiTableSource
    {
        public bool Available => false;
        public string? UnavailableReason => why;
        public IReadOnlyList<byte[]> ReadAll() => [];
    }

    /// <summary>MCFG：表頭 36 ＋ 保留 8 ＋ 每條目 16（基底 u64@0、segment u16@8、start@10、end@11）。</summary>
    private static byte[] Mcfg(ulong baseAddr = 0x6000_0000, ushort segment = 0, byte start = 0, byte end = 255)
    {
        var b = new byte[60];
        Encoding.ASCII.GetBytes("MCFG").CopyTo(b, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4, 4), 60u);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(44, 8), baseAddr);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(52, 2), segment);
        b[54] = start;
        b[55] = end;
        return b;
    }

    private static HardwareFact Fact(IReadOnlyList<HardwareFact> facts, string key) =>
        facts.Single(f => f.Key == key);

    private static IReadOnlyList<HardwareFact> Run(
        IPciConfigReader pci, IMmioReader mmio, IAcpiTableSource acpi) =>
        PcieConfigAccessFactsService.Collect(pci, mmio, acpi,
            PcieConfigAccessFactsService.Probe(pci, mmio, acpi), DateTimeOffset.UnixEpoch);

    // ── Probe ───────────────────────────────────────────────────────────

    [Fact]
    public void 探針固定讀000那一格()
    {
        var pci = new FakePci(true, 0x1234_5678);
        var probe = PcieConfigAccessFactsService.Probe(pci, new FakeMmio(true, 0x1234_5678), new FakeAcpi(Mcfg()));
        Assert.Equal(0, probe.Bus);
        Assert.Equal(0, probe.Device);
        Assert.Equal(0, probe.Function);
        Assert.Equal(0x1234_5678u, probe.Legacy);
        Assert.Equal(0x1234_5678u, probe.Ecam);
        Assert.True(probe.BothReadAndAgree);
    }

    [Fact]
    public void 舊式不可用時不呼叫它()
    {
        var pci = new FakePci(false, 0x1111_2222, "缺 ring0");
        var probe = PcieConfigAccessFactsService.Probe(pci, new FakeMmio(true, 0x1111_2222), new FakeAcpi(Mcfg()));
        Assert.Equal(0, pci.Reads);
        Assert.Null(probe.Legacy);
        Assert.True(probe.OnlyOneSide);
    }

    [Fact]
    public void 沒有MCFG就沒有ECAM可讀()
    {
        var mmio = new FakeMmio(true, 0x1);
        var probe = PcieConfigAccessFactsService.Probe(new FakePci(true, 0x1), mmio, new FakeAcpi());
        Assert.Null(probe.Ecam);
    }

    [Fact]
    public void segment不是0的條目不採用()
    {
        var probe = PcieConfigAccessFactsService.Probe(
            new FakePci(true, 0x1), new FakeMmio(true, 0x1), new FakeAcpi(Mcfg(segment: 3)));
        Assert.Null(probe.Ecam);
    }

    // ── 服務 ────────────────────────────────────────────────────────────

    [Fact]
    public void 兩邊一致_值寫一致且說清一致的意義有限()
    {
        var f = Fact(Run(new FakePci(true, 0xA2AF_8086), new FakeMmio(true, 0xA2AF_8086), new FakeAcpi(Mcfg())),
            PcieConfigAccessFactsService.AgreementKey);
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Null(f.UnavailableReason);
        Assert.Contains("0xA2AF8086", f.Value);
        Assert.Contains("vendor 0x8086", f.Value);
        Assert.Contains("device 0xA2AF", f.Value);
        Assert.Contains("**一致**", f.Value);
        Assert.Contains("不證明那個 ID 是對的", f.Value);
    }

    [Fact]
    public void 兩邊不一致_並列兩個值且不判斷誰對()
    {
        var f = Fact(Run(new FakePci(true, 0x1234_5678), new FakeMmio(true, 0x8888_9999), new FakeAcpi(Mcfg())),
            PcieConfigAccessFactsService.AgreementKey);
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("**兩條路徑的值不同**", f.Value);
        Assert.Contains("0x12345678", f.Value);
        Assert.Contains("0x88889999", f.Value);
        Assert.Contains("本版不判斷哪一個對", f.Value);
    }

    [Fact]
    public void 兩邊都不可用_不給一致也不給不一致()
    {
        var f = Fact(Run(new FakePci(false, why: "缺 ring0"), new FakeMmio(false, why: "未載入"),
            new FakeAcpi(Mcfg())), PcieConfigAccessFactsService.AgreementKey);
        Assert.Equal(FactAvailability.Unknown, f.Availability);
        Assert.Null(f.NumericValue);
        Assert.Contains("兩條路徑都不可用", f.UnavailableReason);
        Assert.Contains("單邊不叫對帳", f.UnavailableReason);
    }

    [Fact]
    public void 只有一條可用_理由要指名是哪一條()
    {
        var noPci = Fact(Run(new FakePci(false, why: "缺 ring0"), new FakeMmio(true, 0x1), new FakeAcpi(Mcfg())),
            PcieConfigAccessFactsService.AgreementKey);
        Assert.Contains("舊式 0xCF8／0xCFC 不可用", noPci.UnavailableReason);

        var noEcam = Fact(Run(new FakePci(true, 0x1), new FakeMmio(false, why: "未載入"), new FakeAcpi(Mcfg())),
            PcieConfigAccessFactsService.AgreementKey);
        Assert.Contains("ECAM／MMIO 不可用", noEcam.UnavailableReason);
    }

    [Fact]
    public void 兩邊都可用但只讀到一邊_是沒有兩邊可比的另一種()
    {
        var f = Fact(Run(new FakePci(true), new FakeMmio(true, 0x1), new FakeAcpi(Mcfg())),
            PcieConfigAccessFactsService.AgreementKey);
        Assert.Equal(FactAvailability.Unknown, f.Availability);
        Assert.Contains("只有一邊讀到", f.Value);
        Assert.Contains("不是「不一致」", f.UnavailableReason);
    }

    [Fact]
    public void 總覽要分開寫兩條路徑的可用性與MCFG條目數()
    {
        var f = Fact(Run(new FakePci(true, 0x1), new FakeMmio(false, why: "未載入驅動"), new FakeAcpi(Mcfg())),
            PcieConfigAccessFactsService.SummaryKey);
        Assert.Contains("舊式 0xCF8／0xCFC：可用", f.Value);
        Assert.Contains("ECAM 基底 0x60000000", f.Value);
        Assert.Contains("MMIO 後端不可用：未載入驅動", f.Value);
        Assert.Contains("MCFG 條目 1 筆", f.Value);
        Assert.Contains("不是兩個來源", f.Value);
    }

    [Fact]
    public void 沒有ACPI時要說無從得知有沒有MCFG()
    {
        var f = Fact(Run(new FakePci(true, 0x1), new FakeMmio(true, 0x1), new DeadAcpi("EnumSystemFirmwareTables 回 0")),
            PcieConfigAccessFactsService.SummaryKey);
        Assert.Contains("ACPI 表列舉不可用", f.Value);
        Assert.Contains("無從得知有沒有 MCFG", f.Value);
    }

    [Fact]
    public void 界線宣告_明說舊式只看得到256位元組()
    {
        var f = Fact(Run(new FakePci(true, 0x1), new FakeMmio(true, 0x1), new FakeAcpi(Mcfg())),
            PcieConfigAccessFactsService.BoundaryKey);
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Null(f.UnavailableReason);
        Assert.Contains("256 位元組", f.Value);
        Assert.Contains("擴充能力鏈（0x100 起）", f.Value);
        Assert.Contains("4096 位元組", f.Value);
        Assert.Contains("沒有逐格全面比對", f.Value);
        Assert.Contains("不說「這台機器沒有 PCIe」", f.Value);
    }

    [Fact]
    public void 界線宣告與機器狀態無關_永遠在()
    {
        var facts = Run(new FakePci(false, why: "x"), new FakeMmio(false, why: "y"), new DeadAcpi("z"));
        Assert.Equal(3, facts.Count);
        Assert.Equal(FactAvailability.Present,
            Fact(facts, PcieConfigAccessFactsService.BoundaryKey).Availability);
    }
}
