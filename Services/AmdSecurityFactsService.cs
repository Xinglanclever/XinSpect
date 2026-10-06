using System.Runtime.Intrinsics.X86;

namespace XinSpect;

/// <summary>
/// AMD 安全事實（唯讀 CPUID／MSR／PCI 類別碼）：SME／SEV／SEV-ES／SNP 支援與啟用狀態＋PSP 控制器存在。
/// <b>非 AMD 平台整組 NotApplicable</b>（不是 NotSupported——那是「AMD 平台但硬體沒有」的語意）。
/// AMD 平台的事實標「未在本機驗證」：本機是 Intel，讀取路徑以 Linux 核心的位元定義交叉核對。
/// 全部唯讀零風險。
/// </summary>
/// <remarks>
/// 出處：
/// ① CPUID 0x8000001F EAX（bit0 SME、bit1 SEV、bit3 SEV-ES、bit4 SNP）——AMD64 APM Vol 3，
///    與 Linux 核心 cpufeatures.h word 19（2026-10-07 抓取）交叉核對。
/// ② MSR 0xC0010131（SEV_STATUS，bit0＝記憶體加密啟用、bit1＝SEV-ES、bit2＝SNP）——AMD64 APM
///    Vol 2 Ch 15/16，與 Linux 核心 msr-index.h 交叉核對。
///    <b>交接規格寫 SME 啟用位在 0xC0010132——核心定義沒有該暫存器的啟用位語意，從核心。</b>
/// ③ PSP 以 PCI 類別碼 0x10800（Encryption controller）＋廠商 0x1022 偵測——PCI-SIG 類別碼規格，
///    AMD 平台的 PSP/CCP 一律掛這個類別。
/// </remarks>
public static class AmdSecurityFactsService
{
    private const string Category = "處理器";
    private const uint MsrSevStatus = 0xC0010131;
    private const ushort AmdVendorId = 0x1022;
    private const uint ClassEncryptionController = 0x010800; // 24-bit class code：0x10800 ＝ Encryption controller

