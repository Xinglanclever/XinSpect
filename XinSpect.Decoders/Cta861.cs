using System;
using System.Collections.Generic;

namespace XinSpect;

/// <summary>CTA-861 擴充塊裡的一個資料塊（tag／長度／內容原樣）。</summary>
/// <param name="Tag">資料塊標籤（標頭高位 3 位）。</param>
/// <param name="Payload">內容位元組（不含標頭）；tag==7 時 <c>Payload[0]</c> 是延伸標籤。</param>
public sealed record CtaDataBlock(int Tag, byte[] Payload)
{
    /// <summary>是否為「使用延伸標籤」（tag 7）。</summary>
    public bool IsExtended => Tag == Cta861.TagExtended;

    /// <summary>延伸標籤；非延伸塊回 -1。</summary>
    public int ExtendedTag => IsExtended && Payload.Length > 0 ? Payload[0] : -1;
}

/// <summary>一個 CTA-861 擴充塊（128 位元組）。</summary>
/// <param name="Index">是第幾個擴充塊（0 起算，不是 EDID 的位元組位移）。</param>
/// <param name="Revision">CTA-861 修訂號（第 2 位元組）。</param>
/// <param name="DtdOffset">詳細時序描述元的起點（第 3 位元組；合法範圍 4–127）。</param>
/// <param name="Flags">第 4 位元組原樣（bit7 未定義、bit6 基本音訊、bit5–4 YCbCr 取樣、bit3–0 原生 DTD 數）。</param>
/// <param name="Blocks">資料塊集合（依出現順序）。</param>
public sealed record CtaExtension(
    int Index, byte Revision, int DtdOffset, byte Flags, IReadOnlyList<CtaDataBlock> Blocks);

/// <summary>視訊資料塊裡的一個短視訊描述元（SVD）：VIC ＋ 原生旗標。</summary>
public sealed record CtaShortVideo(int Vic, bool Native);

/// <summary>HDMI 1.4 廠商特定資料塊（OUI 00-0C-03）解出來的欄位。</summary>
public sealed record CtaHdmiVsdb(
    int PhysicalAddressRaw, string PhysicalAddress,
    bool SupportsAi, bool DeepColor30, bool DeepColor36, bool DeepColor48, bool DeepColorY444, bool DviDual,
    int MaxTmdsClockMhz, bool LatencyPresent, bool InterlacedLatencyPresent, bool HdmiVideoPresent,
    int RawPayloadLength);

/// <summary>HDMI Forum 廠商特定資料塊（OUI C4-5D-D8）解出來的欄位。</summary>
public sealed record CtaHdmiForumVsdb(
    byte Version, int MaxTmdsCharacterRateMhz, bool ScdcPresent, bool ReadRequestCapable, int MaxFrlRateCode);

/// <summary>HDR 靜態中繼資料塊（延伸標籤 6）解出來的欄位——全部保留原始碼值，換算另附。</summary>
public sealed record CtaHdrStatic(byte EotfBits, byte StaticMetadataDescriptor, int? MaxCll, int? MaxFall, int? MinCll)
{
    /// <summary>傳統 gamma（SDR 亮度範圍）——EOTF bit0。</summary>
    public bool TraditionalSdr => (EotfBits & 0x01) != 0;

    /// <summary>傳統 gamma（HDR 亮度範圍）——EOTF bit1。<b>這一項不等於「支援 HDR」</b>。</summary>
    public bool TraditionalHdr => (EotfBits & 0x02) != 0;

    /// <summary>SMPTE ST 2084（PQ，HDR10 的傳輸函數）——EOTF bit2。</summary>
    public bool SmpteSt2084 => (EotfBits & 0x04) != 0;

    /// <summary>BT.2100 HLG——EOTF bit3。</summary>
    public bool Hlg => (EotfBits & 0x08) != 0;

    /// <summary>有沒有一種「真的 HDR」的傳輸函數（PQ 或 HLG）。</summary>
    public bool AnyHdrEotf => SmpteSt2084 || Hlg;

    /// <summary>靜態中繼資料類型 1（SMPTE ST 2086 色度＋亮度）是否宣告。</summary>
    public bool StaticMetadataType1 => (StaticMetadataDescriptor & 0x01) != 0;
}

