namespace XinSpect;

/// <summary>
/// CPUID family／model → 微架構（microarchitecture）對照。
///
/// <para>
/// 與 <see cref="CpuGeneration"/> 的分工：那一支回答「這是哪個行銷世代」（Raptor Lake、Meteor Lake），
/// 這一支回答「這是哪個核心微架構」（raptor_cove、gracemont、zen4）。兩者不是同一件事：
/// 同一個世代可以有兩種核心（P-core 與 E-core），同一種核心也可以橫跨好幾個世代
/// （Skylake 核心從 6 代一路用到 10 代）。要判斷「這顆核心的暫存器佈局」，需要的是後者。
/// </para>
///
/// <para>
/// 表來自 <b>pytorch/cpuinfo</b> 的 <c>src/x86/uarch.c</c>（BSD-2-Clause，見
/// THIRD-PARTY-NOTICES.md）。為什麼用它而不是自己抄型號：那張表把「family 6 model → 核心」
/// 與「AMD family 0x17/0x19/0x1A 的 extended model（model&gt;&gt;4）分段」都寫清楚了，而且
/// 每個 case 都留了型號註解（Zambezi、Vermeer、Raphael……），可以逐格回查。
/// </para>
///
/// <para>
/// 這一支**只做查表，不猜**：沒收錄的 family／model 回 <c>null</c>。呼叫端要誠實地把
/// null 變成人看得懂的三態（未收錄），不是挑一個最像的填上去。
/// </para>
/// </summary>
public static class CpuUarchTable
{
    /// <summary>CPUID leaf 0 的 12 位元廠商字串：GenuineIntel／AuthenticAMD／HygonGenuine。</summary>
    public static string VendorName(uint ebx, uint edx, uint ecx)
    {
        Span<char> chars = stackalloc char[12];
        WriteChars(chars, 0, ebx);
        WriteChars(chars, 4, edx);
        WriteChars(chars, 8, ecx);
        return new string(chars).TrimEnd('\0');
    }

    private static void WriteChars(Span<char> dst, int at, uint value)
    {
        for (int i = 0; i < 4; i++) dst[at + i] = (char)((value >> (8 * i)) & 0xFF);
    }

    /// <summary>生產讀取：CPUID leaf 0 的廠商字串。不支援 x86 時回 null（如實三態）。</summary>
    public static string? ReadVendor()
    {
        if (!System.Runtime.Intrinsics.X86.X86Base.IsSupported) return null;
        var leaf0 = System.Runtime.Intrinsics.X86.X86Base.CpuId(0, 0);
        return VendorName(unchecked((uint)leaf0.Ebx), unchecked((uint)leaf0.Edx), unchecked((uint)leaf0.Ecx));
    }

    /// <summary>
    /// 查微架構代號（小寫、底線分隔，例如 <c>raptor_cove</c>、<c>zen4</c>）。
    /// 未收錄回 <c>null</c>。
    /// </summary>
    /// <param name="vendor">CPUID leaf 0 的廠商字串（比對前會去空白）。</param>
    /// <param name="family">已合併 extended family 的 family。</param>
    /// <param name="model">已合併 extended model 的 model（0x00–0xFF）。</param>
    public static string? Lookup(string? vendor, int family, int model)
    {
        if (string.IsNullOrWhiteSpace(vendor)) return null;
        int extendedModel = (model >> 4) & 0xF;      // cpuinfo 的 model_info->extended_model
        return vendor.Trim() switch
        {
            "GenuineIntel" => Intel(family, model),
            "AuthenticAMD" => Amd(family, model, extendedModel),
            "HygonGenuine" => family == 0x00 ? "dhyana" : null,
            _ => null,
        };
    }

    /// <summary>微架構代號 → 顯示名稱（<c>zen4</c> → <c>Zen 4</c>）。未收錄回 <c>null</c>。</summary>
    public static string? DisplayName(string? uarch) => uarch switch
    {
        "p5" => "P5",
        "quark" => "Quark",
        "p6" => "P6",
        "dothan" => "Dothan",
        "yonah" => "Yonah",
        "conroe" => "Conroe",
        "penryn" => "Penryn",
        "nehalem" => "Nehalem",
        "sandy_bridge" => "Sandy Bridge",
        "ivy_bridge" => "Ivy Bridge",
        "haswell" => "Haswell",
        "broadwell" => "Broadwell",
        "sky_lake" => "Skylake",
        "palm_cove" => "Palm Cove",
        "sunny_cove" => "Sunny Cove",
        "willow_cove" => "Willow Cove",
        "golden_cove" => "Golden Cove",
        "raptor_cove" => "Raptor Cove",
        "redwood_cove" => "Redwood Cove",
        "bonnell" => "Bonnell",
        "saltwell" => "Saltwell",
        "silvermont" => "Silvermont",
        "tremont" => "Tremont",
        "gracemont" => "Gracemont",
        "crestmont" => "Crestmont",
        "darkmont" => "Darkmont",
        "airmont" => "Airmont",
        "goldmont" => "Goldmont",
        "goldmont_plus" => "Goldmont Plus",
        "knights_landing" => "Knights Landing",
        "knights_mill" => "Knights Mill",
        "willamette" => "Willamette",
        "prescott" => "Prescott",
        "coyote_cove" => "Coyote Cove",
        "k5" => "K5",
        "k6" => "K6",
        "k7" => "K7",
        "k8" => "K8",
        "k10" => "K10",
        "geode" => "Geode",
        "bobcat" => "Bobcat",
        "puma" => "Puma",
        "jaguar" => "Jaguar",
        "bulldozer" => "Bulldozer",
        "piledriver" => "Piledriver",
        "steamroller" => "Steamroller",
        "excavator" => "Excavator",
        "zen" => "Zen",
        "zen2" => "Zen 2",
        "zen3" => "Zen 3",
        "zen4" => "Zen 4",
        "zen5" => "Zen 5",
        "zen6" => "Zen 6",
        "oberon" => "Oberon",
        "arden" => "Arden",
        "dhyana" => "Dhyana",
        _ => null,
    };

