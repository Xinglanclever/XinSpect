namespace XinSpect;

/// <summary>PMU 能力（CPUID leaf 0xA 解碼結果）。</summary>
public sealed record PmuCapability(uint Version, uint GeneralCounters, uint GeneralWidthBits,
    uint FixedCounters, uint FixedWidthBits);

/// <summary>
/// WP27 PMU 能力探索的純解碼器：CPUID leaf 0xA——零特權、零副作用。
/// <b>本解碼器只做能力探索</b>：PMU 編程（寫 IA32_PERFEVTSEL 之類的 MSR）需要沙箱驗證方案
/// 完成（docs/PMU-SANDBOX-PLAN.md），驗證前不實作。
/// </summary>
public static class PmuDecoder
{
    /// <summary>解 CPUID leaf 0xA 的能力欄位。版本 0＝無 PMU。</summary>
    [SpecRef("Intel SDM Vol.3B, Table 19-1（CPUID leaf 0xA）：EAX bits[7:0]＝PMU 版本、bits[15:8]＝每邏輯 CPU 通用計數器數、bits[23:16]＝通用計數器位寬；EDX bits[4:0]＝固定功能計數器數、bits[12:5]＝固定計數器位寬")]
    public static PmuCapability DecodeCapability(uint eax, uint edx) => new(
        eax & 0xFF,
        (eax >> 8) & 0xFF,
        (eax >> 16) & 0xFF,
        edx & 0x1F,
        (edx >> 5) & 0xFF);
}
