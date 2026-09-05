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
///   1. Assets/{cpu|gpu|disk|board}/{Id}.png 若存在 → 顯示官方圖檔；
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
    string MarkInk = "#FFFFFF", // 角落／側邊標記文字色（預設白；奔騰金／CEO 用深色）
    // ── 第二枚膠囊：一顆片子同時屬於兩種身分時用（如 X5698 既是 Everest 系列、本身又是 Xeon）──
    string Tier2 = "",          // 第二枚膠囊文字；空＝只畫一枚
    string[]? Chip2 = null);    // 第二枚膠囊底色漸層段；須與 Tier2 同時給才會顯示

public static class DeviceIcons
{
    // ── 常用配色（集中定義，方便日後統一調整）──
    private static readonly string[] Gold    = { "#F3C64B", "#8A5E08" };                 // 金色：現代 i9 至尊
    private static readonly string[] Silver  = { "#E6EAEE", "#8A9099" };                 // 銀色：Core X 系列（非至尊）
    private static readonly string[] Black   = { "#3C3C42", "#050506" };                 // 黑色：經典至尊 / 8086K / Everest 系列 / E5-1680 v2 黑工作站
    private static readonly string[] ExtBlue = { "#3E86E0", "#0A2A6E" };                 // 深海藍：Haswell-E／Broadwell-E 至尊
    private static readonly string[] RedES   = { "#EF4136", "#8A0D08" };                 // 紅色：工程樣品
    private static readonly string[] Rainbow = { "#FF3B3B", "#FF9F1C", "#FFE01B",        // 彩虹：炫彩邊框（9990XE / Everest 系列）
                                                 "#43C94A", "#2E9BF0", "#5A4BE8", "#B24BF5" };
    private static readonly string[] TrBlack   = { "#33333A", "#050506" };               // 撕裂者黑底
    private static readonly string[] IntelBlue = { "#1B9AF0", "#0060B4" };               // Xeon＝原 Intel 藍
    private static readonly string[] PentGold  = { "#FFDA5E", "#C6900A" };               // 奔騰金（比至尊金亮一號）
    private static readonly string[] GpuBlack  = { "#33333A", "#0A0A0C" };               // Titan 黑底
    private static readonly string[] GpuSilver = { "#EDF0F4", "#A2ABB6" };               // Titan V／CEO 銀底（原白底改銀）
    private static readonly string[] NvGreen   = { "#86D01A", "#4A7A00" };               // Tesla 綠（同 NVIDIA 品牌色）
    private static readonly string[] SteamNavy = { "#2A475E", "#171A21" };               // Steam 深藍（同 Steam 站上配色）：CC150
    private static readonly string[] EpycTeal  = { "#2FA0A8", "#0C3A44" };               // EPYC 青藍（AMD 伺服器線）
    private static readonly string[] OpterGrn  = { "#7BAE2B", "#2C4E08" };               // Opteron 綠（AMD 舊伺服器線）
    private static readonly string[] ProBlue   = { "#3AA0E0", "#0B4B7A" };               // Radeon Pro／FirePro 藍（AMD 專業繪圖）
    private static readonly string[] AmdRed    = { "#E4453B", "#7A100B" };               // AMD 紅：Instinct 運算卡
    private static readonly string[] OptaneBlu = { "#3D6BE8", "#0B1B5A" };               // Optane 深藍：3D XPoint 儲存

    private static readonly string[] TrRing = { "#E4322B" };                             // 撕裂者紅圈（單色環）
    private static readonly string[] FrameSilver = { "#EEF1F4" };                        // Titan 銀邊
    private static readonly string[] FrameGold   = { "#F6CE55" };                        // CEO 金邊

