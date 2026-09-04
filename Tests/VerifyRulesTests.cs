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

    // ── R-MEM-02：序號異常。全 0／全 F 是韌體沒燒序號或序號被抹掉；重複則是不可能的事 ──

    [Theory]
    [InlineData("0000000000000000|1234ABCD", VerifyVerdict.Conflict)]
    [InlineData("FFFFFFFF|1234ABCD", VerifyVerdict.Conflict)]
    [InlineData("1234ABCD|1234ABCD", VerifyVerdict.Conflict)]
    [InlineData("1234ABCD|5678EF01", VerifyVerdict.Match)]
    public void R_MEM_02_序號異常(string serials, VerifyVerdict expected)
        => Assert.Equal(expected, One("R-MEM-02", Num(FactId.DimmCount, 2),
            Text(FactId.DimmSerials, serials)).Verdict);

    [Fact]
    public void R_MEM_02_序號重複時判定為較嚴重()
    {
        var f = One("R-MEM-02", Num(FactId.DimmCount, 2), Text(FactId.DimmSerials, "1234ABCD|1234ABCD"));
        Assert.Equal(Severity.Serious, f.Severity);
        Assert.False(string.IsNullOrWhiteSpace(f.BenignCause));
    }

    // ── R-MEM-03：實際運行速度低於標稱（多數情況是沒開 XMP，不是模組的問題）──

    [Theory]
    [InlineData(3200, 3200, VerifyVerdict.Match)]
    [InlineData(3200, 2133, VerifyVerdict.Conflict)]
    public void R_MEM_03_實際速度低於標稱(double rated, double configured, VerifyVerdict expected)
        => Assert.Equal(expected, One("R-MEM-03",
            Num(FactId.DimmSpeedMts, rated, "MT/s"),
            Num(FactId.DimmConfiguredMts, configured, "MT/s")).Verdict);

    // ── R-MEM-04：陣列宣稱與實際安裝對不上 ──

    [Theory]
    [InlineData(32768, 65536, 4, 2, VerifyVerdict.Match)]
    [InlineData(32768, 16384, 4, 2, VerifyVerdict.Conflict)]   // 安裝量超過陣列宣稱上限
    [InlineData(32768, 65536, 2, 4, VerifyVerdict.Conflict)]   // 模組數多於插槽數
    public void R_MEM_04_陣列宣稱與實際對不上(
        double totalMiB, double maxMiB, double slots, double dimms, VerifyVerdict expected)
        => Assert.Equal(expected, One("R-MEM-04",
            Num(FactId.DimmSizeTotalMiB, totalMiB, "MiB"), Num(FactId.ArrayMaxCapacityMiB, maxMiB, "MiB"),
            Num(FactId.ArraySlotCount, slots), Num(FactId.DimmCount, dimms)).Verdict);

    [Fact]
    public void 記憶體四條規則_缺任一依賴都由引擎判為無法判定()
    {
        var empty = VerifyEngine.Run(new VerifyFacts([]));
        Assert.All(empty.Where(x => x.Id.StartsWith("R-MEM-")),
            x => Assert.Equal(VerifyVerdict.Unread, x.Verdict));
        Assert.Equal(4, empty.Count(x => x.Id.StartsWith("R-MEM-")));
    }
}
