using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// AI 請求路徑的端到端測試：用一個假的 <see cref="HttpMessageHandler"/> 取代網路，
/// 驗證重試政策、串流解析、用量解析、失敗訊息，以及「重試上一則」的狀態機。
///
/// <para>
/// 先前這些全部只有「純函式有測試、接線沒有」——429 會不會真的重試、串流的用量讀不讀得到、
/// 重試時會不會多出一則使用者氣泡，都要真的連一次端點才知道。假 handler 把這些變成可重跑的。
/// </para>
/// </summary>
[Collection(WpfCollection.Name)]
public class AiNetworkPathTests
{
    /// <summary>照順序回放預設回應；記下每次請求的網址與 body（重試時用來確認沒有多送東西）。</summary>
    private sealed class FakeHandler(params Func<HttpResponseMessage>[] steps) : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _steps = new(steps);

        public List<string> Calls { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Calls.Add(request.RequestUri + "\n" + body);
            if (_steps.Count == 0) throw new InvalidOperationException("FakeHandler 已經沒有下一個回應");
            return _steps.Dequeue()();
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK)
        => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static Func<HttpResponseMessage> Answer(string text, int prompt = 12, int completion = 5)
        => () => Json("{\"choices\":[{\"message\":{\"content\":\"" + text + "\"}}],"
                      + "\"usage\":{\"prompt_tokens\":" + prompt + ",\"completion_tokens\":" + completion
                      + ",\"total_tokens\":" + (prompt + completion) + "}}");

    private static Func<HttpResponseMessage> Status(HttpStatusCode code, string body = "{\"error\":\"boom\"}")
        => () => Json(body, code);

    private static AiService Make(FakeHandler handler, out SettingsService settings, out string folder)
    {
        settings = new SettingsService();
        folder = Path.Combine(Path.GetTempPath(), "XinSpectNet_" + Guid.NewGuid().ToString("N"));
        settings.AiProvider = (int)AiProvider.OpenAiCompatible;
        settings.AiBaseUrl = "http://127.0.0.1:1/v1";
        settings.AiModel = "test-model";
        settings.AiStreaming = false;
        settings.AiKeepHistory = false;
        settings.AiHistoryTurns = 12;
        var ai = new AiService(settings, folder) { HandlerOverride = handler };
        return ai;
    }

    private static void Cleanup(SettingsService settings, string folder, AiProvider provider, string url, string model)
    {
        settings.AiProvider = (int)provider;
        settings.AiBaseUrl = url;
        settings.AiModel = model;
        try { Directory.Delete(folder, true); } catch { }
    }

    [Fact]
    public async Task 授權失敗要如實回報並提供重試()
    {
        var handler = new FakeHandler(Status(HttpStatusCode.Unauthorized, "{\"error\":\"invalid key\"}"));
        var ai = Make(handler, out var settings, out string folder);
        var (p, u, m) = (settings.AiProviderEnum, settings.AiBaseUrl, settings.AiModel);
        try
        {
            await ai.SendAsync("你好");

            Assert.Single(handler.Calls);                     // 401 不是暫時性錯誤，不重試
            Assert.Single(ai.Messages.Where(x => x.IsUser));
            var reply = ai.Messages[^1];
            Assert.StartsWith("⚠ 呼叫失敗", reply.Text);
            Assert.Contains("401", reply.Text);
            Assert.True(ai.CanRetry, "失敗後應該提供重試");
        }
        finally { Cleanup(settings, folder, p, u, m); }
    }

    [Fact]
    public async Task 暫時性錯誤會重試到成功且最後不提供重試鍵()
    {
        var handler = new FakeHandler(
            Status(HttpStatusCode.ServiceUnavailable),
            Status(HttpStatusCode.BadGateway),
            Answer("收到了"));
        var ai = Make(handler, out var settings, out string folder);
        var (p, u, m) = (settings.AiProviderEnum, settings.AiBaseUrl, settings.AiModel);
        try
        {
            await ai.SendAsync("你好");

            Assert.Equal(3, handler.Calls.Count);              // 第一次＋兩次重試
            Assert.Equal("收到了", ai.Messages[^1].Text);
            Assert.False(ai.CanRetry);                         // 正常回答旁邊不給重試
        }
        finally { Cleanup(settings, folder, p, u, m); }
    }

    [Fact]
    public async Task 重試用完就停止並保留錯誤敘述()
    {
        var handler = new FakeHandler(
            Status(HttpStatusCode.TooManyRequests),
            Status(HttpStatusCode.TooManyRequests),
            Status(HttpStatusCode.TooManyRequests));
        var ai = Make(handler, out var settings, out string folder);
        var (p, u, m) = (settings.AiProviderEnum, settings.AiBaseUrl, settings.AiModel);
        try
        {
            await ai.SendAsync("你好");

            Assert.Equal(AiRetryPolicy.MaxAttempts, handler.Calls.Count);
            Assert.Contains("429", ai.Messages[^1].Text);
            Assert.True(ai.CanRetry);
        }
        finally { Cleanup(settings, folder, p, u, m); }
    }

