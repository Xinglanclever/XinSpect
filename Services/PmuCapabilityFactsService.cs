using System.Runtime.Intrinsics.X86;

namespace XinSpect;

/// <summary>
/// WP27 PMU 第一階段事實：能力探索（CPUID 0xA，usermode）＋固定計數器**唯讀**觀察
/// （MSR 0x309–0x30B，經既有驅動通路）。誠實界線：讀值通路驗證≠編程驗證——
/// 未啟用編程前計數器值可能為 0 或殘留舊值，事實上明說；**編程（寫入）路徑未實作**，
/// 等 docs/PMU-SANDBOX-PLAN.md 的驗證完成。
/// </summary>
public static class PmuCapabilityFactsService
{
    private const string Category = "處理器";
    private const string Source = "CPUID leaf 0xA＋MSR 0x309–0x30B（唯讀觀察）";
    private const uint MsrFixedCounterBase = 0x309;

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        Func<(uint Eax, uint Ebx, uint Ecx, uint Edx)>? cpuidProbe = null, IKernelMsrReader? msr = null)
    {
        (uint, uint, uint, uint) raw = cpuidProbe?.Invoke() ?? ReadCpId0xA();
        var caps = PmuDecoder.DecodeCapability(raw.Item1, raw.Item4);
        if (caps.Version == 0)
        {
            return [new HardwareFact("pmu.version", Category, "PMU 版本", "", "",
                "CPUID leaf 0xA", FactTrustLevel.Unknown, false, at, null, FactAvailability.NotSupported,
                "CPUID 0xA 版本＝0：此處理器沒有架構 PMU——無此硬體不是錯誤")];
        }

        var facts = new List<HardwareFact>
        {
            new("pmu.version", Category, "PMU 版本", caps.Version.ToString(), "",
                "CPUID leaf 0xA", FactTrustLevel.Measured, false, at, caps.Version),
            new("pmu.general_counters", Category, "通用計數器",
                $"{caps.GeneralCounters} 個 × {caps.GeneralWidthBits} bits", "個",
                "CPUID leaf 0xA", FactTrustLevel.Measured, false, at, caps.GeneralCounters),
            new("pmu.fixed_counters", Category, "固定功能計數器",
                $"{caps.FixedCounters} 個 × {caps.FixedWidthBits} bits", "個",
                "CPUID leaf 0xA", FactTrustLevel.Measured, false, at, caps.FixedCounters),
        };

        if (msr is not { Available: true })
        {
            facts.Add(new HardwareFact("pmu.fixed.0", Category, "固定計數器 0（唯讀觀察）", "", "",
                Source, FactTrustLevel.Unknown, false, at, null, FactAvailability.InsufficientPrivilege,
                msr?.UnavailableReason ?? "缺 ring0：MSR 讀取未就緒"));
            return facts;
        }

        for (uint i = 0; i < Math.Min(caps.FixedCounters, 3); i++)
        {
            ulong? value = msr.ReadMsr(MsrFixedCounterBase + i);
            facts.Add(value is { } v
                ? new HardwareFact($"pmu.fixed.{i}", Category, $"固定計數器 {i}（唯讀觀察）",
                    $"0x{v:X}（未啟用編程——值可能為 0 或殘留舊值，僅驗證通路）", "",
                    Source, FactTrustLevel.Measured, false, at, (double)v)
                : UnavailableOne($"pmu.fixed.{i}", at));
        }
        return facts;
    }

    private static HardwareFact UnavailableOne(string key, DateTimeOffset at) =>
        new(key, Category, $"固定計數器 {key[^1]}（唯讀觀察）", "", "", Source,
            FactTrustLevel.Unknown, false, at, null, FactAvailability.ReadError, "MSR 讀取失敗");

    private static (uint, uint, uint, uint) ReadCpId0xA()
    {
        if (!X86Base.IsSupported) return (0, 0, 0, 0);
        var r = X86Base.CpuId(unchecked((int)0xA), 0);
        return (unchecked((uint)r.Eax), unchecked((uint)r.Ebx), unchecked((uint)r.Ecx), unchecked((uint)r.Edx));
    }
}