    private static readonly string[] ChipRed    = { "#C0392B", "#5A0A10" };              // 紅膠囊：至尊 / 撕裂者 / 測試版
    private static readonly string[] ChipGold   = { "#E8B23A", "#8A5A00" };              // 金膠囊：紀念版 / 奔騰金 / CEO
    private static readonly string[] ChipSilver = { "#6E7681", "#2F343B" };              // 鋼灰膠囊：Core X / Titan
    private static readonly string[] ChipBlue   = { "#1E6FC0", "#0A3E7A" };              // 藍膠囊：Xeon 系列 / Radeon Pro
    private static readonly string[] ChipTeal   = { "#1E7F8C", "#08313A" };              // 青藍膠囊：EPYC
    private static readonly string[] ChipGreen  = { "#5A9E1E", "#2E5A00" };              // 綠膠囊：Tesla
    private static readonly string[] ChipBlack  = { "#3A3A42", "#0A0A0C" };              // 黑膠囊：Everest 珠穆朗瑪峰系列
    private static readonly string[] ChipSteam  = { "#2F6E9E", "#161D26" };              // Steam 藍膠囊：CC150

    // Xeon Scalable 右上角級別斜面（雙色漸層＝金屬光澤）
    private static readonly string[] ScBronze   = { "#D9873F", "#7A431A" };
    private static readonly string[] ScSilver   = { "#C7CDD4", "#8C939B" };   // 銀灰
    private static readonly string[] ScGold     = { "#F3C64B", "#8A5E08" };
    private static readonly string[] ScPlatinum = { "#FFFFFF", "#D6DDE4" };   // 亮白金

    private const string White    = "#FFFFFF";
    private const string DarkInk  = "#4A3200";    // 金底上的深色壓印（至尊 XE / 奔騰 P），對比清晰
    private const string SilverInk = "#2E3238";   // 銀底上的深灰 ✕
    private const string CeoInk   = "#6A4E00";    // CEO 白/金底上的深金角標

    // 系列膠囊：Everest 全系列共用一枚（膠囊標系列、中心字標機種），改字只需改這裡。
    private const string EverestSeries = "Everest 珠穆朗瑪峰系列";

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

        // ── Everest 珠穆朗瑪峰系列：Intel 的非路線圖（off-roadmap）極限 bin 路線——把某一代能穩定跑到最高頻的
        //    die 特挑出來，為特定客戶（低延遲金融交易一類）訂製，以核心數與功耗換單執行緒延遲，
        //    韌體字串常直接掛專案代號而非型號。整系列共用一套外觀：黑底 + 炫彩邊框 + 黑膠囊「Everest 珠穆朗瑪峰系列」；
        //    膠囊標系列、中心字標各自的機種，故一個機種一條規則——這樣 BlackOps 不會被冒名成「Everest」。
        //    字串自稱 Xeon 的成員（X5698、E5-2602 v4）另並掛一枚藍色「Xeon」膠囊：它們同時是 Xeon，兩個身分都該看得到。
        //    已知成員（依世代）：
        //      · Xeon X5698        Westmere-EP／LGA1366：2 核 4.4GHz、12MB L3 全留、130W、OEM 特注（SLC32）
        //      · JKT Everest       Sandy Bridge-EP／LGA2011：晶片直接標 "JKT EVEREST SS 4.4GHZ INTERNAL USE ONLY"
        //      · BlackOps 4C / 6C  Ivy Bridge-EP／LGA2011：4C 4.4GHz、6C 4.6GHz，皆 25MB L3（10 核 die 關核而來）、約 250W
        //      · Xeon E5-2602 v4   Broadwell-EP／LGA2011-3：傳聞 4 核 5.1GHz、165W。CPU Shack 有記載但註明未見實物，
        //                          證據等級遠低於上述三者；先掛徽章備用（此編號零售不存在，不會誤判）
        //    刻意不併入本系列的鄰居：Core 2 Extreme QX9775 與 Xeon X5492 雖是同一套「極限 bin」思路的早期代表，
        //    但兩者都是正式零售 SKU，徽章該顯示的是它們自己的正式身分（至尊版 / Xeon），故維持原判。
        //    整組須排在各 Xeon 規則與表末工程樣品（ES / QS）之前：X5698 與 E5-2602 v4 的字串含 "Xeon"，
        //    而這些片子又多以工程樣品形式流出、名稱常帶 "Engineering Sample"，先命中系列規則才不會被搶走。
        new("xeon-x5698",
            @"\bx5698\b",
            EverestSeries, "", "X5698", White,
            Black, ChipBlack,
            Frame: Rainbow, Tier2: "Xeon", Chip2: ChipBlue),   // 雙重身分：黑膠囊掛系列、藍膠囊掛 Xeon

