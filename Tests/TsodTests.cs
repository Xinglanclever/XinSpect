using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// TSOD（TSE2004）溫度感測器的契約：12 位元二補數 1/16°C 解碼（含負溫與旗號位元）、
/// 白名單唯讀 0x18–0x1F、word 交易走完整 SMBus 狀態機（FakeSmbusIo）、空位址不冒充事實。
/// </summary>
public class TsodTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0x1900u, 25.0)]   // 25°C：400 單位 × 1/16
    [InlineData(0x2380u, 35.5)]   // 35.5°C
    [InlineData(0xFA80u, -5.5)]   // 負溫：12 位元二補數 -88 單位
    [InlineData(0x1907u, 25.0)]   // bits[3:0] 旗號不參與溫度
    public void 溫度解碼_正負溫與旗號位元(uint raw, double expected)
    {
        var t = Tsod.TemperatureC((ushort)raw);
        Assert.NotNull(t);
        Assert.Equal(expected, t.Value, precision: 4);
    }

    [Theory]
    [InlineData(0x0000u)]
    [InlineData(0xFFFFu)]
    public void 溫度解碼_全0與全F不給值(uint raw)
    {
        Assert.Null(Tsod.TemperatureC((ushort)raw));
    }

    [Fact]
    public void TSOD白名單_界內准_界外與SPD位址拒()
    {
        for (byte a = 0x18; a <= 0x1F; a++) Assert.True(SpdBusAddresses.IsTsodRead(a));
        Assert.False(SpdBusAddresses.IsTsodRead(0x17));
        Assert.False(SpdBusAddresses.IsTsodRead(0x20));
        Assert.False(SpdBusAddresses.IsTsodRead(0x50)); // SPD 位址不在 TSOD 白名單
        Assert.Throws<ArgumentOutOfRangeException>(() => SpdBusAddresses.EnsureTsodRead(0x50));
        Assert.Throws<ArgumentOutOfRangeException>(() => SpdBusAddresses.EnsureTsodRead(0x17));
    }

    [Fact]
    public void 控制器word讀取_走完整狀態機並組回16位元()
    {
        var io = new FakeSmbusIo
        {
            WordRespond = (slave7, cmd) => slave7 == 0x18 && cmd == 0x05 ? (ushort)0x1900 : slave7 == 0x18 ? (ushort)0x2143 : null,
        };
        var controller = new SmbusController(io, FakeSmbusIo.Base);
        Assert.True(controller.TryAcquireBus(out _));

        Assert.Equal((ushort)0x1900, controller.ReadTsodWord(0x18, 0x05)); // 25°C
        Assert.Equal((ushort)0x2143, controller.ReadTsodWord(0x18, 0x06)); // 製造商 ID 原始值
        Assert.Null(controller.ReadTsodWord(0x19, 0x05));                  // 無裝置 → DEV_ERR
        Assert.Equal(SmbusStatus.NoDevice, controller.LastStatus);
        controller.ReleaseBus();
    }

    [Fact]
    public void 掃描_io未注入與無控制器都三態_有裝置逐顆成列()
    {
        var denied = TsodSurveyor.Collect(null, (_, _, _, _) => null, At);
        var d = Assert.Single(denied);
        Assert.Equal(FactAvailability.InsufficientPrivilege, d.Availability);

        var noController = TsodSurveyor.Collect(new FakeSmbusIo(), (_, _, _, _) => 0xFFFF_FFFF, At);
        var nc = Assert.Single(noController);
        Assert.Equal(FactAvailability.NotApplicable, nc.Availability);
        Assert.Contains("找不到 SMBus", nc.UnavailableReason);

        var io = new FakeSmbusIo
        {
            WordRespond = (slave7, cmd) => slave7 == 0x18 && cmd == 0x05 ? (ushort)0x1900
                                       : slave7 == 0x1B && cmd == 0x05 ? (ushort)0x2380 : null,
        };
        var facts = TsodSurveyor.Collect(io, FakeDiscoveryIntelSmbus(), At);
        Assert.Equal(2, facts.Count);
        var t18 = Assert.Single(facts, f => f.Key == "smbus.tsod.0x18");
        Assert.StartsWith("25°C", t18.Value);
        Assert.Equal(25.0, t18.NumericValue);
        Assert.Single(facts, f => f.Key == "smbus.tsod.0x1b");
    }

    [Fact]
    public void 掃描_空匯流排給一條如實的掃描事實_匯流排被占三態()
    {
        var empty = TsodSurveyor.Collect(new FakeSmbusIo(), FakeDiscoveryIntelSmbus(), At);
        var e = Assert.Single(empty);
        Assert.Equal("smbus.tsod", e.Key);
        Assert.Contains("無裝置回應", e.Value);

        var held = TsodSurveyor.Collect(new FakeSmbusIo { InUseHeldByOther = true }, FakeDiscoveryIntelSmbus(), At);
        var h = Assert.Single(held);
        Assert.Equal(FactAvailability.ReadError, h.Availability);
        Assert.Contains("不搶", h.UnavailableReason);
    }

    /// <summary>假的 PCI 讀取器：在 f.4 報一顆 Intel SMBus 控制器（SMB_BASE=0xF040、I2C 模式關）。類別碼 0x0C05 在 bits[31:16]。</summary>
    private static PciDwordReader FakeDiscoveryIntelSmbus() => (bus, device, function, register) =>
        (device, function, register) switch
        {
            (_, 4, 0x00) => 0xF140_8086,          // vendor=Intel、device 任意
            (_, 4, 0x08) => 0x0C05_0000,          // 類別碼：base 0x0C（bits[31:24]）、sub 0x05（bits[23:16]）
            (_, 4, 0x20) => 0x0000_F041,          // SMB_BASE：I/O 空間、0xF040
            (_, 4, 0x40) => 0x0000_0000,          // HOSTC：I2C_EN=0
            _ => 0xFFFF_FFFF,
        };
}
