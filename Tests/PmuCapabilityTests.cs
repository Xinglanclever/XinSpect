using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// WP27 PMU 第一階段（沙箱驗證方案先行）：**能力探索純解碼**（CPUID leaf 0xA——零特權零副作用）
/// 與**唯讀 MSR 觀察**（固定計數器 0x309–0x30B，經既有驅動通路，不寫入不啟用）。
/// PMU 編程（寫 IA32_PERFEVTSEL）不在本階段——沙箱驗證方案（docs/PMU-SANDBOX-PLAN.md）
/// 完成並實機驗證前不出貨。版本 0＝無 PMU（NotSupported）。
/// </summary>
public class PmuCapabilityTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CPUID0xA_能力欄位逐項釘值()
    {
        // Skylake 桌面典型：版本 1、通用計數器 4、位寬 48；固定計數器 3、位寬 48
        var caps = XinSpect.PmuDecoder.DecodeCapability(
            eax: (1u << 0) | (4u << 8) | (48u << 16),
            edx: (3u << 0) | (48u << 5));
        Assert.Equal(1u, caps.Version);
        Assert.Equal(4u, caps.GeneralCounters);
        Assert.Equal(48u, caps.GeneralWidthBits);
        Assert.Equal(3u, caps.FixedCounters);
        Assert.Equal(48u, caps.FixedWidthBits);

        Assert.Equal(0u, XinSpect.PmuDecoder.DecodeCapability(0, 0).Version); // 無 PMU
    }

    [Fact]
    public void PMU能力_無PMU與缺ring0分得清楚()
    {
        var noPmu = XinSpect.PmuCapabilityFactsService.Collect(At,
            cpuidProbe: () => (0u, 0u, 0u, 0u), msr: new DeniedMsr());
        Assert.Equal(FactAvailability.NotSupported,
            Assert.Single(noPmu, f => f.Key == "pmu.version").Availability);

        var noRing0 = XinSpect.PmuCapabilityFactsService.Collect(At,
            cpuidProbe: () => ((uint)1 | (4u << 8) | (48u << 16), 0u, 0u, (3u << 0) | (48u << 5)),
            msr: new DeniedMsr());
        Assert.Equal(FactAvailability.Present,
            Assert.Single(noRing0, f => f.Key == "pmu.version").Availability);
        Assert.Equal(FactAvailability.InsufficientPrivilege,
            Assert.Single(noRing0, f => f.Key == "pmu.fixed.0").Availability);
    }

    [Fact]
    public void PMU唯讀觀察_可讀帶原始值_不宣稱啟用()
    {
        var facts = XinSpect.PmuCapabilityFactsService.Collect(At,
            cpuidProbe: () => ((uint)1 | (4u << 8) | (48u << 16), 0u, 0u, (3u << 0) | (48u << 5)),
            msr: new ReadingMsr());
        var fixed0 = Assert.Single(facts, f => f.Key == "pmu.fixed.0");
        Assert.Equal(FactAvailability.Present, fixed0.Availability);
        Assert.Contains("0x2A", fixed0.Value);
        Assert.Contains("未啟用", fixed0.Value);   // 誠實：值可能是 0 或舊值，通路驗證不是編程驗證
    }

    private sealed class DeniedMsr : IKernelMsrReader
    {
        public bool Available => false;
        public string? UnavailableReason => "缺 ring0（測試假件）";
        public ulong? ReadMsr(uint index) => null;
    }

    private sealed class ReadingMsr : IKernelMsrReader
    {
        public bool Available => true;
        public string? UnavailableReason => null;
        public ulong? ReadMsr(uint index) => index is >= 0x309 and <= 0x30B ? 0x2A : null;
    }
}
