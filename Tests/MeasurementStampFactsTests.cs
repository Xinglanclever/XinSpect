using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 逐鍵時間戳出處（U10 第三圈 MT-0003，第 310 輪）。
///
/// <para>
/// 這一組釘住的是一句很容易被誤讀的話：「這份報告的數字是什麼時候量的」。
/// 空集合不能被說成「時間一致」，預設時間戳不能被說成「剛剛量的」，
/// 落後幾秒與落後幾天都只是事實、不是評語。
/// </para>
/// </summary>
public sealed class MeasurementStampFactsTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-29T12:00:00+00:00");

    private static HardwareFact Fact(string key, DateTimeOffset at, string value = "值")
        => new(key, "測試", key, value, "", "測試來源", FactTrustLevel.Reported, false, at);

    private static HardwareFact Range(IReadOnlyList<HardwareFact> facts)
        => Assert.Single(facts, f => f.Key == MeasurementStampFactsService.RangeKey);

    [Fact]
    public void 沒有事實時要說無從計算而不是時間一致()
    {
        var facts = MeasurementStampFactsService.Collect([], Now);
        var range = Assert.Single(facts);
        Assert.Equal(FactAvailability.NotApplicable, range.Availability);
        Assert.Contains("沒有任何事實", range.UnavailableReason);
    }

    [Fact]
    public void 時間戳全是預設值時要說說不出來()
    {
        var facts = MeasurementStampFactsService.Collect([Fact("cpu.model", default)], Now);
        var range = Assert.Single(facts);
        Assert.Equal(FactAvailability.ReadError, range.Availability);
        Assert.Contains("預設值", range.UnavailableReason);
    }

    [Fact]
    public void 範圍寫出最早最晚與跨度()
    {
        var facts = MeasurementStampFactsService.Collect([
            Fact("cpu.model", Now.AddSeconds(-90)),
            Fact("cpu.cores", Now.AddSeconds(-30)),
            Fact("mem.total", Now),
        ], Now);

        var range = Range(facts);
        Assert.Contains("最早 2026-10-29T11:58:30Z", range.Value);
        Assert.Contains("最晚 2026-10-29T12:00:00Z", range.Value);
        Assert.Contains("跨度 1.5 分", range.Value);
        Assert.Equal(90, range.NumericValue);
        Assert.Equal(FactAvailability.Present, range.Availability);
    }

    [Fact]
    public void 同一秒的多筆只算一個量測時刻()
    {
        var t = Now.AddSeconds(-10);
        var facts = MeasurementStampFactsService.Collect([
            Fact("cpu.a", t),
            Fact("cpu.b", t.AddMilliseconds(400)),
            Fact("mem.a", Now),
        ], Now);

        Assert.Contains("不同量測時刻 2 組", Range(facts).Value);
    }

    [Fact]
    public void 落後的域要單獨列一筆並寫出落後多少()
    {
        var facts = MeasurementStampFactsService.Collect([
            Fact("cpu.model", Now),
            Fact("st.smart.0", Now.AddSeconds(-60)),
        ], Now);

        var lag = Assert.Single(facts, f => f.Key == MeasurementStampFactsService.LagPrefix + "0001");
        Assert.Contains("：st", lag.Name);
        Assert.StartsWith("st 域", lag.Value);
        Assert.Contains("1 分", lag.Value);
        Assert.Equal(60, lag.NumericValue);
    }

    [Fact]
    public void 落後不到門檻的域不列()
    {
        var facts = MeasurementStampFactsService.Collect([
            Fact("cpu.model", Now),
            Fact("mem.total", Now.AddSeconds(-MeasurementStampFactsService.LagThresholdSeconds)),
        ], Now);

        Assert.DoesNotContain(facts, f => f.Key.StartsWith(MeasurementStampFactsService.LagPrefix, StringComparison.Ordinal));
        // 但整體跨度仍然照算：3 筆以內不是同一個時刻，讀者看得到。
        Assert.Contains("跨度", Range(facts).Value);
    }

    [Fact]
    public void 落後越多排越前面()
    {
        var facts = MeasurementStampFactsService.Collect([
            Fact("cpu.model", Now),
            Fact("a.x", Now.AddSeconds(-20)),
            Fact("b.x", Now.AddSeconds(-120)),
        ], Now);

        var lags = facts.Where(f => f.Key.StartsWith(MeasurementStampFactsService.LagPrefix, StringComparison.Ordinal)).ToList();
        Assert.Equal(2, lags.Count);
        Assert.EndsWith("：b", lags[0].Name);
        Assert.EndsWith("：a", lags[1].Name);
    }

    [Fact]
    public void 超過上限要說出還有幾個域沒列()
    {
        var list = new List<HardwareFact> { Fact("cpu.model", Now) };
        for (int i = 0; i < 6; i++)
            list.Add(Fact("d" + i + ".x", Now.AddSeconds(-30 - i)));

        var facts = MeasurementStampFactsService.Collect(list, Now, maxLagFacts: 2);
        Assert.Equal(2, facts.Count(f => f.Key.StartsWith(MeasurementStampFactsService.LagPrefix, StringComparison.Ordinal)));
        Assert.Contains("另有 4 個未列出", Range(facts).Value);
    }

    [Fact]
    public void 格式錯誤的鍵不會讓整支倒下()
    {
        // 沒有點的鍵、空字串：取域時要能自己處理，不能丟例外（事實集是外部輸入）。
        var facts = MeasurementStampFactsService.Collect([
            Fact("noDotKey", Now.AddSeconds(-60)),
            Fact("", Now),
        ], Now);

        Assert.Contains(facts, f => f.Key == MeasurementStampFactsService.RangeKey);
    }

    [Fact]
    public void 家族與靜態鍵都登記了()
    {
        Assert.True(FactKeyDynamicCatalog.Matches(MeasurementStampFactsService.LagPrefix + "0001"));
        Assert.Contains(MeasurementStampFactsService.RangeKey, FactKeyCatalog.Keys);
    }
}