    [Fact]
    public async Task 回應裡的用量會被記下來()
    {
        var handler = new FakeHandler(Answer("好", prompt: 100, completion: 20));
        var ai = Make(handler, out var settings, out string folder);
        var (p, u, m) = (settings.AiProviderEnum, settings.AiBaseUrl, settings.AiModel);
        try
        {
            await ai.SendAsync("你好");

            Assert.Equal(1, ai.Usage.Requests);
            Assert.Equal(100, ai.Usage.PromptTokens);
            Assert.Equal(20, ai.Usage.CompletionTokens);
            Assert.Equal(120, ai.Usage.TotalTokens);
        }
        finally { Cleanup(settings, folder, p, u, m); }
    }

    [Fact]
    public async Task 串流會逐段拼接並讀到最後一塊的用量()
    {
        string sse =
            "data: {\"choices\":[{\"delta\":{\"content\":\"溫度\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"正常\"}}]}\n\n" +
            "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":30,\"completion_tokens\":7,\"total_tokens\":37}}\n\n" +
            "data: [DONE]\n\n";

        var handler = new FakeHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
        });
        var ai = Make(handler, out var settings, out string folder);
        var (p, u, m) = (settings.AiProviderEnum, settings.AiBaseUrl, settings.AiModel);
        try
        {
            settings.AiStreaming = true;
            await ai.SendAsync("你好");

            Assert.Equal("溫度正常", ai.Messages[^1].Text);
            // 用量在「choices 是空陣列」的最後一塊——先前那個位置會被 continue 跳過
            Assert.Equal(37, ai.Usage.TotalTokens);
        }
        finally { Cleanup(settings, folder, p, u, m); }
    }

    [Fact]
    public async Task 重試上一則不會多出使用者氣泡而且沿用同一份提問()
    {
        var handler = new FakeHandler(Status(HttpStatusCode.Unauthorized), Answer("第二次成功"));
        var ai = Make(handler, out var settings, out string folder);
        var (p, u, m) = (settings.AiProviderEnum, settings.AiBaseUrl, settings.AiModel);
        try
        {
            await ai.SendAsync("ping-42");
            Assert.True(ai.CanRetry);

            await ai.RetryLastAsync();

            Assert.Single(ai.Messages.Where(x => x.IsUser));      // 使用者氣泡只有一個
            Assert.Single(ai.Messages.Where(x => !x.IsUser && !x.IsTool));   // 失敗那一則已被換掉
            Assert.Equal("第二次成功", ai.Messages[^1].Text);
            Assert.False(ai.CanRetry);

            Assert.Equal(2, handler.Calls.Count);
            Assert.Contains("ping-42", handler.Calls[1]);   // 第二次送出的仍是同一則提問（JSON 會把中文轉義，所以用 ASCII）
        }
        finally { Cleanup(settings, folder, p, u, m); }
    }

    /// <summary>
    /// 共用額度自 2026-10-26 起全面開放：自由對話（不是只有「一鍵評價」）要真的送出請求、
    /// 真的拿到回答。先前這條路會在本地直接停住並貼一段「只開放一鍵評價」的說明——
    /// 使用者看起來就是「AI 壞了」。這裡用假 handler 釘住行為，不只看原始碼怎麼寫。
    /// </summary>
    [Fact]
    public async Task 共用額度可以自由對話()
    {
        var handler = new FakeHandler(Answer("共用額度也能對話"));
        var ai = Make(handler, out var settings, out string folder);
        var (p, u, m) = (settings.AiProviderEnum, settings.AiBaseUrl, settings.AiModel);
        try
        {
            settings.AiProvider = (int)AiProvider.SharedFree;
            await ai.SendAsync("你好");

            Assert.Single(handler.Calls);
            Assert.Contains(SharedAiEndpoint.BaseUrl.TrimEnd('/') + "/chat/completions", handler.Calls[0]);
            Assert.Contains(SharedAiEndpoint.Model, handler.Calls[0]);   // 模型代號由中轉決定
            Assert.Equal("共用額度也能對話", ai.Messages[^1].Text);
            Assert.DoesNotContain("只開放", ai.Messages[^1].Text);
        }
        finally { Cleanup(settings, folder, p, u, m); }
    }

    [Fact]
    public async Task 正常回答時不提供重試且狀態判定不接受空字串()
    {
        var handler = new FakeHandler(Answer("好"));
        var ai = Make(handler, out var settings, out string folder);
        var (p, u, m) = (settings.AiProviderEnum, settings.AiBaseUrl, settings.AiModel);
        try
        {
            await ai.SendAsync("你好");
            Assert.False(ai.CanRetry);
        }
        finally { Cleanup(settings, folder, p, u, m); }

        Assert.True(AiService.IsUnusableReply(""));
        Assert.True(AiService.IsUnusableReply(AiService.Placeholder));
        Assert.True(AiService.IsUnusableReply("⚠ 回覆中斷：HTTP 500"));
        Assert.True(AiService.IsUnusableReply("（已停止，這次沒有取得任何回覆）"));
        Assert.False(AiService.IsUnusableReply("這是一段正常的回答"));
    }
}
