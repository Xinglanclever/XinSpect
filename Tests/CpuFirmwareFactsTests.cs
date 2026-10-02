using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// CPU 韌體身分事實的解碼契約：微碼登錄檔佈局不猜（恰一個 DWORD 非零才取值、歧義誠實標）、
/// 逐核不一致不取單核冒充全機、TjMax 取 bits[23:16]。收集端以假讀取器＋注入探測驗證，不碰硬體。
/// </summary>
public class CpuFirmwareFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void 微碼MSR_全核一致給值_逐核不一致誠實標_全失敗標讀取錯誤()
    {
        var ok = CpuFirmwareFactsService.MicrocodeMsrFact(At, [0x02007006u, 0x02007006u, 0x02007006u], null);
        Assert.Equal(FactAvailability.Present, ok.Availability);
        Assert.Equal("0x02007006", ok.Value);
        Assert.Equal(0x02007006u, ok.NumericValue);

        var mixed = CpuFirmwareFactsService.MicrocodeMsrFact(At, [0x02007006u, 0x01007006u], null);
        Assert.Equal(FactAvailability.ReadError, mixed.Availability);
        Assert.Contains("逐核不一致", mixed.UnavailableReason);
        Assert.Null(mixed.NumericValue);

        var allNull = CpuFirmwareFactsService.MicrocodeMsrFact(At, [null, null], null);
        Assert.Equal(FactAvailability.ReadError, allNull.Availability);

        var unavailable = CpuFirmwareFactsService.MicrocodeMsrFact(At, [], "缺 ring0");
        Assert.Equal(FactAvailability.InsufficientPrivilege, unavailable.Availability);
        Assert.Equal("缺 ring0", unavailable.UnavailableReason);
    }

    [Fact]
    public void 微碼登錄檔_兩種佈局都取非零DWORD_雙非零標歧義_雙零如實報()
    {
        var revAtD0 = CpuFirmwareFactsService.MicrocodeRegistryFact(At, [0x06, 0x70, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00]);
        Assert.Equal(FactAvailability.Present, revAtD0.Availability);
        Assert.Equal(0x02007006u, revAtD0.NumericValue);
        Assert.Contains(Convert.ToHexString(new byte[] { 0x06, 0x70, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00 }), revAtD0.Value);

        var revAtD4 = CpuFirmwareFactsService.MicrocodeRegistryFact(At, [0x00, 0x00, 0x00, 0x00, 0x2A, 0x00, 0x00, 0x00]);
        Assert.Equal(FactAvailability.Present, revAtD4.Availability);
        Assert.Equal(0x2Au, revAtD4.NumericValue);

        byte[] ambiguous = [0x01, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00];
        var both = CpuFirmwareFactsService.MicrocodeRegistryFact(At, ambiguous);
        Assert.Equal(FactAvailability.ReadError, both.Availability);
        Assert.Contains("佈局歧義", both.UnavailableReason);
        Assert.Contains(Convert.ToHexString(ambiguous), both.UnavailableReason);
        Assert.Null(both.NumericValue);

        var zero = CpuFirmwareFactsService.MicrocodeRegistryFact(At, new byte[8]);
        Assert.Equal(FactAvailability.Present, zero.Availability);
        Assert.Equal(0u, zero.NumericValue);
        Assert.Contains("可能未載入", zero.Value);
    }

    [Fact]
    public void 微碼登錄檔_缺值與短值都三態()
    {
        var missing = CpuFirmwareFactsService.MicrocodeRegistryFact(At, null);
        Assert.Equal(FactAvailability.NotSupported, missing.Availability);

        var short_ = CpuFirmwareFactsService.MicrocodeRegistryFact(At, [0x01, 0x02, 0x03]);
        Assert.Equal(FactAvailability.ReadError, short_.Availability);
        Assert.Contains("3 位元組", short_.UnavailableReason);
    }

    [Fact]
    public void TjMax取bits高段_讀不到三態()
    {
        var ok = CpuFirmwareFactsService.TjMaxFact(At, 0x005A0000, null);
        Assert.Equal(FactAvailability.Present, ok.Availability);
        Assert.Equal(90u, ok.NumericValue);
        Assert.Equal("°C", ok.Unit);

        var fail = CpuFirmwareFactsService.TjMaxFact(At, null, null);
        Assert.Equal(FactAvailability.ReadError, fail.Availability);

        var denied = CpuFirmwareFactsService.TjMaxFact(At, null, "缺 ring0");
        Assert.Equal(FactAvailability.InsufficientPrivilege, denied.Availability);
    }

    [Fact]
    public void 收集端_不可用讀取器整組三態_可用讀取器逐核讀()
    {
        var denied = CpuFirmwareFactsService.Collect(new FakeMsr(available: false, value: null), At, registryProbe: () => null);
        Assert.All(denied, f => Assert.NotEqual(FactAvailability.Present, f.Availability));

        var ok = CpuFirmwareFactsService.Collect(new FakeMsr(available: true, value: 0x005A0000), At,
            registryProbe: () => [0x06, 0x70, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00]);
        Assert.Equal(FactAvailability.Present, Assert.Single(ok, f => f.Key == "msr.0x8b").Availability);
        Assert.Equal(FactAvailability.Present, Assert.Single(ok, f => f.Key == "reg.microcode").Availability);
        Assert.Equal(FactAvailability.Present, Assert.Single(ok, f => f.Key == "cpu.tjmax").Availability);
        Assert.Equal(0x02007006u, Assert.Single(ok, f => f.Key == "reg.microcode").NumericValue);
    }

    private sealed class FakeMsr(bool available, ulong? value) : IKernelMsrReader
    {
        public bool Available { get; } = available;
        public string? UnavailableReason => Available ? null : "假 MSR 不可用";
        public ulong? ReadMsr(uint index) => value;
    }
}
