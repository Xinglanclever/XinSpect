using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>Vol1 第 SC-001/002/003/010 項（情境包第三波）的事實服務測試。</summary>
public class ScenarioThirdWaveFactsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 15, 2, 0, 0, TimeSpan.Zero);

    // ── SC-001 二手驗機精靈 ─────────────────────────────────────────

    [Fact]
    public void 驗機精靈_無規則結果為不適用()
    {
        var f = ScenarioThirdWaveFactsService.VerifyCheckFact(At, () => null);
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("沒有規則跑過就沒有摘要", f.UnavailableReason);
    }

    [Fact]
    public void 驗機精靈_不修復不優化()
    {
        var f = ScenarioThirdWaveFactsService.VerifyCheckFact(At,
            () => new ScenarioThirdWaveFactsService.VerifyCheckState(40, 35, 2, 3, 0));
        Assert.Contains("唯讀、不修復、不「優化」", f.Value);
        Assert.Contains("不對機器做任何變更", f.Value);
        Assert.Contains("不會為了讓結果好看而調整任何設定", f.Value);
        Assert.Equal(2.0, f.NumericValue);
    }

    [Fact]
    public void 驗機精靈_缺來源顯示為灰不是綠()
    {
        var f = ScenarioThirdWaveFactsService.VerifyCheckFact(At,
            () => new ScenarioThirdWaveFactsService.VerifyCheckState(40, 32, 0, 2, 6));
        Assert.Contains("未納入 6 項", f.Value);
        Assert.Contains("顯示成灰不是綠", f.Value);
        Assert.Contains("沒檢查到與檢查通過是兩件事", f.Value);
    }

    [Fact]
    public void 驗機精靈_讀不到不是通過()
    {
        var f = ScenarioThirdWaveFactsService.VerifyCheckFact(At,
            () => new ScenarioThirdWaveFactsService.VerifyCheckState(40, 35, 0, 5, 0));
        Assert.Contains("讀不到不是通過", f.Value);
    }

    // ── SC-002 該不該修摘要 ─────────────────────────────────────────

    [Fact]
    public void 該不該修_無訊號為不適用不是一切正常()
    {
        var f = ScenarioThirdWaveFactsService.VerdictFact(At,
            () => new ScenarioThirdWaveFactsService.VerdictState(0, 0, 0, []));
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("不是「一切正常」的判決", f.UnavailableReason);
    }

    [Fact]
    public void 該不該修_不給分數不下正品翻新結論()
    {
        var f = ScenarioThirdWaveFactsService.VerdictFact(At,
            () => new ScenarioThirdWaveFactsService.VerdictState(4, 1, 2, ["磁碟延遲", "頻率偏離", "溫度偏高"]));
        Assert.Contains("聚合既有判定，不新增判決", f.Value);
        Assert.Contains("不給分數、不下「正品／翻新」結論", f.Value);
        Assert.Contains("分數會讓人停止思考每一條的成因", f.Value);
        Assert.Contains("最需要看的：磁碟延遲、頻率偏離、溫度偏高", f.Value);
        Assert.Equal(5.0, f.NumericValue);
    }

    [Fact]
    public void 該不該修_有嚴重項時語氣變化()
    {
        var crit = ScenarioThirdWaveFactsService.VerdictFact(At,
            () => new ScenarioThirdWaveFactsService.VerdictState(1, 2, 0, ["X"]));
        Assert.Contains("有嚴重等級的項目", crit.Value);

        var warn = ScenarioThirdWaveFactsService.VerdictFact(At,
            () => new ScenarioThirdWaveFactsService.VerdictState(3, 0, 0, ["X"]));
        Assert.Contains("有警告等級的項目", warn.Value);
    }

    // ── SC-003 效能為什麼慢 ─────────────────────────────────────────

    [Fact]
    public void 效能慢_無樣本不憑感覺列原因()
    {
        var f = ScenarioThirdWaveFactsService.SlowFact(At, () => null);
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("不憑感覺列原因", f.UnavailableReason);
    }

    [Fact]
    public void 效能慢_是排除法不是因果鏈()
    {
        var f = ScenarioThirdWaveFactsService.SlowFact(At,
            () => new ScenarioThirdWaveFactsService.SlowState(18.5, 42.0, 3.7, 88.0, []));
        Assert.Contains("這是排除法不是因果鏈", f.Value);
        Assert.Contains("不宣稱誰是元凶", f.Value);
        Assert.Contains("要定因果需要受控實驗", f.Value);
        Assert.Contains("頻率偏離標稱 18.5%", f.Value);
        Assert.Equal(4.0, f.NumericValue);
    }

    [Fact]
    public void 效能慢_讀不到的環節不能被當成沒問題()
    {
        var f = ScenarioThirdWaveFactsService.SlowFact(At,
            () => new ScenarioThirdWaveFactsService.SlowState(null, 42.0, null, 88.0, ["頻率", "磁碟"]));
        Assert.Contains("2 段讀不到", f.Value);
        Assert.Contains("頻率不可讀", f.Value);
        Assert.Contains("讀不到的環節不能被當成「沒問題」", f.Value);
        Assert.Contains("證據鏈少了那一段就拼不完整", f.Value);
    }

    // ── SC-010 供電穩嗎 ─────────────────────────────────────────────

    [Fact]
    public void 供電_樣本不足不畫曲線()
    {
        var f = ScenarioThirdWaveFactsService.PowerCurveFact(At,
            () => new ScenarioThirdWaveFactsService.PowerCurveSamples([12000, 12010], [65, 70], 5));
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("樣本不足就不畫曲線", f.UnavailableReason);
    }

    [Fact]
    public void 供電_無樣本為不適用()
    {
        var f = ScenarioThirdWaveFactsService.PowerCurveFact(At, () => null);
        Assert.Equal(FactAvailability.NotApplicable, f.Availability);
        Assert.Contains("曲線要資料", f.UnavailableReason);
    }

    [Fact]
    public void 供電_穩定時如實說穩定()
    {
        var v = Enumerable.Range(0, 20).Select(i => 12000.0 + (i % 3)).ToList();
        var w = Enumerable.Range(0, 20).Select(i => 65.0 + i * 0.1).ToList();
        var f = ScenarioThirdWaveFactsService.PowerCurveFact(At,
            () => new ScenarioThirdWaveFactsService.PowerCurveSamples(v, w, 5));
        Assert.Contains("期間供電穩定", f.Value);
        Assert.Contains("觀察不是預測", f.Value);
        Assert.Contains("5% 的門檻已經在量測精度邊緣", f.Value);
    }

    [Fact]
    public void 供電_大波動要標示()
    {
        var v = new List<double> { 11000, 12000, 13000, 11200, 12800, 11500, 12500, 11100, 12900, 11300,
                                   12700, 11400, 12600, 11600, 12400, 11700, 12300, 11800, 12200, 11900 };
        var w = Enumerable.Range(0, 20).Select(i => 70.0).ToList();
        var f = ScenarioThirdWaveFactsService.PowerCurveFact(At,
            () => new ScenarioThirdWaveFactsService.PowerCurveSamples(v, w, 5));
        Assert.Contains("電壓波動", f.Value);
        Assert.Contains("期間電壓起伏明顯", f.Value);
        Assert.Contains("可能是負載變化大或供電不穩", f.Value);
    }

    // ── 整批 ────────────────────────────────────────────────────────

    [Fact]
    public void 整批_四把鍵齊且不重複()
    {
        var v = Enumerable.Range(0, 20).Select(i => 12000.0 + (i % 3)).ToList();
        var facts = ScenarioThirdWaveFactsService.Collect(At,
            () => new ScenarioThirdWaveFactsService.VerifyCheckState(40, 35, 2, 3, 0),
            () => new ScenarioThirdWaveFactsService.VerdictState(3, 0, 1, ["X"]),
            () => new ScenarioThirdWaveFactsService.SlowState(18.5, 42.0, 3.7, 88.0, []),
            () => new ScenarioThirdWaveFactsService.PowerCurveSamples(v, v, 5));

        Assert.Equal(4, facts.Count);
        Assert.Equal(4, facts.Select(f => f.Key).Distinct(StringComparer.Ordinal).Count());
        foreach (string k in new[]
        {
            ScenarioThirdWaveFactsService.VerifyCheckKey, ScenarioThirdWaveFactsService.VerdictKey,
            ScenarioThirdWaveFactsService.SlowKey, ScenarioThirdWaveFactsService.PowerCurveKey,
        })
            Assert.Contains(facts, f => f.Key == k);
    }
}
