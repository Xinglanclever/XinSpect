using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// U10-TS-0146 鍵盤重複設定的契約：索引→文件值對照、篩選鍵接管註記、
/// 索引越界標 Unknown；不呼叫真 SystemParametersInfo。
/// </summary>
public class DeepTestKeyboardRepeatTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 25, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void 設定_速度與延遲對照_篩選鍵關閉()
    {
        var f = KeyboardRepeatService.BuildFact(At, new(31, 0, false, 0, 0));
        Assert.Equal(KeyboardRepeatService.Key, f.Key);
        Assert.Equal(FactAvailability.Present, f.Availability);
        Assert.Contains("250 ms", f.Value);          // 延遲索引 0
        Assert.Contains("索引 31", f.Value);          // 速度索引 31
        Assert.Contains("31→30 字符/秒", f.Value);
        Assert.Contains("篩選鍵關閉", f.Value);
        Assert.Contains("非實測值", f.Value);          // 不以設定值冒充實測
        Assert.Null(f.NumericValue);                  // 索引值不具單一數字語義，不帶 numericValue
        Assert.Null(f.UnavailableReason);
    }

    [Fact]
    public void 設定_篩選鍵開啟_註明接管()
    {
        var f = KeyboardRepeatService.BuildFact(At, new(15, 2, true, 500, 50));
        Assert.Contains("篩選鍵開啟", f.Value);
        Assert.Contains("500 ms", f.Value);
        Assert.Contains("750 ms", f.Value);           // 延遲索引 2
    }

    [Fact]
    public void 設定_索引越界_標Unknown()
    {
        var f = KeyboardRepeatService.BuildFact(At, new(99, 7, false, 0, 0));
        Assert.Equal(FactAvailability.Unknown, f.Availability);
        Assert.Null(f.NumericValue);
        Assert.Contains("越界", f.UnavailableReason);
    }

    [Fact]
    public void 收集_注入樣本()
    {
        var facts = KeyboardRepeatService.Collect(At, new(31, 0, false, 0, 0));
        Assert.Equal(FactAvailability.Present, Assert.Single(facts).Availability);
    }

    [Fact]
    public void 收集_真機路徑_六態其一不丟例外()
    {
        var facts = KeyboardRepeatService.Collect(At, null);
        Assert.Contains(facts, f => f.Availability is FactAvailability.Present
            or FactAvailability.NotSupported or FactAvailability.Unknown
            or FactAvailability.ReadError or FactAvailability.NotApplicable);
    }
}
