using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 特殊型號徽章對照表：至尊版（Extreme Edition）血脈的命中與誤判防線。
/// </summary>
/// <remarks>
/// 這組測試存在的理由是「徽章只能靠名稱字串判斷」這個先天限制。至尊版橫跨
/// Pentium 4 EE → Pentium EE → Core 2 Extreme → Bloomfield / Gulftown → Sandy·Ivy-E，
/// 但韌體回報的字串線索三種都有（自稱 Extreme、專屬編號、專屬標稱頻率），
/// 一條放寬的規則很容易順手把普通型號也掛上至尊徽章——那是無法從畫面上察覺的錯誤，
/// 因為徽章看起來永遠都「很合理」。故命中清單與誤判清單必須成對釘死。
/// </remarks>
public class DeviceIconsTests
{
    private static string? Id(string name) => DeviceIcons.Resolve(BadgeKind.Cpu, name)?.Id;

    // ── 至尊血脈：全員統一歸入同一枚經典至尊徽章 ──────────────────────────

    [Theory]
    // ① 字串自稱 Extreme
    [InlineData("Intel(R) Pentium(R) 4 Extreme Edition CPU 3.40GHz")]
    [InlineData("Intel(R) Pentium(R) D Extreme Edition CPU 840")]
    [InlineData("Intel(R) Pentium(R) Extreme Edition CPU 955")]
    [InlineData("Intel(R) Core(TM)2 Extreme CPU X6800 @ 2.93GHz")]
    [InlineData("Intel(R) Core(TM)2 Extreme CPU Q6850 @ 3.00GHz")]
    [InlineData("Intel(R) Core(TM)2 Extreme CPU QX9775 @ 3.20GHz")]
    // ② 至尊獨佔編號（字串未自稱 Extreme 的機器）
    [InlineData("Intel(R) Core(TM)2 CPU QX6700 @ 2.66GHz")]
    [InlineData("Intel(R) Core(TM)2 CPU QX9650 @ 3.00GHz")]
    [InlineData("Intel(R) Pentium(R) D CPU 965 @ 3.73GHz")]
    [InlineData("Intel(R) Core(TM) i7 CPU 965 @ 3.20GHz")]
    [InlineData("Intel(R) Core(TM) i7 CPU 975 @ 3.33GHz")]
    [InlineData("Intel(R) Core(TM) i7 CPU X 980 @ 3.33GHz")]
    [InlineData("Intel(R) Core(TM) i7-990X CPU @ 3.46GHz")]
    [InlineData("Intel(R) Core(TM) i7-3960X CPU @ 3.30GHz")]
    [InlineData("Intel(R) Core(TM) i7-4960X CPU @ 3.60GHz")]
    // ③ 至尊獨佔標稱頻率（奔騰世代非至尊只有 3.4 / 3.6 / 3.8 GHz）
    [InlineData("Intel(R) Pentium(R) 4 CPU 3.46GHz")]
    [InlineData("Intel(R) Pentium(R) 4 CPU 3.73GHz")]
    public void 至尊血脈_統一命中經典至尊徽章(string name)
    {
        Assert.Equal("i7-extreme", Id(name));
    }

    [Fact]
    public void 至尊徽記文字_整條血脈共用同一枚膠囊()
    {
        var p4 = DeviceIcons.Resolve(BadgeKind.Cpu, "Intel(R) Pentium(R) 4 CPU 3.73GHz");
        var c2 = DeviceIcons.Resolve(BadgeKind.Cpu, "Intel(R) Core(TM)2 Extreme CPU X6800");
        var i7 = DeviceIcons.Resolve(BadgeKind.Cpu, "Intel(R) Core(TM) i7 CPU 965 @ 3.20GHz");
        Assert.Equal("至尊版 Extreme Edition", p4!.Tier);
        Assert.Equal(p4.Tier, c2!.Tier);
        Assert.Equal(p4.Tier, i7!.Tier);
        Assert.Equal(p4.Id, i7.Id);   // 同一 Id ＝ 同一張官方圖覆蓋層、同一組配色
    }

