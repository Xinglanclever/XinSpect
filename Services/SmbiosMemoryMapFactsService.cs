using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace XinSpect;

/// <summary>
/// SMBIOS Type 19（記憶體陣列映射位址）的事實產出（ULTIMATE-1000 階段二 P2-024）。
///
/// <para>
/// <b>這一項回答「韌體說實體位址是怎麼映射的」，不回答「這台機器有多少可用記憶體」。</b>
/// Type 19 的每一列是一段位址範圍＋它所屬的 Type 16 記憶體陣列控制代碼＋分割寬度。
/// 範圍裡可能有<b>保留區</b>（MMIO 視窗、快閃視窗、晶片組映射的 holes），
/// 也可能因為韌體不填而少列——<b>所以合計不等於 OS 看到的可用記憶體，兩者不得互相代言</b>
/// （可用的那一格由 <c>mem.*</c> 與 Type 16／17 那幾支講）。
/// </para>
///
/// <para>
/// <b>規格留了兩個坑，这一支都按規格如實處理，不猜：</b>
/// </para>
/// <list type="bullet">
///   <item>32 位元欄位填 <c>0xFFFFFFFF</c> 代表「改用 64 位元擴充欄位」（SMBIOS 2.7+）；
///     而有些韌體<strong>設了這個旗標卻沒填擴充欄</strong>（結構長度不足）——
///     這時那一列標 <c>NotSupported</c>，<strong>不拿 4 GiB 上限去反推</strong>。</item>
///   <item>位址的<strong>單位不同</strong>：32 位元欄位是 <strong>KiB</strong>、擴充欄位是 <strong>bytes</strong>。
///     弄錯單位會得到一個「看起來合理但差了 1024 倍」的數字，而這種錯不會有任何錯誤訊息。</item>
/// </list>
/// <para>
/// 範圍的<b>末端是含端點的</b>（規格對 Type 19／20 的 Ending Address 明寫 inclusive），
/// 所以大小是 <c>end − start + 1</c>。差一個端點在 KiB 單位下是 1 KiB，在位址上卻是
/// 「這段到底覆不覆蓋最後一格」——這是會被拿去對帳的細節。
/// </para>
///
/// <para>
/// <b>為什麼每列要一把鍵（<c>sb.memmap.{i}</c>）而不是全部塞進摘要：</b>
/// 有幾段是機器的事實（編譯期列不完），而「哪一段被改了」需要能被單獨選取與匯出——
/// 家族前綴的登記見 <see cref="FactKeyDynamicCatalog"/>。
/// </para>
/// </summary>
public static class SmbiosMemoryMapFactsService
{
    private const string Category = "SMBIOS";
    private const string Source = "SMBIOS Type 19（GetSystemFirmwareTable('RSMB')，唯讀）";

    public const string CountKey = "sb.memmap.count";
    public const string TotalKey = "sb.memmap.total";

    /// <summary>
    /// 逐段明細的<strong>家族前綴</strong>。名稱刻意不含 <c>Key</c>——掃描器的字面規則
    /// 把 <c>const string ...Key... = "..."</c> 當成一把鍵，而前綴不是鍵
    /// （同 <see cref="UefiSignatureFactsService"/> 裡 <c>*ListPrefix</c> 的那個講究）。
    /// </summary>
    private const string RangePrefix = "sb.memmap.";

