using System;
using System.Collections.Generic;

using System.Linq;

namespace XinSpect;

/// <summary>
/// EDID CTA-861 擴充塊的事實（U10-GP-0100 ～ U10-GP-0109，十把鍵）。
///
/// <para>
/// <b>題目是「顯示器在 EDID 裡宣告了什麼」，不是「畫質好不好」。</b>
/// CTA-861 擴充塊是面板／轉接器自己寫給系統看的能力清單：有哪些視訊模式（VIC）、
/// 支援哪些數位音訊格式、HDR 用哪個傳輸函數、HDMI 的實體位址接在哪、色度學支援到哪裡。
/// 本服務只<b>轉述</b>那些宣告；它不判斷該不該開 HDR、也不判斷線材夠不夠。
/// </para>
///
/// <para>
/// <b>多顯示器怎麼處理：</b>事實鍵是機器層級的扁平字串，不是「每台顯示器一組」。
/// 所以每一把鍵的值會依顯示器分段（<c>①</c>、<c>②</c>…），每段寫明是哪一台；
/// 只有一台時不加序號。逐 VIC 的鍵（<c>edid.cta.vic.{vic}</c>）取<b>跨顯示器的聯集</b>，
/// 值裡列出是哪些顯示器宣告了它、以及它在哪幾台上被標為原生模式。
/// <b>沒有這個塊</b>與<b>有這個塊但內容是空</b>是兩個不同的斷言，值裡分開寫。
/// </para>
///
/// <para>
/// <b>知識表的位置：</b>VIC 編號 → 時序文字走 <see cref="VicKnowledge"/>（154 條，
/// 出處與抓取日期寫在那一支的檔頭）；不在此表的 VIC 如實標「知識表未收錄」並附編號，
/// <b>不猜解析度</b>。
/// </para>
///
/// <para>
/// <b>輸入從哪來：</b>預設探針走 <see cref="EdidService.ReadRawAll"/>（唯讀登錄檔、
/// 依內容雜湊去重）；測試注入假探針即可完全離線驗證。空輸入＝NotApplicable 並說明，
/// <b>不是「0 台顯示器」</b>。
/// </para>
/// </summary>
public static class EdidCtaFactsService
{
    private const string Category = "顯示";
    private const string Source = "EDID CTA-861 擴充塊（HKLM\\SYSTEM\\CurrentControlSet\\Enum\\DISPLAY，唯讀）";

    /// <summary>CTA 修訂號、DTD 起點與資料塊數量（U10-GP-0100）。</summary>
    public const string RevKey = "edid.cta.rev";

    /// <summary>YCbCr 4:2:0 支援面（U10-GP-0102）。</summary>
    public const string Ycbcr420Key = "edid.cta.ycbcr420";

    /// <summary>視訊能力資料塊：量化選擇與畫面比例／掃描過度（U10-GP-0103）。</summary>
    public const string VcdbKey = "edid.cta.vcdb";

    /// <summary>HDMI 廠商特定資料塊：實體位址、深色、最大 TMDS 時脈（U10-GP-0104）。</summary>
    public const string HdmiVsdbKey = "edid.cta.vsdb.hdmi";

    /// <summary>HDMI Forum 廠商特定資料塊：SCDC、FRL（U10-GP-0105）。</summary>
    public const string HdmiForumKey = "edid.cta.hfvsdb";

    /// <summary>喇叭配置資料塊（U10-GP-0106）。</summary>
    public const string SpeakerKey = "edid.cta.speaker";

    /// <summary>HDR 靜態中繼資料：EOTF 位元與亮度碼值（U10-GP-0107）。</summary>
    public const string HdrKey = "edid.cta.hdr";

    /// <summary>色度學資料塊（U10-GP-0108）。</summary>
    public const string ColorimetryKey = "edid.cta.colorimetry";

    /// <summary>未解讀的資料塊清單，只列編號（U10-GP-0109）。</summary>
    public const string UnknownBlocksKey = "edid.cta.unknownblocks";