    // ── Haswell-E／Broadwell-E 至尊：同一血脈，但徽記轉藍，須與黑底經典分家 ──

    [Theory]
    [InlineData("Intel(R) Core(TM) i7-5960X CPU @ 3.00GHz")]
    [InlineData("Intel(R) Core(TM) i7-6950X CPU @ 3.00GHz")]
    public void HaswellE與BroadwellE至尊_走藍底而非黑底(string name)
    {
        var icon = DeviceIcons.Resolve(BadgeKind.Cpu, name);
        Assert.Equal("i7-extreme-blue", icon?.Id);
        Assert.Equal("至尊版 Extreme Edition", icon!.Tier);   // 徽記文字仍屬至尊血脈
        Assert.Equal("x", icon.Glyph);                         // 仍是交叉 ✕
    }

    [Theory]
    [InlineData("Intel(R) Core(TM) i7-5820K CPU @ 3.30GHz")]   // 同代非至尊，不得沾光
    [InlineData("Intel(R) Core(TM) i7-5930K CPU @ 3.50GHz")]
    [InlineData("Intel(R) Core(TM) i7-6800K CPU @ 3.40GHz")]
    [InlineData("Intel(R) Core(TM) i7-6900K CPU @ 3.20GHz")]
    public void 同代非至尊K系_不得掛至尊(string name)
    {
        Assert.Null(Id(name));
    }

    // ── 誤判防線：只差一個字母／一段頻率的普通型號不得掛上至尊 ────────────

    [Theory]
    [InlineData("Intel(R) Core(TM)2 Quad CPU Q9650 @ 3.00GHz")]   // Q9650 不是 QX9650
    [InlineData("Intel(R) Core(TM)2 Duo CPU E6800 @ 3.33GHz")]    // E6800 不是 X6800
    [InlineData("Intel(R) Pentium(R) 4 CPU 3.40GHz")]             // 與初代 P4 EE 同名，只能放生
    [InlineData("Intel(R) Pentium(R) D CPU 3.20GHz")]             // 與 Pentium EE 840 同名，只能放生
    [InlineData("Intel(R) Pentium(R) D CPU 960 @ 3.60GHz")]
    [InlineData("Intel(R) Core(TM) i7-9750H CPU @ 2.60GHz")]
    [InlineData("Intel(R) Core(TM) i7-1065G7 CPU @ 1.30GHz")]
    [InlineData("Intel(R) Core(TM) i7-8565U CPU @ 1.80GHz")]
    [InlineData("Intel(R) Core(TM) i5-9600K CPU @ 3.70GHz")]
    public void 普通型號_不得誤掛至尊(string name)
    {
        Assert.Null(Id(name));
    }

    [Theory]
    [InlineData("Intel(R) Core(TM) i9-10980XE CPU @ 3.00GHz", "i9-extreme")]   // 現代至尊走金底
    [InlineData("Intel(R) Core(TM) i9-9990XE CPU @ 4.00GHz", "i9-9990xe")]     // 隱藏款走炫彩
    [InlineData("Intel(R) Core(TM) i9-9900X CPU @ 3.50GHz", "core-x")]         // Core X 非至尊
    [InlineData("Intel(R) Core(TM) i7-7740X CPU @ 4.30GHz", "core-x")]
    [InlineData("Intel(R) Pentium(R) Gold G7400 @ 3.70GHz", "pentium-gold")]
    [InlineData("Genuine Intel(R) CPU 0000 @ 2.00GHz", "es-sample")]
    public void 他家徽章_不被放寬後的至尊規則搶走(string name, string expected)
    {
        Assert.Equal(expected, Id(name));
    }

    // ── 特注／限量血脈的其餘成員，以及顯示卡的三個特殊族群 ──────────────────────

