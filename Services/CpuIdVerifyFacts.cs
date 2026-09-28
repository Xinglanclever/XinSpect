using System.Runtime.Intrinsics.X86;

namespace XinSpect;

/// <summary>
/// CPUID／拓撲直讀出來的驗機事實（改標與拼裝偵測）。CPUID 由矽晶直接回答，故信賴度是
/// <see cref="FactTrust.Native"/>；「矽晶推算基礎頻率」另需 MSR 0xCE，走
/// <see cref="FrequencyTruthService.MeasureSiliconBaseMhz"/> 的唯讀量測。
/// </summary>
/// <remarks>
/// <b>唯讀</b>：只讀 CPUID 與（經橋接的）MSR，絕不寫。讀不到就不產出該事實，交給引擎判「無法判定」。
/// </remarks>
public static class CpuIdVerifyFacts
{
    public static IReadOnlyList<VerifyFact> Collect(DateTime now)
    {
        var list = new List<VerifyFact>();
        if (!X86Base.IsSupported) return list;

        string brand = BrandString();
        if (brand.Length > 0)
            list.Add(Cpuid(FactId.CpuBrandString, brand, null, "", "CPUID 0x80000002–4", now));

        int hyp = ((uint)X86Base.CpuId(1, 0).Ecx & 0x8000_0000u) != 0 ? 1 : 0;
        list.Add(Cpuid(FactId.HypervisorPresent, hyp == 1 ? "是" : "否", hyp, "", "CPUID 1 ECX 位 31", now));

        if (MaxLeaf() >= 0x16)
        {
            uint baseMhz = (uint)X86Base.CpuId(0x16, 0).Eax & 0xFFFF;
            if (baseMhz > 0)
                list.Add(Cpuid(FactId.CpuBrandClaimedMhz, $"{baseMhz} MHz", baseMhz, "MHz", "CPUID 0x16 EAX", now));
        }

        int hybrid = MaxLeaf() >= 7 && (((uint)X86Base.CpuId(7, 0).Edx >> 15) & 1) != 0 ? 1 : 0;

        var topo = SafeTopology();
        if (topo is { PhysicalCores: > 0 } && TryCaches(topo, out long l3, out long l2Total))
        {
            list.Add(Cpuid(FactId.CpuL3Bytes, Bytes(l3), l3, "", "CPUID leaf 4（L3 描述元）", now));
            list.Add(Cpuid(FactId.CpuL2TotalBytes, Bytes(l2Total), l2Total, "", "CPUID leaf 4（L2 描述元×核心數）", now));
            list.Add(Cpuid(FactId.CpuPhysicalCores, topo.PhysicalCores.ToString(), topo.PhysicalCores, "",
                "GetLogicalProcessorInformationEx", now));
            list.Add(Cpuid(FactId.CpuIsHybrid, hybrid == 1 ? "是（大小核）" : "否", hybrid, "", "CPUID 7.0 EDX 位 15", now));
        }

        if (FrequencyTruthService.MeasureSiliconBaseMhz() is { } sil && sil > 0)
            list.Add(new VerifyFact(FactId.CpuSiliconBaseMhz, FactCatalog.Name(FactId.CpuSiliconBaseMhz),
                $"{sil:N0} MHz", sil, "MHz", FactSource.Msr,
                "MSR 0xCE 最大非睿頻倍頻 × 實測 BCLK", true, FactTrust.Native, now));

        return list;
    }

    private static VerifyFact Cpuid(FactId id, string value, double? num, string unit, string method, DateTime now)
        => new(id, FactCatalog.Name(id), value, num, unit, FactSource.Cpuid, method, false, FactTrust.Native, now);

    private static uint MaxLeaf() => (uint)X86Base.CpuId(0, 0).Eax;

    private static string BrandString()
    {
        if ((uint)X86Base.CpuId(unchecked((int)0x80000000), 0).Eax < 0x80000004u) return "";
        var bytes = new byte[48];
        int o = 0;
        int[] leaves = { unchecked((int)0x80000002), unchecked((int)0x80000003), unchecked((int)0x80000004) };
        foreach (int leaf in leaves)
        {
            var r = X86Base.CpuId(leaf, 0);
            foreach (int reg in new[] { r.Eax, r.Ebx, r.Ecx, r.Edx })
                for (int i = 0; i < 4; i++) bytes[o++] = (byte)(reg >> (i * 8));
        }
        int len = Array.IndexOf(bytes, (byte)0);
        if (len < 0) len = 48;
        return System.Text.Encoding.ASCII.GetString(bytes, 0, len).Trim();
    }

    private static CpuTopology? SafeTopology()
    {
        try { return CpuTopologyService.Build(); }
        catch (Exception ex) { Diag.Swallow("CpuIdVerifyFacts.Topology", ex, "L2/L3/核心數讀不到，略過 R-CPU-03"); return null; }
    }

    /// <summary>從 CPUID leaf 4 取 L3（單一描述元＝整包共用即總量）與 L2 總量（單核 L2 × L2 個數）。</summary>
    private static bool TryCaches(CpuTopology topo, out long l3, out long l2Total)
    {
        l3 = 0; l2Total = 0;
        if (MaxLeaf() < 4) return false;
        long perL2 = 0; int l2ThreadsSharing = 0;
        for (int i = 0; i < 16; i++)
        {
            var (eax, ebx, ecx, _) = X86Base.CpuId(4, i);
            int type = eax & 0x1F;
            if (type == 0) break;                       // 0＝再無更多快取描述元
            int level = (eax >> 5) & 0x7;
            long ways = ((ebx >> 22) & 0x3FF) + 1;
            long parts = ((ebx >> 12) & 0x3FF) + 1;
            long line = (ebx & 0xFFF) + 1;
            long sets = (uint)ecx + 1L;
            long size = ways * parts * line * sets;
            if (level == 3) l3 = size;                  // L3 整包共用一份，即總量
            else if (level == 2) { perL2 = size; l2ThreadsSharing = ((eax >> 14) & 0xFFF) + 1; }
        }
        if (perL2 == 0) return l3 > 0;                  // 少數平台無 L2 描述元；至少回報 L3
        int threadsPerCore = topo.LogicalProcessors > 0 && topo.PhysicalCores > 0
            ? Math.Max(1, topo.LogicalProcessors / topo.PhysicalCores) : 1;
        int coresSharingL2 = Math.Max(1, l2ThreadsSharing / threadsPerCore);
        l2Total = perL2 * (topo.PhysicalCores / coresSharingL2);
        return true;
    }

    private static string Bytes(long b) => b <= 0 ? "0"
        : b >= 1 << 20 ? $"{b / (1024.0 * 1024):0.##} MiB" : $"{b / 1024.0:0.##} KiB";
}