        new("everest",
            @"(?<![a-z\d])everest(?![a-z\d])",      // 內部標記常以底線相連（EVEREST_SS），故不用 \b（底線也算字元邊界內）
            EverestSeries, "", "Everest", White,
            Black, ChipBlack,
            Frame: Rainbow),

        new("blackops",
            @"(?<![a-z\d])black[\s\-_]?ops(?![a-z\d])",   // blackops / black ops / black-ops / BLACK_OPS 皆可
            EverestSeries, "", "BlackOps", White,
            Black, ChipBlack,
            Frame: Rainbow),

        new("xeon-e5-2602v4",
            @"\be5[- ]?2602\s*v4\b",               // 零售 v4 由 E5-2603 起跳，此編號不存在，故不會誤判
            EverestSeries, "", "E5-2602", White,
            Black, ChipBlack,
            Frame: Rainbow, Tier2: "Xeon", Chip2: ChipBlue),   // 同 X5698：字串自稱 Xeon，故並掛藍膠囊

        // ── Intel Core CC150：不在零售路線圖上的半訂製處理器（Coffee Lake／LGA1151，8C/16T、
        //    固定 3.5GHz 無 Turbo、16MB L3 全留、95W、sSpec SRFBT），供雲端遊戲服務的伺服器整櫃使用。
        //    徽章走 Steam 深藍底 + 白色蒸汽閥標記，膠囊標「Steam 訂製版」。
        //    Text 留空 → 日後若把官方標誌放到 Assets\cpu\cc150.png（csproj 已含該 glob），
        //    圖檔會自動覆蓋這枚向量標記，不必改任何程式。
        //    註：公開報導多把 CC150 與 NVIDIA GeForce NOW 的伺服器連結（Tom's Hardware、wccftech 等），
        //    此處的 Steam 歸屬為專案定調；要改口只需動這一列的 Tier 與配色。
        new("cc150",
            @"\bcc150\b",
            "Steam 訂製版", "steam", "", White,
            SteamNavy, ChipSteam),

        // ── AMD Ryzen X3D（3D V-Cache 堆疊快取）：與撕裂者／一般 Ryzen 同款黑底紅圈，
        //    但右下角加「3D」小標、膠囊改標「3D V-Cache」。須排在 threadripper／ryzen 之前才攔得到。
        //    比對只看 "x3d" 結尾：5600X3D～9950X3D 全系列皆含此字，零售他家型號不會出現。
        //    註：EPYC 的 Milan-X／Genoa-X（7773X、9684X）雖同為 3D V-Cache，名稱字串裡沒有 "3D"，
        //        單憑名稱無法分辨，故不入此列（維持 EPYC 徽章）。
        new("ryzen-x3d",
            @"x3d\b",
            "3D V-Cache", "", "Ryzen", White,
            TrBlack, ChipRed,
            Ring: TrRing, Corner: "3D"),

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

        // ── AMD Athlon 64 FX：AMD 在 K8 時代的至尊，對位 Intel 的 Extreme Edition（FX-51～FX-62）。
        //    黑底紅膠囊 + 中心「FX」。以「字串同時有 athlon 與 fx」比對，故不會誤收後來的推土機 FX 系列。
        new("athlon-fx",
            @"athlon.*\bfx",
            "Athlon 64 FX（AMD 至尊）", "", "FX", White,
            TrBlack, ChipRed),

        // ── AMD FX-9590／FX-9370：史上第一顆標稱 5.0GHz 的零售處理器（9590），TDP 220W，
        //    當年隨機附水冷、限量出貨。中心「FX」+ 紅膠囊標明它的身分。
        //    韌體字串常寫成 "AMD FX(tm)-9590"，故 fx 與型號之間允許幾個非數字字元。
        new("fx-9590",
            @"\bfx[^0-9]{0,6}(?:9590|9370)\b",
            "FX 5GHz 限量", "", "FX", White,
            TrBlack, ChipRed),