    /// <summary>
    /// 逐 VIC 的<strong>家族前綴</strong>：成員＝這台機器的顯示器實際宣告的 VIC 編號，
    /// 那是顯示器的事實（插哪一台、轉接器怎麼寫 EDID），編譯期列不完。
    /// </summary>
    private const string VicPrefix = "edid.cta.vic.";

    /// <summary>逐 VIC 鍵最多出幾把——超過就明說「等 N 個」，不假裝列完。</summary>
    private const int MaxVicKeys = 64;

    /// <summary>EDID 讀取探針：回（標籤, 原始 EDID）。預設走唯一那份登錄檔讀取實作。</summary>
    public delegate IReadOnlyList<(string Label, byte[] Edid)> EdidProbe();

    /// <summary>一台顯示器的一個 CTA 擴充塊。</summary>
    private sealed record DisplayExtension(string Label, CtaExtension Extension, bool MultipleExtensions);

    /// <summary>收集這一輪的所有 CTA 事實。</summary>
    public static IReadOnlyList<HardwareFact> Collect(DateTimeOffset at, EdidProbe? probe = null)
    {
        IReadOnlyList<(string Label, byte[] Edid)> edids;
        try { edids = (probe ?? new EdidProbe(EdidService.ReadRawAll))(); }
        catch (Exception ex)
        {
            return AllUnavailable(at, FactAvailability.ReadError,
                "EDID 讀取失敗：" + ex.Message + "——「讀不到」與「沒有宣告」是不同的斷言");
        }

        if (edids is null || edids.Count == 0)
            return AllUnavailable(at, FactAvailability.NotApplicable,
                "沒有讀到任何顯示器的 EDID（登錄檔沒有可解析的 EDID 值）——"
                + "本服務**不從 WMI 顯示器清單推測 CTA 能力**：那兩者是不同的來源。");

        var found = new List<DisplayExtension>();
        foreach (var (label, edid) in edids)
        {
            var extensions = Cta861.ParseExtensions(edid);
            for (int i = 0; i < extensions.Count; i++)
                found.Add(new DisplayExtension(label, extensions[i], extensions.Count > 1));
        }

        if (found.Count == 0)
            return AllUnavailable(at, FactAvailability.NotApplicable,
                $"{edids.Count} 台顯示器的 EDID 都沒有 CTA-861 擴充塊——"
                + "那代表這些面板沒有宣告任何 CTA 能力，**不是「宣告了 0 項」**");

        var facts = new List<HardwareFact>();

        facts.Add(new HardwareFact(RevKey, Category, "CTA 修訂與資料塊",
            Segments(found, RevText) + ScopeNote(edids.Count, found.Count)
            + "界線：CTA 修訂號講的是這份 EDID 用哪一版規格寫的，不是面板的新舊。",
            "", Source, FactTrustLevel.Reported, false, at));

        facts.AddRange(VicFacts(at, found));

        facts.Add(PerDisplayFact(at, Ycbcr420Key, "YCbCr 4:2:0", found,
            Ycbcr420Text, ext => true,
            "YCbCr 4:2:0（色度取樣減半）的宣告面"));
        facts.Add(PerDisplayFact(at, VcdbKey, "視訊能力資料塊", found,
            VcdbText, ext => Cta861.VideoCapability(ext) is not null,
            "量化選擇、畫面比例、IT 內容與掃描過度"));
        facts.Add(PerDisplayFact(at, HdmiVsdbKey, "HDMI 廠商資料塊", found,
            HdmiVsdbText, ext => Cta861.HdmiVsdb(ext) is not null,
            "HDMI 實體位址、深色支援與最大 TMDS 時脈"));
        facts.Add(PerDisplayFact(at, HdmiForumKey, "HDMI Forum 資料塊", found,
            HdmiForumText, ext => Cta861.HdmiForumVsdb(ext) is not null,
            "SCDC、讀取請求與 FRL 速率上限"));
        facts.Add(PerDisplayFact(at, SpeakerKey, "喇叭配置", found,
            SpeakerText, ext => Cta861.SpeakerAllocation(ext) is not null,
            "喇叭配置位圖"));
        facts.Add(PerDisplayFact(at, HdrKey, "HDR 靜態中繼資料", found,
            HdrText, ext => Cta861.HdrStatic(ext) is not null,
            "HDR 傳輸函數與期望亮度（宣告值，不是量測值）"));
        facts.Add(PerDisplayFact(at, ColorimetryKey, "色度學", found,
            ColorimetryText, ext => Cta861.Colorimetry(ext) is not null,
            "色度學支援位元"));
        facts.Add(PerDisplayFact(at, UnknownBlocksKey, "未解讀資料塊", found,
            UnknownBlocksText, ext => Cta861.UndecodedBlocks(ext).Count > 0,
            "本解碼器沒有實作的資料塊（只列編號，不命名）"));

        return facts;
    }