/// <summary>
/// CTA-861 擴充區塊（EDID 第 2 個 128 位元組之後的 tag 0x02 區塊）的純解碼器。
///
/// <para>
/// <b>出處與抓取日期（2026-10-27）：</b>標籤與延伸標籤編號、欄位位移、亮度換算式逐項對照
/// Linux 核心 <c>drivers/gpu/drm/drm_edid.c</c>（<c>CTA-861-H Table 60／62</c> 的常數表、
/// <c>drm_parse_hdr_metadata_block</c>、<c>drm_calculate_luminance_range</c>、
/// <c>drm_parse_hdmi_vsdb_*</c>）與 HDMI 1.4b §8.3.2（VSDB 佈局）。
/// <b>本檔不解讀沒有出處的位元</b>：收不到的欄位回 null／false，並在呼叫端如實說明。
/// </para>
///
/// <para>
/// <b>這一支為什麼值得存在：</b>專案原有的 <see cref="HdrCapabilityService"/> 自己走一遍 CTA 資料塊、
/// 用 <c>pos+2／+3／+4</c> 抓 EOTF／最大亮度／最小亮度——那三個位移各差一格
/// （EOTF 在 <c>payload[1]</c>、最大亮度在 <c>payload[3]</c>、最小亮度在 <c>payload[5]</c>），
/// 而且把「傳統 gamma HDR 範圍」（EOTF bit1）當成「支援 HDR」。
/// 真機 EDID（<c>E6 06 0D 01 A2 A2 10</c>：EOTF=0x0D＝SDR＋PQ＋HLG、maxCLL=maxFALL=162、minCLL=16）
/// 在舊實作下會被讀成「HDR 支援＝否、最大亮度＝1 nit」。<b>位移錯一格不會拋例外，它只會說錯話。</b>
/// </para>
///
/// <para>
/// <b>耐用性契約：</b>全部方法對任意位元組輸入都不丟例外——畸形輸入回空集合或 null。
/// 呼叫端（<c>EdidCtaFactsService</c>）負責把「沒有這個塊」與「解不出來」分成不同的狀態。
/// </para>
/// </summary>
public static class Cta861
{
    /// <summary>擴充區塊的 tag（EDID 第 <c>128k</c> 位元組；0x02＝CTA-861）。</summary>
    public const byte ExtensionTag = 0x02;

    /// <summary>資料塊標籤：音訊。</summary>
    public const int TagAudio = 1;

    /// <summary>資料塊標籤：視訊（短視訊描述元清單）。</summary>
    public const int TagVideo = 2;

    /// <summary>資料塊標籤：廠商特定（前 3 位元組＝OUI）。</summary>
    public const int TagVendor = 3;

    /// <summary>資料塊標籤：喇叭配置。</summary>
    public const int TagSpeaker = 4;

    /// <summary>資料塊標籤：使用延伸標籤。</summary>
    public const int TagExtended = 7;

    /// <summary>延伸標籤：視訊能力（VCDB）。</summary>
    public const int ExtVideoCapability = 0;

    /// <summary>延伸標籤：色度學。</summary>
    public const int ExtColorimetry = 5;

    /// <summary>延伸標籤：HDR 靜態中繼資料。</summary>
    public const int ExtHdrStatic = 6;

    /// <summary>延伸標籤：YCbCr 4:2:0 視訊資料塊（Y420VDB）。</summary>
    public const int ExtYcbcr420Video = 14;

    /// <summary>延伸標籤：YCbCr 4:2:0 能力對照表（Y420CMDB）。</summary>
    public const int ExtYcbcr420CapabilityMap = 15;

    /// <summary>HDMI Licensing 的 OUI（位元組序為 03 0C 00）。</summary>
    public const int OuiHdmi = 0x000C03;

    /// <summary>HDMI Forum 的 OUI（位元組序為 D8 5D C4）。</summary>
    public const int OuiHdmiForum = 0xC45DD8;

