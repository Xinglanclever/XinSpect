using Xunit;

namespace XinSpect.Tests;

public class UncorePmuServiceTests
{
    private sealed class FakeMsr : UncorePmuService.IMsrReader
    {
        public int Family { get; set; } = 6;
        public int Model { get; set; } = 0x55;
        public int Step { get; set; } = 4;
        public Dictionary<uint, ulong> Values { get; } = new();

        public ulong? ReadMsr(uint index) => Values.TryGetValue(index, out var v) ? v : null;
        public (int, int, int) CpuId() => (Family, Model, Step);
    }

    // ── 平台偵測 ─────────────────────────────────────────

    [Fact]
    public void SkylakeX_Model55_在白名單內()
    {
        var svc = new UncorePmuService(new FakeMsr { Model = 0x55 });
        var s = svc.Detect();
        Assert.True(s.Supported);
        Assert.Contains("Skylake", s.PlatformName);
        Assert.NotEmpty(s.Available);
    }

    [Theory]
    [InlineData(0x8E)]   // Kaby Lake
    [InlineData(0x9E)]   // Coffee Lake
    [InlineData(0xA7)]   // Rocket Lake
    [InlineData(0xB7)]   // Raptor Lake
    public void 非SkylakeX平台_未收錄(int model)
    {
        var svc = new UncorePmuService(new FakeMsr { Model = model });
        var s = svc.Detect();
        Assert.False(s.Supported);
        Assert.Contains("未收錄", s.Status);
    }

    [Fact]
    public void AMD平台_未收錄()
    {
        var svc = new UncorePmuService(new FakeMsr { Family = 0x19, Model = 0x21 });
        var s = svc.Detect();
        Assert.False(s.Supported);
    }

    // ── 合理性檢查 ────────────────────────────────────────

    [Fact]
    public void 全0值_判為未實作()
        => Assert.False(UncorePmuService.IsPlausible(0UL));

    [Fact]
    public void 全1值_判為未實作()
        => Assert.False(UncorePmuService.IsPlausible(ulong.MaxValue));

    [Fact]
    public void Null值_判為未實作()
        => Assert.False(UncorePmuService.IsPlausible(null));

    [Fact]
    public void 合理值_通過()
        => Assert.True(UncorePmuService.IsPlausible(0x0000_1E00_0000_0C00UL));

    // ── 量測 ────────────────────────────────────────────

    [Fact]
    public void 白名單外平台_Measure回空清單()
    {
        var svc = new UncorePmuService(new FakeMsr { Model = 0x9E });
        Assert.Empty(svc.Measure());
    }

    [Fact]
    public void SkylakeX_讀到UncoreRatioLimit()
    {
        var msr = new FakeMsr();
        // MSR 0x620: min ratio = 12 (bits 6:0), max ratio = 30 (bits 14:8)
        msr.Values[0x620] = (30UL << 8) | 12UL;
        msr.Values[0x621] = 24UL;  // current ratio = 24

        var svc = new UncorePmuService(msr);
        var readings = svc.Measure();

        Assert.True(readings.Count >= 2);
        Assert.Contains(readings, r => r.Name.Contains("頻率範圍") && r.ValueText.Contains("12x") && r.ValueText.Contains("30x"));
        Assert.Contains(readings, r => r.Name.Contains("目前倍頻") && r.ValueText.Contains("24x"));
    }

    [Fact]
    public void SkylakeX_MSR全讀不到_回降級訊息()
    {
        var svc = new UncorePmuService(new FakeMsr());
        var readings = svc.Measure();

        Assert.Single(readings);
        Assert.Equal("—", readings[0].ValueText);
        Assert.Contains("未回傳有效值", readings[0].Note);
    }

    // ── 不可達項目明確列出 ────────────────────────────────

    [Fact]
    public void SkylakeX_不可達項目包含CHA與iMC()
    {
        var svc = new UncorePmuService(new FakeMsr());
        var s = svc.Detect();

        Assert.Contains(s.Unavailable, u => u.Contains("CHA") || u.Contains("mesh"));
        Assert.Contains(s.Unavailable, u => u.Contains("iMC"));
    }

    [Fact]
    public void SkylakeX_UPI_PMON位址不確定故不列入()
    {
        var svc = new UncorePmuService(new FakeMsr());
        var s = svc.Detect();
        Assert.Contains(s.Unavailable, u => u.Contains("UPI PMON") && u.Contains("未經實機驗證"));
    }
}
