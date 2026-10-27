using System.Text.Json;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// AI 用量顯示：端點回報的 token 數。
///
/// <para>
/// 這裡的核心不是「顯示數字」，而是<b>不編數字</b>：本程式不估算 token（估算要假設 tokenizer，
/// 不同模型的分詞方式不同），所以只用端點在 <c>usage</c> 裡真的給的三個值；
/// 端點沒給就寫「端點沒有回報用量」，不印 0——0 看起來像「這次沒花錢」。
/// </para>
/// </summary>
public class AiUsageTests
{
    private static AiUsage Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return AiUsage.Parse(doc.RootElement);
    }

    [Fact]
    public void 完整的三個數字都照端點說的()
    {
        var u = Parse("""{"usage":{"prompt_tokens":1234,"completion_tokens":567,"total_tokens":1801}}""");
        Assert.Equal(1234, u.PromptTokens);
        Assert.Equal(567, u.CompletionTokens);
        Assert.Equal(1801, u.TotalTokens);
        Assert.True(u.Reported);
        Assert.Contains("輸入 1,234", u.Describe());
        Assert.Contains("輸出 567", u.Describe());
        Assert.Contains("合計 1,801", u.Describe());
    }

    [Fact]
    public void 沒有usage就是沒有回報而不是零()
    {
        var u = Parse("""{"choices":[{"message":{"content":"hi"}}]}""");
        Assert.False(u.Reported);
        Assert.Equal("端點沒有回報用量", u.Describe());
    }

    [Fact]
    public void usage為null不算錯誤()
    {
        var u = Parse("""{"usage":null,"choices":[]}""");
        Assert.False(u.Reported);
    }

    [Fact]
    public void 缺總量但兩個分項都在時才做加法()
    {
        var u = Parse("""{"usage":{"prompt_tokens":10,"completion_tokens":5}}""");
        Assert.Equal(15, u.TotalTokens);      // 加法是定義，不是估算

        var partial = Parse("""{"usage":{"completion_tokens":5}}""");
        Assert.Equal(0, partial.TotalTokens); // 只有一半的資料不得補出總量
        Assert.Equal(0, partial.PromptTokens);
    }

    [Fact]
    public void 欄位型別不對時當成沒有()
    {
        var u = Parse("""{"usage":{"prompt_tokens":"12","completion_tokens":null,"total_tokens":-3}}""");
        Assert.False(u.Reported);
    }

    // ── 累計 ──────────────────────────────────────────────────────────────

    [Fact]
    public void 累計只在真的回報時加數並分開記次數()
    {
        var c = new AiUsageCounter();
        Assert.Equal("", c.Summary);                       // 沒有請求就不佔版面

        c.Add(new AiUsage(100, 20, 120));
        c.Add(AiUsage.None);                                // 代理模式中的某一次沒有回報
        c.Add(new AiUsage(50, 10, 60));

        Assert.Equal(3, c.Requests);
        Assert.Equal(2, c.Reported);
        Assert.Equal(150, c.PromptTokens);
        Assert.Equal(30, c.CompletionTokens);
        Assert.Equal(180, c.TotalTokens);
        Assert.Contains("3 次請求", c.Summary);
        Assert.Contains("其中 2 次有回報", c.Summary);
        Assert.Contains("端點回報", c.Summary);
    }

    [Fact]
    public void 全部都沒回報時明說而不是顯示零()
    {
        var c = new AiUsageCounter();
        c.Add(AiUsage.None);
        Assert.Contains("端點沒有回報用量", c.Summary);
        Assert.DoesNotContain("輸入 0", c.Summary);
    }

    [Fact]
    public void 清空對話要把累計歸零()
    {
        var c = new AiUsageCounter();
        c.Add(new AiUsage(1, 2, 3));
        c.Reset();
        Assert.Equal(0, c.Requests);
        Assert.Equal(0, c.TotalTokens);
        Assert.Equal("", c.Summary);
    }

    [Fact]
    public void 摘要會通知畫面更新()
    {
        var c = new AiUsageCounter();
        var raised = new List<string?>();
        c.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        c.Add(new AiUsage(1, 1, 2));
        Assert.Contains(nameof(AiUsageCounter.Summary), raised);
    }
}
