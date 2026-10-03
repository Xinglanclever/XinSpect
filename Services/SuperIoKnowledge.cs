namespace XinSpect;

/// <summary>
/// Super I/O 晶片名稱對照（V7 WP30／D2 知識庫第一批有出處的名字）。
/// <b>出處</b>：coreboot <c>util/superiotool</c>（GPL-2.0-or-later）的 ite.c／nuvoton.c 晶片 ID 表
/// （2026-10-03 抓取），晶片 ID 讀自 LDN0 暫存器 0x20/0x21（高低位元組序與本專案
/// <see cref="SuperIo.DecodeChipId"/> 一致）。只收錄常見子集；未收錄回 null——
/// 名稱知識錯了比沒有更糟（V7 誠實契約），抓到的新表再擴充。
/// </summary>
public static class SuperIoKnowledge
{
    /// <summary>晶片 ID → 型號名。未收錄回 null。</summary>
    [SpecRef("coreboot util/superiotool（GPL-2.0-or-later）ite.c／nuvoton.c 晶片 ID 表，2026-10-03 抓取；ID 暫存器佈局見 SuperIo.DecodeChipId")]
    public static string? ChipName(ushort chipId) => chipId switch
    {
        // ── ITE（ite.c）──
        0x8502 => "IT8502E/TE/G",
        0x8510 => "IT8510E/TE/G",
        0x8512 => "IT8512E/F/G",
        0x8613 => "IT8613E",
        0x8616 => "IT8616E/IT8656E",
        0x8623 => "IT8623E",
        0x8625 => "IT8625E",
        0x8659 => "IT8659E",
        0x8661 => "IT8661F/IT8770F",
        0x8673 => "IT8673F",
        0x8681 => "IT8671F/IT8687R",
        0x8689 => "IT8689E",
        0x8705 => "IT8705F/AF / IT8700F",
        0x8708 => "IT8708F",
        0x8712 => "IT8712F",
        0x8716 => "IT8716F",
        0x8718 => "IT8718F",
        0x8720 => "IT8720F",
        0x8721 => "IT8721F",
        0x8722 => "IT8722F",
        0x8726 => "IT8726F",
        0x8728 => "IT8728F",
        0x8761 => "IT8761E",
        0x8772 => "IT8772F",
        0x8780 => "IT8780F",
        0x8783 => "IT8783E/F",
        0x8786 => "IT8786E-I",

        // ── Nuvoton／Winbond NCT（nuvoton.c）──
        0xB472 => "NCT6775F (A)",
        0xB473 => "NCT6775F (B) / NCT5572D (B)",
        0xC332 => "NCT6776F (B)",
        0xC333 => "NCT6776F/D (C)",
        0xC562 => "NCT6779D",
        0xC563 => "NCT6779D（未記載 ID）",
        0xC452 => "NCT6102D / NCT6106D",
        0xC803 => "NCT6791D",
        0xD42A => "NCT6796D",
        0xD121 => "NCT5539D",
        0xC73A => "NCT6685D/NCT6686D",
        0xD592 => "NCT6687D-W",
        _ => null,
    };
}