    private static string? Intel(int family, int model) => family switch
    {
        0x05 => model switch
        {
            0x01 or 0x02 or 0x03 or 0x04 => "p5",
            0x09 => "quark",
            _ => null,
        },
        0x0F => model switch
        {
            0x00 or 0x01 or 0x02 => "willamette",
            0x03 or 0x04 or 0x06 => "prescott",
            _ => null,
        },
        0x12 => model == 0x01 ? "coyote_cove" : null,
        0x06 => model switch
        {
            // 大核心世代
            0x01 or 0x03 or 0x05 or 0x06 or 0x07 or 0x08 or 0x0A or 0x0B => "p6",
            0x09 or 0x0D or 0x15 => "dothan",
            0x0E => "yonah",
            0x0F or 0x16 => "conroe",
            0x17 or 0x1D => "penryn",
            0x1A or 0x1E or 0x1F or 0x25 or 0x2C or 0x2E or 0x2F => "nehalem",
            0x2A or 0x2D => "sandy_bridge",
            0x3A or 0x3E => "ivy_bridge",
            0x3C or 0x3F or 0x45 or 0x46 => "haswell",
            0x3D or 0x47 or 0x4F or 0x56 => "broadwell",
            0x4E or 0x55 or 0x5E or 0x8E or 0x9E or 0xA5 or 0xA6 => "sky_lake",
            0x66 => "palm_cove",
            0x6A or 0x6C or 0x7D or 0x7E => "sunny_cove",
            0x8C or 0x8D => "willow_cove",
            0x8F => "golden_cove",
            0xCF => "raptor_cove",
            0xAD => "redwood_cove",
            // Atom／低功耗世代
            0x1C or 0x26 => "bonnell",
            0x27 or 0x35 or 0x36 => "saltwell",
            0x37 or 0x4A or 0x4D or 0x5A or 0x5D => "silvermont",
            0x4C or 0x75 => "airmont",
            0x5C or 0x5F => "goldmont",
            0x7A => "goldmont_plus",
            0x86 or 0x8A or 0x96 or 0x9C => "tremont",
            0xBE => "gracemont",
            0xAF => "crestmont",
            0xDD => "darkmont",
            // 加速卡
            0x57 => "knights_landing",
            0x85 => "knights_mill",
            _ => null,
        },
        _ => null,
    };

    private static string? Amd(int family, int model, int extendedModel) => family switch
    {
        0x05 => model switch
        {
            0x00 or 0x01 or 0x02 => "k5",
            0x06 or 0x07 or 0x08 or 0x0D => "k6",
            0x0A => "geode",
            _ => null,
        },
        0x06 => "k7",
        0x0F or 0x11 => "k8",
        0x10 or 0x12 => "k10",
        0x14 => "bobcat",
        0x15 => model switch
        {
            0x00 or 0x01 => "bulldozer",
            0x02 or 0x10 or 0x13 => "piledriver",
            0x30 or 0x38 => "steamroller",
            0x60 or 0x65 or 0x70 => "excavator",
            // 未收錄的 model 走 extended model 分段（cpuinfo 的預設分支）
            _ => extendedModel switch
            {
                0x0 => "bulldozer",
                0x1 or 0x2 => "piledriver",
                0x3 or 0x4 => "steamroller",
                _ => null,
            },
        },
        0x16 => extendedModel >= 0x03 ? "puma" : "jaguar",
        // Zen 世代用 extended model（model >> 4）分段——同一個 family 裡 0x00–0x0F 與 0x10–0x1F
        // 可能是不同架構，所以不能拿 model 直接查。
        0x17 => extendedModel switch
        {
            0x0 or 0x1 => "zen",
            0x3 or 0x6 or 0x7 or 0x9 or 0xA => "zen2",
            0x4 => "oberon",
            0x8 => "arden",
            _ => null,
        },
        0x19 => extendedModel switch
        {
            0x0 or 0x2 or 0x3 or 0x4 or 0x5 => "zen3",
            0x1 or 0x6 or 0x7 or 0xA => "zen4",
            _ => null,
        },
        0x1A => extendedModel switch
        {
            0x0 or 0x1 or 0x2 or 0x4 => "zen5",
            0x5 or 0x8 or 0x9 or 0xA or 0xC => "zen6",
            _ => null,
        },
        _ => null,
    };
}
