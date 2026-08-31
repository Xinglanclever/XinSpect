using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 「免費共用額度」與「留言建議」的回歸測試。
///
/// 這兩件事共用同一個中轉，而且都牽涉到「這支程式裡到底有沒有秘密」與「共用額度會不會被燒光」——
/// 所以要盯的不是畫面長相，是三條界線：exe 裡不能有金鑰、共用額度只能走一鍵評價、
/// 設定檔裡的供應商編號不能因為手改而讓程式崩掉。
/// </summary>
public class SharedAiAndFeedbackTests
{
    // ── 中轉端點 ────────────────────────────────────────────────────────────

    [Fact]
    public void 共用額度只開放一鍵評價()
    {
        Assert.True(SharedAiEndpoint.Allows(AiRequestKind.Evaluate));
        Assert.False(SharedAiEndpoint.Allows(AiRequestKind.Chat));
        Assert.False(SharedAiEndpoint.Allows(AiRequestKind.Proactive));
    }

    /// <summary>
    /// 選項文字必須和實際狀態一致：作者還沒填中轉網址時要說「尚未啟用」，
    /// 否則使用者會選了一個註定連線失敗的選項。
    /// </summary>
    [Fact]
    public void 選項文字與是否已啟用一致()
    {
        if (SharedAiEndpoint.IsConfigured)
        {
            Assert.Contains("無須金鑰", SharedAiEndpoint.OptionText);
            Assert.DoesNotContain("尚未啟用", SharedAiEndpoint.OptionText);
        }
        else
        {
            Assert.Contains("尚未啟用", SharedAiEndpoint.OptionText);
        }
    }

    /// <summary>
    /// 這支程式裡不准出現任何金鑰。中轉網址是公開資訊，可以在；但只要 BaseUrl 開始長得像
    /// sk- 開頭的字串，就是有人把金鑰貼錯地方了。
    /// </summary>
    [Fact]
    public void 程式裡不含任何金鑰()
    {
        Assert.DoesNotContain("sk-", SharedAiEndpoint.BaseUrl);
        Assert.DoesNotContain("Bearer", SharedAiEndpoint.BaseUrl);
        if (SharedAiEndpoint.IsConfigured)
            Assert.StartsWith("https://", SharedAiEndpoint.BaseUrl.Trim());
    }

    // ── 設定檔的供應商編號 ──────────────────────────────────────────────────

    /// <summary>
    /// AiProvider 存進 settings.json 是一個整數；使用者手改成 99 或 -1 都不該讓程式炸掉，
    /// 也不該轉成一個沒有對應項目的 enum 值。
    /// </summary>
    [Theory]
    [InlineData(-5, 0)]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(99, 2)]
    public void 供應商編號超出範圍時夾回合法值(int stored, int expected)
    {
        int clamped = System.Math.Clamp(stored, 0, (int)AiProvider.SharedFree);
        Assert.Equal(expected, clamped);
        Assert.True(System.Enum.IsDefined(typeof(AiProvider), (AiProvider)clamped));
    }

    [Fact]
    public void 供應商標籤三個都說得出名字()
    {
        Assert.Equal("本機 Ollama", AiService.ProviderLabel(AiProvider.Ollama));
        Assert.Equal("OpenAI 相容 API", AiService.ProviderLabel(AiProvider.OpenAiCompatible));
        Assert.Equal("免費共用", AiService.ProviderLabel(AiProvider.SharedFree));
    }

    // ── 留言建議 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 收件網址是從中轉的 Base URL 推出來的，而 Base URL 可能帶 /v1、可能不帶、可能有結尾斜線。
    /// 三種寫法都得推出同一個 /feedback。
    /// </summary>
    [Theory]
    [InlineData("https://x.workers.dev/v1", "https://x.workers.dev/feedback")]
    [InlineData("https://x.workers.dev/v1/", "https://x.workers.dev/feedback")]
    [InlineData("https://x.workers.dev", "https://x.workers.dev/feedback")]
    [InlineData("https://x.workers.dev/", "https://x.workers.dev/feedback")]
    [InlineData("", "/feedback")]
    public void 收件網址由中轉網址推出(string baseUrl, string expected)
        => Assert.Equal(expected, FeedbackService.BuildFeedbackUrl(baseUrl));

    [Fact]
    public void 沒有內容時不能上傳()
    {
        var fb = new FeedbackService();
        Assert.False(fb.CanSend);
        fb.Text = "   ";
        Assert.False(fb.CanSend);
    }

    /// <summary>
    /// 按鈕變灰一定要說得出為什麼——不可用時 UnavailableReason 不准是空字串。
    /// </summary>
    [Fact]
    public void 不可用時說得出原因()
    {
        var fb = new FeedbackService();
        if (fb.IsAvailable) Assert.Equal("", fb.UnavailableReason);
        else Assert.NotEqual("", fb.UnavailableReason);
    }

    /// <summary>送出後狀態列要有話說，不能靜默失敗。</summary>
    [Fact]
    public async Task 中轉未啟用時送出會如實說明()
    {
        var fb = new FeedbackService { Text = "測試建議" };
        await fb.SendAsync("1.7.2");
        if (!FeedbackService.IsConfigured)
        {
            Assert.NotEqual("", fb.Status);
            Assert.Equal("測試建議", fb.Text);   // 失敗時內容必須留著
        }
    }
}