    /// <summary>一段映射位址。<paramref name="Unusable"/>＝規格要用的擴充欄位不存在，如實標、不換算。</summary>
    public sealed record MappedRange(long StartBytes, long EndBytes, ushort ArrayHandle,
        byte PartitionWidth, bool UsedExtended, bool Unusable);

    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at,
        Func<IReadOnlyList<SmbiosStruct>>? structsProbe = null)
    {
        IReadOnlyList<SmbiosStruct> structs;
        try { structs = (structsProbe ?? (() => new SmbiosService().Structs))(); }
        catch (Exception ex)
        {
            return [Unavailable(CountKey, "SMBIOS 映射範圍數", at, "GB", FactAvailability.ReadError,
                "SMBIOS 表讀取失敗：" + ex.Message + "——「讀不到表」與「表裡沒有 Type 19」是不同的斷言"),
                Unavailable(TotalKey, "SMBIOS 映射位址合計", at, "GB", FactAvailability.ReadError,
                    "同一次 SMBIOS 讀取失敗——不单独猜一個合計")];
        }

        var ranges = structs.Where(s => s.Type == 19).Select(Decode).ToList();
        if (ranges.Count == 0)
            return
            [
                new HardwareFact(CountKey, Category, "SMBIOS 映射範圍數", "0 段", "段", Source,
                    FactTrustLevel.Measured, false, at, null, FactAvailability.NotApplicable,
                    "**韌體沒有列 Type 19**——這是查證後的「沒有列」，不是讀取失敗。"
                    + "消費級平台常常不填，而<strong>缺席不代表記憶體沒有映射</strong>（OS 與韌體各有一套說法）"),
                new HardwareFact(TotalKey, Category, "SMBIOS 映射位址合計", "", "GB", Source,
                    FactTrustLevel.Measured, false, at, null, FactAvailability.NotApplicable,
                    "沒有 Type 19 可加——<strong>不拿其他來源的記憶體數字冒充這個合計</strong>"),
            ];

        var facts = new List<HardwareFact> { Count(at, ranges), Total(at, ranges) };
        for (int i = 0; i < ranges.Count; i++) facts.Add(RangeFact(at, i, ranges[i]));
        return facts;
    }

    private static HardwareFact Count(DateTimeOffset at, IReadOnlyList<MappedRange> ranges)
    {
        int unusable = ranges.Count(r => r.Unusable);
        return new HardwareFact(CountKey, Category, "SMBIOS 映射範圍數",
            unusable == 0 ? $"{ranges.Count} 段" : $"{ranges.Count} 段（其中 {unusable} 段解不開，見逐段明細）",
            "段", Source, FactTrustLevel.Measured, false, at, ranges.Count,
            FactAvailability.Present,
            unusable == 0
                ? "這是<strong>韌體宣告</strong>的映射段數，不是 OS 看到的記憶體區塊數"
                : $"{unusable} 段的 32 位元欄位是 0xFFFFFFFF（代表要用 64 位元擴充欄），"
                  + "而那一列的結構長度沒有擴充欄——<strong>不反推、不換算</strong>");
    }

    private static HardwareFact Total(DateTimeOffset at, IReadOnlyList<MappedRange> ranges)
    {
        var usable = ranges.Where(r => !r.Unusable).ToList();
        if (usable.Count == 0)
            return Unavailable(TotalKey, "SMBIOS 映射位址合計", at, "GB", FactAvailability.NotSupported,
                $"{ranges.Count} 段全部都需要 64 位元擴充欄位而結構裡沒有——"
                + "<strong>沒有可加總的數字，就不出合計</strong>（不是 0 GB）");

        long bytes = usable.Sum(r => Math.Max(0, r.EndBytes - r.StartBytes + 1));
        double gb = bytes / 1024.0 / 1024.0 / 1024.0;
        string gap = usable.Count == ranges.Count
            ? ""
            : $"（另有 {ranges.Count - usable.Count} 段解不開，<strong>不以 0 計入</strong>）";
        return new HardwareFact(TotalKey, Category, "SMBIOS 映射位址合計",
            $"{gb:0.###} GB＝{bytes.ToString("#,0", CultureInfo.InvariantCulture)} bytes{gap}",
            "GB", Source, FactTrustLevel.Derived, false, at, Math.Round(gb, 3),
            FactAvailability.Present,
            "<strong>這是位址空間的映射範圍合計，不等於可用記憶體</strong>——"
            + "範圍裡可能含保留區（MMIO／快閃視窗／晶片組 holes），也可能因韌體不填而少列。"
            + "可用容量請看 Type 16／17 與 OS 那幾把鍵，兩者不得互相代言。");
    }

    private static HardwareFact RangeFact(DateTimeOffset at, int index, MappedRange r)
    {
        string key = RangePrefix + index.ToString(CultureInfo.InvariantCulture);
        if (r.Unusable)
            return new HardwareFact(key, Category, $"映射範圍 {index}", "", "GB", Source,
                FactTrustLevel.Unknown, false, at, null, FactAvailability.NotSupported,
                "32 位元位址欄是 0xFFFFFFFF（規格：改用 64 位元擴充欄），"
                + "而這一列的結構長度不足以帶擴充欄——<strong>那是一個規格留白，不是 4 GiB 邊界，也不是 0</strong>");

        long bytes = Math.Max(0, r.EndBytes - r.StartBytes + 1);
        double gb = bytes / 1024.0 / 1024.0 / 1024.0;
        string via = r.UsedExtended ? "（經 64 位元擴充欄位）" : "（32 位元欄位，單位 KiB）";
        return new HardwareFact(key, Category, $"映射範圍 {index}",
            $"0x{r.StartBytes:X}–0x{r.EndBytes:X}＝{gb:0.###} GB{via}、所屬記憶體陣列 0x{r.ArrayHandle:X4}"
            + (r.PartitionWidth > 0 ? $"、分割寬度 {r.PartitionWidth}" : "、分割寬度未回報"),
            "GB", Source, FactTrustLevel.Measured, false, at, Math.Round(gb, 3),
            FactAvailability.Present,
            r.PartitionWidth > 1
                ? $"分割寬度 {r.PartitionWidth} 代表這段橫跨所屬陣列的多個分區（交錯映射）——這是韌體的說法，不是量測"
                : "這一列講的是位址映射，不是「這段現在可用」");
    }

    private static HardwareFact Unavailable(string key, string name, DateTimeOffset at, string unit,
        FactAvailability availability, string reason)
        => new(key, Category, name, "", unit, Source, FactTrustLevel.Unknown, false, at, null, availability, reason);

    /// <summary>
    /// Type 19 → 一段映射位址（純函式；結構長度不足時標 <see cref="MappedRange.Unusable"/>，不猜）。
    /// </summary>
    [SpecRef("SMBIOS Specification, Memory Array Mapped Address Structure (Type 19)：04h Starting Address（DWORD，單位 KB）、08h Ending Address（DWORD，單位 KB，含端點）、0Ch Memory Array Handle（WORD）、0Eh Partition Width（BYTE）、0Fh Extended Starting Address（QWORD，單位 bytes，SMBIOS 2.7+）、17h Extended Ending Address（QWORD，bytes，2.7+）。32 位元欄位值為 FFFFFFFFh 時必須改用對應的擴充欄位；結構長度不足以容納擴充欄位時不得自行推定。")]
    internal static MappedRange Decode(SmbiosStruct s)
    {
        ushort handle = s.Length >= 0x0E ? s.WordAt(0x0C) : (ushort)0;
        byte width = s.Length >= 0x0F ? s.ByteAt(0x0E) : (byte)0;
        uint startRaw = s.DwordAt(0x04), endRaw = s.DwordAt(0x08);
        bool needsExtended = startRaw == 0xFFFFFFFF || endRaw == 0xFFFFFFFF;

        if (needsExtended)
        {
            // 擴充欄需要結構長度至少到 0x1E（最後一個位元組索引 0x1E ⇒ 長度 ≥ 0x1F）。
            if (s.Length < 0x1F) return new MappedRange(0, 0, handle, width, true, Unusable: true);
            long start = Qword(s, 0x0F), end = Qword(s, 0x17);
            if (start == -1 || end == -1) return new MappedRange(0, 0, handle, width, true, Unusable: true);
            return new MappedRange(start, end, handle, width, UsedExtended: true, Unusable: false);
        }

        // 32 位元欄位的單位是 KB（規格），末端含端點 ⇒ ×1024＋(1024−1)。
        long startBytes = (long)startRaw * 1024;
        long endBytes = (long)endRaw * 1024 + 1023;
        return new MappedRange(startBytes, endBytes, handle, width, UsedExtended: false, Unusable: false);
    }

    /// <summary>小端序 QWORD（SMBIOS 的整數都是 little-endian）。</summary>
    private static long Qword(SmbiosStruct s, int offset)
        => (long)s.DwordAt(offset) | ((long)s.DwordAt(offset + 4) << 32);
}
