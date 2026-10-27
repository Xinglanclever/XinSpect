using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 客戶端與中轉之間的「同一條界線，兩份寫死的數字」對帳。
/// </summary>
/// <remarks>
/// <para>
/// 共用額度的限制自 2026-10-26 起全部搬到中轉那一側（<c>cloudflare/ai-proxy/worker.js</c>），
/// 客戶端只剩兩個常數與它對應：提示詞字元上限與建議長度上限。這種「兩邊各寫一個數字」的耦合
/// 沒有守門就一定漂移——而且漂移的方向很安靜：
/// </para>
/// <list type="bullet">
/// <item>中轉比較嚴 → 使用者把預算調高只會拿到一句 413，看不出是長度問題。</item>
/// <item>中轉比較鬆 → 本地先截斷了，中轉其實吃得下，白白丟掉內容。</item>
/// </list>
/// <para>
/// 所以這裡直接<b>讀那個 JS 檔</b>把數字抓出來，與 C# 常數逐值比對。不驗證 Worker 的邏輯
/// （那要在 Cloudflare 上跑），只驗「兩邊講的是同一個數字」——那正是會壞掉的那一項。
/// </para>
/// </remarks>
[Collection(WpfCollection.Name)]
public class RelayLimitParityTests
{
    private static string WorkerJs()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "XinSpect.csproj")))
                return File.ReadAllText(Path.Combine(dir.FullName, "cloudflare", "ai-proxy", "worker.js"));
            dir = dir.Parent;
        }
        throw new InvalidOperationException("從測試輸出目錄往上找不到 XinSpect.csproj——讀不到中轉原始碼");
    }

    private static int Limit(string name)
    {
        var m = Regex.Match(WorkerJs(), @"\b" + Regex.Escape(name) + @"\s*:\s*(\d+)");
        Assert.True(m.Success, $"worker.js 的 LIMITS 找不到 {name}——改名就要一起改這條守門");
        return int.Parse(m.Groups[1].Value);
    }

    [Fact]
    public void 中轉的提示詞上限要等於客戶端常數()
        => Assert.Equal(SharedAiEndpoint.PromptCharCap, Limit("maxPromptChars"));

    [Fact]
    public void 中轉的建議字數上限要等於客戶端常數()
        => Assert.Equal(FeedbackService.MaxLength, Limit("feedbackMaxChars"));

    /// <summary>
    /// 中轉原始碼的說明也要跟著改。這不是吹毛求疵：這支 Worker 會被部署出去，
    /// 而它的檔頭是下一個看這份程式碼的人唯一的說明——留著「只給一鍵評價用」，
    /// 等於告訴維護者這條路不該給自由對話走，然後就會有人把限制加回去。
    /// </summary>
    [Fact]
    public void 中轉原始碼不得再宣稱只給一鍵評價使用()
    {
        string js = WorkerJs();
        Assert.DoesNotContain("只給「一鍵評價」用", js);
        Assert.Contains("自由對話", js);
    }
}
