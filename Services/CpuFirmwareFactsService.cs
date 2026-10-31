using Microsoft.Win32;

namespace XinSpect;

/// <summary>
/// CPU 韌體身分事實（供交叉對帳 WP5）：目前生效微碼修訂版的兩個獨立視角——
/// CPU 自己說的（MSR 0x8B bits[63:32]，逐實體核綁定讀取）與 Windows 說的
/// （登錄檔 Update Revision）——加上 TjMax（MSR 0x1A2 bits[23:16]）。
/// 讀不到標三態；微碼逐核不一致時誠實標 ReadError 帶逐核清單，不取第一核冒充全機。
/// </summary>
public static class CpuFirmwareFactsService
{
    private const string Category = "韌體安全";
    private const uint MsrBiosSignId = 0x8B;
    private const uint MsrTemperatureTarget = 0x1A2;

    public static IReadOnlyList<HardwareFact> Collect(IKernelMsrReader msr, DateTimeOffset at,
        Func<byte[]?>? registryProbe = null, Func<uint?>? cpuIdProbe = null, Func<string?>? vendorProbe = null,
        Func<CoreKind>? coreKindProbe = null)
    {
        byte[]? reg = (registryProbe ?? ReadUpdateRevision)();
        uint? signature = (cpuIdProbe ?? CpuGeneration.ReadSignature)();
        string? vendor = (vendorProbe ?? CpuUarchTable.ReadVendor)();
        if (!msr.Available)
        {
            string reason = msr.UnavailableReason ?? "缺 ring0：特權讀取未就緒";
            return [MicrocodeMsrFact(at, [], reason), MicrocodeRegistryFact(at, reg), TjMaxFact(at, null, reason),
                    GenerationFact(signature, at), UarchFact(signature, vendor, at, coreKindProbe)];
        }

        var revisions = new List<uint?>();
        foreach (var core in CpuAffinity.PhysicalCores(CpuAffinity.IsMultiGroup, ulong.MaxValue))
        {
            using var pin = CpuAffinity.Pinned(core.First); // 綁到該實體核，RDMSR 才讀得到那顆核的值
            revisions.Add(msr.ReadMsr(MsrBiosSignId) is { } sign ? (uint)(sign >> 32) : null);
        }
        ulong? tempTarget = msr.ReadMsr(MsrTemperatureTarget);
        return [MicrocodeMsrFact(at, revisions, null), MicrocodeRegistryFact(at, reg), TjMaxFact(at, tempTarget, null),
                GenerationFact(signature, at), UarchFact(signature, vendor, at, coreKindProbe)];
    }

    /// <summary>
    /// 微架構事實：廠商字串 ＋ family／model → 核心（Golden Cove、Gracemont、Zen 4……）。
    /// 與 <see cref="GenerationFact"/> 的差別是「行銷世代」與「核心」：同一個世代可能有兩種核心，
    /// 同一種核心也會橫跨多個世代，要判斷暫存器佈局需要的是後者。對照表未收錄時標 Unknown，不猜。
    ///
    /// <para>
    /// <b>兩張表，各管一段。</b>Intel 先問 <see cref="MicroarchProfile"/>——那是本專案既有、
    /// 給 TMA 分母把關用的同一份事實，它認得 0x97／0xB7／0xA7 這些<b>用戶端</b>型號，也分得出
    /// 大小核（同一個 model 在 P 核與 E 核上回不同答案）。其餘（AMD、Hygon、cpuinfo 收錄但
    /// MicroarchProfile 沒有的舊世代）才走 <see cref="CpuUarchTable"/>，那一張是 pytorch/cpuinfo
    /// 的 <c>src/x86/uarch.c</c> 逐格搬過來的。反過來只靠 cpuinfo 不行：它的最新幾格是伺服器
    /// 優先（0x8F／0xCF／0xAD），桌機的 0x97／0xB7／0xAA 根本沒有 case——照抄會讓一台
    /// i5-13600K 顯示「未收錄」。
    /// </para>
    /// </summary>
    public static HardwareFact UarchFact(uint? signature, string? vendor, DateTimeOffset at,
        Func<CoreKind>? coreKindProbe = null)
    {
        const string key = "cpu.uarch", name = "處理器微架構（CPUID 對照）",
            source = "CPUID leaf 0（廠商字串）＋ leaf 1 EAX（family／model，Intel 另讀 0x1A 分大小核）；"
                   + "對照表：Intel 用 MicroarchProfile，其餘用 pytorch/cpuinfo";
        if (signature is not { } eax)
            return new HardwareFact(key, Category, name, "", "", source, FactTrustLevel.Unknown, false, at, null,
                FactAvailability.NotSupported, "CPUID 不可用（非 x86 環境）");

        var (family, model, stepping) = CpuGeneration.DecodeSignature(eax);
        string sig = $"family {family}、model 0x{model:X2}、stepping {stepping}";
        if (string.IsNullOrWhiteSpace(vendor))
            return new HardwareFact(key, Category, name, "", "", source, FactTrustLevel.Unknown, false, at, null,
                FactAvailability.Unknown, "讀不到廠商字串（CPUID leaf 0 無回應）——不猜");

        if (vendor == "GenuineIntel")
        {
            CoreKind kind = coreKindProbe?.Invoke() ?? ReadCoreKind();
            var info = MicroarchProfile.Identify(family, model, kind);
            if (info.IsKnown)
            {
                string what = info.Uarch;
                if (info.IsHybrid)
                {
                    // 混合部件：同一個 model 兩種核心。只報大核等於只講一半，所以兩顆都寫出來。
                    string ecore = MicroarchProfile.Identify(family, model, CoreKind.Efficiency).Uarch;
                    what = ecore.Length > 0 && ecore != info.Uarch
                        ? $"{info.Uarch}（P 核）＋ {ecore}（E 核）"
                        : $"{info.Uarch}（單一核型）";
                }
                return new HardwareFact(key, Category, name, $"{what} ・ {sig}", "", source,
                    FactTrustLevel.Measured, false, at, NumericValue: (model << 8) | family);
            }
        }

        string? uarch = CpuUarchTable.Lookup(vendor, family, model);
        if (uarch is null)
            return new HardwareFact(key, Category, name, "", "", source, FactTrustLevel.Measured, false, at, null,
                FactAvailability.Unknown, $"{vendor} {sig}：對照表未收錄——不猜");

        string display = CpuUarchTable.DisplayName(uarch) ?? uarch;
        return new HardwareFact(key, Category, name, $"{display}（{uarch}）・ {sig}", "", source,
            FactTrustLevel.Measured, false, at, NumericValue: (model << 8) | family);
    }

