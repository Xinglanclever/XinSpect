using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 驗機規則：命中、不命中，以及「缺一邊」時必須判成無法判定。
/// </summary>
/// <remarks>
/// 每條規則都要三種測試：相符、矛盾、缺一邊。第三種最重要——二手驗機的情境下，
/// 「這台機器讀不到 SMART」本身就是資訊，不能靜靜地當作通過。
/// 另外每個「矛盾」都必須附得出正當成因（<c>BenignCause</c>）：工具的職責到「指出對不上」為止，
/// 再往前一步就是替使用者認定賣家有惡意。
/// </remarks>
public class VerifyRulesTests
{
    internal static VerifyFact Text(FactId id, string value) => new(
        id, FactCatalog.Name(id), value, null, "", FactSource.Smbios,
        "SMBIOS Type 17", false, FactTrust.FirmwareReported, DateTime.UnixEpoch);

    internal static VerifyFact Num(FactId id, double n, string unit = "") => new(
        id, FactCatalog.Name(id), n.ToString("0.##"), n, unit, FactSource.Smbios,
        "SMBIOS Type 17", false, FactTrust.FirmwareReported, DateTime.UnixEpoch);

    internal static VerifyFinding One(string ruleId, params VerifyFact[] facts)
        => VerifyEngine.Run(new VerifyFacts(facts)).Single(x => x.Id == ruleId);

    [Fact]
    public void 缺事實時_由引擎判為無法判定_並指出缺哪一個()
    {
        var f = One("R-MEM-01", Num(FactId.DimmCount, 2));      // 故意不給製造商與料號
        Assert.Equal(VerifyVerdict.Unread, f.Verdict);
        Assert.Equal(Severity.Neutral, f.Severity);
        Assert.Contains(FactCatalog.Name(FactId.DimmManufacturers), f.Explanation);
        Assert.Empty(f.Evidence);
    }

    [Fact]
    public void R_MEM_01_同廠同料號判為相符()
    {
        var f = One("R-MEM-01", Num(FactId.DimmCount, 2),
            Text(FactId.DimmManufacturers, "Micron|Micron"),
            Text(FactId.DimmPartNumbers, "MTA8ATF1G64AZ|MTA8ATF1G64AZ"));
        Assert.Equal(VerifyVerdict.Match, f.Verdict);
        Assert.Equal(Severity.Good, f.Severity);
    }

    [Fact]
    public void R_MEM_01_不同料號判為矛盾_且必須附上正當成因()
    {
        var f = One("R-MEM-01", Num(FactId.DimmCount, 2),
            Text(FactId.DimmManufacturers, "Micron|SK Hynix"),
            Text(FactId.DimmPartNumbers, "MTA8ATF1G64AZ|HMA81GU6JJR8N"));
        Assert.Equal(VerifyVerdict.Conflict, f.Verdict);
        Assert.Equal(Severity.Warning, f.Severity);
        Assert.False(string.IsNullOrWhiteSpace(f.BenignCause));   // 混批常常只是使用者自己加的
        Assert.Equal(2, f.Evidence.Length);
    }

    [Fact]
    public void R_MEM_01_只有一條模組時無從混批_判為相符()
    {
        var f = One("R-MEM-01", Num(FactId.DimmCount, 1),
            Text(FactId.DimmManufacturers, "Micron"),
            Text(FactId.DimmPartNumbers, "MTA8ATF1G64AZ"));
        Assert.Equal(VerifyVerdict.Match, f.Verdict);
    }
}
