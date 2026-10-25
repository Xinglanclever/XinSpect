using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 核×時間熱圖（U10-VZ-0004／<c>viz.heatmap.coretime</c>）的<b>數學守門</b>：
/// 欄壓縮、格子矩形與時間比例全是純函式，用合成資料直接驗證，不開 UI。
/// 色階本身不在此測——它只有 <see cref="HeatScale"/> 一份（既有測試盯著），本控制項不得另造。
/// </summary>
public class CoreTimeHeatmapTests
{
    /// <summary>解開可空樣本：先釘 NotNull 再取值——null 與 0 在這裡必須分得開。</summary>
    private static double V(double? v)
    {
        Assert.NotNull(v);
        return v!.Value;
    }

    [Fact]
    public void 追蹤鍵_以U10目錄正式鍵為準()
        => Assert.Equal("viz.heatmap.coretime", VizTrackingKeys.HeatmapCoretimeFactKey);

    // ── 欄壓縮 ─────────────────────────────────────────────────────────────

    [Fact]
    public void 壓縮_每欄取非缺格樣本的平均()
    {
        // 4 樣本壓 2 欄：欄 0＝(10+20)/2、欄 1＝(30+40)/2
        var r = CoreTimeHeatmapMath.Compress([10.0, 20.0, 30.0, 40.0], 2);

        Assert.Equal(2, r.Length);
        Assert.Equal(15.0, V(r[0]), 9);
        Assert.Equal(35.0, V(r[1]), 9);
    }

    [Fact]
    public void 壓縮_缺格不計入平均_全缺的欄仍是缺格()
    {
        // 欄 0：10 與缺格 → 平均＝10；欄 1：全是缺格 → 仍是 null（不冒充 0）
        var r = CoreTimeHeatmapMath.Compress([10.0, null, null, null], 2);

        Assert.Equal(10.0, V(r[0]), 9);
        Assert.Null(r[1]);
    }

    [Fact]
    public void 壓縮_序列比欄數短_補缺格到欄數_不放大圖形()
    {
        var r = CoreTimeHeatmapMath.Compress([50.0, null], 4);

        Assert.Equal(4, r.Length);          // 時間軸長度如實
        Assert.Equal(50.0, V(r[0]), 9);
        Assert.Null(r[1]);
        Assert.Null(r[2]);
        Assert.Null(r[3]);
    }

    [Fact]
    public void 壓縮_空序列_回全缺格_不丟例外()
    {
        var r = CoreTimeHeatmapMath.Compress([], 3);

        Assert.Equal(3, r.Length);
        Assert.All(r, v => Assert.Null(v));
    }

    // ── 格子矩形 ───────────────────────────────────────────────────────────

    [Fact]
    public void 格子矩形_含縫_首格貼區域左上()
    {
        var area = new System.Windows.Rect(0, 0, 101, 32);   // 3 欄 3 列、縫 1：格寬 (101−2)/3＝33、格高 (32−2)/3＝10
        var cell = CoreTimeHeatmapMath.CellRect(0, 0, 3, 3, area, gap: 1);

        Assert.Equal(0, cell.X, 9);
        Assert.Equal(0, cell.Y, 9);
        Assert.Equal(33, cell.Width, 9);
        Assert.Equal(10, cell.Height, 9);
    }

    [Fact]
    public void 格子矩形_末格貼區域右下_縫不擠壓格子()
    {
        var area = new System.Windows.Rect(0, 0, 101, 32);
        var cell = CoreTimeHeatmapMath.CellRect(2, 2, 3, 3, area, gap: 1);

        Assert.Equal(68, cell.X, 9);              // 前兩格＋兩道縫：2×(33+1)＝68：2×(33+1)＝68
        Assert.Equal(33, cell.Width, 9);          // 末格右緣 68＋33＝101（貼齊區域）
        Assert.Equal(22, cell.Y, 9);              // 列位移同式：2×(10+1)＝22
    }

    [Fact]
    public void 格子矩形_單行單列_佔滿整個區域()
    {
        var area = new System.Windows.Rect(5, 7, 100, 50);
        var cell = CoreTimeHeatmapMath.CellRect(0, 0, 1, 1, area, gap: 2);

        Assert.Equal(100, cell.Width, 9);
        Assert.Equal(50, cell.Height, 9);
    }

    // ── 時間比例 ───────────────────────────────────────────────────────────

    [Fact]
    public void 時間比例_左零右一_中點在中間()
    {
        Assert.Equal(0.0, CoreTimeHeatmapMath.ColumnFraction(0, 5), 9);
        Assert.Equal(1.0, CoreTimeHeatmapMath.ColumnFraction(4, 5), 9);
        Assert.Equal(0.5, CoreTimeHeatmapMath.ColumnFraction(2, 5), 9);
    }
}