    /// <summary>CPUID 0x1A 的 EAX[31:24] → 核型。leaf 0x1A 不存在時不能拿「最高 leaf 的回音」當答案，先擋掉。</summary>
    private static CoreKind ReadCoreKind()
    {
        if (!System.Runtime.Intrinsics.X86.X86Base.IsSupported) return CoreKind.Unknown;
        if ((uint)System.Runtime.Intrinsics.X86.X86Base.CpuId(0, 0).Eax < 0x1A) return CoreKind.Unknown;
        return MicroarchProfile.CoreKindFromCpuid1A((uint)System.Runtime.Intrinsics.X86.X86Base.CpuId(0x1A, 0).Eax);
    }

    /// <summary>世代判定事實：CPUID family/model → 微架構名。usermode 可讀，不依賴 ring0；未收錄的 model 誠實標「未收錄」。</summary>
    public static HardwareFact GenerationFact(uint? signature, DateTimeOffset at)
    {
        const string key = "cpu.generation", name = "處理器世代判定",
            source = "CPUID leaf 1 EAX（family／model／stepping）";
        if (signature is not { } eax)
            return new HardwareFact(key, Category, name, "", "", source, FactTrustLevel.Unknown, false, at, null,
                FactAvailability.NotSupported, "CPUID 不可用（非 x86 環境）");

        var (family, model, stepping) = CpuGeneration.DecodeSignature(eax);
        var gen = CpuGeneration.GenerationName(family, model);
        string value = gen is not null
            ? $"{gen}（family {family}、model 0x{model:X2}、stepping {stepping}）"
            : $"family {family}、model 0x{model:X2}、stepping {stepping}（世代對照表未收錄，不猜）";
        return new HardwareFact(key, Category, name, value, "", source, FactTrustLevel.Measured, false, at,
            NumericValue: (model << 8) | family);
    }

    /// <summary>由逐核讀值組成微碼事實：全核一致才給值；不一致或全失敗誠實標三態。</summary>
    public static HardwareFact MicrocodeMsrFact(DateTimeOffset at, IReadOnlyList<uint?> revisions, string? unavailableReason)
    {
        const string key = "msr.0x8b", name = "微碼修訂版（MSR 0x8B）",
            source = "MSR 0x8B（IA32_BIOS_SIGN_ID）bits[63:32]，逐實體核綁定讀取";
        if (unavailableReason is not null)
            return Unavailable(key, name, source, at, FactAvailability.InsufficientPrivilege, unavailableReason);
        var known = revisions.Where(r => r is not null).Select(r => r!.Value).ToList();
        if (known.Count == 0)
            return Unavailable(key, name, source, at, FactAvailability.ReadError, "0x8B 逐核皆讀取失敗");
        var groups = known.GroupBy(r => r).ToList();
        if (groups.Count > 1)
            return Unavailable(key, name, source, at, FactAvailability.ReadError,
                "逐核不一致（" + string.Join("、", groups.Select(g => $"0x{g.Key:X8}×{g.Count()}")) + "）——不取單核冒充全機");
        uint rev = groups[0].Key;
        return new HardwareFact(key, Category, name, $"0x{rev:X8}", "", source, FactTrustLevel.Measured, false, at, rev);
    }