    /// <summary>資料塊標籤 → 名稱。未收錄回「tag N（未收錄）」——不猜。</summary>
    [SpecRef("CTA-861-H Table 60（CTA Tag Codes）：1 Audio、2 Video、3 Vendor Specific、4 Speaker Allocation、7 Use Extended Tag")]
    public static string TagName(int tag) => tag switch
    {
        TagAudio => "音訊資料塊",
        TagVideo => "視訊資料塊",
        TagVendor => "廠商特定資料塊",
        TagSpeaker => "喇叭配置資料塊",
        TagExtended => "延伸標籤資料塊",
        _ => $"tag {tag}（未收錄）",
    };

    /// <summary>延伸標籤 → 名稱。未收錄回「延伸 tag N（未收錄）」——不猜。</summary>
    [SpecRef("CTA-861-H Table 62（CTA Extended Tag Codes）：0 Video Capability、1 Vendor-Specific Video、5 Colorimetry、6 HDR Static Metadata、14 YCbCr 4:2:0 Video、15 YCbCr 4:2:0 Capability Map")]
    public static string ExtendedTagName(int extendedTag) => extendedTag switch
    {
        ExtVideoCapability => "視訊能力資料塊",
        ExtColorimetry => "色度學資料塊",
        ExtHdrStatic => "HDR 靜態中繼資料塊",
        ExtYcbcr420Video => "YCbCr 4:2:0 視訊資料塊",
        ExtYcbcr420CapabilityMap => "YCbCr 4:2:0 能力對照塊",
        _ => $"延伸 tag {extendedTag}（未收錄）",
    };

    /// <summary>
    /// 把整份 EDID 裡的 CTA-861 擴充塊全部解出來（依出現順序）。
    /// 沒有擴充塊、擴充塊標籤不是 0x02、或長度不足 128 的區塊一律跳過——回空集合。
    /// </summary>
    [SpecRef("EDID 1.4 §3.10：0x7E＝擴充塊數；每個擴充塊 128 位元組，第 1 位元組為 tag（CTA-861 為 0x02）")]
    public static IReadOnlyList<CtaExtension> ParseExtensions(byte[]? edid)
    {
        var list = new List<CtaExtension>();
        if (edid is null || edid.Length < 128) return list;

        int extensionCount = edid[0x7E];
        for (int i = 0; i < extensionCount; i++)
        {
            int start = 128 * (i + 1);
            if (start + 128 > edid.Length) break;
            if (edid[start] != ExtensionTag) continue;

            byte revision = edid[start + 1];
            int dtdOffset = edid[start + 2];
            byte flags = edid[start + 3];

            var blocks = new List<CtaDataBlock>();
            // 資料塊集合位於第 4 位元組到 DTD 起點之間；起點超出 4–127 表示區塊壞了，
            // 此時不猜資料塊（回空的 Blocks 但保留修訂號與旗標——那兩格仍然讀得到）。
            if (dtdOffset is >= 4 and <= 127)
            {
                int pos = 4;
                while (pos < dtdOffset)
                {
                    byte header = edid[start + pos];
                    int tag = (header >> 5) & 0x07;
                    int length = header & 0x1F;
                    // 長度 0 的塊如果照公式前進會停在原地——那是無窮迴圈，直接停止。
                    if (length == 0) break;
                    if (pos + 1 + length > dtdOffset) break;

                    var payload = new byte[length];
                    Array.Copy(edid, start + pos + 1, payload, 0, length);
                    blocks.Add(new CtaDataBlock(tag, payload));
                    pos += 1 + length;
                }
            }

            list.Add(new CtaExtension(i, revision, dtdOffset, flags, blocks));
        }
        return list;
    }

    /// <summary>
    /// 視訊資料塊裡的短視訊描述元：每 1 位元組一個，bit7＝原生模式、bits6:0＝VIC。
    /// 全部視訊資料塊合併回傳；沒有視訊資料塊回空集合。
    /// </summary>
    [SpecRef("CTA-861-H §7.5.2 Video Data Block：每個 SVD 1 位元組，bit7＝native、bits6:0＝VIC（Table 3）")]
    public static IReadOnlyList<CtaShortVideo> ShortVideos(CtaExtension? extension)
    {
        var list = new List<CtaShortVideo>();
        if (extension is null) return list;
        foreach (var block in extension.Blocks)
        {
            if (block.Tag != TagVideo) continue;
            foreach (byte b in block.Payload)
                list.Add(new CtaShortVideo(b & 0x7F, (b & 0x80) != 0));
        }
        return list;
    }