        // ── AMD EPYC：AMD 的伺服器線，與 Intel Xeon 對位——同樣是青藍底 + 處理器字形 + 左上橫書「EPYC」。
        //    一條規則吃整條線（7001～9005 各世代），世代差異留給型號文字本身表達。
        new("epyc",
            @"\bepyc\b",
            "EPYC", "cpu", "", White,
            EpycTeal, ChipTeal,
            Side: "EPYC"),

        // ── AMD Opteron：EPYC 的前身（K8～推土機世代的伺服器線），沿用當年包裝的綠色。
        //    「Opteron」比 Xeon／EPYC 長，放進左上橫書會擠，故不畫側標，改由膠囊標示。
        new("opteron",
            @"\bopteron\b",
            "Opteron", "cpu", "", White,
            OpterGrn, ChipGreen),

        // ── Pentium Gold 奔騰黃金版：比至尊金亮一號的金底 + 處理器字形 + 右下「P」。
        new("pentium-gold",
            @"pentium(?:\(r\))?\s*gold",
            "Pentium 黃金版", "cpu", "", DarkInk,
            PentGold, ChipGold,
            Corner: "P", MarkInk: DarkInk),

        // ── Core i9「KS」特別版（Special Edition）：整代最好的體質特挑出來、限量出貨的全核衝頻版。
        //    9900KS（首顆全核 5.0GHz）、13900KS（6.0GHz）、14900KS（6.2GHz）。金底 + 中心「KS」+ 紅膠囊。
        //    型號結尾是 ks 而非 xe／x，故不會被下方至尊與 Core X 的規則攔走。
        new("i9-ks",
            @"\bi9[- ]?(?:9900|13900|14900)ks\b",
            "特別版 Special Edition", "", "KS", DarkInk,
            Gold, ChipRed),

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

        // ── Haswell-E／Broadwell-E 至尊（i7-5960X / 6950X）：藍底 + 交叉 ✕ + 紅膠囊。
        //    這兩代 Intel 官方仍掛 Extreme Edition 名號，但徽記已換成藍色系（CPU-Z 亦以藍徽記呈現），
        //    故自經典黑底至尊分家、另立一色；膠囊仍用至尊紅，血脈關係看得出來。
        //    prefix 59 / 69 不在上方 Core X 白名單內，故不會被銀底規則先行攔走。
        new("i7-extreme-blue",
            @"\b(?:5960|6950)x\b",
            "至尊版 Extreme Edition", "x", "", White,
            ExtBlue, ChipRed),