    /// <summary>
    /// 解碼登錄檔「Update Revision」。標準佈局為 8 位元組（兩個 DWORD）——文獻對修訂版落在
    /// 哪個 DWORD 說法不一（offset 0 與 offset 4 都有人引用）——這裡不猜：兩個 DWORD 恰一個非零
    /// 就取那個，皆非零標佈局歧義（原始 hex 一併附上可稽核），皆零視為「可能未載入」。
    /// 實測另有 <b>4 位元組變體</b>（2026-10-03 本機：06 70 00 02＝LE DWORD 0x02007006，
    /// 恰等於 MSR 0x8B 高 32 位）——同樣以 LE DWORD 解碼並在畫面註明變體，不冒充標準佈局。
    /// </summary>
    public static HardwareFact MicrocodeRegistryFact(DateTimeOffset at, byte[]? raw)
    {
        const string key = "reg.microcode", name = "微碼修訂版（登錄檔）",
            source = "登錄檔 CentralProcessor\\0「Update Revision」";
        if (raw is null)
            return Unavailable(key, name, source, at, FactAvailability.NotSupported, "登錄值不存在");
        string hex = Convert.ToHexString(raw);
        if (raw.Length == 4)
        {
            uint rev4 = BitConverter.ToUInt32(raw, 0);
            return new HardwareFact(key, Category, name, $"0x{rev4:X8}（4 位元組變體，原始 {hex}）", "", source,
                FactTrustLevel.Reported, false, at, rev4);
        }
        if (raw.Length < 8)
            return Unavailable(key, name, source, at, FactAvailability.ReadError, $"格式不明（{raw.Length} 位元組，須 4 或 8）");
        uint d0 = BitConverter.ToUInt32(raw, 0), d4 = BitConverter.ToUInt32(raw, 4);
        if (d0 != 0 && d4 != 0)
            return Unavailable(key, name, source, at, FactAvailability.ReadError,
                $"佈局歧義（兩個 DWORD 皆非零：0x{d0:X8}/0x{d4:X8}）——原始 {hex}，不解碼");
        uint rev = d0 != 0 ? d0 : d4;
        string note = rev == 0 ? "兩個 DWORD 皆 0：可能未載入任何微碼修訂" : $"原始 {hex}";
        return new HardwareFact(key, Category, name, $"0x{rev:X8}（{note}）", "", source, FactTrustLevel.Reported, false, at, rev);
    }

    /// <summary>TjMax＝MSR 0x1A2 bits[23:16]。AMD／未實作平台讀失敗時誠實三態（合理性檢查由對帳規則負責）。</summary>
    public static HardwareFact TjMaxFact(DateTimeOffset at, ulong? raw, string? unavailableReason)
    {
        const string key = "cpu.tjmax", name = "TjMax 目標溫度",
            source = "MSR 0x1A2（IA32_TEMPERATURE_TARGET）bits[23:16]";
        if (unavailableReason is not null)
            return new HardwareFact(key, "處理器", name, "", "", source, FactTrustLevel.Unknown, false, at, null,
                FactAvailability.InsufficientPrivilege, unavailableReason);
        if (raw is null)
            return new HardwareFact(key, "處理器", name, "", "", source, FactTrustLevel.Unknown, false, at, null,
                FactAvailability.ReadError, "平台未實作或讀取失敗");
        uint tj = (uint)((raw.Value >> 16) & 0xFF);
        return new HardwareFact(key, "處理器", name, tj.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "°C", source, FactTrustLevel.Measured, false, at, tj);
    }

    /// <summary>讀 Windows 記錄的微碼修訂（HKLM\HARDWARE\...\CentralProcessor\0「Update Revision」）；不存在或失敗回 null。</summary>
    public static byte[]? ReadUpdateRevision()
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return key?.GetValue("Update Revision") as byte[];
        }
        catch { return null; }
    }

    private static HardwareFact Unavailable(string key, string name, string source, DateTimeOffset at,
        FactAvailability availability, string reason) =>
        new(key, Category, name, "", "", source, FactTrustLevel.Unknown, false, at, null, availability, reason);
}