    // ── 逐鍵的產生 ────────────────────────────────────────────────────────

    /// <summary>逐 VIC 的家族鍵：跨顯示器聯集，值裡寫明誰宣告了它、誰標成原生。</summary>
    private static IEnumerable<HardwareFact> VicFacts(
        DateTimeOffset at, List<DisplayExtension> found)
    {
        var map = new SortedDictionary<int, List<(string Label, bool Native)>>();
        foreach (var item in found)
        {
            foreach (var svd in Cta861.ShortVideos(item.Extension))
            {
                if (!map.TryGetValue(svd.Vic, out var list))
                    map[svd.Vic] = list = [];
                if (!list.Any(x => x.Label == item.Label))
                    list.Add((item.Label, svd.Native));
                else if (svd.Native)
                    list[list.FindIndex(x => x.Label == item.Label)] = (item.Label, true);
            }
        }

        if (map.Count == 0) yield break;

        int emitted = 0;
        int omitted = 0;
        foreach (var (vic, owners) in map)
        {
            if (emitted >= MaxVicKeys) { omitted++; continue; }
            emitted++;
            string timing = VicKnowledge.Describe(vic) ?? $"VIC {vic}（知識表未收錄——不猜時序）";
            string native = string.Join("、", owners.Where(o => o.Native).Select(o => o.Label));
            string who = owners.Count == 1 ? owners[0].Label : string.Join("、", owners.Select(o => o.Label));
            string value = timing + $"；宣告於 {who}"
                + (native.Length > 0 ? $"；原生模式：{native}" : "；未標原生模式");
            yield return new HardwareFact($"{VicPrefix}{vic}", Category, $"VIC {vic}",
                value, "", Source, FactTrustLevel.Reported, false, at);
        }

        if (omitted > 0)
            yield return new HardwareFact($"{VicPrefix}truncated", Category, "VIC 清單截斷",
                $"另有 {omitted} 個 VIC 未逐個成鍵（本版上限 {MaxVicKeys} 把）——"
                + "列出上限是為了不讓事實集被單一顯示器的長清單淹掉；"
                + "被略過的編號仍在 edid.cta.rev 的資料塊計數裡。",
                "", Source, FactTrustLevel.Reported, false, at);
    }

