using System.IO;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// AI 端點自檢：按一下就知道「端點通不通、多快、有哪些模型」。
///
/// <para>
/// 這一段刻意<b>只查模型清單、不送出對話</b>：使用者會擔心按一下測試就花錢，所以成功訊息必須
/// 寫明沒有送出對話。失敗訊息要附上耗時——「連不上」與「連很慢才失敗」是不同的問題。
/// </para>
/// </summary>
[Collection(WpfCollection.Name)]
public class AiProbeTests
{
    [Fact]
    public void 成功時要說延遲模型數並明說沒有送出對話()
    {
        string text = AiService.DescribeProbe(true, 123, 7, null);
        Assert.Contains("端點可用", text);
        Assert.Contains("123 ms", text);
        Assert.Contains("7 個模型", text);
        Assert.Contains("沒有送出對話", text);      // 使用者最在意的那一句
        Assert.Contains("不會產生 token 費用", text);
    }

    [Fact]
    public void 失敗時要附上原始錯誤與耗時()
    {
        string text = AiService.DescribeProbe(false, 4567, 0, "HTTP 401：invalid api key");
        Assert.Contains("端點不可用", text);
        Assert.Contains("4,567 ms", text);
        Assert.Contains("HTTP 401：invalid api key", text);   // 錯誤原樣轉述，不吞掉
    }

    [Fact]
    public async Task 共用額度不去查網路並說明為什麼()
    {
        var settings = new SettingsService();
        var origProvider = settings.AiProviderEnum;
        var origUrl = settings.AiBaseUrl;
        string folder = Path.Combine(Path.GetTempPath(), "XinSpectProbe_" + Guid.NewGuid().ToString("N"));
        try
        {
            settings.AiProvider = (int)AiProvider.SharedFree;
            // 故意給一個不存在的網域：這條路徑本來就不該連出去，連了就會失敗。
            settings.AiBaseUrl = "https://should-not-be-called.invalid/v1";

            var ai = new AiService(settings, folder);
            string text = await ai.TestConnectionAsync();

            Assert.Equal(AiService.SharedProbeText, text);
            Assert.Contains("共用額度", text);
            Assert.Equal(text, ai.StatusText);
        }
        finally
        {
            settings.AiProvider = (int)origProvider;
            settings.AiBaseUrl = origUrl;
            try { Directory.Delete(folder, true); } catch { }
        }
    }

    [Fact]
    public async Task 沒有端點時直接說沒填而不要去連()
    {
        var settings = new SettingsService();
        var origProvider = settings.AiProviderEnum;
        var origUrl = settings.AiBaseUrl;
        string folder = Path.Combine(Path.GetTempPath(), "XinSpectProbe_" + Guid.NewGuid().ToString("N"));
        try
        {
            settings.AiProvider = (int)AiProvider.OpenAiCompatible;
            settings.AiBaseUrl = "";

            var ai = new AiService(settings, folder);
            string text = await ai.TestConnectionAsync();

            Assert.Contains("尚未填入", text);
            Assert.Equal(text, ai.StatusText);
        }
        finally
        {
            settings.AiProvider = (int)origProvider;
            settings.AiBaseUrl = origUrl;
            try { Directory.Delete(folder, true); } catch { }
        }
    }
}
