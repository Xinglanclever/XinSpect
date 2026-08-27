using System.Text.RegularExpressions;

namespace XinSpect;

/// <summary>
/// 特殊硬體型號的專屬圖示定義（處理器 + 顯示卡）。
///
/// 資料驅動對照表：每個值得獨立圖示的知名型號在此登記一筆，含比對規則、
/// 徽章配色（<see cref="Emblem"/> 多段漸層）、版本標記膠囊配色（<see cref="Chip"/>）、
/// 中心字形或文字（<see cref="Glyph"/> / <see cref="Text"/>）與前景色（<see cref="Ink"/>），
/// 外加各式選配疊加層（圓環 / 邊框 / 左側直書 / 四角小標）。
/// 想新增型號，只要往對應表（<c>Cpu</c> / <c>Gpu</c>）加一列即可，不需改動任何顯示程式。
///
/// 顯示優先序（於 BrandBadge 實作）：
///   1. Assets/{cpu|gpu}/{Id}.png 若存在 → 顯示官方圖檔；
///   2. 否則 → 以本表的原創向量徽章（配色 + 字形/文字 + 疊加層）呈現；
///   3. 若型號未命中本表 → 退回品牌通用字形（既有行為）。
/// </summary>
public sealed record BadgeIcon(
    string Id,
    string Regex,
    string Tier,      // 版本標記膠囊文字，如「至尊版 Extreme Edition」
    string Glyph,     // 中心向量字形代號："x" | "star" | "cpu" | "gpu"；當 Text 非空時忽略；空=不畫中心字形
    string Text,      // 中心文字（如 "ES" / "XE" / "Everest"）；非空則以文字取代字形
    string Ink,       // 中心字形／文字前景色
    string[] Emblem,  // 徽章底色漸層段（1 段=純色、2 段=對角、3+ 段=彩虹）
    string[] Chip,    // 版本膠囊底色漸層段
    // ── 以下為選配疊加層（預設不顯示）──
    string[]? Ring = null,      // 圓形環：1 段=純色、2+ 段=漸層（撕裂者紅圈）；null=不畫
    string[]? Frame = null,     // 矩形邊框（沿徽章圓角）：1 段=純色、2+ 段=炫彩（8086K・9990XE・Everest 炫彩）
    string Side = "",           // 左上角橫書縮小字（如 "Xeon"）
    string LowerLeft = "",      // 左下角小標文字（如 "8086"）
    string Corner = "",         // 右下角小標文字（如 "Ryzen" / "P" / "Titan" / "CEO" / "Tesla"）
    string TopMark = "",        // 右上角小標文字（如 "W" / "E3" / "40^TH"，^ 後為上標縮小）
    string[]? TopColor = null,  // 右上角級別斜面填色（Xeon Scalable 銅/銀/金/白金）；與 TopMark 互斥
    string MarkInk = "#FFFFFF"); // 角落／側邊標記文字色（預設白；奔騰金／CEO 用深色）

public static class DeviceIcons
{
    // ── 常用配色（集中定義，方便日後統一調整）──
    private static readonly string[] Gold    = { "#F3C64B", "#8A5E08" };                 // 金色：現代 i9 至尊
    private static readonly string[] Silver  = { "#E6EAEE", "#8A9099" };                 // 銀色：Core X 系列（非至尊）
    private static readonly string[] Black   = { "#3C3C42", "#050506" };                 // 黑色：經典至尊 / 8086K / Everest
    private static readonly string[] RedES   = { "#EF4136", "#8A0D08" };                 // 紅色：工程樣品
    private static readonly string[] Rainbow = { "#FF3B3B", "#FF9F1C", "#FFE01B",        // 彩虹：炫彩邊框（9990XE / Everest）
                                                 "#43C94A", "#2E9BF0", "#5A4BE8", "#B24BF5" };
    private static readonly string[] TrBlack   = { "#33333A", "#050506" };               // 撕裂者黑底
    private static readonly string[] IntelBlue = { "#1B9AF0", "#0060B4" };               // Xeon＝原 Intel 藍
    private static readonly string[] PentGold  = { "#FFDA5E", "#C6900A" };               // 奔騰金（比至尊金亮一號）
    private static readonly string[] GpuBlack  = { "#33333A", "#0A0A0C" };               // Titan 黑底
    private static readonly string[] GpuSilver = { "#EDF0F4", "#A2ABB6" };               // Titan V／CEO 銀底（原白底改銀）
    private static readonly string[] NvGreen   = { "#86D01A", "#4A7A00" };               // Tesla 綠（同 NVIDIA 品牌色）

