using System.Runtime.Intrinsics.X86;

namespace XinSpect;

/// <summary>
/// 處理器深化（唯讀 MSR／CPUID）：記憶體加密（TME／SGX）與 package C-state 駐留。
/// 這些是「這台機器的記憶體有沒有被加密、處理器省電狀態分佈」的直接事實——全部唯讀零風險。
/// 平台不支援＝NotSupported、MSR 讀不到＝三態。
/// </summary>
public static class MemoryEncryptionFactsService
{
    private const string Category = "處理器";
    private const uint MsrTmeActivate = 0x982;

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        Func<(uint Eax, uint Ebx, uint Ecx, uint Edx)>? cpuidProbe = null, IKernelMsrReader? msr = null,
        Func<uint, (uint Eax, uint Ebx, uint Ecx, uint Edx)>? cpuid12Probe = null)
    {
        var c7 = (cpuidProbe ?? ReadCpId7)();
        bool tmeSupport = (c7.Ecx & (1u << 25)) != 0;
        bool sgxSupport = (c7.Ecx & (1u << 30)) != 0;

        var facts = new List<HardwareFact>();

        // TME
        if (!tmeSupport)
        {
            facts.Add(Unsupported("mem.encryption.tme", "TME 記憶體加密", "CPUID leaf 7 ECX bit25＝0：此處理器不支援 TME"));
        }
        else if (msr is not { Available: true })
        {
            facts.Add(new HardwareFact("mem.encryption.tme", Category, "TME 記憶體加密", "", "",
                "CPUID leaf 7＋MSR 0x982（TME_ACTIVATE）", FactTrustLevel.Unknown, false, at, null,
                FactAvailability.InsufficientPrivilege, msr?.UnavailableReason ?? "缺 ring0：MSR 讀取未就緒"));
        }
        else
        {
            ulong? raw = msr.ReadMsr(MsrTmeActivate);
            facts.Add(raw is { } v
                ? TmeFact(at, MemoryEncryptionDecoder.DecodeTmeActivate(v))
                : Unavailable("mem.encryption.tme", "TME 記憶體加密", "MSR 0x982 讀取失敗"));
        }

        // SGX＋EPC 逐區列舉（U10-CP-0148：cpuid.12.epc{n}，鍵隨服務命名空間落為 mem.encryption.sgx.epc.{i}）
        if (!sgxSupport)
        {
            facts.Add(Unsupported("mem.encryption.sgx", "SGX 安全飛地", "CPUID leaf 7 ECX bit30＝0：此處理器不支援（或已停用）SGX"));
        }
        else
        {
            var probe12 = cpuid12Probe ?? ReadCpuid12Subleaf;
            var regions = new List<SgxEpcRegion>();
            const uint FirstEpcSubleaf = 2;   // sub 0/1 是能力與屬性葉；EPC 區自 sub 2 起（SDM Table 41-3）
            for (uint sub = FirstEpcSubleaf; sub < FirstEpcSubleaf + 32; sub++)
            {
                var (e, b, c, d) = probe12(sub);
                var r = MemoryEncryptionDecoder.DecodeEpcRegion(e, b, c, d);
                if (!r.Valid) break;
                regions.Add(r);
            }

            facts.Add(regions.Count == 0
                ? new HardwareFact("mem.encryption.sgx", Category, "SGX 安全飛地",
                    "支援（EPC 列舉得 0 區——BIOS 未配置 EPC 或本葉讀值全部無效，兩種可能分不開，不猜）", "",
                    "CPUID leaf 7 ECX bit30＋leaf 0x12 sub≥2", FactTrustLevel.Measured, false, at, 0,
                    FactAvailability.Present)
                : new HardwareFact("mem.encryption.sgx", Category, "SGX 安全飛地",
                    $"支援（EPC {regions.Count} 區，逐區見 mem.encryption.sgx.epc.*）", "",
                    "CPUID leaf 7 ECX bit30＋leaf 0x12 sub≥2", FactTrustLevel.Measured, false, at, regions.Count));

            for (int i = 0; i < regions.Count; i++)
            {
                var r = regions[i];
                string size = r.Size >= 1 << 20
                    ? $"{r.Size / (double)(1 << 20):F1} MiB（{r.Size} bytes）"
                    : $"{r.Size} bytes";
                facts.Add(new HardwareFact($"mem.encryption.sgx.epc.{i}", Category, $"SGX EPC 區 {i}",
                    $"Base 0x{r.Base:X}・Size {size}", "",
                    "CPUID leaf 0x12 sub≥2（Intel SDM Table 41-3；解碼 MemoryEncryptionDecoder.DecodeEpcRegion）",
                    FactTrustLevel.Measured, false, at, r.Size));
            }
        }
        return facts;
    }

    private static HardwareFact TmeFact(DateTimeOffset at, TmeActivateState state) =>
        new("mem.encryption.tme", Category, "TME 記憶體加密",
            state.Enabled ? $"已啟用（{state.Algorithm}）" : $"支援，未啟用（現值演算法欄＝{state.Algorithm}）", "",
            "CPUID leaf 7＋MSR 0x982（TME_ACTIVATE）", FactTrustLevel.Measured, false, at, null);

    private static HardwareFact Unsupported(string key, string name, string reason) =>
        new(key, Category, name, "", "", "CPUID leaf 7", FactTrustLevel.Unknown, false, DateTimeOffset.UtcNow,
            null, FactAvailability.NotSupported, reason);

    private static HardwareFact Unavailable(string key, string name, string reason) =>
        new(key, Category, name, "", "", "CPUID leaf 7＋MSR 0x982", FactTrustLevel.Unknown, false,
            DateTimeOffset.UtcNow, null, FactAvailability.ReadError, reason);

    private static (uint, uint, uint, uint) ReadCpId7()
    {
        if (!X86Base.IsSupported) return (0, 0, 0, 0);
        var r = X86Base.CpuId(unchecked((int)7), 0);
        return (unchecked((uint)r.Eax), unchecked((uint)r.Ebx), unchecked((uint)r.Ecx), unchecked((uint)r.Edx));
    }

    /// <summary>
    /// leaf 0x12 逐子葉的預設讀取：葉 0 的最大標準葉不到 0x12 時回全 0
    /// （解碼後 Valid＝false，列舉即止）——「沒有這個葉」與「EPC 為空」在事實層同樣如實呈現。
    /// </summary>
    private static (uint, uint, uint, uint) ReadCpuid12Subleaf(uint sub)
    {
        if (!X86Base.IsSupported) return (0, 0, 0, 0);
        if ((uint)X86Base.CpuId(0, 0).Eax < 0x12) return (0, 0, 0, 0);
        var r = X86Base.CpuId(unchecked((int)0x12), (int)sub);
        return (unchecked((uint)r.Eax), unchecked((uint)r.Ebx), unchecked((uint)r.Ecx), unchecked((uint)r.Edx));
    }
}

