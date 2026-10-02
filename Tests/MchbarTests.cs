using Xunit;

namespace XinSpect.Tests;

public sealed class MchbarTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);
    private const ulong ExpectedBase = 0xFEDC0000;

    private sealed class FakePci(uint? low, uint? high = 0) : IPciConfigReader
    {
        public bool Available => true;
        public string? UnavailableReason => null;
        public uint? ReadDword(byte bus, byte device, byte function, uint register)
            => register == 0x48 ? low : register == 0x4C ? high : null;
    }

    private sealed class FakePciUnavailable : IPciConfigReader
    {
        public bool Available => false;
        public string? UnavailableReason => "缺 ring0";
        public uint? ReadDword(byte bus, byte device, byte function, uint register) => null;
    }

    private sealed class FakeMmio(byte[]? block) : IMmioReader
    {
        public bool Available => true;
        public string? UnavailableReason => null;
        public byte[]? ReadBlock(ulong physicalAddress, int length) => block;
    }

    [Fact]
    public void 基底解析_enable與對齊正確()
    {
        Assert.Equal(ExpectedBase, MchbarService.ResolveBase(new FakePci(0xFEDC0001u))); // bit0=enable、位址落在高位
        Assert.Null(MchbarService.ResolveBase(new FakePci(0xFEDC0000u))); // 未啟用
        Assert.Null(MchbarService.ResolveBase(new FakePci(0xFFFFFFFFu))); // 無主機橋
        Assert.Null(MchbarService.ResolveBase(new FakePciUnavailable()));
    }

    [Fact]
    public void 服務_基底可讀_驅動未載時暫存器三態且帶基底()
    {
        var facts = MchbarService.Collect(new FakePci(0xFEDC0001u), new NotLoadedMmioReader(), At);
        var baseFact = facts.Single(x => x.Key == "mchbar.base");
        Assert.Equal(FactAvailability.Present, baseFact.Availability);
        Assert.Contains("0xFEDC0000", baseFact.Value);
        Assert.Contains("僅報基底", baseFact.Value);

        var regs = facts.Single(x => x.Key == "mchbar.registers");
        Assert.Equal(FactAvailability.InsufficientPrivilege, regs.Availability);
        Assert.Contains("缺自家核心驅動", regs.UnavailableReason);
    }

    [Fact]
    public void 服務_驅動在_暫存器映射如實回報()
    {
        var facts = MchbarService.Collect(new FakePci(0xFEDC0001u), new FakeMmio(new byte[0x100]), At,
            cpuIdProbe: () => 0x050654);
        var regs = facts.Single(x => x.Key == "mchbar.registers");
        Assert.Equal(FactAvailability.Present, regs.Availability);
        Assert.Contains("Skylake-X / Cascade Lake", regs.Value); // 世代判定已接入
        Assert.Contains("公開規格未定義", regs.Value);            // 不出值的界線說清楚
        Assert.Contains("不是待辦遺漏", regs.Value);
    }

    [Fact]
    public void 服務_MCHBAR未啟用_兩筆都標不適用()
    {
        var facts = MchbarService.Collect(new FakePci(0xFEDC0000u), new NotLoadedMmioReader(), At);
        Assert.All(facts, f => Assert.Equal(FactAvailability.NotApplicable, f.Availability));
    }

    [Fact]
    public void 收集器_MCHBAR驅動未載_三態區帶基底位址()
    {
        var regions = RawRegisterCollectService.Collect(
            new FakePci(0xFEDC0001u), new FakeAcpiSource([]), FakeMsrReader.Empty, new NotLoadedMmioReader(), At);
        var mchbar = regions.Single(r => r.Source.StartsWith("mmio:mchbar:"));
        Assert.Equal(FactAvailability.InsufficientPrivilege, mchbar.Availability);
        Assert.Contains("0xFEDC0000", mchbar.Source);
    }

    private sealed class FakeAcpiSource(IReadOnlyList<byte[]> tables) : IAcpiTableSource
    {
        public bool Available => true;
        public string? UnavailableReason => null;
        public IReadOnlyList<byte[]> ReadAll() => tables;
    }

    private sealed class FakeMsrReader : IKernelMsrReader
    {
        public static readonly FakeMsrReader Empty = new();
        public bool Available => true;
        public string? UnavailableReason => null;
        public ulong? ReadMsr(uint index) => null;
    }
}