    /// <summary>「某個塊」類事實的共用產生器：有沒有人宣告、有誰沒宣告、內容是什麼。</summary>
    private static HardwareFact PerDisplayFact(
        DateTimeOffset at, string key, string name, List<DisplayExtension> found,
        Func<CtaExtension, string> render, Func<CtaExtension, bool> hasBlock, string what)
    {
        if (!found.Any(f => hasBlock(f.Extension)))
            return new HardwareFact(key, Category, name, "", "", Source,
                FactTrustLevel.Reported, false, at, null, FactAvailability.NotApplicable,
                $"這一輪讀到的 {found.Count} 份 CTA 擴充塊裡都沒有{what}——"
                + "**「沒有宣告」不是「宣告了 0 項」**，也沒有可推測的替代來源");

        // 有這個塊的寫內容；沒有的那一台也要寫出來（「這台沒有」本身是資訊）。
        string body = JoinPerDisplay(found,
            ext => hasBlock(ext) ? render(ext) : null,
            "（這台沒有這個塊）");

        return new HardwareFact(key, Category, name,
            body + "。界線：這些全部是顯示器（或轉接器）自己寫在 EDID 裡的宣告，"
            + "不是本機量測到的畫質，也不代表線材與輸出端撐得住。",
            "", Source, FactTrustLevel.Reported, false, at);
    }

    // ── 逐塊的文字 ────────────────────────────────────────────────────────

    private static string RevText(CtaExtension ext)
    {
        int nativeDtd = ext.Flags & 0x0F;
        var sampling = new List<string>();
        if ((ext.Flags & 0x10) != 0) sampling.Add("YCbCr 4:2:2");
        if ((ext.Flags & 0x20) != 0) sampling.Add("YCbCr 4:4:4");
        return $"CTA 修訂 {ext.Revision}、DTD 起點 {ext.DtdOffset}、資料塊 {ext.Blocks.Count} 個、"
             + $"原生 DTD {nativeDtd} 個"
             + ((ext.Flags & 0x40) != 0 ? "、基本音訊" : "")
             + (sampling.Count > 0 ? "、支援 " + string.Join("／", sampling) : "");
    }

    private static string Ycbcr420Text(CtaExtension ext)
    {
        var (only, capable) = Cta861.Ycbcr420(ext);
        if (only.Count == 0 && capable.Count == 0)
            return "未宣告 4:2:0（YCbCr 4:2:0 的兩種塊都不在）";
        var parts = new List<string>();
        if (only.Count > 0) parts.Add("僅 4:2:0：" + JoinVics(only));
        if (capable.Count > 0) parts.Add("也可 4:2:0：" + JoinVics(capable));
        return string.Join("；", parts);
    }

    private static string JoinVics(IReadOnlyList<int> vics)
        => string.Join("、", vics.Take(12).Select(v => "VIC " + v))
         + (vics.Count > 12 ? $"（等 {vics.Count} 個）" : "");

    private static string VcdbText(CtaExtension ext)
    {
        var vcdb = Cta861.VideoCapability(ext);
        if (vcdb is null) return "（沒有視訊能力資料塊）";
        byte flags = vcdb.Value.Flags;
        string pt = ((flags >> 4) & 0x03) switch
        {
            0 => "未宣告畫面比例",
            1 => "只 4:3",
            2 => "只 16:9",
            _ => "4:3 與 16:9（PT=" + ((flags >> 4) & 0x03) + "）",
        };
        string it = ((flags >> 2) & 0x03) switch
        {
            0 => "無 IT 內容資訊",
            1 => "支援 IT 內容",
            _ => "IT 欄位保留值 " + ((flags >> 2) & 0x03),
        };
        string ce = (flags & 0x03) switch
        {
            0 => "未宣告掃描範圍",
            1 => "只過掃描",
            2 => "只欠掃描",
            _ => "過掃描與欠掃描皆可",
        };
        return $"可選量化：RGB {((flags & 0x80) != 0 ? "是" : "否")}、YCbCr {((flags & 0x40) != 0 ? "是" : "否")}；"
             + $"{pt}；{it}；{ce}";
    }

