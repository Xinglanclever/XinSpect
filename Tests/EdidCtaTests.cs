using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// EDID CTA-861 擴充塊解碼（第 276 輪）：純解碼器 <see cref="Cta861"/>、VIC 知識表
/// <see cref="VicKnowledge"/>、事實層 <see cref="EdidCtaFactsService"/>，以及
/// <see cref="HdrCapabilityService"/> 在這一輪修掉的兩個實質缺陷的回歸測試。
///
/// <para>
/// <b>測試資料從哪來：</b>兩份<b>真實 EDID 的 CTA 擴充塊</b>（本機 2026-10-27 從
/// <c>HKLM\SYSTEM\CurrentControlSet\Enum\DISPLAY\…\Device Parameters\EDID</c> 取出）：
/// </para>
/// <list type="bullet">
///   <item><c>Chd0030</c>（CHU／型號 CHUD，256 位元組）：CTA 修訂 3、DTD@64，
///     視訊資料塊 17 個 VIC（16 與 31 標原生）、音訊、延伸標籤 0x11、喇叭配置、
///     HDMI VSDB（實體位址 2.0.0.0、最大 TMDS 300 MHz）、視訊能力資料塊。</item>
///   <item><c>Mtt1337</c>（「VDD by MTT」虛擬顯示器，256 位元組）：CTA 修訂 3、DTD@32，
///     <b>HDR 靜態中繼資料</b>（EOTF 0x0D＝SDR＋PQ＋HLG、maxCLL／maxFALL 162、minCLL 16）、
///     色度學（0xD8）、HDMI Forum VSDB（版本 1、550 MHz、SCDC）、HDMI VSDB。</item>
/// </list>
/// <para>
/// 這兩份之所以有價值：<b>它們是舊實作讀錯的那兩段</b>。舊碼在資料塊集合裡找「標籤 6」
/// （保留值）而不是「標籤 7＋延伸標籤 6」，所以 MTT1337 明明宣告了 HDR，
/// 畫面卻只能說「未找到」；而亮度位移也各差一格。合成 fixture 可以證明實作符合我以為的規格，
/// <b>只有真機資料能證明我以為的規格就是這台機器在用的那一版</b>。
/// </para>
/// </summary>
public class Cta861DecoderTests
{
    /// <summary>CHU／CHUD 的 CTA 擴充塊（128 位元組，含尾端 checksum 0x33）。</summary>
    private static readonly byte[] Chd0030Extension = Hex(
        "020340F25190020304050701121314169F20220611152C091707150750570601670403E61146D000708083470000" +
        "6E030C002000B83C20008001020304E200FF1622569E510031303E90A500721053000018023A80D072382D4010" +
        "2C458072105300001E00000000000000000000000000000000000000000000000000000033");

    /// <summary>MTT1337（「VDD by MTT」）的 CTA 擴充塊（128 位元組，含尾端 checksum 0x8C）。</summary>
    private static readonly byte[] Mtt1337Extension = Hex(
        "02032040E6060D01A2A210E305D80067D85DC4016E800068030C00000030000B000000000000000000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
        "0000000000000000000000000000000000000000000000000000008C");