    /// <summary>HDR 靜態中繼資料塊（延伸標籤 6）；沒有這個塊回 null。</summary>
    [SpecRef("CTA-861-G HDR Static Metadata Data Block（延伸標籤 6）：payload[1]＝EOTF、[2]＝靜態中繼資料描述元、[3]＝期望最大亮度、[4]＝期望最大畫面平均亮度、[5]＝期望最小亮度；交叉 Linux drm_edid.c drm_parse_hdr_metadata_block")]
    public static CtaHdrStatic? HdrStatic(CtaExtension? extension)
    {
        if (extension is null) return null;
        foreach (var block in extension.Blocks)
        {
            if (!block.IsExtended || block.ExtendedTag != ExtHdrStatic) continue;
            if (block.Payload.Length < 5) continue;   // 至少要 EOTF＋描述元＋最大亮度

            byte eotf = block.Payload[1];
            byte descriptor = block.Payload[2];
            int? maxCll = block.Payload[3];
            int? maxFall = block.Payload.Length > 4 ? block.Payload[4] : null;
            int? minCll = block.Payload.Length > 5 ? block.Payload[5] : null;
            return new CtaHdrStatic(eotf, descriptor, maxCll, maxFall, minCll);
        }
        return null;
    }

    /// <summary>
    /// 期望最大亮度碼值 → cd/m²（<c>50 × 2^(CV/32)</c>）。<b>這是規格換算式，不是量測值。</b>
    /// </summary>
    [SpecRef("CTA-861-G：Desired Content Max Luminance 的碼值 CV 換算為 50*2^(CV/32) cd/m²（交叉 Linux drm_edid.c drm_calculate_luminance_range 的同一式子）")]
    public static double MaxLuminanceNits(int cv) => 50.0 * Math.Pow(2.0, cv / 32.0);

    /// <summary>
    /// 期望最小亮度碼值 → cd/m²（<c>最大亮度 × (CV/255)² / 100</c>）。同樣是規格換算式。
    /// </summary>
    [SpecRef("CTA-861-G：Desired Content Min Luminance 的碼值 CV 換算為 最大亮度*(CV/255)^2/100（見 Linux drm_edid.c drm_calculate_luminance_range 的註解）")]
    public static double MinLuminanceNits(double maxNits, int cv)
    {
        double ratio = cv / 255.0;
        return maxNits * ratio * ratio / 100.0;
    }

    /// <summary>HDMI 1.4 廠商特定資料塊（OUI 00-0C-03）；沒有回 null。</summary>
    [SpecRef("HDMI 1.4b §8.3.2（HDMI VSDB 佈局）：payload[0..2]＝OUI 03 0C 00、[3..4]＝物理位址、[5]＝Supports_AI／DC_48／DC_36／DC_30／DC_Y444／DVI_Dual、[6]＝最大 TMDS 時脈÷5MHz、[7]＝延遲與 HDMI 視訊旗標")]
    public static CtaHdmiVsdb? HdmiVsdb(CtaExtension? extension)
    {
        if (extension is null) return null;
        foreach (var block in extension.Blocks)
        {
            if (block.Tag != TagVendor) continue;
            if (!OuiMatches(block.Payload, OuiHdmi)) continue;
            if (block.Payload.Length < 6) continue;   // OUI＋物理位址＝至少 5，旗標再接一位

            int high = block.Payload[3], low = block.Payload[4];
            int raw = (high << 8) | low;
            string address = $"{high >> 4}.{high & 0x0F}.{low >> 4}.{low & 0x0F}";
            byte flags = block.Payload[5];
            bool hasClock = block.Payload.Length >= 7;
            int maxTmds = hasClock ? block.Payload[6] * 5 : 0;
            byte flags2 = block.Payload.Length >= 8 ? block.Payload[7] : (byte)0;

            return new CtaHdmiVsdb(
                raw, address,
                SupportsAi: (flags & 0x80) != 0,
                DeepColor48: (flags & 0x40) != 0,
                DeepColor36: (flags & 0x20) != 0,
                DeepColor30: (flags & 0x10) != 0,
                DeepColorY444: (flags & 0x08) != 0,
                DviDual: (flags & 0x01) != 0,
                MaxTmdsClockMhz: maxTmds,
                LatencyPresent: (flags2 & 0x80) != 0,
                InterlacedLatencyPresent: (flags2 & 0x40) != 0,
                HdmiVideoPresent: (flags2 & 0x20) != 0,
                RawPayloadLength: block.Payload.Length);
        }
        return null;
    }