        // ── 經典至尊 Extreme Edition：黑底 + 交叉 ✕ + 紅膠囊。整條至尊血脈統一歸此一類：
        //    Pentium 4 EE（Gallatin 3.4 / 3.46、Prescott 2M 3.73）、Pentium EE 840 / 955 / 965、
        //    Core 2 Extreme（X6800、QX6700/6850/9650/9770/9775、行動 QX9300 等）、
        //    i7-965 / 975（Bloomfield）、i7-980X / 990X（Gulftown）、i7-3960X / 3970X / 4960X。
        //    （Haswell-E 之後的 5960X / 6950X 徽記轉藍，見上方 i7-extreme-blue。）
        //    比對分三路，因為韌體字串的至尊線索並不一致：
        //      ① 字串自稱 "Extreme"（Core 2 Extreme、多數 P4 EE / Pentium EE 皆屬此路）；
        //      ② 至尊獨佔的型號編號（QX 四位、X6800、Pentium 955/965、i7 965/975/980X/990X…）；
        //      ③ 至尊獨佔的標稱頻率 3.46 / 3.73 GHz——奔騰世代非至尊只有 3.4 / 3.6 / 3.8 GHz。
        //    980X 舊機常報為 "Core(TM) i7 CPU X 980"（X 與數字分離），故該路比對放寬。
        //    註：3.4 GHz 的初代 P4 EE 與 Pentium 4 550 / 3.4E 名稱完全相同，僅憑名稱無法分辨，
        //        故不以 3.4 GHz 入列（寧可漏判，不可把普通 P4 誤掛至尊）。
        new("i7-extreme",
            @"\bextreme\b"                                                     // ① 自稱至尊
            + @"|\b(?:x6800|qx\d{4})\b"                                        // ② Core 2 Extreme 專屬編號
            + @"|pentium.*\b(?:955|965)\b"                                     // ② Pentium EE 955 / 965
            + @"|\bi7\b.*\b(?:965|975)\b"                                      // ② Bloomfield 至尊
            + @"|\b(?:980|990)x\b|\bi7\b.*\bx\s?9[89]0\b"                      // ② Gulftown 至尊（含分離寫法）
            + @"|\b(?:3960|3970|4960)x\b"                                      // ② Sandy·Ivy-E 至尊
            + @"|pentium.*\b3\.(?:46|73)\s*ghz",                               // ③ 至尊獨佔頻率
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

        // ── Xeon W-3175X：28 核 LGA3647 的「消費級」旗艦，只搭一款主機板、限量出貨，
        //    是 Xeon W 裡唯一解倍頻的一顆。沿用 Xeon W 版面，另加銀邊表示限量。須排在通用 xeon-w 之前。
        new("xeon-w3175x",
            @"\bw[- ]?3175x\b",
            "Xeon W · 限量", "cpu", "", White,
            IntelBlue, ChipBlue,
            Frame: FrameSilver, Side: "Xeon", TopMark: "W"),

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
        // ── OEM 專供 Xeon E5：不在 Intel 官方型號表（ARK）上、只出給特定客戶的整櫃機，
        //    後來大量流入二手市場：E5-2696 v2/v3/v4、E5-2686 v3/v4 與 E5-2666 v3（AWS 專用）、E5-2699A v4。
        //    沿用 Xeon E5 版面，另加銀邊表示「查不到官方規格表」這件事。須排在通用 xeon-e5 之前。
        new("xeon-oem",
            @"\be5[- ]?(?:2696|2686|2666|2699a)\s*v[234]\b",
            "Xeon E5 · OEM 專供", "cpu", "", White,
            IntelBlue, ChipBlue,
            Frame: FrameSilver, Side: "Xeon", TopMark: "E5"),

        // E5-1680 v2（黑色工作站頂規）：Ivy Bridge-EP／LGA2011 單路 8 核 3.0／3.9GHz、25MB L3、倍頻未鎖，
        //    2013 年 Mac Pro（黑色圓柱）與 HP Z420／Z620（黑塔）的頂級單路選項，也是那一代最受追捧的高頻八核。
        //    沿用 Xeon E5 的整套版面（處理器字形＋左上 Xeon＋右上 E5），只把底色與膠囊換成黑色，對應那批黑色工作站。
        //    須排在通用 xeon-e5 之前才攔得到；黑底的另一族是 Everest 系列，兩者以炫彩邊框區分（本列不畫邊框）。
        new("xeon-e5-1680v2",
            @"\be5[- ]?1680\s*v2\b",
            "Xeon E5", "cpu", "", White,
            Black, ChipBlack,
            Side: "Xeon", TopMark: "E5"),
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

        // ── Xeon Phi（Knights Landing／Knights Corner）：Larrabee 的遺產，最多 72 核、可直接開機當主處理器
        //    的眾核 x86，2020 年整條線收攤。深海藍 + 右上「Φ」，與一般 Xeon 分家。須排在通用 Xeon 之前。
        new("xeon-phi",
            @"xeon\s*phi",
            "Xeon Phi 眾核", "cpu", "", White,
            ExtBlue, ChipBlue,
            Side: "Xeon", TopMark: "Φ"),

        // ── Itanium 安騰（IA-64）：與 x86 完全不同的指令集，HP-UX／大型主機的最後據點，
        //    2021 年 Kittson 出貨結束後整個架構退場。銀底 + 深灰字形 + 右上「64」，刻意不與 Xeon 同色。
        new("itanium",
            @"\bitanium\b",
            "Itanium 安騰（IA-64）", "cpu", "", SilverInk,
            Silver, ChipSilver,
            TopMark: "64", MarkInk: SilverInk),

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
        // ── 雙芯顯示卡：一張卡兩顆 GPU 的那個年代（GTX 295／590／690、TITAN Z、HD 5970／6990／7990、
        //    R9 295X2、Radeon Pro Duo）。黑底 + 右下「×2」。須排在最前：TITAN Z 與 Radeon Pro Duo
        //    的名稱同時命中下方的 Titan／Radeon Pro 規則，雙芯這個身分更專一。
        new("dual-gpu",
            @"\bgtx\s*(?:295|590|690)\b|\btitan\s*z\b|\bhd\s*(?:5970|6990|7990)\b"
            + @"|\br9\s*295x2\b|\bpro\s*duo\b",
            "雙芯顯示卡", "gpu", "", White,
            GpuBlack, ChipSilver,
            Corner: "×2"),

        // ── 中國特供版（D 卡）：為出口管制另行推出的降規版本，型號尾巴多一個 D。
        new("china-d",
            @"\brtx\s*(?:4080|4090|5080|5090)\s*d\b",
            "中國特供版", "gpu", "", White,
            GpuBlack, ChipRed,
            Corner: "D"),

        // ── NVIDIA CMP 挖礦專用卡：砍掉顯示輸出、只留運算的礦潮產物（30HX～90HX）。
        new("nv-cmp",
            @"\bcmp\s*\d{2,3}hx\b",
            "CMP 挖礦卡", "gpu", "", White,
            GpuBlack, ChipSilver),
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

        // ── NVIDIA 資料中心加速器（Tesla 品牌退場後的後繼）：同一條血脈故沿用 NVIDIA 綠，右下改標「HPC」。
        //    以明確型號清單比對而非通則，避免把消費卡（RTX 4090 之類）誤判成加速卡。
        //    排在 tesla 之後：字串仍自稱 Tesla 的（Tesla V100／P100…）先取 Tesla 徽章，那是它們當年的正式身分。
        new("nv-datacenter",
            @"\b(?:k80|m40|m60|p40|p100|v100|a100|a800|h100|h200|h800|b100|b200|gb200|gb300)\b",
            "資料中心加速器", "gpu", "", White,
            NvGreen, ChipGreen,
            Corner: "HPC"),

        // ── NVIDIA Quadro 專業繪圖卡：工作站黑底 + 右下「Pro」+ 綠膠囊，與消費卡分家。
        new("nv-quadro",
            @"\bquadro\b",
            "Quadro 專業卡", "gpu", "", White,
            GpuBlack, ChipGreen,
            Corner: "Pro"),

        // ── NVIDIA RTX 專業卡（Quadro 更名後）：RTX A 系列、RTX xxxx Ada、RTX PRO 皆屬此列。
        //    以「A + 數字」「Ada」「PRO」三種線索比對，故不會把 RTX 4090 之類的消費卡吃進來。
        new("nv-rtx-pro",
            @"\brtx\s*pro\b|\brtx\s*a\d{3,4}\b|\brtx\s*\d{4,5}\s*ada\b",
            "RTX 專業卡", "gpu", "", White,
            GpuBlack, ChipGreen,
            Corner: "Pro"),

        // ── AMD Instinct 運算卡（MI 系列）：AMD 紅 + 紅膠囊，對位 NVIDIA 的資料中心線。
        new("amd-instinct",
            @"\binstinct\b|\bmi\d{2,3}x?\b",
            "Instinct 運算卡", "gpu", "", White,
            AmdRed, ChipRed),

        // ── AMD Radeon Pro／FirePro 專業繪圖卡：沿用這條線的藍色識別，右下「Pro」。
        new("amd-radeon-pro",
            @"radeon\s*pro\b|\bfirepro\b",
            "Radeon Pro 專業卡", "gpu", "", White,
            ProBlue, ChipBlue,
            Corner: "Pro"),
    };