    private static byte[] Hex(string s)
    {
        var bytes = new byte[s.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(s.Substring(i * 2, 2), 16);
        return bytes;
    }

    /// <summary>把一段 CTA 擴充塊包成一份 256 位元組 EDID（表頭合法、擴充塊數 1）。</summary>
    private static byte[] EdidWithExtension(byte[] extension)
    {
        var edid = new byte[256];
        byte[] header = [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00];
        header.CopyTo(edid, 0);
        edid[0x7E] = 1;
        extension.CopyTo(edid, 128);
        return edid;
    }

    // ── 擴充塊走訪 ────────────────────────────────────────────────────────

    [Fact]
    public void 沒有擴充塊的EDID回空集合_不拋例外()
    {
        Assert.Empty(Cta861.ParseExtensions(null));
        Assert.Empty(Cta861.ParseExtensions([]));
        Assert.Empty(Cta861.ParseExtensions(new byte[128]));
    }

    [Fact]
    public void 擴充塊數宣告與實際長度不符時_只解真的存在的那一個()
    {
        var edid = EdidWithExtension(Chd0030Extension);
        edid[0x7E] = 4;   // 宣告 4 個，實際只有 1 個 128 位元組區塊
        var extensions = Cta861.ParseExtensions(edid);
        Assert.Single(extensions);
        Assert.Equal(3, extensions[0].Revision);
        Assert.Equal(64, extensions[0].DtdOffset);
    }

    [Fact]
    public void DTD起點越界時_不猜資料塊但保留修訂號()
    {
        var extension = (byte[])Chd0030Extension.Clone();
        extension[2] = 0;       // DTD 起點 0（非法，合法範圍 4–127）
        var parsed = Cta861.ParseExtensions(EdidWithExtension(extension));
        Assert.Single(parsed);
        Assert.Empty(parsed[0].Blocks);
        Assert.Equal(3, parsed[0].Revision);
    }

    [Fact]
    public void 長度為零的資料塊不會讓走訪停不下來()
    {
        var extension = new byte[128];
        extension[0] = 0x02; extension[1] = 0x03; extension[2] = 0x10; // DTD@16
        extension[4] = 0x00;   // 標籤 0、長度 0：照公式前進會停在原地
        extension[5] = 0x51;   // 後面還有看似合法的位元組
        var parsed = Cta861.ParseExtensions(EdidWithExtension(extension));
        Assert.Single(parsed);
        Assert.Empty(parsed[0].Blocks);
    }

    [Fact]
    public void 資料塊長度超出DTD起點時停止_不讀到詳細時序區()
    {
        var extension = new byte[128];
        extension[0] = 0x02; extension[1] = 0x03; extension[2] = 0x08; // DTD@8
        extension[4] = 0x45;   // 標籤 2、長度 5——超過 DTD 起點（4+1+5 > 8）
        var parsed = Cta861.ParseExtensions(EdidWithExtension(extension));
        Assert.Single(parsed);
        Assert.Empty(parsed[0].Blocks);
    }

    // ── 真實 EDID：視訊資料塊與 VIC ──────────────────────────────────────

    [Fact]
    public void 真機EDID_CHD0030的視訊塊有十七個VIC且兩個標為原生()
    {
        var extension = Cta861.ParseExtensions(EdidWithExtension(Chd0030Extension)).Single();
        var svds = Cta861.ShortVideos(extension);

        Assert.Equal(17, svds.Count);
        Assert.Equal(new[] { 16, 2, 3, 4, 5, 7, 1, 18, 19, 20, 22, 31, 32, 34, 6, 17, 21 },
            svds.Select(s => s.Vic).ToArray());
        Assert.Equal(new[] { 16, 31 }, svds.Where(s => s.Native).Select(s => s.Vic).ToArray());
    }

    [Fact]
    public void 真機EDID_CHD0030的修訂與旗標逐欄相符()
    {
        var extension = Cta861.ParseExtensions(EdidWithExtension(Chd0030Extension)).Single();
        Assert.Equal(3, extension.Revision);
        Assert.Equal(64, extension.DtdOffset);
        Assert.Equal(6, extension.Blocks.Count);
        // flags 0xF2：bit6 基本音訊、bit5 YCbCr 4:4:4、bit4 YCbCr 4:2:2、低 4 位原生 DTD 數＝2
        Assert.True((extension.Flags & 0x40) != 0, "bit6 基本音訊");
        Assert.Equal(2, extension.Flags & 0x0F);
    }

    // ── 真實 EDID：HDR（本輪修掉的缺陷）────────────────────────────────────

    [Fact]
    public void 真機EDID_MTT1337的HDR塊在延伸標籤6而不是標籤6()
    {
        var extension = Cta861.ParseExtensions(EdidWithExtension(Mtt1337Extension)).Single();

        // 這一段就是舊實作永遠找不到它的原因：標頭 0xE6 的標籤是 7（延伸），不是 6。
        var hdrBlock = extension.Blocks.Single(b => b.IsExtended && b.ExtendedTag == Cta861.ExtHdrStatic);
        Assert.Equal(7, hdrBlock.Tag);
        Assert.Equal(6, hdrBlock.ExtendedTag);
        Assert.NotEqual("HDR 靜態中繼資料塊", Cta861.TagName(6));
    }

    [Fact]
    public void 真機EDID_MTT1337的HDR欄位逐欄相符()
    {
        var hdr = Cta861.HdrStatic(
            Cta861.ParseExtensions(EdidWithExtension(Mtt1337Extension)).Single());

        Assert.NotNull(hdr);
        Assert.Equal(0x0D, hdr!.EotfBits);
        Assert.True(hdr.TraditionalSdr, "EOTF bit0 傳統 gamma SDR");
        Assert.False(hdr.TraditionalHdr, "EOTF bit1 傳統 gamma HDR 未設——它不是『支援 HDR』");
        Assert.True(hdr.SmpteSt2084, "EOTF bit2 PQ");
        Assert.True(hdr.Hlg, "EOTF bit3 HLG");
        Assert.True(hdr.AnyHdrEotf);
        Assert.True(hdr.StaticMetadataType1);
        Assert.Equal(162, hdr.MaxCll);
        Assert.Equal(162, hdr.MaxFall);
        Assert.Equal(16, hdr.MinCll);
    }

    [Fact]
    public void 亮度換算式是五十乘二的CV除三十二次方()
    {
        // 真機碼值 162 → 50×2^(162/32) ≈ 1670.8 cd/m²（不是舊碼的 (162+1)/2＝81.5）
        Assert.Equal(1670.8, Cta861.MaxLuminanceNits(162), precision: 1);
        Assert.Equal(50.0, Cta861.MaxLuminanceNits(0), precision: 3);
        Assert.Equal(100.0, Cta861.MaxLuminanceNits(32), precision: 3);

        // 最小亮度＝最大亮度 ×(CV/255)²/100：CV=16 時約 0.0658 cd/m²
        Assert.Equal(0.0658, Cta861.MinLuminanceNits(1670.8, 16), precision: 4);
        Assert.Equal(0.0, Cta861.MinLuminanceNits(1000, 0), precision: 6);
    }

    [Fact]
    public void 標籤六的保留塊不會被當成HDR靜態中繼資料()
    {
        // 造一個「資料塊標籤 6、內容長得像 HDR」的塊：舊實作會把它讀成 HDR，
        // 而 CTA-861-H 的標籤 6 是保留值——正確行為是「不是 HDR 塊」。
        var extension = new byte[128];
        extension[0] = 0x02; extension[1] = 0x03; extension[2] = 0x10;
        extension[4] = 0xC6;                       // 標籤 6、長度 6
        extension[5] = 0x06; extension[6] = 0x0D;  // 看起來像延伸標籤＋EOTF
        extension[7] = 0x01; extension[8] = 0xA2; extension[9] = 0xA2; extension[10] = 0x10;

        var parsed = Cta861.ParseExtensions(EdidWithExtension(extension)).Single();
        Assert.Single(parsed.Blocks);
        Assert.Equal(6, parsed.Blocks[0].Tag);
        Assert.Null(Cta861.HdrStatic(parsed));
    }

    // ── 真實 EDID：HDMI VSDB／HDMI Forum／喇叭／色度學 ────────────────────

    [Fact]
    public void 真機EDID_CHD0030的HDMI廠商塊解析實體位址與深色與TMDS()
    {
        var vsdb = Cta861.HdmiVsdb(
            Cta861.ParseExtensions(EdidWithExtension(Chd0030Extension)).Single());

        Assert.NotNull(vsdb);
        Assert.Equal("2.0.0.0", vsdb!.PhysicalAddress);
        Assert.True(vsdb.SupportsAi);
        Assert.True(vsdb.DeepColor30);
        Assert.True(vsdb.DeepColor36);
        Assert.False(vsdb.DeepColor48);
        Assert.True(vsdb.DeepColorY444);
        Assert.False(vsdb.DviDual);
        Assert.Equal(300, vsdb.MaxTmdsClockMhz);   // 0x3C×5 MHz
        Assert.True(vsdb.HdmiVideoPresent);
    }

    [Fact]
    public void 真機EDID_MTT1337的HDMI論壇塊解析SCDC與FRL()
    {
        var vsdb = Cta861.HdmiForumVsdb(
            Cta861.ParseExtensions(EdidWithExtension(Mtt1337Extension)).Single());

        Assert.NotNull(vsdb);
        Assert.Equal(1, vsdb!.Version);
        Assert.Equal(550, vsdb.MaxTmdsCharacterRateMhz);   // 0x6E×5 MHz
        Assert.True(vsdb.ScdcPresent);
        Assert.False(vsdb.ReadRequestCapable);
        Assert.Equal(0, vsdb.MaxFrlRateCode);
    }

    [Fact]
    public void 真機EDID_CHD0030的喇叭配置位圖()
    {
        var payload = Cta861.SpeakerAllocation(
            Cta861.ParseExtensions(EdidWithExtension(Chd0030Extension)).Single());
        Assert.NotNull(payload);
        Assert.Equal(3, payload!.Length);
        Assert.Equal(0x47, payload[0]);
        Assert.Equal(0x00, payload[1]);
    }

    [Fact]
    public void 真機EDID_MTT1337的色度學位元()
    {
        var colorimetry = Cta861.Colorimetry(
            Cta861.ParseExtensions(EdidWithExtension(Mtt1337Extension)).Single());
        Assert.NotNull(colorimetry);
        Assert.Equal(0xD8, colorimetry!.Value.Colorimetry);
        // 0xD8 = 位元 3／4／6／7＝opYCC601、opRGB、BT2020YCC、BT2020RGB
        Assert.True((colorimetry.Value.Colorimetry & 0x08) != 0);
        Assert.True((colorimetry.Value.Colorimetry & 0x80) != 0);
    }

    [Fact]
    public void 真機EDID_CHD0030的視訊能力塊量化選擇兩位都設()
    {
        var vcdb = Cta861.VideoCapability(
            Cta861.ParseExtensions(EdidWithExtension(Chd0030Extension)).Single());
        Assert.NotNull(vcdb);
        Assert.Equal(0xFF, vcdb!.Value.Flags);
        Assert.True((vcdb.Value.Flags & 0x80) != 0, "可選 RGB 量化");
        Assert.True((vcdb.Value.Flags & 0x40) != 0, "可選 YCbCr 量化");
    }

    [Fact]
    public void 未解讀的資料塊只列編號_音訊與未知延伸標籤都在清單裡()
    {
        var blocks = Cta861.UndecodedBlocks(
            Cta861.ParseExtensions(EdidWithExtension(Chd0030Extension)).Single());
        Assert.Contains(blocks, b => b.Tag == Cta861.TagAudio);
        // CHD0030 的延伸標籤 0x11 不在本解碼器的解讀範圍內
        Assert.Contains(blocks, b => b.ExtendedTag == 0x11);
        // 已解讀的視訊資料塊不得出現在清單裡
        Assert.DoesNotContain(blocks, b => b.Tag == Cta861.TagVideo);
    }

    [Fact]
    public void OUI比對是低位在前()
    {
        Assert.True(Cta861.OuiMatches([0x03, 0x0C, 0x00], Cta861.OuiHdmi));
        Assert.False(Cta861.OuiMatches([0x00, 0x0C, 0x03], Cta861.OuiHdmi));
        Assert.True(Cta861.OuiMatches([0xD8, 0x5D, 0xC4], Cta861.OuiHdmiForum));
        Assert.False(Cta861.OuiMatches([0x03, 0x0C], Cta861.OuiHdmi));
        Assert.False(Cta861.OuiMatches(null, Cta861.OuiHdmi));
    }

    [Fact]
    public void 沒有4比2比0塊時回空集合()
    {
        var (only, capable) = Cta861.Ycbcr420(
            Cta861.ParseExtensions(EdidWithExtension(Chd0030Extension)).Single());
        Assert.Empty(only);
        Assert.Empty(capable);
    }

    [Fact]
    public void 能力對照表的位元對應視訊資料塊的序號()
    {
        // 視訊塊 3 個 SVD（VIC 16、31、4），能力對照表只有 1 個位元組、bit0 與 bit2 設起
        var extension = new byte[128];
        extension[0] = 0x02; extension[1] = 0x03; extension[2] = 0x14;  // DTD@20
        extension[4] = 0x43;                                            // 標籤 2、長度 3
        extension[5] = 16; extension[6] = 31; extension[7] = 4;
        extension[8] = 0xE2;   // 標籤 7（延伸）、長度 2
        extension[9] = 0x0F;   // 延伸標籤 15（4:2:0 能力對照）
        extension[10] = 0x05;  // 位圖：bit0 與 bit2＝視訊塊第 1 個與第 3 個 SVD

        var parsed = Cta861.ParseExtensions(EdidWithExtension(extension)).Single();
        var (_, capable) = Cta861.Ycbcr420(parsed);
        Assert.Equal(new[] { 16, 4 }, capable.ToArray());
    }

    [Fact]
    public void 能力對照表長度為零時代表全部視訊模式都支援()
    {
        var extension = new byte[128];
        extension[0] = 0x02; extension[1] = 0x03; extension[2] = 0x10;
        extension[4] = 0x42;                                            // 標籤 2、長度 2
        extension[5] = 16; extension[6] = 31;
        extension[7] = 0xE1; extension[8] = 0x0F;                       // 延伸 15、長度 1（沒有位圖）

        var parsed = Cta861.ParseExtensions(EdidWithExtension(extension)).Single();
        var (_, capable) = Cta861.Ycbcr420(parsed);
        Assert.Equal(new[] { 16, 31 }, capable.ToArray());
    }
}

/// <summary>VIC 知識表：收錄數、抽樣值與保留區間。</summary>
public class VicKnowledgeTests
{
    [Fact]
    public void 收錄數與宣告相符()
    {
        Assert.Equal(154, VicKnowledge.Count);
        // 1–127 與 193–219 兩個區間都是滿的
        for (int vic = 1; vic <= 127; vic++)
            Assert.NotNull(VicKnowledge.Describe(vic));
        for (int vic = 193; vic <= 219; vic++)
            Assert.NotNull(VicKnowledge.Describe(vic));
    }