    [Theory]
    [InlineData("Intel(R) Core(TM) i9-9900KS CPU @ 4.00GHz", "i9-ks")]
    [InlineData("Intel(R) Core(TM) i9-14900KS CPU @ 3.20GHz", "i9-ks")]
    [InlineData("Intel(R) Core(TM) i9-14900K CPU @ 3.20GHz", null)]          // 少一個 S 就是普通零售旗艦
    [InlineData("Intel(R) Xeon(R) W-3175X CPU @ 3.10GHz", "xeon-w3175x")]
    [InlineData("Intel(R) Xeon(R) W-2295 CPU @ 3.00GHz", "xeon-w")]
    [InlineData("Intel(R) Xeon(R) CPU E5-2696 v4 @ 2.20GHz", "xeon-oem")]
    [InlineData("Intel(R) Xeon(R) CPU E5-2666 v3 @ 2.90GHz", "xeon-oem")]
    [InlineData("Intel(R) Xeon(R) CPU E5-2690 v4 @ 2.60GHz", "xeon-e5")]     // 查得到官方規格表的照舊
    [InlineData("Intel(R) Xeon Phi(TM) CPU 7250 @ 1.40GHz", "xeon-phi")]
    [InlineData("Intel(R) Itanium(R) Processor 9750 @ 2.53GHz", "itanium")]
    [InlineData("AMD Athlon(tm) 64 FX-60 Processor", "athlon-fx")]
    [InlineData("AMD FX(tm)-9590 Eight-Core Processor", "fx-9590")]
    [InlineData("AMD FX(tm)-8350 Eight-Core Processor", null)]               // 一般推土機 FX 不算限量
    public void 特注與限量血脈_各自命中自己的徽章(string name, string? expected)
    {
        Assert.Equal(expected, Id(name));
    }

    [Theory]
    [InlineData("NVIDIA GeForce GTX 690", "dual-gpu")]
    [InlineData("NVIDIA GeForce GTX TITAN Z", "dual-gpu")]                   // 雙芯身分比 Titan 更專一
    [InlineData("AMD Radeon Pro Duo", "dual-gpu")]                           // 也比 Radeon Pro 更專一
    [InlineData("AMD Radeon R9 295X2", "dual-gpu")]
    [InlineData("NVIDIA GeForce RTX 4090 D", "china-d")]
    [InlineData("NVIDIA GeForce RTX 4090", null)]                            // 非特供版不掛徽章
    [InlineData("NVIDIA CMP 90HX", "nv-cmp")]
    public void 雙芯卡與特供版與礦卡(string model, string? expected)
    {
        Assert.Equal(expected, DeviceIcons.Resolve(BadgeKind.Gpu, model)?.Id);
    }

    // ── 補洞：整條產品線原本沒有專屬徽章的（X3D / EPYC / Opteron / 專業卡 / 運算卡）─────

    [Theory]
    [InlineData("AMD Ryzen 7 5800X3D 8-Core Processor", "ryzen-x3d")]
    [InlineData("AMD Ryzen 7 9800X3D 8-Core Processor", "ryzen-x3d")]
    [InlineData("AMD Ryzen 9 7950X3D 16-Core Processor", "ryzen-x3d")]
    [InlineData("AMD Ryzen 9 5950X 16-Core Processor", "ryzen")]                    // 非 X3D 仍走一般 Ryzen
    [InlineData("AMD Ryzen Threadripper 3990X 64-Core Processor", "threadripper")]
    [InlineData("AMD EPYC 9754 128-Core Processor", "epyc")]
    [InlineData("AMD EPYC 7773X 64-Core Processor", "epyc")]                        // Milan-X 字串沒有 3D，維持 EPYC
    [InlineData("AMD Opteron(tm) Processor 6276", "opteron")]
    public void AMD各條產品線_各自命中自己的徽章(string name, string expected)
    {
        Assert.Equal(expected, Id(name));
    }