    private static readonly string[] TrRing = { "#E4322B" };                             // 撕裂者紅圈（單色環）
    private static readonly string[] FrameSilver = { "#EEF1F4" };                        // Titan 銀邊
    private static readonly string[] FrameGold   = { "#F6CE55" };                        // CEO 金邊

    private static readonly string[] ChipRed    = { "#C0392B", "#5A0A10" };              // 紅膠囊：至尊 / 撕裂者 / 測試版
    private static readonly string[] ChipGold   = { "#E8B23A", "#8A5A00" };              // 金膠囊：紀念版 / 奔騰金 / CEO
    private static readonly string[] ChipSilver = { "#6E7681", "#2F343B" };              // 鋼灰膠囊：Core X / Titan
    private static readonly string[] ChipBlue   = { "#1E6FC0", "#0A3E7A" };              // 藍膠囊：Xeon 系列
    private static readonly string[] ChipGreen  = { "#5A9E1E", "#2E5A00" };              // 綠膠囊：Tesla
    private static readonly string[] ChipBlack  = { "#3A3A42", "#0A0A0C" };              // 黑膠囊：Everest

    // Xeon Scalable 右上角級別斜面（雙色漸層＝金屬光澤）
    private static readonly string[] ScBronze   = { "#D9873F", "#7A431A" };
    private static readonly string[] ScSilver   = { "#C7CDD4", "#8C939B" };   // 銀灰
    private static readonly string[] ScGold     = { "#F3C64B", "#8A5E08" };
    private static readonly string[] ScPlatinum = { "#FFFFFF", "#D6DDE4" };   // 亮白金

    private const string White    = "#FFFFFF";
    private const string DarkInk  = "#4A3200";    // 金底上的深色壓印（至尊 XE / 奔騰 P），對比清晰
    private const string SilverInk = "#2E3238";   // 銀底上的深灰 ✕
    private const string CeoInk   = "#6A4E00";    // CEO 白/金底上的深金角標

