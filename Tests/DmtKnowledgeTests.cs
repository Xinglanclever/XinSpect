using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// DMT 顯示時序知識包（U10 第四圈批次二）的守門：唯一分母、逐欄抽樣、
/// est3 全數對到 DMT、est1 的非 DMT 遺產如實、摘要對帳。
/// </summary>
public class DmtKnowledgeTests
{
    private static HardwareFact Fact(List<HardwareFact> facts, string key) =>
        facts.Single(f => f.Key == key);

    private static List<HardwareFact> Collect() =>
        DmtFactsService.Collect(DateTimeOffset.UnixEpoch).ToList();

    [Fact]
    public void 知識包唯一分母_八十八DMT十七est一四十四est三()
    {
        Assert.Equal(88, DmtKnowledge.Modes.Count);
        Assert.Equal(17, DmtKnowledge.EstablishedTiming1.Count);
        Assert.Equal(44, DmtKnowledge.EstablishedTiming3.Count);
    }

    [Fact]
    public void 代碼零到五十八全唯一且無缺號()
    {
        var codes = DmtKnowledge.Modes.Select(m => m.Code).OrderBy(c => c).ToList();
        Assert.Equal(Enumerable.Range(1, 88), codes);
    }

    [Fact]
    public void 抽樣逐欄對照_0x04與0x57()
    {
        // 0x04＝640x480@60：DMT 表最經典的一條（VGA 標準時序）。
        var m = DmtKnowledge.FindByCode(0x04);
        Assert.NotNull(m);
        Assert.Equal("640x480", m!.Name);
        Assert.Equal("60", m.RefreshHz);
        Assert.Equal(25175, m.ClockKhz);
        Assert.Equal((640, 656, 752, 800), (m.HActive, m.HSyncStart, m.HSyncEnd, m.HTotal));
        Assert.Equal((480, 490, 492, 525), (m.VActive, m.VSyncStart, m.VSyncEnd, m.VTotal));
        Assert.Equal("N", m.HSync);
        Assert.Equal("N", m.VSync);
        Assert.False(m.Interlaced);

        // 0x57＝4096x2160@60 RB；0x58 才是 59.94——刷新率非整數、RB 變體的字串原樣抽樣。
        var rb = DmtKnowledge.FindByCode(0x57);
        Assert.NotNull(rb);
        Assert.Equal("60", rb!.RefreshHz);
        Assert.True(rb.ReducedBlanking);
        Assert.Equal(556744, rb.ClockKhz);
        var rr = DmtKnowledge.FindByCode(0x58);
        Assert.Equal("59.94", rr!.RefreshHz);
        Assert.Equal(556188, rr.ClockKhz);
    }

    [Fact]
    public void 唯一交錯式是0x0f()
    {
        var interlaced = DmtKnowledge.Modes.Where(m => m.Interlaced).ToList();
        Assert.Single(interlaced);
        Assert.Equal(0x0f, interlaced[0].Code);
        Assert.Equal("1024x768i", interlaced[0].Name);
        Assert.Equal("43", interlaced[0].RefreshHz);
    }

    [Fact]
    public void 幾何查找_全欄位都對才命中()
    {
        Assert.Equal(0x04, DmtKnowledge.FindByGeometry(640, 480, "60")?.Code);
        // 640x480@60 有 DMT 版；同幾何要求 RB 版就沒有——「全欄位都對才命中」。
        Assert.Null(DmtKnowledge.FindByGeometry(640, 480, "60", reducedBlanking: true));
        Assert.Equal(0x0f, DmtKnowledge.FindByGeometry(1024, 768, "43", interlaced: true)?.Code);
        Assert.Null(DmtKnowledge.FindByGeometry(999, 999, "60"));
    }

    [Fact]
    public void est3全數對到DMT()
    {
        foreach (var e in DmtKnowledge.EstablishedTiming3)
        {
            Assert.True(DmtKnowledge.ResolveEst3(e) is int,
                $"est3 bit{e.Bit}（{e.Width}x{e.Height}@{e.RefreshHz}）對不到 DMT——對照表與位圖表脫鉤了");
        }
    }

