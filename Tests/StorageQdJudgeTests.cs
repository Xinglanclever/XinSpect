using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// 儲存佇列深度掃描的判讀（純函式）。核心要釘住兩件事：
/// ①同一顆 SSD 在 QD1 與 QD32 的 IOPS 可以差一個數量級，只報一個數字等於沒說；
/// ②延遲隨 QD 上升是排隊的必然結果（QD ＝ IOPS × 延遲），不是故障——
/// 該注意的是 IOPS 有沒有明顯趨緩。
/// </summary>
public class StorageQdJudgeTests
{
    private static StorageQdPoint P(int qd, double iops, double latUs, int block = 4096)
        => new(block, qd, iops, iops * 0.9, latUs, latUs * 1.1);

    private static StorageQdScan Scan(params StorageQdPoint[] pts)
        => new(pts.OrderBy(p => p.QueueDepth).ToList());

    // ── 分類：已知答案 ────────────────────────────────────────────────────

    [Fact]
    public void 每級都明顯上升_判為仍有餘裕()
    {
        // QD1 10k → QD8 60k → QD32 120k，每級都超過 10%
        var v = StorageQdJudge.Judge(Scan(P(1, 10_000, 100), P(8, 60_000, 133), P(32, 120_000, 267)));

        Assert.Equal(StorageQdJudge.QdKind.ScalesWithDepth, v.Kind);
        Assert.Equal(0, v.SaturationQd);
        Assert.False(v.Access);
        Assert.Contains("還有餘裕", v.Headline);
    }

    [Fact]
    public void 某級之後不再上升_判為飽和並指出QD()
    {
        // QD8 → QD32 只成長 5%（低於 10% 門檻）
        var v = StorageQdJudge.Judge(Scan(P(1, 10_000, 100), P(8, 100_000, 80), P(32, 105_000, 305)));

        Assert.Equal(StorageQdJudge.QdKind.Saturates, v.Kind);
        Assert.Equal(32, v.SaturationQd);
        Assert.Contains("不再明顯上升", v.Headline);
        Assert.Contains("實用上限", v.Evidence);
        // 必須說明延遲上升是必然的，否則會被當成故障
        Assert.Contains("排隊的必然結果", v.Evidence);
    }

    [Fact]
    public void QD加深IOPS反而下降_判為回歸且值得查()
    {
        // SLC 快取用盡／過熱降速的形狀
        var v = StorageQdJudge.Judge(Scan(P(1, 10_000, 100), P(8, 200_000, 40), P(32, 120_000, 267)));

        Assert.Equal(StorageQdJudge.QdKind.Regresses, v.Kind);
        Assert.True(v.Access);
        Assert.Contains("反而下降", v.Headline);
        Assert.Contains("SLC 快取", v.Evidence);
        Assert.Contains("不推論原因", v.Evidence);
    }

    [Fact]
    public void 只有一點_判為單點並說明看不出趨勢()
    {
        var v = StorageQdJudge.Judge(Scan(P(16, 50_000, 320)));

        Assert.Equal(StorageQdJudge.QdKind.SinglePoint, v.Kind);
        Assert.Contains("只有 QD16 一點", v.Headline);
        Assert.Contains("沒有第二點", v.Evidence);
        Assert.False(v.Access);
    }

    [Fact]
    public void 沒有有效樣本_判為未知而不是零()
    {
        var v = StorageQdJudge.Judge(Scan(P(1, 0, 0)));
        Assert.Equal(StorageQdJudge.QdKind.Unknown, v.Kind);
        Assert.Contains("沒有可用的量測點", v.Headline);
    }

    [Fact]
    public void 判讀順序_先判回歸再判飽和()
    {
        // 曲線先升後降：既有回歸也有平台，回歸優先講（那是更嚴重的訊號）
        var v = StorageQdJudge.Judge(Scan(P(1, 10_000, 100), P(8, 150_000, 53), P(16, 151_000, 106), P(32, 100_000, 320)));

        Assert.Equal(StorageQdJudge.QdKind.Regresses, v.Kind);
    }

    // ── 邊界：門檻附近不得誤判 ────────────────────────────────────────────

    [Fact]
    public void 成長率剛好超過門檻_不算飽和()
    {
        // 10% 門檻：成長 11% 應該算「仍在上升」
        var v = StorageQdJudge.Judge(Scan(P(1, 100_000, 10), P(16, 111_000, 144)));
        Assert.Equal(StorageQdJudge.QdKind.ScalesWithDepth, v.Kind);
    }

    [Fact]
    public void 成長率剛好低於門檻_算飽和()
    {
        // 成長 9% 應該算「已飽和」
        var v = StorageQdJudge.Judge(Scan(P(1, 100_000, 10), P(16, 109_000, 147)));
        Assert.Equal(StorageQdJudge.QdKind.Saturates, v.Kind);
        Assert.Equal(16, v.SaturationQd);
    }

    [Fact]
    public void 樣本未排序_判讀要先自行排序()
    {
        // 呼叫端順序不該影響結果
        var v = StorageQdJudge.Judge(new StorageQdScan([P(32, 120_000, 267), P(1, 10_000, 100), P(8, 60_000, 133)]));
        Assert.Equal(StorageQdJudge.QdKind.ScalesWithDepth, v.Kind);
    }

    // ── 單執行緒與滿載的分野 ──────────────────────────────────────────────

    [Fact]
    public void 單執行緒差距_要講出倍數並說明體感()
    {
        string text = StorageQdJudge.SingleThreadGap([P(1, 10_000, 100), P(32, 200_000, 160)]);

        Assert.Contains("20 倍", text);
        Assert.Contains("QD1", text);
        Assert.Contains("體感", text);
    }

    [Fact]
    public void 沒有QD1_如實說無法比較()
    {
        string text = StorageQdJudge.SingleThreadGap([P(8, 100_000, 80), P(32, 200_000, 160)]);
        Assert.Contains("無法比較", text);
    }

    [Fact]
    public void 只有一點_如實說無法比較()
    {
        string text = StorageQdJudge.SingleThreadGap([P(1, 10_000, 100)]);
        Assert.Contains("只有一個佇列深度", text);
    }

    // ── 不得誤導 ──────────────────────────────────────────────────────────

    [Fact]
    public void 任何分類_Headline與Evidence都不得為空()
    {
        var scans = new[]
        {
            Scan(P(1, 10_000, 100), P(32, 120_000, 267)),
            Scan(P(1, 10_000, 100), P(8, 100_000, 80), P(32, 105_000, 305)),
            Scan(P(1, 10_000, 100), P(8, 200_000, 40), P(32, 120_000, 267)),
            Scan(P(16, 50_000, 320)),
            Scan(P(1, 0, 0)),
        };
        foreach (var s in scans)
        {
            var v = StorageQdJudge.Judge(s);
            Assert.False(string.IsNullOrWhiteSpace(v.Headline));
            Assert.False(string.IsNullOrWhiteSpace(v.Evidence));
        }
    }

    [Fact]
    public void 判讀不得宣稱裝置的規格上限()
    {
        // 量到的是本次條件的結果，不是出廠規格
        var v = StorageQdJudge.Judge(Scan(P(1, 10_000, 100), P(8, 100_000, 80), P(32, 105_000, 305)));

        Assert.Contains("本次條件", v.Evidence);
        Assert.DoesNotContain("規格上限", v.Headline);
    }
}