    /// <summary>
    /// 特殊處理器對照表。比對以 CPU 名稱（WMI 回報字串，已轉小寫）為輸入，
    /// 由上而下取第一個命中者，故較專一的規則應排在前面。
    /// </summary>
    private static readonly BadgeIcon[] Cpu =
    {
        // ── Core i7-8086K：8086 處理器 40 週年紀念版（2018）。黑底 + 炫彩外圈 + 中心★，左下 8086、右上 40ᵀᴴ（皆白字）。
        new("i7-8086k",
            @"\bi7[- ]?8086k\b",
            "40 週年紀念版", "star", "", White,
            Black, ChipGold,
            Frame: Rainbow, LowerLeft: "8086", TopMark: "40^TH", MarkInk: White),

        // ── Everest 珠穆朗瑪峰系列：極罕見、為特定客戶訂製的英特爾處理器。黑底 + 炫彩邊框 + 「Everest」字。
        new("everest",
            @"\beverest\b",
            "Everest 訂製版", "", "Everest", White,
            Black, ChipBlack,
            Frame: Rainbow),

        // ── AMD Ryzen Threadripper 綫程撕裂者：黑底 + 紅色圓環 + 圓環中央放大「Ryzen」（大過圓圈），紅膠囊「Threadripper」。
        new("threadripper",
            @"threadripper",
            "Threadripper", "", "Ryzen", White,
            TrBlack, ChipRed,
            Ring: TrRing),

        // ── AMD Ryzen（一般 Ryzen 3/5/7/9、行動版、PRO 等）：與撕裂者同款黑底紅圈 + 圓環中央放大「Ryzen」，紅膠囊標「Ryzen」。
        //    須排在 threadripper 之後（撕裂者名稱亦含 "Ryzen"，較專一者優先命中）。
        new("ryzen",
            @"\bryzen\b",
            "Ryzen", "", "Ryzen", White,
            TrBlack, ChipRed,
            Ring: TrRing),

        // ── Pentium Gold 奔騰黃金版：比至尊金亮一號的金底 + 處理器字形 + 右下「P」。
        new("pentium-gold",
            @"pentium(?:\(r\))?\s*gold",
            "Pentium 黃金版", "cpu", "", DarkInk,
            PentGold, ChipGold,
            Corner: "P", MarkInk: DarkInk),

        // ── Core i9-9990XE：拍賣限定、OEM 專供的「隱藏款」至尊。金底 + 炫彩邊框 + 中心「XE」。
        new("i9-9990xe",
            @"\bi9[- ]?9990xe\b",
            "至尊版 · 隱藏款", "", "XE", DarkInk,
            Gold, ChipRed,
            Frame: Rainbow),

        // ── Core i9 至尊 Extreme Edition（真正的至尊旗艦，型號結尾 XE）：金底 + 中心「XE」+ 紅膠囊。
        //    7980XE / 9980XE / 10980XE 三款（9990XE 見上，另有炫彩邊框）。
        new("i9-extreme",
            @"\bi9[- ]?(?:7980|9980|10980)xe\b",
            "至尊版 Extreme Edition", "", "XE", DarkInk,
            Gold, ChipRed),

        // ── Core X 系列（7~10 代 HEDT 的 i7/i9「X」，非至尊版）：銀底 + 交叉 ✕ + 鋼灰膠囊。
        //    Skylake-X / Kaby Lake-X / Cascade Lake-X：如 7800X 7820X 7900X 7920X 7940X 7960X、
        //    9800X 9820X 9900X 9920X 9940X 9960X、10900X 10920X 10940X、Kaby-X 7640X 7740X。
        //    結尾 x\b（非 xe）→ 自動排除上方 XE 至尊；prefix 76~79 / 98~99 / 109 → 不觸經典至尊(39/49/980/990)。
        new("core-x",
            @"\bi[79][- ]?(?:7[6-9]\d\d|9[89]\d\d|109\d\d)x\b",
            "Core X 系列", "x", "", SilverInk,
            Silver, ChipSilver),

        // ── 經典至尊 Extreme Edition：黑底 + 交叉 ✕ + 紅膠囊。
        //    Core 2 Extreme、i7-980X / 990X（Gulftown）、i7-3960X / 3970X / 4960X。
        //    980X 舊機常報為 "Core(TM) i7 CPU X 980"（X 與數字分離），故比對放寬。
        new("i7-extreme",
            @"core(?:\(tm\)|\(r\))?\s*2\s*extreme|\b(?:980|990)x\b|\bi7\b.*\bx\s?9[89]0\b|\b(?:3960|3970|4960)x\b",
            "至尊版 Extreme Edition", "x", "", White,
            Black, ChipRed),

        // ── Intel Xeon Scalable：統一 Intel 藍 + 左側直書「XEON」+ 右上角級別色點（銅/銀/金/白金）。
        new("xeon-platinum",
            @"xeon.*platinum",
            "Xeon Scalable 白金", "cpu", "", White,
            IntelBlue, ChipBlue,
            Side: "Xeon", TopColor: ScPlatinum),
        new("xeon-gold",
            @"xeon.*\bgold\b",
            "Xeon Scalable 金牌", "cpu", "", White,
            IntelBlue, ChipBlue,
            Side: "Xeon", TopColor: ScGold),
        new("xeon-silver",
            @"xeon.*\bsilver\b",
            "Xeon Scalable 銀牌", "cpu", "", White,
            IntelBlue, ChipBlue,
            Side: "Xeon", TopColor: ScSilver),
        new("xeon-bronze",
            @"xeon.*\bbronze\b",
            "Xeon Scalable 銅牌", "cpu", "", White,
            IntelBlue, ChipBlue,
            Side: "Xeon", TopColor: ScBronze),

        // ── Xeon W / Xeon E：Intel 藍 + 左上「Xeon」+ 右上角字母標記。
        new("xeon-w",
            @"xeon.*\bw[- ]?\d",
            "Xeon W", "cpu", "", White,
            IntelBlue, ChipBlue,
            Side: "Xeon", TopMark: "W"),
        // Xeon E3 / E5 / E7（較舊的 v 世代，如 E3-1230 / E5-2680 / E7-8890）：右上標「E3 / E5 / E7」。
        //    須排在下方通用 Xeon E 之前（\be[- ]?\d 亦會命中 e3/e5/e7）。
        new("xeon-e7",
            @"xeon.*\be7[- ]?\d",
            "Xeon E7", "cpu", "", White,
            IntelBlue, ChipBlue,
            Side: "Xeon", TopMark: "E7"),
        new("xeon-e5",
            @"xeon.*\be5[- ]?\d",
            "Xeon E5", "cpu", "", White,
            IntelBlue, ChipBlue,
            Side: "Xeon", TopMark: "E5"),
        new("xeon-e3",
            @"xeon.*\be3[- ]?\d",
            "Xeon E3", "cpu", "", White,
            IntelBlue, ChipBlue,
            Side: "Xeon", TopMark: "E3"),
        // 新世代 Xeon E（如 E-2288G / E-2100・2200 系列）：右上標「E」。
        new("xeon-e",
            @"xeon.*\be[- ]?\d",
            "Xeon E", "cpu", "", White,
            IntelBlue, ChipBlue,
            Side: "Xeon", TopMark: "E"),

        // ── 一般 Xeon（無級別／無 W/E）：Intel 藍 + 左上「Xeon」。置於各 Xeon 規則之末。
        new("xeon",
            @"\bxeon\b",
            "Xeon", "cpu", "", White,
            IntelBlue, ChipBlue,
            Side: "Xeon"),

        // ── 工程／驗證樣品（ES / QS）：紅底 + 「ES」文字 + 紅膠囊。
        //    多見於 "Genuine Intel(R) CPU 0000"、含 "ES"/"QS"/"Engineering Sample" 等字樣。
        //    置於表末（最低優先）：零售特殊型號先取其專屬徽章，通用樣品名稱才落此。
        new("es-sample",
            @"\bes\b|\bqs\b|engineering sample|qualification sample|confidential|\b0000\b",
            "工程樣品 ES / QS", "", "ES", White,
            RedES, ChipRed),
    };