/// <summary>Package C-state 駐留時間（µs，Intel 架構 MSR，唯讀）。平台未實作的項如實三態。</summary>
public static class CStateResidencyFactsService
{
    private const string Category = "處理器";
    private static readonly (uint Msr, string Key, string Name)[] Targets =
    [
        (0x60D, "cpu.pkg_c2_us", "Package C2 駐留"),
        (0x3FC, "cpu.pkg_c3_us", "Package C3 駐留"),
        (0x3F9, "cpu.pkg_c6_us", "Package C6 駐留"),
        (0x3FA, "cpu.pkg_c7_us", "Package C7 駻留"),
    ];

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at, IKernelMsrReader? msr)
    {
        if (msr is not { Available: true })
            return Targets.Select(t => new HardwareFact(t.Key, Category, t.Name, "", "",
                "MSR（Package C-state residency，µs）", FactTrustLevel.Unknown, false, at, null,
                FactAvailability.InsufficientPrivilege, msr?.UnavailableReason ?? "缺 ring0：MSR 讀取未就緒")).ToList();

        return Targets.Select(t =>
        {
            ulong? raw = msr.ReadMsr(t.Msr);
            return raw is { } v
                ? new HardwareFact(t.Key, Category, t.Name, $"{v} µs（累計）", "µs",
                    $"MSR 0x{t.Msr:X}", FactTrustLevel.Measured, false, at, v)
                : new HardwareFact(t.Key, Category, t.Name, "", "", $"MSR 0x{t.Msr:X}",
                    FactTrustLevel.Unknown, false, at, null, FactAvailability.NotSupported,
                    "此 MSR 讀取失敗或平台未實作——如實標");
        }).ToList();
    }
}