    [Fact]
    public void 保留區間沒有條目_不猜時序()
    {
        for (int vic = 0; vic <= 0; vic++)
            Assert.Null(VicKnowledge.Describe(vic));
        for (int vic = 128; vic <= 192; vic++)
            Assert.Null(VicKnowledge.Describe(vic));
        for (int vic = 220; vic <= 255; vic++)
            Assert.Null(VicKnowledge.Describe(vic));
    }

    [Fact]
    public void 抽樣值逐字等於規格表的註解()
    {
        Assert.Equal("640x480@60Hz 4:3", VicKnowledge.Describe(1));
        Assert.Equal("1920x1080i@60Hz 16:9", VicKnowledge.Describe(5));
        Assert.Equal("1920x1080@60Hz 16:9", VicKnowledge.Describe(16));
        Assert.Equal("1920x1080@50Hz 16:9", VicKnowledge.Describe(31));
        Assert.Equal("1280x720@24Hz 16:9", VicKnowledge.Describe(60));
        Assert.Equal("1920x1080@120Hz 16:9", VicKnowledge.Describe(63));
        Assert.Equal("3840x2160@50Hz 16:9", VicKnowledge.Describe(96));
        Assert.Equal("4096x2160@50Hz 256:135", VicKnowledge.Describe(101));
        Assert.Equal("3840x2160@24Hz 64:27", VicKnowledge.Describe(103));
        Assert.Equal("4096x2160@100Hz 256:135", VicKnowledge.Describe(218));
        Assert.Equal("10240x4320@120Hz 64:27", VicKnowledge.Describe(217));
    }
}

/// <summary>
/// 事實層：三態、多顯示器分段、逐 VIC 聯集，以及與 HDR 服務的一致性。
/// </summary>
public class EdidCtaFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);

    private static readonly byte[] Chd0030 = Wrap(Cta861DecoderTests_Extension("CHD"));
    private static readonly byte[] Mtt1337 = Wrap(Cta861DecoderTests_Extension("MTT"));

    /// <summary>與 <see cref="Cta861DecoderTests"/> 共用的兩份真實擴充塊（只取需要的長度）。</summary>
    private static byte[] Cta861DecoderTests_Extension(string which)
    {
        string hex = which == "CHD"
            ? "020340F25190020304050701121314169F20220611152C091707150750570601670403E61146D000708083470000" +
              "6E030C002000B83C20008001020304E200FF1622569E510031303E90A500721053000018023A80D072382D4010" +
              "2C458072105300001E00000000000000000000000000000000000000000000000000000033"
            :             "02032040E6060D01A2A210E305D80067D85DC4016E800068030C00000030000B000000000000000000000000000000000000" +
            "0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
            "0000000000000000000000000000000000000000000000000000008C";
        var bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return bytes;
    }

    private static byte[] Wrap(byte[] extension)
    {
        var edid = new byte[256];
        byte[] header = [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00];
        header.CopyTo(edid, 0);
        edid[0x7E] = 1;
        extension.CopyTo(edid, 128);
        return edid;
    }

    private static IReadOnlyList<HardwareFact> Collect(params (string Label, byte[] Edid)[] edids)
        => EdidCtaFactsService.Collect(At, () => edids);

    // ── 三態 ──────────────────────────────────────────────────────────────

    [Fact]
    public void 沒有讀到任何EDID時_九把固定鍵都標不適用並說明()
    {
        var facts = Collect();
        Assert.Equal(9, facts.Count);
        Assert.All(facts, f =>
        {
            Assert.Equal(FactAvailability.NotApplicable, f.Availability);
            Assert.Null(f.NumericValue);
            Assert.Contains("不從 WMI 顯示器清單推測", f.UnavailableReason!);
        });
    }

    [Fact]
    public void 讀取丟例外時標讀取錯誤而不是沒有宣告()
    {
        var facts = EdidCtaFactsService.Collect(At,
            () => throw new InvalidOperationException("機碼鎖住"));
        Assert.All(facts, f =>
        {
            Assert.Equal(FactAvailability.ReadError, f.Availability);
            Assert.Contains("機碼鎖住", f.UnavailableReason!);
        });
    }

    [Fact]
    public void EDID有但沒有CTA擴充塊時標不適用_不是宣告了零項()
    {
        var plain = new byte[128];
        byte[] header = [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00];
        header.CopyTo(plain, 0);
        var facts = Collect(("DEL S2719DGF", plain));
        Assert.All(facts, f =>
        {
            Assert.Equal(FactAvailability.NotApplicable, f.Availability);
            Assert.Contains("不是「宣告了 0 項」", f.UnavailableReason!);
        });
    }

    [Fact]
    public void 某個塊只有一台宣告時_另一台也要寫出來()
    {
        var facts = Collect(("CHU CHUD", Chd0030), ("VDD by MTT", Mtt1337));
        var hdr = facts.Single(f => f.Key == EdidCtaFactsService.HdrKey);

        Assert.Equal(FactAvailability.Present, hdr.Availability);
        Assert.Contains("（這台沒有這個塊）", hdr.Value);
        Assert.Contains("①", hdr.Value);
        Assert.Contains("②", hdr.Value);
    }

    [Fact]
    public void 沒有任何一台宣告某個塊時_該鍵標不適用並點名是哪個塊()
    {
        var facts = Collect(("CHU CHUD", Chd0030));
        var hdr = facts.Single(f => f.Key == EdidCtaFactsService.HdrKey);
        Assert.Equal(FactAvailability.NotApplicable, hdr.Availability);
        Assert.Contains("HDR 傳輸函數與期望亮度", hdr.UnavailableReason!);
        Assert.Contains("不是「宣告了 0 項」", hdr.UnavailableReason!);
    }

    // ── 內容 ──────────────────────────────────────────────────────────────

    [Fact]
    public void 多顯示器時修訂號事實依顯示器分段()
    {
        var facts = Collect(("CHU CHUD", Chd0030), ("VDD by MTT", Mtt1337));
        var rev = facts.Single(f => f.Key == EdidCtaFactsService.RevKey);

        Assert.Equal(FactAvailability.Present, rev.Availability);
        Assert.Contains("CHU CHUD", rev.Value);
        Assert.Contains("VDD by MTT", rev.Value);
        Assert.Contains("CTA 修訂 3", rev.Value);
        Assert.Contains("共 2 台顯示器", rev.Value);
    }

    [Fact]
    public void 逐VIC鍵是跨顯示器聯集並註明誰標原生()
    {
        var facts = Collect(("CHU CHUD", Chd0030), ("VDD by MTT", Mtt1337));

        var vic16 = facts.Single(f => f.Key == "edid.cta.vic.16");
        Assert.Equal(FactAvailability.Present, vic16.Availability);
        Assert.Contains("1920x1080@60Hz 16:9", vic16.Value);
        Assert.Contains("原生模式：CHU CHUD", vic16.Value);

        // 只有 CHD0030 宣告 VIC 31
        var vic31 = facts.Single(f => f.Key == "edid.cta.vic.31");
        Assert.Contains("1920x1080@50Hz 16:9", vic31.Value);
        Assert.DoesNotContain("VDD by MTT", vic31.Value);
    }

    [Fact]
    public void HDR事實寫的是宣告值並附碼值()
    {
        var facts = Collect(("VDD by MTT", Mtt1337));
        var hdr = facts.Single(f => f.Key == EdidCtaFactsService.HdrKey);

        Assert.Contains("SMPTE ST 2084（PQ）", hdr.Value);
        Assert.Contains("BT.2100 HLG", hdr.Value);
        Assert.Contains("期望最大亮度", hdr.Value);
        Assert.Contains("碼值 162", hdr.Value);
        Assert.Contains("宣告值而非量測值", hdr.Value);
    }

    [Fact]
    public void 未解讀資料塊事實只列編號()
    {
        var facts = Collect(("CHU CHUD", Chd0030));
        var unknown = facts.Single(f => f.Key == EdidCtaFactsService.UnknownBlocksKey);
        Assert.Equal(FactAvailability.Present, unknown.Availability);
        // 音訊塊（標籤 1）與延伸標籤 0x11 都不是本解碼器實作的範圍
        Assert.Contains("標籤 1", unknown.Value);
        Assert.Contains("延伸標籤 17", unknown.Value);
    }

    [Fact]
    public void 事實鍵目錄與家族都收錄了這一輪的鍵()
    {
        var keys = Collect(("CHU CHUD", Chd0030), ("VDD by MTT", Mtt1337))
            .Select(f => f.Key).ToList();

        foreach (string key in keys)
        {
            bool known = FactKeyCatalog.Keys.Contains(key, StringComparer.Ordinal)
                      || FactKeyDynamicCatalog.Matches(key);
            Assert.True(known, $"{key} 不在靜態目錄也不在任何動態家族——執行期對帳會紅燈");
        }
    }
}