    [Theory]
    [InlineData("NVIDIA Quadro P5000", "nv-quadro")]
    [InlineData("Quadro RTX 8000", "nv-quadro")]                      // 舊命名兩者皆有，Quadro 先命中
    [InlineData("NVIDIA RTX A6000", "nv-rtx-pro")]
    [InlineData("NVIDIA RTX 6000 Ada Generation", "nv-rtx-pro")]
    [InlineData("NVIDIA RTX PRO 6000 Blackwell", "nv-rtx-pro")]
    [InlineData("NVIDIA H100 80GB HBM3", "nv-datacenter")]
    [InlineData("NVIDIA A100-SXM4-80GB", "nv-datacenter")]
    [InlineData("NVIDIA Tesla V100-SXM2-16GB", "tesla")]              // 自稱 Tesla 者維持當年的正式身分
    [InlineData("AMD Instinct MI300X", "amd-instinct")]
    [InlineData("AMD Radeon Instinct MI50", "amd-instinct")]
    [InlineData("AMD Radeon Pro W7900", "amd-radeon-pro")]
    [InlineData("ATI FirePro W9100", "amd-radeon-pro")]
    public void 專業卡與運算卡_各自命中自己的徽章(string model, string expected)
    {
        Assert.Equal(expected, DeviceIcons.Resolve(BadgeKind.Gpu, model)?.Id);
    }

    [Theory]
    [InlineData("NVIDIA GeForce RTX 4090")]
    [InlineData("NVIDIA GeForce RTX 5080")]
    [InlineData("NVIDIA GeForce GTX 1080 Ti")]
    [InlineData("AMD Radeon RX 7900 XTX")]
    [InlineData("Intel(R) Arc(TM) A770 Graphics")]
    public void 消費顯示卡_不得被專業卡或運算卡規則吃掉(string model)
    {
        Assert.Null(DeviceIcons.Resolve(BadgeKind.Gpu, model));
    }

    // ── Everest 珠穆朗瑪峰系列（X5698 / JKT Everest / BlackOps / E5-2602 v4）───────
    //    Intel 非路線圖的極限 bin：韌體字串掛專案代號或非零售編號，故膠囊標系列、中心字標機種。
    //    全系列共用一枚膠囊，但中心字不可互相冒名。這些片子多以工程樣品形式流出、
    //    名稱常帶 "Engineering Sample"，X5698 與 E5-2602 v4 的字串又含 "Xeon"，
    //    故一併釘死「不可被通用 Xeon／ES 徽章搶走」。

    [Theory]
    [InlineData("Intel(R) Xeon(R) CPU X5698 @ 4.40GHz", "xeon-x5698")]
    [InlineData("Intel Everest", "everest")]
    [InlineData("Intel(R) Everest CPU @ 4.40GHz", "everest")]
    [InlineData("Intel JKT EVEREST SS 4.4GHZ INTERNAL USE ONLY", "everest")]
    [InlineData("INTEL BLACKOPS 6 CORE 4.60GHz", "blackops")]
    [InlineData("Intel(R) BlackOps CPU @ 4.60GHz", "blackops")]
    [InlineData("Intel Black Ops", "blackops")]
    [InlineData("Intel Black-Ops 4.60GHz", "blackops")]
    [InlineData("Intel(R) Xeon(R) CPU E5-2602 v4", "xeon-e5-2602v4")]
    [InlineData("Intel(R) Xeon(R) CPU E5 2602 v4 @ 5.10GHz", "xeon-e5-2602v4")]
    public void Everest系列_各自命中自己的機種(string name, string expected)
    {
        Assert.Equal(expected, Id(name));
    }

    [Theory]
    [InlineData("Intel Everest 6 Core Engineering Sample", "everest")]
    [InlineData("Intel BlackOps 6 Core 4.60GHz Engineering Sample", "blackops")]
    [InlineData("Intel(R) Xeon(R) CPU X5698 @ 4.40GHz Engineering Sample", "xeon-x5698")]
    public void Everest系列_不被通用Xeon或工程樣品徽章搶走(string name, string expected)
    {
        Assert.Equal(expected, Id(name));
    }

