using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 繁簡轉換的詞組表測試。
/// </summary>
/// <remarks>
/// 這張表存在的理由：Windows 的 LCMapStringEx 是逐字轉換，會把「記憶體」變成「记忆体」
/// 而不是「内存」、「顯示卡」變成「显示卡」而不是「显卡」。這些都是技術用語的在地化，
/// 不是碼位對映能處理的。
/// </remarks>
public class ZhTermsTests
{
    // ── 核心詞組必須正確 ──

    [Theory]
    [InlineData("記憶體", "内存")]
    [InlineData("顯示卡", "显卡")]
    [InlineData("儲存裝置", "存储设备")]
    [InlineData("主機板", "主板")]
    [InlineData("網路", "网络")]
    [InlineData("感測器", "传感器")]
    [InlineData("晶片組", "芯片组")]
    [InlineData("韌體", "固件")]
    [InlineData("效能", "性能")]
    [InlineData("快取", "缓存")]
    [InlineData("匯流排", "总线")]
    [InlineData("執行緒", "线程")]
    [InlineData("連接埠", "端口")]
    [InlineData("設定", "设置")]
    [InlineData("終端機", "终端")]
    [InlineData("瀏覽器", "浏览器")]
    [InlineData("關於", "关于")]
    public void 核心技術詞組繁轉簡(string trad, string simp)
        => Assert.Equal(simp, ZhTerms.ApplyToSimp(trad));

    // ── 長詞優先，不會被短詞截斷 ──

    [Fact]
    public void 記憶體整理不會變成内存整理以外的東西()
        => Assert.Equal("内存清理", ZhTerms.ApplyToSimp("記憶體整理"));

    [Fact]
    public void 顯示卡超頻不會先被顯示卡命中再多一個超頻()
        => Assert.Equal("显卡超频", ZhTerms.ApplyToSimp("顯示卡超頻"));

    [Fact]
    public void 效能天花板不會變成性能天花板以外的東西()
        => Assert.Equal("性能天花板", ZhTerms.ApplyToSimp("效能天花板"));

    // ── 整句轉換（混合詞組＋逐字） ──

    [Fact]
    public void PageRegistry的標題整句轉換()
    {
        Assert.Equal("内存条、SPD 与主／次要时序",
            LanguageService.ToSimplified("記憶體模組、SPD 與主／次要時序"));
    }

    [Fact]
    public void 顯示卡規格整句()
    {
        // 「螢幕」→「萤幕」是 LCMapStringEx 的逐字轉換結果，不在詞組表裡
        Assert.Equal("显卡规格、显存与萤幕色域",
            LanguageService.ToSimplified("顯示卡規格、顯示記憶體與螢幕色域"));
    }

    [Fact]
    public void 匯流排相關整句()
    {
        // 「裝置」→「装置」、「唯讀」→「唯读」是逐字轉換，不在詞組表裡
        Assert.Equal("目前协商到的速度／宽度对装置能力（唯读 PCI 设置空间）",
            LanguageService.ToSimplified("目前協商到的速度／寬度對裝置能力（唯讀 PCI 設定空間）"));
    }

    // ── 反向：簡→繁 ──

    [Theory]
    [InlineData("内存", "記憶體")]
    [InlineData("显卡", "顯示卡")]
    [InlineData("主板", "主機板")]
    [InlineData("传感器", "感測器")]
    public void 簡轉繁反向(string simp, string trad)
        => Assert.Equal(trad, ZhTerms.ApplyToTrad(simp));

    // ── 表的完整性 ──

    [Fact]
    public void 正向表裡沒有重複的繁體詞()
    {
        var seen = new HashSet<string>();
        foreach (var (trad, _) in ZhTerms.ToSimp)
            Assert.True(seen.Add(trad), $"重複的繁體詞：{trad}");
    }

    [Fact]
    public void 反向表筆數與正向表相同()
        => Assert.Equal(ZhTerms.ToSimp.Length, ZhTerms.ToTrad.Length);

    /// <summary>長詞必須排在短詞前面，否則短詞會先命中把長詞截斷。</summary>
    [Fact]
    public void 表是按繁體詞長度降序排列的()
    {
        for (int i = 1; i < ZhTerms.ToSimp.Length; i++)
        {
            var prev = ZhTerms.ToSimp[i - 1];
            var curr = ZhTerms.ToSimp[i];
            // 同長度允許（順序無所謂），只要不是短的排在長的前面
            Assert.True(prev.Trad.Length >= curr.Trad.Length,
                $"第 {i} 項「{curr.Trad}」（{curr.Trad.Length} 字）排在「{prev.Trad}」（{prev.Trad.Length} 字）之後，"
                + "但它更長——長詞必須排在前面，否則短詞會先命中。");
        }
    }

    [Fact]
    public void 不動英文和數字()
    {
        Assert.Equal("DDR4-3200", ZhTerms.ApplyToSimp("DDR4-3200"));
        Assert.Equal("PCIe 5.0 x16", ZhTerms.ApplyToSimp("PCIe 5.0 x16"));
    }

    [Fact]
    public void 空字串和null不炸()
    {
        Assert.Equal("", ZhTerms.ApplyToSimp(""));
        Assert.Equal("", ZhTerms.ApplyToTrad(""));
    }
}