    /// <summary>
    /// 特殊顯示卡對照表。比對以顯示卡名稱（已轉小寫）為輸入，由上而下取第一個命中者。
    /// </summary>
    private static readonly BadgeIcon[] Gpu =
    {
        // ── Titan V CEO Edition：銀底 + 金邊 + 右下「CEO」（黃金珍藏版，NVIDIA 內部贈禮）。
        new("titan-ceo",
            @"titan.*ceo|ceo\s*edition",
            "Titan V · CEO Edition", "gpu", "", CeoInk,
            GpuSilver, ChipGold,
            Frame: FrameGold, Corner: "CEO", MarkInk: CeoInk),

        // ── Titan V（Volta 泰坦）：與 CEO 版同款銀底金邊，膠囊改標「Titan V」、右下角亦為「Titan V」。
        new("titan-v",
            @"\btitan\s*v\b",
            "Titan V", "gpu", "", CeoInk,
            GpuSilver, ChipGold,
            Frame: FrameGold, Corner: "Titan V", MarkInk: CeoInk),

        // ── Titan 系列（RTX / X / Xp…）：黑底 + 銀邊 + 右下「Titan」。
        new("titan",
            @"\btitan\b",
            "Titan 系列", "gpu", "", White,
            GpuBlack, ChipSilver,
            Frame: FrameSilver, Corner: "Titan"),

        // ── Tesla 運算卡：沿用 NVIDIA 綠 + 右下「Tesla」。
        new("tesla",
            @"\btesla\b",
            "Tesla 運算卡", "gpu", "", White,
            NvGreen, ChipGreen,
            Corner: "Tesla"),
    };

    /// <summary>由型號字串解析出專屬圖示；未命中或非 CPU/GPU 類別回傳 <c>null</c>（呼叫端退回通用字形）。</summary>
    public static BadgeIcon? Resolve(BadgeKind kind, string? model) => kind switch
    {
        BadgeKind.Cpu => Match(Cpu, model),
        BadgeKind.Gpu => Match(Gpu, model),
        _ => null,
    };

    /// <summary>對應資產子資料夾（官方 logo 覆蓋層）："cpu" / "gpu"；其餘類別無專屬圖示。</summary>
    public static string? AssetFolder(BadgeKind kind) => kind switch
    {
        BadgeKind.Cpu => "cpu",
        BadgeKind.Gpu => "gpu",
        _ => null,
    };

    private static BadgeIcon? Match(BadgeIcon[] table, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var s = name.ToLowerInvariant();
        foreach (var icon in table)
        {
            if (Regex.IsMatch(s, icon.Regex, RegexOptions.CultureInvariant))
                return icon;
        }
        return null;
    }
}