    /// <summary>
    /// 特殊儲存裝置對照表。比對以裝置型號（已轉小寫）為輸入，由上而下取第一個命中者。
    /// 這張表刻意只收「機制上獨一無二」的裝置，不收單純跑得快的旗艦 SSD——
    /// 否則對照表會退化成產品目錄，徽章也就不再代表任何東西。
    /// </summary>
    private static readonly BadgeIcon[] Disk =
    {
        // ── Intel Optane（3D XPoint）：不是 NAND 而是相變記憶體，低佇列深度的延遲至今沒有對手，
        //    2022 年整條線停產，所以只會愈來愈罕見。韌體回報的多是料號而非行銷名：
        //      900P／P4800X（AIC）＝SSDPED*、905P／P4800X（U.2）＝SSDPE21*、
        //      P5800X＝SSDPF21*、Optane Memory＝MEMPEK*
        //    同一家的 NAND 資料中心碟是 SSDPE2K* / SSDPE2M* / SSDSC*，不在上列前綴內，不會誤判。
        new("optane",
            @"\boptane\b|\bssdp(?:ed|e21|f21)|\bmempek",
            "Optane 3D XPoint", "disk", "", White,
            OptaneBlu, ChipBlue),
    };

    /// <summary>
    /// 特殊主機板對照表。比對以主機板型號（已轉小寫）為輸入。
    /// 收的是各家「超頻名門」系列——這些字本身就是識別，與用料等級直接掛鉤，
    /// 不是行銷後綴（Gaming／Pro／Plus 之類一概不收，那些只是價位分層）。
    /// </summary>
    private static readonly BadgeIcon[] Board =
    {
        // ASUS ROG APEX：兩根記憶體槽換極限記憶體超頻，是 ROG 最上面那一階。
        new("board-apex",
            @"\bapex\b",
            "APEX 超頻板", "board", "", White,
            Black, ChipRed),
        // EVGA DARK / DARK KINGPIN：EVGA 的極限超頻板。
        //    排除 ASUS 的「Crosshair … Dark Hero」——那是別家的板子，只是名字裡也有 dark。
        new("board-dark",
            @"\bdark\b(?!\s*hero)",
            "DARK 超頻板", "board", "", White,
            Black, ChipSilver),
        // MSI GODLIKE：MEG／MPG 的頂規。
        new("board-godlike",
            @"\bgodlike\b",
            "GODLIKE 旗艦板", "board", "", White,
            Black, ChipGold),
        // GIGABYTE AORUS TACHYON：技嘉的超頻專用板。
        new("board-tachyon",
            @"\btachyon\b",
            "TACHYON 超頻板", "board", "", White,
            Black, ChipSilver),
    };

