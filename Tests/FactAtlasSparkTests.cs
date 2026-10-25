using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 事實總表走勢欄（U10-VZ-0064／<c>ui.factatlas.spark</c>）的守門：
/// 歷史只來自本工作階段各輪載入的實際數值讀值；「有歷史才有線」——
/// 少於兩個數值點、值不是純數字、讀不到，一律如實留空。
/// </summary>
public class FactAtlasSparkTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    private static HardwareFact Fact(string key, string value) =>
        new(key, "測試", key, value, "", "單元測試", FactTrustLevel.Measured, false, At, null);

    private static HardwareFact Unavailable(string key) =>
        new(key, "測試", key, "", "", "單元測試", FactTrustLevel.Unknown, false, At, null,
            FactAvailability.NotSupported, "平台不支援");

    [Fact]
    public void 兩輪不同讀值_走勢兩點且順序為時間序()
    {
        var at = new FactAtlasService();
        at.Load([Fact("cpu.pkg_c2_us", "10")]);
        at.Load([Fact("cpu.pkg_c2_us", "20")]);

        var row = at.Keys.Single(r => r.Key == "cpu.pkg_c2_us");
        Assert.NotNull(row.Spark);
        Assert.Equal(new[] { 10.0, 20.0 }, row.Spark);
    }

    [Fact]
    public void 同一輪重複載入_不重複記點()
    {
        var at = new FactAtlasService();
        var round = new HardwareFact[] { Fact("cpu.pkg_c2_us", "7") };
        at.Load(round);
        at.Load(round);
        at.Load(round);

        var row = at.Keys.Single(r => r.Key == "cpu.pkg_c2_us");
        Assert.Null(row.Spark);   // 一直只有一輪、一點——畫不出走勢，也不該假裝有三點
    }

    [Fact]
    public void 值帶單位或文字_如實留空_不硬拆硬畫()
    {
        var at = new FactAtlasService();
        at.Load([Fact("cpu.pkg_c3_us", "有")]);
        at.Load([Fact("cpu.pkg_c3_us", "無")]);
        at.Load([Fact("cpu.pkg_c7_us", "37 °C")]);
        at.Load([Fact("cpu.pkg_c7_us", "45 °C")]);

        Assert.Null(at.Keys.Single(r => r.Key == "cpu.pkg_c3_us").Spark);
        Assert.Null(at.Keys.Single(r => r.Key == "cpu.pkg_c7_us").Spark);
    }

    [Fact]
    public void 讀不到的輪_不補舊值_只有單輪讀到時不成線()
    {
        var at = new FactAtlasService();
        at.Load([Fact("cpu.pkg_c2_us", "10")]);
        at.Load([Unavailable("cpu.pkg_c2_us")]);   // 第二輪讀不到：不拿 10 冒充
        at.Load([Fact("cpu.pkg_c2_us", "12")]);

        var row = at.Keys.Single(r => r.Key == "cpu.pkg_c2_us");
        // 歷史只記了 10 與 12 兩個真實讀值——讀不到的那輪不是資料點
        Assert.NotNull(row.Spark);
        Assert.Equal(new[] { 10.0, 12.0 }, row.Spark);
    }

    [Fact]
    public void 點數達上限_丟最舊的_容量如聲明()
    {
        var at = new FactAtlasService();
        for (int i = 0; i < FactAtlasService.SparkCapacity + 5; i++)
            at.Load([Fact("cpu.pkg_c2_us", i.ToString(System.Globalization.CultureInfo.InvariantCulture))]);

        var row = at.Keys.Single(r => r.Key == "cpu.pkg_c2_us");
        Assert.NotNull(row.Spark);
        Assert.Equal(FactAtlasService.SparkCapacity, row.Spark!.Length);
        Assert.Equal(FactAtlasService.SparkCapacity + 4, row.Spark[^1]);   // 最新的一點在尾端
    }
}