    // "AuthenticAMD" 的三個 dword（CPUID leaf 0：EBX→EDX→ECX）
    private const uint VendorEbx = 0x68747541, VendorEdx = 0x69746E65, VendorEcx = 0x444D4163;

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        Func<uint, (uint Eax, uint Ebx, uint Ecx, uint Edx)>? cpuidProbe = null,
        IKernelMsrReader? msr = null,
        IPciConfigReader? pci = null)
    {
        var leaf0 = (cpuidProbe ?? ReadCpId)(0);
        bool isAmd = leaf0.Ebx == VendorEbx && leaf0.Edx == VendorEdx && leaf0.Ecx == VendorEcx;
        if (!isAmd)
            return Keys.Select(key =>
                Unavailable(key, Name(key), "CPUID leaf 0", FactAvailability.NotApplicable,
                    "CPUID leaf 0 廠商非 AuthenticAMD——本頁整組不適用（NotApplicable）")).ToList();

        var ext = (cpuidProbe ?? ReadCpId)(0x8000001F);
        bool sme = (ext.Eax & (1u << 0)) != 0;
        bool sev = (ext.Eax & (1u << 1)) != 0;
        bool sevEs = (ext.Eax & (1u << 3)) != 0;
        bool snp = (ext.Eax & (1u << 4)) != 0;

        var facts = new List<HardwareFact>
        {
            SupportedBit("amd.sme.supported", "SME 支援", sme, "CPUID 0x8000001F EAX bit0"),
            SupportedBit("amd.sev.supported", "SEV 支援", sev, "CPUID 0x8000001F EAX bit1"),
            SupportedBit("amd.sev_es.supported", "SEV-ES 支援", sevEs, "CPUID 0x8000001F EAX bit3"),
            SupportedBit("amd.sev_snp.supported", "SEV-SNP 支援", snp, "CPUID 0x8000001F EAX bit4"),
        };

        if (msr is not { Available: true })
        {
            facts.Add(Unavailable("amd.mem_enc.enabled", "記憶體加密（SME/SEV）啟用",
                "CPUID 0x8000001F＋MSR 0xC0010131", FactAvailability.InsufficientPrivilege,
                msr?.UnavailableReason ?? "缺 ring0：MSR 讀取未就緒"));
            facts.Add(Unavailable("amd.sev_es.enabled", "SEV-ES 啟用",
                "MSR 0xC0010131 bit1", FactAvailability.InsufficientPrivilege,
                msr?.UnavailableReason ?? "缺 ring0：MSR 讀取未就緒"));
            facts.Add(Unavailable("amd.sev_snp.enabled", "SEV-SNP 啟用",
                "MSR 0xC0010131 bit2", FactAvailability.InsufficientPrivilege,
                msr?.UnavailableReason ?? "缺 ring0：MSR 讀取未就緒"));
        }
        else
        {
            ulong? raw = msr.ReadMsr(MsrSevStatus);
            if (raw is { } v)
            {
                facts.Add(EnabledBit("amd.mem_enc.enabled", "記憶體加密（SME/SEV）啟用", v, 0, "MSR 0xC0010131 bit0"));
                facts.Add(EnabledBit("amd.sev_es.enabled", "SEV-ES 啟用", v, 1, "MSR 0xC0010131 bit1"));
                facts.Add(EnabledBit("amd.sev_snp.enabled", "SEV-SNP 啟用", v, 2, "MSR 0xC0010131 bit2"));
            }
            else
            {
                facts.Add(Unavailable("amd.mem_enc.enabled", "記憶體加密（SME/SEV）啟用",
                    "MSR 0xC0010131", FactAvailability.ReadError, "MSR 讀取失敗（非 AMD 平台或未實作）——如實標"));
                facts.Add(Unavailable("amd.sev_es.enabled", "SEV-ES 啟用",
                    "MSR 0xC0010131 bit1", FactAvailability.ReadError, "MSR 讀取失敗——如實標"));
                facts.Add(Unavailable("amd.sev_snp.enabled", "SEV-SNP 啟用",
                    "MSR 0xC0010131 bit2", FactAvailability.ReadError, "MSR 讀取失敗——如實標"));
            }
        }

        facts.Add(PspFact(at, pci));
        return facts;
    }

    private static HardwareFact PspFact(DateTimeOffset at, IPciConfigReader? pci)
    {
        const string key = "amd.psp.present", name = "PSP 安全處理器";
        string source = "PCI bus 0 掃描：廠商 0x1022＋類別碼 0x10800（Encryption controller）";
        if (pci is not { Available: true })
            return Unavailable(key, name, source, FactAvailability.InsufficientPrivilege,
                pci?.UnavailableReason ?? "缺 ring0：PCI 設定空間讀取未就緒");

        for (byte dev = 0; dev < 32; dev++)
        {
            for (byte fn = 0; fn < 8; fn++)
            {
                var id0 = pci.ReadDword(0, dev, fn, 0x00);
                if (id0 is null || id0.Value == 0xFFFFFFFF) continue;
                uint classCode = (pci.ReadDword(0, dev, fn, 0x08) ?? 0) >> 8; // 24-bit class code
                if ((ushort)id0.Value == AmdVendorId && classCode == ClassEncryptionController)
                    return new HardwareFact(key, Category, name,
                        $"偵測到（bus 0 {dev:X2}:{fn:X2}，裝置 ID 0x{(uint)id0.Value >> 16:X4}）"
                        + "——未在本機驗證（本機非 AMD 平台，偵測規則以 PCI-SIG 類別碼為據）", "",
                        source, FactTrustLevel.Measured, false, at, null);
            }
        }
        return Unavailable(key, name, source, FactAvailability.NotSupported,
            "bus 0 上沒有 AMD Encryption controller——PSP 不在此匯流排或未啟用，如實標");
    }

    private static HardwareFact SupportedBit(string key, string name, bool supported, string source) =>
        new(key, Category, name,
            supported ? "支援" : "不支援",
            "", source + "（未在本機驗證）",
            FactTrustLevel.Measured, false, DateTimeOffset.UtcNow, null,
            supported ? FactAvailability.Present : FactAvailability.NotSupported,
            supported ? "" : "CPUID 該位＝0：此處理器不支援");

    private static HardwareFact EnabledBit(string key, string name, ulong value, int bit, string source)
    {
        bool on = (value & (1UL << bit)) != 0;
        return new HardwareFact(key, Category, name, on ? "已啟用" : "未啟用", "",
            source + "（未在本機驗證）", FactTrustLevel.Measured, false, DateTimeOffset.UtcNow, on ? 1UL : 0UL);
    }

    private static readonly string[] Keys =
    [
        "amd.sme.supported", "amd.sev.supported", "amd.sev_es.supported", "amd.sev_snp.supported",
        "amd.mem_enc.enabled", "amd.sev_es.enabled", "amd.sev_snp.enabled", "amd.psp.present",
    ];

    private static string Name(string key) => key switch
    {
        "amd.sme.supported" => "SME 支援",
        "amd.sev.supported" => "SEV 支援",
        "amd.sev_es.supported" => "SEV-ES 支援",
        "amd.sev_snp.supported" => "SEV-SNP 支援",
        "amd.mem_enc.enabled" => "記憶體加密（SME/SEV）啟用",
        "amd.sev_es.enabled" => "SEV-ES 啟用",
        "amd.sev_snp.enabled" => "SEV-SNP 啟用",
        _ => "PSP 安全處理器",
    };

    private static HardwareFact Unavailable(string key, string name, string source,
        FactAvailability availability, string reason) =>
        new(key, Category, name, "", "", source, FactTrustLevel.Unknown, false, DateTimeOffset.UtcNow,
            null, availability, reason);

    private static (uint, uint, uint, uint) ReadCpId(uint leaf)
    {
        if (!X86Base.IsSupported) return (0, 0, 0, 0);
        var r = X86Base.CpuId(unchecked((int)leaf), 0);
        return (unchecked((uint)r.Eax), unchecked((uint)r.Ebx), unchecked((uint)r.Ecx), unchecked((uint)r.Edx));
    }
}
