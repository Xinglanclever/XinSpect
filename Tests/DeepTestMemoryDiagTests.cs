using System.Diagnostics.Eventing.Reader;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// U10-TS-0051 記憶體診斷結果服務的契約：1101/1102 判讀、零事件不冒充、
/// 頻道不存在（NotSupported）與讀取失敗（ReadError）分離、六態不帶數字。
/// </summary>
public class DeepTestMemoryDiagTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 25, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void 判讀_偵測到錯誤的最近一次()
    {
        var events = new[]
        {
            new MemoryDiagResultsFactsService.MemDiagEvent(1101, At.AddDays(-2), "無錯誤"),
            new MemoryDiagResultsFactsService.MemDiagEvent(1102, At.AddDays(-1), "偵測到錯誤"),
        };
        var fact = MemoryDiagResultsFactsService.BuildFact(At, events);
        Assert.Equal(MemoryDiagResultsFactsService.Key, fact.Key);
        Assert.Equal(FactAvailability.Present, fact.Availability);
        Assert.Null(fact.NumericValue);
        Assert.Contains("1102", fact.Value);
        Assert.Contains("2 筆", fact.Value);
        Assert.Null(fact.UnavailableReason);
    }

    [Fact]
    public void 判讀_僅無錯誤與未知ID照實列()
    {
        var events = new[]
        {
            new MemoryDiagResultsFactsService.MemDiagEvent(1101, At.AddDays(-1), "無錯誤"),
            new MemoryDiagResultsFactsService.MemDiagEvent(9999, At, "不知名事件"),
        };
        var (value, availability, reason) = MemoryDiagResultsFactsService.Summarize(events);
        Assert.Equal(FactAvailability.Present, availability);
        Assert.Null(reason);
        Assert.Contains("9999×1", value);
        Assert.Contains("未偵測到錯誤", value);
    }

    [Fact]
    public void 零事件_不冒充跑過沒錯誤()
    {
        var fact = MemoryDiagResultsFactsService.BuildFact(At, []);
        Assert.Equal(FactAvailability.Present, fact.Availability);
        Assert.Contains("未留下任何", fact.Value);
        Assert.DoesNotContain("無錯誤", fact.Value);
    }

    [Fact]
    public void 頻道不存在_標NotSupported()
    {
        var facts = MemoryDiagResultsFactsService.Collect(At,
            _ => throw new EventLogNotFoundException());
        var f = Assert.Single(facts);
        Assert.Equal(FactAvailability.NotSupported, f.Availability);
        Assert.Null(f.NumericValue);
        Assert.Contains("頻道不存在", f.UnavailableReason);
    }

    [Fact]
    public void 讀取失敗_標ReadError帶原因()
    {
        var facts = MemoryDiagResultsFactsService.Collect(At,
            _ => throw new InvalidOperationException("頻道被政策關閉"));
        var f = Assert.Single(facts);
        Assert.Equal(FactAvailability.ReadError, f.Availability);
        Assert.Contains("政策關閉", f.UnavailableReason);
    }
}
