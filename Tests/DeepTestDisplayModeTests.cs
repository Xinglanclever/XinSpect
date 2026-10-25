using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// U10-TS-0112／U10-TS-0108 顯示模式清單與刷新率核對的契約：
/// 模式清單判讀、零模式不冒充、刷新一致性三態（一致／不一致標 Unknown／讀不到標 ReadError）。
/// </summary>
public class DeepTestDisplayModeTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 25, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void 模式清單_計數與刷新率集合()
    {
        var device = new DisplayModeListService.DisplayDeviceModes("DISPLAY1", "測試螢幕",
        [
            new(1920, 1080, 32, 60), new(1920, 1080, 32, 144), new(1920, 1080, 32, 60),
            new(1280, 720, 32, 60),
        ]);
        var facts = DisplayModeListService.Collect(At, () => [device]);
        var f = Assert.Single(facts);
        Assert.StartsWith("ts.disp.modes.cycle", f.Key);
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("3 個離散模式", f.Value);
        Assert.Contains("2 種解析度", f.Value);
        Assert.Contains("60/144", f.Value);
        Assert.Null(f.NumericValue); // 模式清單無單一數字語義
    }

    [Fact]
    public void 模式清單_零模式是Unknown不是0個()
    {
        var device = new DisplayModeListService.DisplayDeviceModes("DISPLAY1", "測試螢幕", []);
        var (value, availability, reason) = DisplayModeListService.Describe(device);
        Assert.Equal(FactAvailability.Unknown, availability);
        Assert.Equal("", value);
        Assert.Contains("不以 0 冒充", reason);
    }

    [Fact]
    public void 模式清單_列舉不到裝置與讀取失敗()
    {
        var empty = DisplayModeListService.Collect(At, () => []);
        var e = Assert.Single(empty);
        Assert.Equal(FactAvailability.NotSupported, e.Availability);

        var failed = DisplayModeListService.Collect(At,
            () => throw new InvalidOperationException("無桌面"));
        var x = Assert.Single(failed);
        Assert.Equal(FactAvailability.ReadError, x.Availability);
        Assert.Contains("無桌面", x.UnavailableReason);
    }

    [Fact]
    public void 刷新核對_一致_帶數值且無理由欄()
    {
        var report = new DisplayRefreshVerifyService.RefreshReport("DISPLAY1", 144, [60, 100, 144]);
        var facts = DisplayRefreshVerifyService.Collect(At, () => [report]);
        var f = Assert.Single(facts);
        Assert.Equal(DisplayRefreshVerifyService.Key, f.Key);
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Equal(144, f.NumericValue);
        Assert.Equal("Hz", f.Unit);
        Assert.Null(f.UnavailableReason);
        Assert.Contains("一致", f.Value);
    }

    [Fact]
    public void 刷新核對_不一致_Unknown不得當已確認()
    {
        var report = new DisplayRefreshVerifyService.RefreshReport("DISPLAY1", 165, [60, 144]);
        var facts = DisplayRefreshVerifyService.Collect(At, () => [report]);
        var f = Assert.Single(facts);
        Assert.Equal(FactAvailability.Unknown, f.Availability);
        Assert.Null(f.NumericValue); // 非 Present 不得帶數字
        Assert.Contains("165", f.Value);
        Assert.NotNull(f.UnavailableReason);
    }

    [Fact]
    public void 刷新核對_現值讀不到與宣告清單空()
    {
        var (value, availability, reason) = DisplayRefreshVerifyService.Judge(
            new DisplayRefreshVerifyService.RefreshReport("DISPLAY1", null, [60]));
        Assert.Equal(FactAvailability.ReadError, availability);
        Assert.Contains("ENUM_CURRENT_SETTINGS", reason);

        var (_, availability2, reason2) = DisplayRefreshVerifyService.Judge(
            new DisplayRefreshVerifyService.RefreshReport("DISPLAY1", 60, []));
        Assert.Equal(FactAvailability.Unknown, availability2);
        Assert.Contains("無從核對", reason2);
    }

    [Fact]
    public void 多裝置_逐裝置索引鍵()
    {
        var reports = new[]
        {
            new DisplayRefreshVerifyService.RefreshReport("DISPLAY1", 60, [60]),
            new DisplayRefreshVerifyService.RefreshReport("DISPLAY2", 60, [60]),
        };
        var facts = DisplayRefreshVerifyService.Collect(At, () => reports);
        Assert.Equal(2, facts.Count);
        Assert.Equal("ts.disp.refresh.verify.0", facts[0].Key);
        Assert.Equal("ts.disp.refresh.verify.1", facts[1].Key);
    }
}
