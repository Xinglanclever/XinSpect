using XinSpect;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 即時走勢緩衝的誠實性測試：「沒讀到」不得被當成「量到 0」。
/// 這是沒有溫度感測器或無獨立顯示卡的機器最容易看到假數字的地方（畫面上一條貼底的 0 °C 曲線）。
/// </summary>
public class MetricHistoryTests
{
    private static MetricHistory New(int cap = 5) => new(cap, "°C", null, "0.#");

    // ── 沒讀到 ──────────────────────────────────────────────────────────────

    [Fact]
    public void FreshBuffer_HasNoData()
    {
        var h = New();
        Assert.False(h.HasData);
        Assert.Equal("—", h.CurrentText);
        Assert.Equal("—", h.MinText);
        Assert.Equal("—", h.AvgText);
        Assert.Equal("—", h.MaxText);
    }

    [Fact]
    public void AllNullPushes_StillHaveNoData()
    {
        var h = New();
        for (int i = 0; i < 5; i++) h.Push(null);
        Assert.False(h.HasData);
        Assert.Equal("—", h.CurrentText);
        Assert.Equal("—", h.MaxText);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NonNumericPushes_CountAsNotRead(double bad)
    {
        var h = New();
        h.Push(bad);
        Assert.False(h.HasData);
        Assert.Equal("—", h.CurrentText);
    }

    // ── 量到 0 ──────────────────────────────────────────────────────────────

    [Fact]
    public void RealZero_IsAMeasurement()
    {
        var h = New();
        h.Push(0);
        Assert.True(h.HasData);
        Assert.Equal("0 °C", h.CurrentText);
        Assert.Equal(0, h.Max);
    }

    // ── 統計只算真實讀值 ────────────────────────────────────────────────────

    [Fact]
    public void Statistics_IgnoreMissingSamples()
    {
        var h = New();
        h.Push(60);
        h.Push(null);      // 這一拍讀取失敗
        h.Push(70);
        Assert.True(h.HasData);
        Assert.Equal(60, h.Min);    // 沒讀到的點不應把最小值拉到 0
        Assert.Equal(70, h.Max);
        Assert.Equal(65, h.Avg);    // 也不應稀釋平均
    }

    [Fact]
    public void CurrentText_ReportsTheLatestSampleOnly()
    {
        var h = New();
        h.Push(55);
        Assert.Equal("55 °C", h.CurrentText);
        h.Push(null);
        // 最新一拍沒讀到就說沒讀到；但先前量到的統計仍在
        Assert.Equal("—", h.CurrentText);
        Assert.Equal("55 °C", h.MaxText);
    }

    [Fact]
    public void Statistics_FollowTheRingBufferWindow()
    {
        var h = New(3);
        h.Push(10); h.Push(20); h.Push(30);
        Assert.Equal(10, h.Min);
        h.Push(40);                 // 10 被擠出視窗
        Assert.Equal(20, h.Min);
        Assert.Equal(40, h.Max);
        Assert.Equal(30, h.Avg);
    }

    [Fact]
    public void HasData_TurnsFalseAgainOnceRealSamplesLeaveTheWindow()
    {
        var h = New(2);
        h.Push(42);
        Assert.True(h.HasData);
        h.Push(null); h.Push(null);      // 真實讀值已被擠出
        Assert.False(h.HasData);
        Assert.Equal("—", h.MaxText);
    }

    // ── 快照 ────────────────────────────────────────────────────────────────

    [Fact]
    public void Snapshot_IsChronologicalAndFinite()
    {
        var h = New(4);
        h.Push(1); h.Push(null); h.Push(3);
        var s = h.Snapshot();
        Assert.Equal(3, s.Length);
        // 沒讀到的格子存 0（走勢圖的時間軸須等距，NaN 會讓幾何運算失效）
        Assert.Equal(new double[] { 1, 0, 3 }, s);
        Assert.All(s, v => Assert.True(double.IsFinite(v)));
    }

    [Fact]
    public void Push_RaisesUpdated()
    {
        var h = New();
        int n = 0;
        h.Updated += () => n++;
        h.Push(1);
        h.Push(null);
        Assert.Equal(2, n);
    }
}