    [Fact]
    public void Everest系列_全員共用系列膠囊_中心字各標自己機種()
    {
        var x5698    = DeviceIcons.Resolve(BadgeKind.Cpu, "Intel(R) Xeon(R) CPU X5698 @ 4.40GHz")!;
        var everest  = DeviceIcons.Resolve(BadgeKind.Cpu, "Intel JKT Everest")!;
        var blackops = DeviceIcons.Resolve(BadgeKind.Cpu, "Intel BlackOps")!;
        var bdw      = DeviceIcons.Resolve(BadgeKind.Cpu, "Intel(R) Xeon(R) CPU E5-2602 v4")!;

        // 膠囊＝系列名（改名只需改 DeviceIcons.EverestSeries 一處）
        Assert.Equal("Everest 珠穆朗瑪峰系列", x5698.Tier);
        Assert.Equal(x5698.Tier, everest.Tier);
        Assert.Equal(x5698.Tier, blackops.Tier);
        Assert.Equal(x5698.Tier, bdw.Tier);

        // 中心字＝機種，不可互相冒名
        Assert.Equal("X5698", x5698.Text);
        Assert.Equal("Everest", everest.Text);
        Assert.Equal("BlackOps", blackops.Text);
        Assert.Equal("E5-2602", bdw.Text);

        // 外觀同系列共用：黑底、黑膠囊、炫彩邊框
        foreach (var m in new[] { everest, blackops, bdw })
        {
            Assert.Equal(x5698.Emblem, m.Emblem);
            Assert.Equal(x5698.Chip, m.Chip);
            Assert.Equal(x5698.Frame, m.Frame);
            Assert.NotEqual(x5698.Id, m.Id);
        }
    }

    // 同一套「極限 bin」思路的早期代表，但兩者都是正式零售 SKU：
    // 徽章應顯示自己的正式身分（至尊版 / Xeon），不得被 Everest 系列吸走。
    [Theory]
    [InlineData("Intel(R) Core(TM)2 Extreme CPU QX9775 @ 3.20GHz", "i7-extreme")]
    [InlineData("Intel(R) Xeon(R) CPU X5492 @ 3.40GHz", "xeon")]
    public void 零售極限bin_維持自己的正式徽章(string name, string expected)
    {
        Assert.Equal(expected, Id(name));
    }

    // ── 雙重身分：一顆片子同時屬於兩種類別時，兩枚膠囊都要在 ───────────────────

    [Fact]
    public void X5698與E5_2602v4_黑膠囊掛系列_藍膠囊掛Xeon()
    {
        var x5698 = DeviceIcons.Resolve(BadgeKind.Cpu, "Intel(R) Xeon(R) CPU X5698 @ 4.40GHz")!;
        var bdw   = DeviceIcons.Resolve(BadgeKind.Cpu, "Intel(R) Xeon(R) CPU E5-2602 v4")!;
        var xeon  = DeviceIcons.Resolve(BadgeKind.Cpu, "Intel(R) Xeon(R) CPU E5-2680 v3 @ 2.50GHz")!;

        Assert.Equal("Everest 珠穆朗瑪峰系列", x5698.Tier);
        Assert.Equal("Xeon", x5698.Tier2);
        Assert.Equal(xeon.Chip, x5698.Chip2);   // 第二枚膠囊＝與一般 Xeon 同一組藍，看得出血脈
        Assert.Equal("Xeon", bdw.Tier2);
        Assert.Equal(xeon.Chip, bdw.Chip2);
    }

    [Theory]
    [InlineData("Intel JKT Everest")]                            // 字串不自稱 Xeon
    [InlineData("Intel BlackOps")]
    [InlineData("Intel(R) Core(TM) i9-9990XE CPU @ 4.00GHz")]
    [InlineData("Intel(R) Core(TM) CC150 CPU @ 3.50GHz")]
    public void 單一身分_不畫第二枚膠囊(string name)
    {
        var icon = DeviceIcons.Resolve(BadgeKind.Cpu, name)!;
        Assert.Equal("", icon.Tier2);
        Assert.Null(icon.Chip2);
    }