    /// <summary>由型號字串解析出專屬圖示；未命中或無對照表的類別回傳 <c>null</c>（呼叫端退回通用字形）。</summary>
    public static BadgeIcon? Resolve(BadgeKind kind, string? model) => kind switch
    {
        BadgeKind.Cpu => Match(Cpu, model),
        BadgeKind.Gpu => Match(Gpu, model),
        BadgeKind.Disk => Match(Disk, model),
        BadgeKind.Board => Match(Board, model),
        _ => null,
    };

    /// <summary>對應資產子資料夾（官方 logo 覆蓋層）："cpu" / "gpu" / "disk" / "board"；其餘類別無專屬圖示。</summary>
    public static string? AssetFolder(BadgeKind kind) => kind switch
    {
        BadgeKind.Cpu => "cpu",
        BadgeKind.Gpu => "gpu",
        BadgeKind.Disk => "disk",
        BadgeKind.Board => "board",
        _ => null,
    };

    /// <summary>
    /// 對照表本身，供測試檢查整表不變式（Id 唯一、規則可編譯、字形代號有效…）。
    /// 顯示端請用 <see cref="Resolve"/>；沒有對照表的類別回傳空清單。
    /// </summary>
    public static IReadOnlyList<BadgeIcon> Table(BadgeKind kind) => kind switch
    {
        BadgeKind.Cpu => Cpu,
        BadgeKind.Gpu => Gpu,
        BadgeKind.Disk => Disk,
        BadgeKind.Board => Board,
        _ => Array.Empty<BadgeIcon>(),
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