    private static string HdmiVsdbText(CtaExtension ext)
    {
        var vsdb = Cta861.HdmiVsdb(ext);
        if (vsdb is null) return "（沒有 HDMI 廠商特定資料塊）";
        var deep = new List<string>();
        if (vsdb.DeepColor30) deep.Add("30 位元");
        if (vsdb.DeepColor36) deep.Add("36 位元");
        if (vsdb.DeepColor48) deep.Add("48 位元");
        if (vsdb.DeepColorY444) deep.Add("YCbCr 4:4:4 深色");
        return $"實體位址 {vsdb.PhysicalAddress}、最大 TMDS 時脈 {vsdb.MaxTmdsClockMhz} MHz、"
             + $"深色 {(deep.Count > 0 ? string.Join("／", deep) : "未宣告")}"
             + (vsdb.SupportsAi ? "、支援 ACP／ISRC 內容" : "")
             + (vsdb.DviDual ? "、DVI 雙鏈" : "")
             + (vsdb.LatencyPresent ? "、有延遲欄位" : "")
             + (vsdb.HdmiVideoPresent ? "、有 HDMI 視訊欄位" : "");
    }

    private static string HdmiForumText(CtaExtension ext)
    {
        var vsdb = Cta861.HdmiForumVsdb(ext);
        if (vsdb is null) return "（沒有 HDMI Forum 資料塊）";
        return $"版本 {vsdb.Version}、最大 TMDS Character Rate {vsdb.MaxTmdsCharacterRateMhz} MHz、"
             + $"SCDC {(vsdb.ScdcPresent ? "支援" : "未宣告")}、"
             + $"讀取請求 {(vsdb.ReadRequestCapable ? "支援" : "未宣告")}、"
             + $"FRL 速率代碼 {vsdb.MaxFrlRateCode}（代碼到速率的對照表本版未收錄）";
    }

    private static string SpeakerText(CtaExtension ext)
    {
        var payload = Cta861.SpeakerAllocation(ext);
        if (payload is null) return "（沒有喇叭配置資料塊）";
        if (payload.Length == 0) return "（喇叭配置塊長度為 0）";
        byte b0 = payload[0];
        byte b1 = payload.Length > 1 ? payload[1] : (byte)0;
        var sets = new List<string>();
        (int Bit, string Name)[] first =
        [
            (0, "前左／前右"), (1, "低頻"), (2, "中置"), (3, "後左／後右"),
            (4, "後中"), (5, "前左中／前右中"), (6, "後左中／後右中"),
        ];
        foreach (var (bit, name) in first)
            if ((b0 & (1 << bit)) != 0) sets.Add(name);
        (int Bit, string Name)[] second =
        [ (0, "前左寬／前右寬"), (1, "前左高／前右高"), (2, "上中"), (3, "前中高") ];
        foreach (var (bit, name) in second)
            if ((b1 & (1 << bit)) != 0) sets.Add(name);
        return sets.Count == 0
            ? $"喇叭配置位圖 {b0:X2} {b1:X2}（一位都沒設）"
            : string.Join("、", sets) + $"（位圖 {b0:X2} {b1:X2}）";
    }

    private static string HdrText(CtaExtension ext)
    {
        var hdr = Cta861.HdrStatic(ext);
        if (hdr is null) return "（沒有 HDR 靜態中繼資料塊）";

        var eotf = new List<string>();
        if (hdr.TraditionalSdr) eotf.Add("傳統 gamma（SDR 範圍）");
        if (hdr.TraditionalHdr) eotf.Add("傳統 gamma（HDR 範圍）");
        if (hdr.SmpteSt2084) eotf.Add("SMPTE ST 2084（PQ）");
        if (hdr.Hlg) eotf.Add("BT.2100 HLG");

        string text = "EOTF " + (eotf.Count == 0 ? "（一位都沒設）" : string.Join("／", eotf))
                    + $"; HDR {(hdr.AnyHdrEotf ? "有" : "無")}（看 PQ／HLG 兩位元，不看傳統 gamma HDR）"
                    + (hdr.StaticMetadataType1 ? "; 靜態中繼資料類型 1" : "; 未宣告靜態中繼資料類型 1");

        if (hdr.MaxCll is int maxCode)
        {
            double maxNits = Cta861.MaxLuminanceNits(maxCode);
            text += $"; 期望最大亮度 {maxNits:F0} nits（碼值 {maxCode}）";
            if (hdr.MinCll is int minCode)
                text += $"; 期望最小亮度 {Cta861.MinLuminanceNits(maxNits, minCode):F4} nits（碼值 {minCode}）";
        }
        if (hdr.MaxFall is int fallCode)
            text += $"; 期望最大畫面平均亮度碼值 {fallCode}";
        return text + "（以上亮度是 EDID 宣告值而非量測值）";
    }

