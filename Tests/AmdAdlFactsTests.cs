using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// Radeon ADL 深度遙測（R8）的契約：沒有 AMD 卡／DLL 時整組 NotApplicable；
/// 有快照時溫度／風扇／功耗三事實帶「未在本機驗證」標記。interop 本體（DLL 載入、
/// 單位換算）無法在本機驗證——行為形狀以注入的後端假件釘住，換算公式以
/// OpenHardwareMonitor 的長期實作為出處（已寫進 interop 的 SpecRef 註解）。
/// </summary>
public class AmdAdlFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 7, 0, 0, 0, TimeSpan.Zero);

    private sealed class FakeBackend(AmdGpuSnapshot? snapshot) : IAmdAdlBackend
    {
        public AmdGpuSnapshot? CollectSingleGpu() => snapshot;
    }

    [Fact]
    public void 沒有Radeon時整組NotApplicable()
    {
        var facts = AmdAdlFactsService.Collect(At, new FakeBackend(null));
        Assert.Equal(3, facts.Count);
        Assert.All(facts, f => Assert.Equal(FactAvailability.NotApplicable, f.Availability));
        Assert.Contains(facts, f => f.Key == "gpu.radeon.temp");
    }

    [Fact]
    public void 有遙測快照時三事實帶未驗證標記()
    {
        var snapshot = new AmdGpuSnapshot("AMD Radeon RX 9070 XT", 0, 3, 0, 0,
            TempC: 61.0, FanRpm: 1450, PowerW: 233.0, GfxClockMhz: 2900);
        var facts = AmdAdlFactsService.Collect(At, new FakeBackend(snapshot));

        var name = Assert.Single(facts, f => f.Key == "gpu.radeon.name");
        Assert.Contains("RX 9070 XT", name.Value);
        Assert.Contains("未在本機驗證", name.Source);

        Assert.Equal(61.0, Assert.Single(facts, f => f.Key == "gpu.radeon.temp").NumericValue);
        Assert.Equal(1450, Assert.Single(facts, f => f.Key == "gpu.radeon.fan").NumericValue);
        Assert.Equal(233.0, Assert.Single(facts, f => f.Key == "gpu.radeon.power").NumericValue);
    }

    [Fact]
    public void 驅動不回報的項如實NotSupported()
    {
        var snapshot = new AmdGpuSnapshot("AMD Radeon", 0, 3, 0, 0,
            TempC: null, FanRpm: null, PowerW: null, GfxClockMhz: null);
        var facts = AmdAdlFactsService.Collect(At, new FakeBackend(snapshot));

        Assert.All(facts.Where(f => f.Key != "gpu.radeon.name"), f =>
            Assert.Equal(FactAvailability.NotSupported, f.Availability));
        Assert.All(facts.Where(f => f.Key != "gpu.radeon.name"), f =>
            Assert.NotEqual(FactAvailability.NotApplicable, f.Availability)); // 卡在＝不適用語意不對
    }
}