/// <summary>
/// <see cref="HdrCapabilityService"/> 第 276 輪的回歸測試：真機 EDID 的 HDR 區塊要被找到，
/// 而且亮度要照規格換算。
/// </summary>
public class HdrCapabilityRegressionTests
{
    private static byte[] RealHdrEdid()
    {
        const string hex =
                        "02032040E6060D01A2A210E305D80067D85DC4016E800068030C00000030000B000000000000000000000000000000000000" +
            "0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
            "0000000000000000000000000000000000000000000000000000008C";
        var edid = new byte[256];
        byte[] header = [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00];
        header.CopyTo(edid, 0);
        edid[0x7E] = 1;
        for (int i = 0; i < 128; i++)
            edid[128 + i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return edid;
    }

    [Fact]
    public void 真機EDID的HDR區塊要被找到_不再回未找到()
    {
        var rows = HdrCapabilityService.Read(RealHdrEdid());

        var support = rows.SingleOrDefault(r => r.Label == "HDR 支援");
        Assert.NotNull(support);
        Assert.Equal("是", support!.Value);

        // 舊實作在這一台會回「未找到」，而且即使匹配到也會把亮度算成 81 nits。
        Assert.DoesNotContain(rows, r => r.Value == "未找到");
        var max = rows.SingleOrDefault(r => r.Label == "最大亮度");
        Assert.NotNull(max);
        Assert.Contains("1671 nits", max!.Value);
    }

    [Fact]
    public void EOTF欄位逐位列出_傳統gammaHDR不算支援HDR()
    {
        var rows = HdrCapabilityService.Read(RealHdrEdid());
        var eotf = rows.Single(r => r.Label == "EOTF");
        Assert.Contains("傳統 gamma（SDR 範圍）", eotf.Value);
        Assert.Contains("SMPTE ST 2084（PQ）", eotf.Value);
        Assert.Contains("BT.2100 HLG", eotf.Value);
        Assert.DoesNotContain("傳統 gamma（HDR 範圍）", eotf.Value);
    }
}

/// <summary>
/// <c>nw.edid.summary</c> 的第 276 輪修正：四個數字原本只有「顯示器台數」有來源，
/// 另外三個（checksum 合格／HDR 區塊／CTA 擴充）寫死 0——於是真機上長期顯示
/// 「HDR 區塊 0」，而其中一台顯示器明明宣告了 HDR。這組測試把四個數字都釘住。
/// </summary>
public class EdidSummaryCountTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);

    private static byte[] WithExtension(byte[] extension)
    {
        var edid = new byte[256];
        byte[] header = [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00];
        header.CopyTo(edid, 0);
        edid[0x7E] = 1;
        extension.CopyTo(edid, 128);
        return edid;
    }

    private static byte[] PlainBasicBlock()
    {
        var edid = new byte[128];
        byte[] header = [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00];
        header.CopyTo(edid, 0);
        return edid;
    }

    [Fact]
    public void 沒有EDID時回null而不是四顆零()
    {
        Assert.Null(FinalInventoryFactsService.CountEdids([]));

        var fact = FinalInventoryFactsService.EdidFact(At, () => null);
        Assert.Equal(FactAvailability.ReadError, fact.Availability);
        Assert.Null(fact.NumericValue);
        Assert.Contains("不是「0 台顯示器」", fact.UnavailableReason!);
    }

    [Fact]
    public void checksum的算法是每個位元組相加對256取餘數()
    {
        var good = PlainBasicBlock();
        // 讓 128 位元組的和為 0 mod 256：最後一位補上抵消值
        int sum = good.Sum(b => (int)b);
        good[127] = (byte)((256 - (sum & 0xFF)) & 0xFF);

        var state = FinalInventoryFactsService.CountEdids([("A", good)]);
        Assert.NotNull(state);
        Assert.Equal(1, state!.Monitors);
        Assert.Equal(1, state.ValidChecksums);

        good[127] = (byte)(good[127] + 1);
        var broken = FinalInventoryFactsService.CountEdids([("A", good)]);
        Assert.Equal(0, broken!.ValidChecksums);
    }

    [Fact]
    public void HDR與CTA的計數由真的解碼器數出來()
    {
        // 與 Cta861DecoderTests 同一份 MTT1337 擴充塊（含 HDR 靜態中繼資料）
        const string mtt =
            "02032040E6060D01A2A210E305D80067D85DC4016E800068030C00000030000B000000000000000000000000000000000000" +
            "0000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000" +
            "0000000000000000000000000000000000000000000000000000008C";
        var ext = new byte[128];
        for (int i = 0; i < 128; i++)
            ext[i] = Convert.ToByte(mtt.Substring(i * 2, 2), 16);

        var state = FinalInventoryFactsService.CountEdids(
            [("VDD by MTT", WithExtension(ext)), ("無擴充塊", PlainBasicBlock())]);

        Assert.NotNull(state);
        Assert.Equal(2, state!.Monitors);
        Assert.Equal(1, state.HdrBlocks);
        Assert.Equal(1, state.CtaExtensions);
    }

    [Fact]
    public void 事實文字要把四個數字都寫出來並指向逐塊鍵()
    {
        var fact = FinalInventoryFactsService.EdidFact(At, () => new(2, 1, 1, 1));
        Assert.Contains("顯示器 2", fact.Value);
        Assert.Contains("checksum 合格 1", fact.Value);
        Assert.Contains("宣告 HDR 靜態中繼資料的 1 台", fact.Value);
        Assert.Contains("CTA-861 擴充塊 1 份", fact.Value);
        Assert.Contains("edid.cta.*", fact.Value);
    }
}