    // ── CC150：不在零售路線圖上的半訂製處理器，走 Steam 標記 ──────────────────

    [Theory]
    [InlineData("Intel(R) Core(TM) CC150 CPU @ 3.50GHz", "cc150")]
    [InlineData("Intel Core CC150", "cc150")]
    [InlineData("Genuine Intel(R) CPU CC150 @ 3.50GHz", "cc150")]   // 帶 Genuine 字樣也不落通用 ES 徽章
    public void CC150_命中Steam訂製徽章(string name, string expected)
    {
        Assert.Equal(expected, Id(name));
    }

    [Fact]
    public void CC150_以Steam向量標記呈現_並留白讓官方圖檔可覆蓋()
    {
        var cc150 = DeviceIcons.Resolve(BadgeKind.Cpu, "Intel(R) Core(TM) CC150 CPU @ 3.50GHz")!;
        Assert.Equal("Steam 訂製版", cc150.Tier);
        Assert.Equal("steam", cc150.Glyph);
        Assert.Equal("", cc150.Text);   // Text 留空才會去試 Assets\cpu\cc150.png；有字就永遠蓋不掉
    }

    // ── 內部標記的分隔符：底線相連的樣品字串也要歸入所屬系列 ──────────────────

    [Theory]
    [InlineData("INTEL_JKT_EVEREST_SS_4.4GHZ_INTERNAL_USE_ONLY", "everest")]
    [InlineData("INTEL_BLACK_OPS_6C_4.6GHZ", "blackops")]
    public void 底線相連的內部標記_仍歸入所屬系列(string name, string expected)
    {
        Assert.Equal(expected, Id(name));
    }

    // ── Xeon E5-1680 v2：黑色工作站頂規，走黑底但仍是 Xeon E5 身分 ─────────────

    [Theory]
    [InlineData("Intel(R) Xeon(R) CPU E5-1680 v2 @ 3.00GHz", "xeon-e5-1680v2")]
    [InlineData("Intel Xeon E5 1680 v2", "xeon-e5-1680v2")]
    [InlineData("Intel(R) Xeon(R) CPU E5-2680 v4 @ 2.40GHz", "xeon-e5")]   // 其餘 E5 仍走藍底通用規則
    [InlineData("Intel(R) Xeon(R) CPU E5-1680 v3 @ 3.20GHz", "xeon-e5")]   // 只認 v2：黑色工作站是那一代的事
    [InlineData("Intel(R) Xeon(R) CPU E5-1650 v2 @ 3.50GHz", "xeon-e5")]
    public void E5_1680v2_獨走黑色XeonE5徽章(string name, string expected)
    {
        Assert.Equal(expected, Id(name));
    }

    [Fact]
    public void E5_1680v2_沿用XeonE5版面_只換黑底()
    {
        var black   = DeviceIcons.Resolve(BadgeKind.Cpu, "Intel(R) Xeon(R) CPU E5-1680 v2 @ 3.00GHz")!;
        var blue    = DeviceIcons.Resolve(BadgeKind.Cpu, "Intel(R) Xeon(R) CPU E5-2680 v4 @ 2.40GHz")!;
        var everest = DeviceIcons.Resolve(BadgeKind.Cpu, "Intel JKT Everest")!;

        Assert.Equal(blue.Tier, black.Tier);          // 身分仍是 Xeon E5
        Assert.Equal(blue.Glyph, black.Glyph);        // 同一枚處理器字形
        Assert.Equal(blue.Side, black.Side);          // 左上仍是 Xeon
        Assert.Equal(blue.TopMark, black.TopMark);    // 右上仍標 E5
        Assert.NotEqual(blue.Emblem, black.Emblem);   // 但底色換黑
        Assert.Equal(everest.Emblem, black.Emblem);   // 與 Everest 系列同一組黑
        Assert.Null(black.Frame);                     // 沒有炫彩邊框——那是 Everest 系列的專屬記號
        Assert.NotNull(everest.Frame);
    }
}
