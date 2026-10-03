using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// WP15 第三組：事件記錄摘要的彙整契約。來源是 Windows 事件記錄（System log，usermode）——
/// 通路以注入探測替代，彙整（7 天內 Error/Critical 計數、最常見來源×事件 ID）逐項釘值；
/// 匯出格式（canonical JSON）與去敏行為釘住。
/// </summary>
public class EventLogSummaryTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 3, 0, 0, 0, TimeSpan.Zero);

    private static XinSpect.EvtEntry E(int level, string provider, ushort id, DateTimeOffset time) =>
        new(level, provider, id, time);

    [Fact]
    public void 事件記錄_7天內錯誤計數與最常見來源逐項釘值()
    {
        var entries = new List<XinSpect.EvtEntry>
        {
            E(2, "Kernel-Power", 41, At.AddDays(-1)),
            E(2, "Kernel-Power", 41, At.AddDays(-2)),
            E(1, "BugCheck", 1001, At.AddDays(-3)),
            E(3, "Warning-Provider", 7, At.AddDays(-1)),     // 警告不算錯誤
            E(2, "Old-Provider", 5, At.AddDays(-20)),        // 7 天外
            E(4, "Info", 1, At.AddDays(-1)),                 // 資訊不算
        };

        var facts = XinSpect.EventLogSummaryService.Collect(At, probe: () => entries);

        Assert.All(facts, f => Assert.Equal("系統與軟體", f.Category));
        var errors = Assert.Single(facts, f => f.Key == "evt.system.7d");
        Assert.Equal(3u, errors.NumericValue);                        // 嚴重(1)+錯誤(2) 且 7 天內
        Assert.Contains("Kernel-Power 41×2", errors.Value);
        Assert.Contains("BugCheck 1001×1", errors.Value);
    }

    [Fact]
    public void 事件記錄_零錯誤與讀不到分得清楚()
    {
        var clean = XinSpect.EventLogSummaryService.Collect(At, probe: () => []);
        var zero = Assert.Single(clean, f => f.Key == "evt.system.7d");
        Assert.Equal(FactAvailability.Present, zero.Availability);
        Assert.Equal(0u, zero.NumericValue);

        var fail = XinSpect.EventLogSummaryService.Collect(At, probe: () => null);
        Assert.All(fail, f => Assert.Equal(FactAvailability.ReadError, f.Availability));
    }

    [Fact]
    public void 事件記錄_匯出JSON含機器內容與筆數()
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"xinsp-evt-{Guid.NewGuid():N}.json");
        try
        {
            var entries = new List<XinSpect.EvtEntry>
            {
                E(2, "Kernel-Power", 41, At),
                E(1, "BugCheck", 1001, At),
            };
            int written = XinSpect.EventLogSummaryService.ExportJson(path, entries);
            Assert.Equal(2, written);
            string json = System.IO.File.ReadAllText(path);
            Assert.Contains("Kernel-Power", json);
            Assert.Contains("\"level\": 2", json);                    // canonical camelCase
            Assert.DoesNotContain("recordId", json);                  // 匯出不夾探測沒給的欄位
        }
        finally { System.IO.File.Delete(path); }
    }
}