    /// <summary>HDMI Forum 廠商特定資料塊（OUI C4-5D-D8）；沒有回 null。</summary>
    [SpecRef("HDMI Forum VSDB（OUI C4-5D-D8）：payload[3]＝版本、[4]＝最大 TMDS Character Rate÷5MHz、[5] bit7＝SCDC_Present、bit6＝RR_Capable、bits3:0＝Max_FRL_Rate（交叉 Linux drm_edid.c cea_db_is_hdmi_forum_vsdb 與 drm_parse_hdmi_forum_vsdb）")]
    public static CtaHdmiForumVsdb? HdmiForumVsdb(CtaExtension? extension)
    {
        if (extension is null) return null;
        foreach (var block in extension.Blocks)
        {
            if (block.Tag != TagVendor) continue;
            if (!OuiMatches(block.Payload, OuiHdmiForum)) continue;
            if (block.Payload.Length < 6) continue;

            byte flags = block.Payload[5];
            return new CtaHdmiForumVsdb(
                Version: block.Payload[3],
                MaxTmdsCharacterRateMhz: block.Payload[4] * 5,
                ScdcPresent: (flags & 0x80) != 0,
                ReadRequestCapable: (flags & 0x40) != 0,
                MaxFrlRateCode: flags & 0x0F);
        }
        return null;
    }

    /// <summary>色度學資料塊（延伸標籤 5）的兩個位元組；沒有回 null。</summary>
    [SpecRef("CTA-861-G Colorimetry Data Block（延伸標籤 5）：payload[1] bit0 xvYCC601、bit1 xvYCC709、bit2 sYCC601、bit3 opYCC601、bit4 opRGB、bit5 BT2020cYCC、bit6 BT2020YCC、bit7 BT2020RGB；payload[2] 為中繼資料描述元")]
    public static (byte Colorimetry, byte MetadataDescriptor)? Colorimetry(CtaExtension? extension)
    {
        if (extension is null) return null;
        foreach (var block in extension.Blocks)
        {
            if (!block.IsExtended || block.ExtendedTag != ExtColorimetry) continue;
            if (block.Payload.Length < 2) continue;
            return (block.Payload[1], block.Payload.Length >= 3 ? block.Payload[2] : (byte)0);
        }
        return null;
    }

    /// <summary>視訊能力資料塊（延伸標籤 0）的兩個位元組；沒有回 null。</summary>
    [SpecRef("CTA-861-G Video Capability Data Block（延伸標籤 0）：payload[1] bit7 QS、bit6 QY、bits5:4 PT（畫面比例）、bits3:2 IT、bits1:0 CE（掃描過度）")]
    public static (byte Flags, byte Second)? VideoCapability(CtaExtension? extension)
    {
        if (extension is null) return null;
        foreach (var block in extension.Blocks)
        {
            if (!block.IsExtended || block.ExtendedTag != ExtVideoCapability) continue;
            if (block.Payload.Length < 2) continue;
            return (block.Payload[1], block.Payload.Length >= 3 ? block.Payload[2] : (byte)0);
        }
        return null;
    }

    /// <summary>喇叭配置資料塊（tag 4）的原樣位元組；沒有回 null。</summary>
    [SpecRef("CTA-861-G Speaker Allocation Data Block（tag 4）：3 位元組位圖，payload[0] bit0 FL/FR、bit1 LFE、bit2 FC、bit3 RL/RR、bit4 RC、bit5 FLC/FRC、bit6 RLC/RRC；payload[1] bit0 FLW/FRW、bit1 FLH/FRH、bit2 TC、bit3 FCH")]
    public static byte[]? SpeakerAllocation(CtaExtension? extension)
    {
        if (extension is null) return null;
        foreach (var block in extension.Blocks)
        {
            if (block.Tag != TagSpeaker) continue;
            return block.Payload.Length >= 1 ? block.Payload : null;
        }
        return null;
    }

