using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// AI 對話匯出（Markdown）。
///
/// <para>
/// 匯出的價值不在排版，而在<b>標頭把「這份檔案怎麼來的」寫清楚</b>：什麼時候、用哪個模型、
/// 有沒有遮蔽、遮蔽了幾處、以及哪些內容根本不在檔案裡（圖片）。貼給別人看時，
/// 讀的人不必猜「裡面有沒有他的序號」。
/// </para>
/// </summary>
public class AiTranscriptTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 14, 30, 0, TimeSpan.FromHours(8));

    private static List<AiMessage> Sample() =>
    [
        new AiMessage { IsUser = true, Text = "我的序號是 SN-ABC12345，這台溫度正常嗎" },
        new AiMessage { IsUser = false, IsTool = true, Text = "🔍 temperatures\n    結果：CPU 65 °C" },
        new AiMessage { IsUser = false, Text = "65 °C 在滿載下是正常的。" },
    ];

    [Fact]
    public void 標頭要寫明時間模型供應商與遮蔽狀態()
    {
        string md = AiTranscript.Build(Sample(), Now, "qwen2.5", "本機 Ollama", redact: true);

        Assert.StartsWith("# 曦覽 XinSpect ・ AI 對話紀錄", md, StringComparison.Ordinal);
        Assert.Contains("2026-10-26 14:30:00", md);
        Assert.Contains("qwen2.5", md);
        Assert.Contains("本機 Ollama", md);
        Assert.Contains("**遮蔽**：已開啟", md);
        Assert.Contains("序號", md);                 // 遮蔽了哪一類要寫出來
    }

    [Fact]
    public void 遮蔽開啟時序號不會出現在檔案裡()
    {
        string md = AiTranscript.Build(Sample(), Now, "m", "p", redact: true);
        Assert.DoesNotContain("SN-ABC12345", md);
        Assert.Contains("（已遮蔽：序號）", md);
        Assert.Contains("65 °C 在滿載下是正常的。", md);   // 判斷用的讀值不能動
    }

    [Fact]
    public void 遮蔽關閉時要明說檔案可能有原文()
    {
        string md = AiTranscript.Build(Sample(), Now, "m", "p", redact: false);
        Assert.Contains("**遮蔽**：已關閉", md);
        Assert.Contains("SN-ABC12345", md);
        Assert.Contains("可能含有", md);   // 只確認有警示語氣，不鎖死整句
    }

    [Fact]
    public void 工具查詢列變成引用塊()
    {
        string md = AiTranscript.Build(Sample(), Now, "m", "p", redact: false);
        Assert.Contains("> 🔍 temperatures", md);
        Assert.Contains(">     結果：CPU 65 °C", md);
        Assert.DoesNotContain("## AI\n\n🔍", md);      // 工具列不該被當成 AI 的一段話
    }

    [Fact]
    public void 角色標題與順序要對得上()
    {
        string md = AiTranscript.Build(Sample(), Now, "m", "p", redact: false);
        int user = md.IndexOf("## 你", StringComparison.Ordinal);
        int ai = md.IndexOf("## AI", StringComparison.Ordinal);
        Assert.True(user > 0 && ai > user, "先使用者、後 AI");
    }

    [Fact]
    public void 空對話要說沒有內容而不是空白檔案()
    {
        string md = AiTranscript.Build([], Now, "m", "p", redact: true);
        Assert.Contains("（沒有對話內容）", md);
        Assert.Contains("**遮蔽**：已開啟（共 0 處", md);
    }

    [Fact]
    public void 表格儲存格裡的直線與換行不得弄歪表格()
    {
        string md = AiTranscript.Build([], Now, "model|with|pipes", "供應商\n第二行", redact: true);
        foreach (string line in md.Split('\n'))
            if (line.StartsWith("| 模型", StringComparison.Ordinal) || line.StartsWith("| 供應商", StringComparison.Ordinal))
                Assert.Equal(3, line.Count(c => c == '|'));   // 只有頭尾與中間各一個
    }

    [Fact]
    public void 有回報用量時要寫進標頭()
    {
        string md = AiTranscript.Build([], Now, "m", "p", redact: true, sessionUsage: new AiUsage(100, 20, 120));
        Assert.Contains("本次工作階段用量", md);
        Assert.Contains("120", md);
    }

    [Fact]
    public void 沒回報用量時不留一個看起來像零的欄位()
    {
        string md = AiTranscript.Build([], Now, "m", "p", redact: true, sessionUsage: AiUsage.None);
        Assert.DoesNotContain("本次工作階段用量", md);
    }

    [Fact]
    public void 建議檔名帶時間戳且沒有非法字元()
    {
        string name = AiTranscript.SuggestFileName(Now);
        Assert.StartsWith("xinspect-ai-20261026-1430", name);
        Assert.EndsWith(".md", name);
        foreach (char c in System.IO.Path.GetInvalidFileNameChars())
            Assert.DoesNotContain(c, name);
    }
}
