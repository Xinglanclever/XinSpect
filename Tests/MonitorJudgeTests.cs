using System;
using System.Collections.Generic;
using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// 顯示器真偽判讀（純函式）。Windows 的顯示器清單混了三種東西：接了線的真實螢幕（有 EDID）、
/// 軟體虛擬螢幕（驅動合成的最小 EDID）、以及後面沒有實體裝置的預設監視器物件。
/// 「接了 1 台螢幕」與「接了 3 台但只有 1 台是真的」在這份資料上很容易長得一樣。
/// </summary>
public class MonitorJudgeTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 8, 0, 0, 0, TimeSpan.Zero);

    private static MonitorSample Real(string name = @"DISPLAY\MTT1337\1&33320F0&0&UID256",
        string mfg = "MTT", string friendly = "VDD by MTT", int year = 2024, int week = 12)
        => new(name, mfg, friendly, year, week, true, 5, true);

    private static MonitorSample Synthetic(string name = @"DISPLAY\XXX9999\1&1&0&UID1")
        => new(name, "", "", 0, 0, false, int.MinValue, true);

    private static MonitorSample Placeholder(string name = "DesktopMonitor1")
        => new(name, "", "", 0, 0, false, int.MinValue, false);

    // ── 三種實例要分得開 ──────────────────────────────────────────────────

    [Fact]
    public void 有EDID識別與實體路徑_判為真實螢幕且計入台數()
    {
        var v = MonitorJudge.Judge(Real());

        Assert.Equal(MonitorJudge.MonitorKind.Physical, v.Kind);
        Assert.True(v.CountsAsDisplay);
        Assert.Contains("MTT", v.Headline);
        Assert.Contains("2024", v.Evidence);
    }

    [Fact]
    public void 有實體路徑但無EDID識別_判為可能是虛擬顯示驅動()
    {
        var v = MonitorJudge.Judge(Synthetic());

        Assert.Equal(MonitorJudge.MonitorKind.Synthetic, v.Kind);
        Assert.False(v.CountsAsDisplay);
        Assert.Contains("虛擬顯示驅動", v.Headline);
        Assert.Contains("EDID", v.Evidence);
        // 不得指名是哪一套軟體（本機就有三套虛擬顯示軟體，猜錯的代價高）
        Assert.DoesNotContain("MuMu", v.Evidence);
        Assert.DoesNotContain("GameViewer", v.Evidence);
    }

    [Fact]
    public void 沒有實體路徑_判為預設監視器物件且不計入()
    {
        // 本機實況：DesktopMonitor1／2／4 都是這種，沒有 PNPDeviceID
        var v = MonitorJudge.Judge(Placeholder());

        Assert.Equal(MonitorJudge.MonitorKind.Placeholder, v.Kind);
        Assert.False(v.CountsAsDisplay);
        Assert.Contains("沒有實體螢幕", v.Headline);
        Assert.Contains("不應計入", v.Evidence);
    }

    [Fact]
    public void 沒有實例名稱_判為未知()
    {
        var v = MonitorJudge.Judge(new MonitorSample("", "", "", 0, 0, false, int.MinValue, false));
        Assert.Equal(MonitorJudge.MonitorKind.Unknown, v.Kind);
    }

    [Fact]
    public void 只有名稱沒有廠商_仍算有EDID識別()
    {
        var v = MonitorJudge.Judge(new MonitorSample(@"DISPLAY\AAA1111\1&1", "", "Some Panel", 0, 0, true, 5, true));
        Assert.Equal(MonitorJudge.MonitorKind.Physical, v.Kind);
    }

    [Fact]
    public void 只有年份沒有名稱_也算有EDID識別()
    {
        var v = MonitorJudge.Judge(new MonitorSample(@"DISPLAY\AAA1111\1&1", "", "", 2021, 0, true, 5, true));
        Assert.Equal(MonitorJudge.MonitorKind.Physical, v.Kind);
        Assert.Contains("2021", v.Evidence);
    }

    // ── 摘要 ──────────────────────────────────────────────────────────────

    [Fact]
    public void 摘要_虛擬與預設物件分開算並說明只有EDID那幾台是真的()
    {
        // 本機實況的形狀：1 台有 EDID（VDD by MTT）＋3 個預設監視器物件
        var summary = MonitorJudge.Summarize(
        [
            Real(),
            Placeholder("DesktopMonitor1"),
            Placeholder("DesktopMonitor2"),
            Placeholder("DesktopMonitor4"),
        ]);

        Assert.Contains("1 台真實螢幕", summary);
        Assert.Contains("3 個作業系統預設監視器物件", summary);
        Assert.Contains("只有帶 EDID 的那幾台", summary);
    }

    [Fact]
    public void 摘要_沒有實例要說讀不到而不是沒有接螢幕()
    {
        var summary = MonitorJudge.Summarize([]);
        Assert.Contains("沒有讀到", summary);
        Assert.Contains("不代表機器沒有接螢幕", summary);
    }

    [Fact]
    public void 摘要_全部都是真實螢幕時不得出現軟體提醒()
    {
        var summary = MonitorJudge.Summarize([Real(), Real(@"DISPLAY\DEL4098\1&2")]);
        Assert.Contains("2 台真實螢幕", summary);
        Assert.DoesNotContain("軟體", summary);
    }

    // ── EDID 字串轉換（WMI 的地雷） ───────────────────────────────────────

    [Fact]
    public void EDID字串_是ushort陣列不是字串()
    {
        // WMI 把 EDID 字串回成 ushort[]，每個字元一個元素
        // 直接 ToString() 會得到 "System.UInt16[]" —— 這是很容易踩的坑
        object value = new ushort[] { 'M', 'T', 'T', 0 };
        Assert.Equal("MTT", MonitorFactsService.EdidString(value));
    }

    [Fact]
    public void EDID字串_遇到補零就停並去掉空白()
    {
        Assert.Equal("VDD by MTT", MonitorFactsService.EdidString(
            new ushort[] { 'V', 'D', 'D', ' ', 'b', 'y', ' ', 'M', 'T', 'T', 0, 0, 0 }));
        Assert.Equal("", MonitorFactsService.EdidString(new ushort[] { 0 }));
    }

    [Fact]
    public void EDID字串_非陣列要回空字串而不是丟例外()
    {
        Assert.Equal("", MonitorFactsService.EdidString(null));
        Assert.Equal("", MonitorFactsService.EdidString("已經是字串"));
    }

    // ── 事實收集 ──────────────────────────────────────────────────────────

    [Fact]
    public void 事實收集_摘要為Derived且含連接介面()
    {
        var facts = MonitorFactsService.Collect(At, () => [Real(), Placeholder()]);

        var summary = Assert.Single(facts, f => f.Key == "monitor.summary");
        Assert.Equal(FactTrustLevel.Derived, summary.Trust);
        Assert.Contains("1 台真實螢幕", summary.Value);

        // 逐台的事實：名稱是實例名，Source 帶連接介面
        var first = Assert.Single(facts, f => f.Key.StartsWith("monitor.DISPLAY"));
        Assert.Contains("MTT", first.Value);            // 真實螢幕的判讀句含 EDID 廠商碼
        Assert.Contains("WMI", first.Source);           // 來源欄如實標示
    }

    [Fact]
    public void 事實收集_沒有實例為ReadError()
    {
        var facts = MonitorFactsService.Collect(At, () => []);
        Assert.Equal(FactAvailability.ReadError, Assert.Single(facts).Availability);
    }

    [Fact]
    public void 事實收集_擲回例外以ReadError回報()
    {
        var facts = MonitorFactsService.Collect(At, () => throw new InvalidOperationException("模擬失敗"));
        Assert.Equal(FactAvailability.ReadError, Assert.Single(facts).Availability);
    }
}