    [Fact]
    public void est1的遺產模式如實對不到DMT()
    {
        // 640x480@67（Apple Mac II）與 1024x768@43i 不在 DMT 表裡——ResolveEst1 回 null 是對照的結果。
        var apple = DmtKnowledge.EstablishedTiming1.Single(e => e.Width == 640 && e.Height == 480 && e.RefreshHz == "67");
        Assert.Null(DmtKnowledge.ResolveEst1(apple));
        var il = DmtKnowledge.EstablishedTiming1.Single(e => e.Interlaced);
        // 1024x768@43i 在 Linux 的 DMT 表裡（0x0f，唯一交錯式）——對得到，不是遺產。
        Assert.Equal(0x0f, DmtKnowledge.ResolveEst1(il));
        var vga88 = DmtKnowledge.EstablishedTiming1.Single(e => e.Width == 720 && e.Height == 400 && e.RefreshHz == "88");
        Assert.Null(DmtKnowledge.ResolveEst1(vga88));
        // 對得到的抽樣：800x600@60 → DMT 0x11？——不寫死碼號，只斷言「對得到」。
        var s60 = DmtKnowledge.EstablishedTiming1.Single(e => e.Width == 800 && e.Height == 600 && e.RefreshHz == "60");
        Assert.True(DmtKnowledge.ResolveEst1(s60) is int);
    }

    [Fact]
    public void 位元編號零起連續不重複()
    {
        Assert.Equal(Enumerable.Range(0, 17), DmtKnowledge.EstablishedTiming1.Select(e => e.Bit));
        Assert.Equal(Enumerable.Range(0, 44), DmtKnowledge.EstablishedTiming3.Select(e => e.Bit));
    }

    [Fact]
    public void 摘要數字與明細對帳()
    {
        var facts = Collect();
        var summary = Fact(facts, DmtFactsService.SummaryKey);
        Assert.True(summary.NumericValue == DmtKnowledge.Modes.Count,
            "summary 的數字必須是 DMT 表筆數（由 Count 算出，不得手寫常數）");
        Assert.Contains($"{DmtKnowledge.Modes.Count} 條 VESA DMT 模式", summary.Value);
        Assert.Contains($"{DmtKnowledge.EstablishedTiming1.Count} 條 established timing I", summary.Value);
        Assert.Contains($"{DmtKnowledge.EstablishedTiming3.Count} 條 established timing III", summary.Value);
    }

    [Fact]
    public void 模式表值包含全部八十八條()
    {
        var facts = Collect();
        var modes = Fact(facts, DmtFactsService.ModesKey);
        foreach (var m in DmtKnowledge.Modes)
            Assert.Contains($"0x{m.Code:X2}＝", modes.Value);
    }

    [Fact]
    public void 自校鍵的數字與實算一致()
    {
        var facts = Collect();
        var sc = Fact(facts, DmtFactsService.SelfCheckKey);
        int est3Resolved = DmtKnowledge.EstablishedTiming3.Count(e => DmtKnowledge.ResolveEst3(e) is int);
        Assert.True(sc.NumericValue == est3Resolved,
            "自校鍵的數字必須是 est3 對到的筆數");
        Assert.Contains($"est3 {est3Resolved}/{DmtKnowledge.EstablishedTiming3.Count} 條對到 DMT", sc.Value);
    }

    [Fact]
    public void HDMI與CTA分命名空間成文()
    {
        var facts = Collect();
        var hdmi = Fact(facts, DmtFactsService.HdmiVsCtaKey);
        Assert.Contains("兩個命名空間", hdmi.Value);
        Assert.Contains("HDMI_VIC", hdmi.Value);
        var boundary = Fact(facts, DmtFactsService.BoundaryKey);
        Assert.Contains("CVT-RB 生成式時序明文不做", boundary.Value);
    }

    [Fact]
    public void 八把鍵一次到齊且繁中無簡體()
    {
        var facts = Collect();
        Assert.Equal(8, facts.Count);
        var keys = facts.Select(f => f.Key).ToHashSet(StringComparer.Ordinal);
        Assert.Contains(DmtFactsService.SummaryKey, keys);
        Assert.Contains(DmtFactsService.ModesKey, keys);
        Assert.Contains(DmtFactsService.Est1Key, keys);
        Assert.Contains(DmtFactsService.Est3Key, keys);
        Assert.Contains(DmtFactsService.SelfCheckKey, keys);
        Assert.Contains(DmtFactsService.HdmiVsCtaKey, keys);
        Assert.Contains(DmtFactsService.VersionKey, keys);
        Assert.Contains(DmtFactsService.BoundaryKey, keys);

        var simplified = new[] { "处", "络", "检", "认", "应", "发", "这", "数", "时", "显" };
        foreach (var f in facts)
            foreach (var text in new[] { f.Value, f.Name, f.UnavailableReason ?? "" })
                foreach (var s in simplified)
                    Assert.False(text.Contains(s), $"{f.Key} 出現簡體字「{s}」");
    }
}