    private static string ColorimetryText(CtaExtension ext)
    {
        var c = Cta861.Colorimetry(ext);
        if (c is null) return "（沒有色度學資料塊）";
        byte bits = c.Value.Colorimetry;
        (int Bit, string Name)[] map =
        [
            (0, "xvYCC601"), (1, "xvYCC709"), (2, "sYCC601"), (3, "opYCC601／AdobeYCC601"),
            (4, "opRGB／AdobeRGB"), (5, "BT2020cYCC"), (6, "BT2020YCC"), (7, "BT2020RGB"),
        ];
        var on = map.Where(m => (bits & (1 << m.Bit)) != 0).Select(m => m.Name).ToList();
        return on.Count == 0
            ? $"色度學位元 {bits:X2}（一位都沒設）"
            : string.Join("、", on) + $"（位元 {bits:X2}）";
    }

    private static string UnknownBlocksText(CtaExtension ext)
    {
        var blocks = Cta861.UndecodedBlocks(ext);
        if (blocks.Count == 0) return "（所有資料塊都在本解碼器的解讀範圍內）";
        return string.Join("、", blocks.Select(b =>
            b.ExtendedTag >= 0
                ? $"延伸標籤 {b.ExtendedTag}（{b.Length} 位元組）"
                : $"標籤 {b.Tag}（{b.Length} 位元組）"));
    }

    // ── 共用小工具 ────────────────────────────────────────────────────────

    private static string Segments(List<DisplayExtension> found, Func<CtaExtension, string> render)
        => JoinPerDisplay(found, ext => render(ext), "（無）");

    /// <summary>
    /// 依顯示器分段：單台不加序號；多台用 ①②… 標頭並寫出顯示器標籤。
    /// <paramref name="render"/> 回 <c>null</c> 表示這一台沒有這個塊，改用 <paramref name="missingText"/>。
    /// </summary>
    private static string JoinPerDisplay(
        List<DisplayExtension> found, Func<CtaExtension, string?> render, string missingText)
    {
        if (found.Count == 1) return render(found[0].Extension) ?? missingText;

        var parts = new List<string>(found.Count);
        for (int i = 0; i < found.Count; i++)
        {
            string marker = ((char)('①' + Math.Min(i, 19))).ToString();
            string extra = found[i].MultipleExtensions
                ? $"[擴充塊{found[i].Extension.Index + 1}]"
                : "";
            parts.Add($"{marker}{extra} {found[i].Label}：{render(found[i].Extension) ?? missingText}");
        }
        return string.Join("；", parts);
    }

    private static string ScopeNote(int displayCount, int extensionCount)
        => displayCount == extensionCount
            ? $"（共 {displayCount} 台顯示器）。"
            : $"（共 {displayCount} 台顯示器、{extensionCount} 份 CTA 擴充塊）。";

    private static IReadOnlyList<HardwareFact> AllUnavailable(
        DateTimeOffset at, FactAvailability availability, string reason)
    {
        string[] keys =
        [
            RevKey, Ycbcr420Key, VcdbKey, HdmiVsdbKey, HdmiForumKey,
            SpeakerKey, HdrKey, ColorimetryKey, UnknownBlocksKey,
        ];
        return keys.Select(k => new HardwareFact(k, Category, k, "", "", Source,
            FactTrustLevel.Reported, false, at, null, availability, reason)).ToList();
    }
}
