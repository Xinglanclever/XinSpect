using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// GPU 非 NVIDIA 事實（R6）的契約：loader 不存在時整組 NotApplicable（不是三態噪音）；
/// 有快照時三個事實都帶「未在本機驗證」標記。Level Zero interop 本體本機無法驗證——
/// 行為形狀以注入的後端假件釘住。
/// </summary>
public class LevelZeroFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 7, 0, 0, 0, TimeSpan.Zero);

    private sealed class FakeBackend(LevelZeroGpuSnapshot? snapshot) : ILevelZeroBackend
    {
        public LevelZeroGpuSnapshot? CollectSingleGpu() => snapshot;
    }

    [Fact]
    public void 沒有loader時整組NotApplicable()
    {
        var facts = LevelZeroFactsService.Collect(At, new FakeBackend(null));
        Assert.Equal(3, facts.Count);
        Assert.All(facts, f => Assert.Equal(FactAvailability.NotApplicable, f.Availability));
        Assert.Contains(facts, f => f.Key == "gpu.lz.name");
    }

    [Fact]
    public void 有GPU快照時三事實都帶未驗證標記()
    {
        var snapshot = new LevelZeroGpuSnapshot("Intel(R) Arc(TM) A770 Graphics", 0x8086, 0x56A0,
            MemoryBytes: 16UL * 1024 * 1024 * 1024, MemoryClockMhz: 2187, BusWidthBits: 256, DriverVersion: 0x01007F80);
        var facts = LevelZeroFactsService.Collect(At, new FakeBackend(snapshot));

        var name = Assert.Single(facts, f => f.Key == "gpu.lz.name");
        Assert.Equal(FactAvailability.Present, name.Availability);
        Assert.Contains("A770", name.Value);
        Assert.Contains("未在本機驗證", name.Source);

        var mem = Assert.Single(facts, f => f.Key == "gpu.lz.memory");
        Assert.Contains("16 GB", mem.Value);
        Assert.Contains("2187", mem.Value);
        Assert.Contains("256", mem.Value);

        var ver = Assert.Single(facts, f => f.Key == "gpu.lz.driver_version");
        Assert.Contains("0x01007F80", ver.Value);
    }
}
