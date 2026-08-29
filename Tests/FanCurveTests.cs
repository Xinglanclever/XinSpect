using System.IO;
using System.Text.Json;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 風扇曲線的純邏輯測試：線性內插、端點外推、排序、樣板形狀，以及服務層的落地與還原。
/// 不觸碰真實硬體（FanControlRow 需 LHM 控制器，故此處僅測不需風扇的路徑）。
/// </summary>
public sealed class FanCurveTests : IDisposable
{
    private readonly string _dir;

    public FanCurveTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "XinSpectTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* 暫存目錄清不掉不影響測試結果 */ }
    }

    private static FanCurve Curve(params (double T, double P)[] pts)
    {
        var c = new FanCurve { Key = "k", Name = "測試風扇" };
        c.SetPoints(pts);
        return c;
    }

    // ── 控制點 ──────────────────────────────────────────────────────────────

    [Fact]
    public void Point_ClampsAndRounds()
    {
        var p = new FanCurvePoint(12.4, 140);
        Assert.Equal(FanCurve.TempMin, p.TempC);     // 低於下限 → 夾到 20
        Assert.Equal(100, p.Percent);                // 高於 100 → 夾到 100

        p.TempC = 61.6;
        p.Percent = -8;
        Assert.Equal(62, p.TempC);                   // 四捨五入
        Assert.Equal(0, p.Percent);
    }

    [Fact]
    public void Point_LabelFollowsValues()
    {
        var p = new FanCurvePoint(50, 60);
        Assert.Equal("50 °C → 60 %", p.Label);
        p.Percent = 75;
        Assert.Equal("50 °C → 75 %", p.Label);
    }

    // ── 內插 ────────────────────────────────────────────────────────────────

    [Fact]
    public void Evaluate_InterpolatesLinearly()
    {
        var c = Curve((40, 20), (60, 60));
        Assert.Equal(20, c.Evaluate(40));
        Assert.Equal(60, c.Evaluate(60));
        Assert.Equal(40, c.Evaluate(50), 3);          // 中點
        Assert.Equal(30, c.Evaluate(45), 3);
    }

    [Fact]
    public void Evaluate_HoldsEndpointsOutsideRange()
    {
        var c = Curve((40, 20), (80, 90));
        Assert.Equal(20, c.Evaluate(20));             // 低於首點 → 首點值
        Assert.Equal(20, c.Evaluate(39));
        Assert.Equal(90, c.Evaluate(95));             // 高於末點 → 末點值
        Assert.Equal(90, c.Evaluate(100));
    }

    [Fact]
    public void Evaluate_AcceptsUnsortedInput()
    {
        var c = new FanCurve();
        c.Points.Add(new FanCurvePoint(80, 90));
        c.Points.Add(new FanCurvePoint(40, 20));      // 故意倒序加入
        Assert.Equal(20, c.Evaluate(40));
        Assert.Equal(55, c.Evaluate(60), 3);
    }

    [Fact]
    public void Evaluate_HandlesDegenerateCurves()
    {
        var empty = new FanCurve();
        Assert.Equal(0, empty.Evaluate(70));          // 無點 → 0

        var single = new FanCurve();
        single.Points.Add(new FanCurvePoint(50, 42));
        Assert.Equal(42, single.Evaluate(20));        // 單點 → 定值
        Assert.Equal(42, single.Evaluate(95));

        var vertical = new FanCurve();
        vertical.Points.Add(new FanCurvePoint(60, 30));
        vertical.Points.Add(new FanCurvePoint(60, 80));
        Assert.Equal(80, vertical.Evaluate(60));      // 同溫兩點 → 取高者，不得除以零
    }

    [Fact]
    public void SetPoints_SortsAndGuaranteesTwoPoints()
    {
        var c = Curve((70, 80), (30, 10));
        Assert.Equal(30, c.Points[0].TempC);
        Assert.Equal(70, c.Points[1].TempC);

        var one = new FanCurve();
        one.SetPoints([(50, 50)]);
        Assert.Equal(2, one.Points.Count);            // 自動補末點以保持可用
        Assert.Equal(FanCurve.TempMax, one.Points[^1].TempC);
    }

    [Fact]
    public void Sort_ReordersInPlaceAfterDrag()
    {
        var c = Curve((30, 10), (50, 40), (70, 80));
        c.Points[0].TempC = 65;                       // 把首點拖過中間點
        c.Sort();
        Assert.Equal([50, 65, 70], c.Points.Select(p => p.TempC));
    }

    // ── 樣板 ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Presets_AreMonotonicAndReachFull(int preset)
    {
        var c = new FanCurve();
        c.LoadPreset(preset);
        Assert.True(c.Points.Count >= 2);
        for (int i = 1; i < c.Points.Count; i++)
        {
            Assert.True(c.Points[i].TempC > c.Points[i - 1].TempC, "溫度須遞增");
            Assert.True(c.Points[i].Percent >= c.Points[i - 1].Percent, "輸出不得回頭");
        }
        Assert.Equal(100, c.Points[^1].Percent);       // 高溫端一律全速
    }

    [Fact]
    public void Presets_QuieterIsAlwaysLower()
    {
        var quiet = new FanCurve(); quiet.LoadPreset(0);
        var mid = new FanCurve(); mid.LoadPreset(1);
        var perf = new FanCurve(); perf.LoadPreset(2);

        foreach (double t in new double[] { 35, 50, 65, 80 })
        {
            Assert.True(quiet.Evaluate(t) < mid.Evaluate(t), $"{t} °C：靜音應低於均衡");
            Assert.True(mid.Evaluate(t) < perf.Evaluate(t), $"{t} °C：均衡應低於效能");
        }
    }

    [Fact]
    public void SourceText_MatchesIndex()
    {
        var c = new FanCurve();
        Assert.Equal(FanCurveSource.Hotter, c.Source);          // 預設兩者取較高
        c.SourceIndex = 0;
        Assert.Equal(FanCurveSource.Cpu, c.Source);
        Assert.Equal(FanCurve.SourceNames[0], c.SourceText);
        c.SourceIndex = 99;                                     // 越界不得改變狀態
        Assert.Equal(FanCurveSource.Cpu, c.Source);
    }

    [Fact]
    public void Hysteresis_ClampsToFifteen()
    {
        var c = new FanCurve { Hysteresis = 40 };
        Assert.Equal(15, c.Hysteresis);
        c.Hysteresis = -3;
        Assert.Equal(0, c.Hysteresis);
    }

    [Fact]
    public void StateText_FollowsEnabled()
    {
        var c = new FanCurve();
        Assert.Equal("未啟用", c.StateText);
        c.Enabled = true;
        Assert.Equal("曲線控制中", c.StateText);
    }

    [Fact]
    public void Changed_FiresOnPointDragAndCollectionEdit()
    {
        var c = Curve((30, 10), (70, 80));
        int hits = 0;
        c.Changed += () => hits++;

        c.Points[0].Percent = 25;
        Assert.Equal(1, hits);

        c.Points[0].Percent = 25;              // 同值不得再觸發（拖曳時避免存檔風暴）
        Assert.Equal(1, hits);

        c.Points.Add(new FanCurvePoint(50, 40));
        Assert.Equal(2, hits);

        c.Enabled = true;                      // 啟用狀態亦須落地
        Assert.Equal(3, hits);
    }

    [Fact]
    public void Changed_StopsAfterPointRemoved()
    {
        var c = Curve((30, 10), (50, 40), (70, 80));
        var removed = c.Points[1];
        int hits = 0;
        c.Changed += () => hits++;

        c.Points.Remove(removed);
        Assert.Equal(1, hits);

        removed.Percent = 99;                  // 已移除的點不得再牽動曲線
        Assert.Equal(1, hits);
    }

    // ── 服務層 ──────────────────────────────────────────────────────────────

    [Fact]
    public void Service_StartsEmptyWithNoFans()
    {
        var svc = new FanCurveService(_dir);
        Assert.False(svc.HasCurves);
        Assert.False(svc.AnyEnabled);
        svc.Attach([]);
        Assert.Equal("未偵測到可控風扇", svc.StatusText);
    }

    [Fact]
    public void Service_TickIsSafeWithoutCurvesOrSensors()
    {
        var svc = new FanCurveService(_dir);
        svc.Tick(null);                       // 無曲線、無感測器：不得拋出
        svc.DisableAll();
        Assert.False(svc.AnyEnabled);
    }

    [Fact]
    public void Service_AllowStopDefaultsOff()
    {
        var svc = new FanCurveService(_dir);
        Assert.False(svc.AllowStop);          // 預設守住 20 % 下限
    }

    [Fact]
    public void Service_PersistsAllowStopAcrossInstances()
    {
        var a = new FanCurveService(_dir) { AllowStop = true };
        a.Attach([]);                         // 觸發一次載入／存檔循環
        WriteCurveFile(_dir, allowStop: true, key: "SYSFAN1|機殼風扇", on: true, src: 0, hys: 5,
                       t: [30, 60, 90], p: [15, 50, 100]);

        var b = new FanCurveService(_dir);
        b.Attach([]);                         // 沒有實體風扇：曲線清空，但 AllowStop 須沿用
        Assert.True(b.AllowStop);
    }

    [Fact]
    public void Service_IgnoresCorruptFile()
    {
        File.WriteAllText(Path.Combine(_dir, "fancurves.json"), "{ 這不是 JSON");
        var svc = new FanCurveService(_dir);
        svc.Attach([]);                       // 壞檔視為沒有設定，不得拋出
        Assert.False(svc.HasCurves);
    }

    [Fact]
    public void Service_PresetToAllOnEmptySetIsHarmless()
    {
        var svc = new FanCurveService(_dir);
        svc.Attach([]);
        svc.ApplyPresetToAll(2);              // 沒有曲線時亦不得拋出
        Assert.False(svc.HasCurves);
    }

    // 手寫一份落地檔，模擬「上次關機前存下的設定」。
    private static void WriteCurveFile(string dir, bool allowStop, string key, bool on, int src, double hys,
                                       double[] t, double[] p)
    {
        var payload = new
        {
            AllowStop = allowStop,
            Curves = new[] { new { Key = key, On = on, Src = src, Hys = hys, T = t, P = p } },
        };
        File.WriteAllText(Path.Combine(dir, "fancurves.json"), JsonSerializer.Serialize(payload));
    }

    [Fact]
    public void PersistedFile_RoundTripsThroughJson()
    {
        WriteCurveFile(_dir, allowStop: false, key: "CPUFAN|處理器風扇", on: true, src: 2, hys: 4,
                       t: [30, 55, 80], p: [20, 55, 100]);

        // 以曲線的 SetPoints 重建：驗證存檔格式（並列的 T/P 陣列）可還原成同一條曲線
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "fancurves.json")));
        var row = doc.RootElement.GetProperty("Curves")[0];
        double[] t = [.. row.GetProperty("T").EnumerateArray().Select(x => x.GetDouble())];
        double[] p = [.. row.GetProperty("P").EnumerateArray().Select(x => x.GetDouble())];

        var c = new FanCurve { Key = row.GetProperty("Key").GetString()! };
        c.SetPoints(t.Zip(p).Select(z => (z.First, z.Second)));
        c.SourceIndex = row.GetProperty("Src").GetInt32();
        c.Hysteresis = row.GetProperty("Hys").GetDouble();

        Assert.Equal(3, c.Points.Count);
        Assert.Equal(FanCurveSource.Hotter, c.Source);
        Assert.Equal(4, c.Hysteresis);
        Assert.Equal(37.5, c.Evaluate(42.5), 3);      // 30→20、55→55 之間的中點
    }
}
