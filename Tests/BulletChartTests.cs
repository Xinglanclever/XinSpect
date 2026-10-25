using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 子彈圖（U10-VZ-0022／<c>viz.bullet</c>）的<b>數學守門</b>：映射、帶跨度與軸範圍
/// 全是純函式，用合成資料直接驗證，不開 UI。
/// 規格值不內建在控制項裡的紀律，由「帶一律由呼叫端傳入」的 API 形狀本身守著——
/// 這裡測的是給了帶之後幾何對不對。
/// </summary>
public class BulletChartTests
{
    /// <summary>解開可空回傳：先釘 NotNull 再取值。</summary>
    private static double V(double? v)
    {
        Assert.NotNull(v);
        return v!.Value;
    }

    [Fact]
    public void 追蹤鍵_以U10目錄正式鍵為準()
        => Assert.Equal("viz.bullet", VizTrackingKeys.BulletFactKey);

    // ── 映射 ───────────────────────────────────────────────────────────────

    [Fact]
    public void 映射_min貼左max貼右_中點在中間()
    {
        Assert.Equal(50, BulletChartMath.MapX(0, 0, 10, 50, 100), 9);
        Assert.Equal(150, BulletChartMath.MapX(10, 0, 10, 50, 100), 9);
        Assert.Equal(100, BulletChartMath.MapX(5, 0, 10, 50, 100), 9);
    }

    [Fact]
    public void 映射_超出軸範圍的值_夾限不畫出繪圖區()
    {
        Assert.Equal(50, BulletChartMath.MapX(-1, 0, 10, 50, 100), 9);
        Assert.Equal(150, BulletChartMath.MapX(11, 0, 10, 50, 100), 9);
    }

    // ── 帶跨度 ─────────────────────────────────────────────────────────────

    [Fact]
    public void 帶跨度_ATX容差帶落在正確的位置()
    {
        // 12V 軸 10–14：±5% 帶＝11.4–12.6 → 35%..65%
        var band = new SensorDashboardService.SpecBand(11.4, 12.6, "ATX 規格 ±5%");
        var (x0, x1) = BulletChartMath.BandSpan(band, 10, 14, 0, 100);

        Assert.Equal(35, x0, 6);
        Assert.Equal(65, x1, 6);
    }

    [Fact]
    public void 帶跨度_帶比軸寬_夾進軸內不越界()
    {
        var band = new SensorDashboardService.SpecBand(-5, 50, "寬帶");
        var (x0, x1) = BulletChartMath.BandSpan(band, 0, 10, 0, 100);

        Assert.Equal(0, x0, 9);
        Assert.Equal(100, x1, 9);
    }

    // ── 軸範圍 ─────────────────────────────────────────────────────────────

    [Fact]
    public void 軸範圍_以帶與值為包絡再留邊距()
    {
        var bands = new SensorDashboardService.SpecBand[]
        {
            new(10.8, 13.2, "±10%"),
            new(11.4, 12.6, "ATX ±5%"),
        };
        var (min, max) = BulletChartMath.Scale(bands, value: 12.9, target: 12);

        Assert.Equal(10.8 - (13.2 - 10.8) * 0.05, min, 9);   // 包絡 10.8–13.2，邊距 5%
        Assert.Equal(13.2 + (13.2 - 10.8) * 0.05, max, 9);
    }

    [Fact]
    public void 軸範圍_沒有帶也沒有值_回中性01軸_不丟例外()
    {
        var (min, max) = BulletChartMath.Scale([], null, null);
        Assert.Equal(0, min, 9);
        Assert.Equal(1, max, 9);
    }

    [Fact]
    public void 軸範圍_全段等值_撐開避免除零()
    {
        var bands = new SensorDashboardService.SpecBand[] { new(12, 12, "等值") };
        var (min, max) = BulletChartMath.Scale(bands, 12, null);

        Assert.True(min < 12 && max > 12);
    }

    // ── 服務端的 ATX 配對（規格帶的單一來源，頁面與測試共用） ────────────────

    [Fact]
    public void ATX軌_只認以加號開頭的軌名_Vcore不算ATX軌()
    {
        Assert.Equal(12.0, V(SensorDashboardService.AtxRailNominal("+12V")), 9);
        Assert.Equal(12.0, V(SensorDashboardService.AtxRailNominal("+12V #2")), 9);
        Assert.Equal(5.0, V(SensorDashboardService.AtxRailNominal("+5V")), 9);
        Assert.Equal(3.3, V(SensorDashboardService.AtxRailNominal("+3.3V")), 9);
        Assert.Null(SensorDashboardService.AtxRailNominal("CPU VCore"));     // CPU 廠商規格，不是 ATX
        Assert.Null(SensorDashboardService.AtxRailNominal("Voltage #3"));    // 沒有軌名：不猜
        Assert.Null(SensorDashboardService.AtxRailNominal(null));
        Assert.Null(SensorDashboardService.AtxRailNominal(""));
    }

    [Fact]
    public void ATX容差帶_12V與5V取百分之5_3V3取百分之4_外層一律百分之10()
    {
        var b12 = SensorDashboardService.AtxBands(12.0);
        Assert.Equal(10.8, b12[0].Lo, 9);   // ±10%（監看容忍帶，非 ATX 規格）
        Assert.Equal(13.2, b12[0].Hi, 9);
        Assert.Equal(11.4, b12[1].Lo, 9);   // ATX ±5%
        Assert.Equal(12.6, b12[1].Hi, 9);

        var b3v3 = SensorDashboardService.AtxBands(3.3);
        Assert.Equal(3.168, b3v3[1].Lo, 9); // 3.3 × 0.96
        Assert.Equal(3.432, b3v3[1].Hi, 9); // 3.3 × 1.04
    }
}
