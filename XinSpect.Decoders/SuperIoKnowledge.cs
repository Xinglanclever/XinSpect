namespace XinSpect;

/// <summary>晶片家族——決定 HWM 暫存器佈局與公式（見 <see cref="SuperIoHwmDecoder"/>）。</summary>
public enum SuperIoFamily
{
    /// <summary>知識庫未收錄——不出處化解讀，HWM 通路只報基址不猜公式。</summary>
    Unlisted,
    ITE,
    /// <summary>Nuvoton／Winbond NCT67xx 主系列（banked HWM，LDN 0x0B）。</summary>
    NuvotonNct,
    /// <summary>Nuvoton 家族但 HWM 佈局不同（NCT6106／NCT668x／NPCD378）——名稱可辨識，感測器不解（不猜）。</summary>
    NuvotonOther,
    Fintek,
}

/// <summary>
/// Super I/O 晶片名稱對照（V7 WP30／D2 知識庫第一批有出處的名字）。
/// <b>出處</b>：coreboot <c>util/superiotool</c>（GPL-2.0-or-later）的 ite.c／nuvoton.c／fintek.c 晶片 ID 表
/// （2026-10-03 抓取 ite.c／nuvoton.c，2026-10-07 抓取 fintek.c），晶片 ID 讀自 LDN0 暫存器 0x20/0x21（高低位元組序與本專案
/// <see cref="SuperIo.DecodeChipId"/> 一致）。只收錄常見子集；未收錄回 null——
/// 名稱知識錯了比沒有更糟（V7 誠實契約），抓到的新表再擴充。
/// </summary>
public static class SuperIoKnowledge
{
    /// <summary>晶片家族——知識庫未收錄回 <see cref="SuperIoFamily.Unlisted"/>。</summary>
    [SpecRef("coreboot util/superiotool ite.c／nuvoton.c／fintek.c 晶片 ID 表的家族歸屬；HWM 佈局差異另見 SuperIoHwmDecoder 各公式引用")]
    public static SuperIoFamily Family(ushort chipId) =>
        FamilyTable.TryGetValue(chipId, out SuperIoFamily family) ? family : SuperIoFamily.Unlisted;

    /// <summary>晶片 ID → 型號名。未收錄回 null。</summary>
    [SpecRef("coreboot util/superiotool（GPL-2.0-or-later）ite.c／nuvoton.c／fintek.c 晶片 ID 表；ID 暫存器佈局見 SuperIo.DecodeChipId")]
    public static string? ChipName(ushort chipId) => chipId switch
    {
        _ when IteIds.TryGetValue(chipId, out string? ite) => ite,
        _ when Nct67xxIds.TryGetValue(chipId, out string? nct) => nct,
        _ when OtherNuvotonIds.TryGetValue(chipId, out string? other) => other,
        _ when FintekIds.TryGetValue(chipId, out string? fintek) => fintek,
        _ => null,
    };

    // ── ITE（superiotool ite.c）──
    private static readonly IReadOnlyDictionary<ushort, string> IteIds = new Dictionary<ushort, string>
    {
        [0x8502] = "IT8502E/TE/G",
        [0x8510] = "IT8510E/TE/G",
        [0x8512] = "IT8512E/F/G",
        [0x8613] = "IT8613E",
        [0x8616] = "IT8616E/IT8656E",
        [0x8623] = "IT8623E",
        [0x8625] = "IT8625E",
        [0x8659] = "IT8659E",
        [0x8661] = "IT8661F/IT8770F",
        [0x8673] = "IT8673F",
        [0x8681] = "IT8671F/IT8687R",
        [0x8689] = "IT8689E",
        [0x8705] = "IT8705F/AF / IT8700F",
        [0x8708] = "IT8708F",
        [0x8712] = "IT8712F",
        [0x8716] = "IT8716F",
        [0x8718] = "IT8718F",
        [0x8720] = "IT8720F",
        [0x8721] = "IT8721F",
        [0x8722] = "IT8722F",
        [0x8726] = "IT8726F",
        [0x8728] = "IT8728F",
        [0x8761] = "IT8761E",
        [0x8772] = "IT8772F",
        [0x8780] = "IT8780F",
        [0x8783] = "IT8783E/F",
        [0x8786] = "IT8786E-I",
    };

    // ── Nuvoton／Winbond NCT67xx 主系列（superiotool nuvoton.c；HWM＝banked 佈局，見 SuperIoHwmDecoder）──
    private static readonly IReadOnlyDictionary<ushort, string> Nct67xxIds = new Dictionary<ushort, string>
    {
        [0xB472] = "NCT6775F (A)",
        [0xB473] = "NCT6775F (B) / NCT5572D (B)",
        [0xC332] = "NCT6776F (B)",
        [0xC333] = "NCT6776F/D (C)",
        [0xC562] = "NCT6779D",
        [0xC563] = "NCT6779D（未記載 ID）",
        [0xC803] = "NCT6791D",
        [0xD42A] = "NCT6796D",
        [0xD451] = "NCT6797D（superiotool 標 experimental）",
        [0xD121] = "NCT5539D",
    };

    // ── Nuvoton 家族但 HWM 佈局不同（名稱有出處、感測器不解讀）──
    private static readonly IReadOnlyDictionary<ushort, string> OtherNuvotonIds = new Dictionary<ushort, string>
    {
        [0xC452] = "NCT6102D / NCT6106D",
        [0x1C11] = "NPCD378（superiotool 標 experimental）",
        [0xC73A] = "NCT6685D/NCT6686D",
        [0xD592] = "NCT6687D-W",
    };

    // ── Fintek（superiotool fintek.c，2026-10-07 抓取）──
    private static readonly IReadOnlyDictionary<ushort, string> FintekIds = new Dictionary<ushort, string>
    {
        [0x0106] = "F71862FG / F71863FG",
        [0x0110] = "F71808A",
        [0x0710] = "F71869A/AD",
        [0x1408] = "F71869E/ED",
        [0x2307] = "F71889",
        [0x4103] = "F71872F/FG / F71806F/FG",
        [0x4105] = "F71882FG / F71883FG",
        [0x0604] = "F71805F/FG",
        [0x0581] = "F8000（Fintek/ASUS）",
        [0x0802] = "F81216D/DG",
        [0x1602] = "F81216AD",
        [0x0407] = "F81865F/F-I",
        [0x1010] = "F81866",
        [0x0215] = "F81804/F81962/F81964/F81966/F81967",
    };

    private static readonly Dictionary<ushort, SuperIoFamily> FamilyTable = BuildFamilyTable();

    private static Dictionary<ushort, SuperIoFamily> BuildFamilyTable()
    {
        var table = new Dictionary<ushort, SuperIoFamily>();
        foreach (ushort id in IteIds.Keys) table[id] = SuperIoFamily.ITE;
        foreach (ushort id in Nct67xxIds.Keys) table[id] = SuperIoFamily.NuvotonNct;
        foreach (ushort id in OtherNuvotonIds.Keys) table[id] = SuperIoFamily.NuvotonOther;
        foreach (ushort id in FintekIds.Keys) table[id] = SuperIoFamily.Fintek;
        return table;
    }
}