    /// <summary>
    /// YCbCr 4:2:0 的兩種塊合併解讀：
    /// <paramref name="onlyVics"/>＝Y420VDB 直接列出的 VIC；
    /// <paramref name="capableFromVideoBlock"/>＝Y420CMDB 位圖對應到的視訊資料塊 SVD（0 起算的序號換成 VIC）。
    /// 沒有 4:2:0 塊時兩者皆為空集合。
    /// </summary>
    [SpecRef("CTA-861-G YCbCr 4:2:0 Video Data Block（延伸標籤 14）與 Capability Map（延伸標籤 15）；交叉 Linux drm_edid.c parse_cta_y420cmdb（bit i 對應視訊資料塊第 i 個 SVD；對照塊長度 0 時表示全部支援）")]
    public static (IReadOnlyList<int> OnlyVics, IReadOnlyList<int> CapableFromVideoBlock) Ycbcr420(CtaExtension? extension)
    {
        var only = new List<int>();
        var capable = new List<int>();
        if (extension is null) return (only, capable);

        foreach (var block in extension.Blocks)
        {
            if (!block.IsExtended) continue;
            if (block.ExtendedTag == ExtYcbcr420Video)
            {
                for (int i = 1; i < block.Payload.Length; i++)
                {
                    int vic = block.Payload[i] & 0x7F;
                    if (vic != 0) only.Add(vic);
                }
            }
            else if (block.ExtendedTag == ExtYcbcr420CapabilityMap)
            {
                var svds = ShortVideos(extension);
                int mapLength = block.Payload.Length - 1;
                if (mapLength <= 0)
                {
                    // 「沒有任何位元組」＝全部視訊資料塊的模式都支援 4:2:0。
                    foreach (var svd in svds) capable.Add(svd.Vic);
                    continue;
                }
                for (int byteIndex = 0; byteIndex < mapLength && byteIndex < 8; byteIndex++)
                {
                    byte bits = block.Payload[byteIndex + 1];
                    for (int bit = 0; bit < 8; bit++)
                    {
                        if ((bits & (1 << bit)) == 0) continue;
                        int svdIndex = byteIndex * 8 + bit;
                        if (svdIndex < svds.Count) capable.Add(svds[svdIndex].Vic);
                    }
                }
            }
        }
        return (only, capable);
    }

    /// <summary>
    /// 未解讀的資料塊清單（標籤號／延伸標籤號＋長度），<b>只列編號不命名</b>。
    /// 音訊塊與喇叭配置以外的 tag 都算「未解讀」——本解碼器不假裝懂它沒實作的塊。
    /// </summary>
    [SpecRef("CTA-861-H Table 60／62：本解碼器只解視訊、廠商特定（HDMI／HDMI Forum）、喇叭配置與延伸標籤 0／5／6／14／15；其餘如實列編號")]
    public static IReadOnlyList<(int Tag, int ExtendedTag, int Length)> UndecodedBlocks(CtaExtension? extension)
    {
        var list = new List<(int, int, int)>();
        if (extension is null) return list;
        foreach (var block in extension.Blocks)
        {
            if (block.Tag == TagVideo) continue;
            if (block.Tag == TagSpeaker) continue;
            if (block.Tag == TagVendor && (OuiMatches(block.Payload, OuiHdmi) || OuiMatches(block.Payload, OuiHdmiForum))) continue;
            if (block.IsExtended && block.ExtendedTag is ExtVideoCapability or ExtColorimetry or ExtHdrStatic or ExtYcbcr420Video or ExtYcbcr420CapabilityMap) continue;
            list.Add((block.Tag, block.ExtendedTag, block.Payload.Length));
        }
        return list;
    }

    /// <summary>OUI 比對：資料塊前 3 位元組是低位在前（03 0C 00 表示 OUI 0x000C03）。</summary>
    [SpecRef("CTA-861-H §7.5.3 Vendor Specific Data Block：前 3 位元組為 IEEE OUI，位元組序低位在前")]
    public static bool OuiMatches(byte[]? payload, int oui)
    {
        if (payload is null || payload.Length < 3) return false;
        int value = payload[0] | (payload[1] << 8) | (payload[2] << 16);
        return value == oui;
    }
}
