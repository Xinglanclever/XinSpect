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
}
