using System.Text.RegularExpressions;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 說明文字、更新紀錄與工具提示<b>全部是純文字</b>，所以裡面不能有標記語法。
///
/// <para>
/// 這條規則的來歷：<c>HelpDot</c> 的提示、關於頁的更新紀錄、工具箱的滑鼠提示，畫的都是
/// <c>TextBlock.Text</c>。往裡面塞 <c>&lt;b&gt;…&lt;/b&gt;</c> 或 Markdown 的 <c>**粗體**</c>
/// 不會變粗，只會把那幾個字元原樣顯示出來——寫的時候看起來像在強調，使用者看到的是
/// 「這是<b>推估</b>」這種夾著尖角括號的句子。1.9.0 之前就有四處 <c>**…**</c> 這樣露在提示裡。
/// </para>
/// <para>
/// 要強調就用中文的引號（「」）或破折號，那在純文字裡看得出來。
/// </para>
/// </summary>
public class PlainTextCatalogTests
{
    /// <summary>會被原樣顯示出來的標記：HTML 標籤、HTML 實體、Markdown 粗體／斜體標記。</summary>
    private static readonly Regex Markup = new(
        @"</?[a-zA-Z][a-zA-Z0-9]*\s*/?>|&(?:lt|gt|amp|quot|apos|#\d+);|\*\*|__",
        RegexOptions.Compiled);

    private static void Check(string where, string? text, List<string> bad)
    {
        if (string.IsNullOrEmpty(text)) return;
        var m = Markup.Match(text);
        if (m.Success) bad.Add($"{where}：出現「{m.Value}」——這裡是純文字，會照字面顯示。");
    }

    [Fact]
    public void 說明表的文字不含標記語法()
    {
        var bad = new List<string>();
        foreach ((string key, HelpEntry e) in HelpCatalog.All)
        {
            Check($"{key}.Title", e.Title, bad);
            Check($"{key}.What", e.What, bad);
            Check($"{key}.Does", e.Does, bad);
            Check($"{key}.Safety", e.Safety, bad);
        }
        Assert.True(bad.Count == 0, string.Join("\n", bad));
    }

    [Fact]
    public void 更新紀錄的文字不含標記語法()
    {
        var bad = new List<string>();
        foreach (var e in ChangelogCatalog.Entries)
        {
            Check($"{e.Version}.Title", e.Title, bad);
            for (int i = 0; i < e.Items.Count; i++) Check($"{e.Version} 第 {i + 1} 項", e.Items[i], bad);
        }
        Assert.True(bad.Count == 0, string.Join("\n", bad));
    }

    [Fact]
    public void 工具箱的說明與危險提示不含標記語法()
    {
        var bad = new List<string>();
        foreach (var t in new ToolboxService().Tools)
        {
            Check($"{t.Name}.Description", t.Description, bad);
            Check($"{t.Name}.RiskNote", t.RiskNote, bad);
            Check($"{t.Name}.NativeNote", t.NativeNote, bad);
            Check($"{t.Name}.Tip", t.Tip, bad);
        }
        Assert.True(bad.Count == 0, string.Join("\n", bad));
    }

    [Fact]
    public void 體質評分產出的每一段文字都不含標記語法()
    {
        var bad = new List<string>();
        var input = new SiliconInput
        {
            Uarch = MicroarchProfile.Identify(6, 0x55),
            Points =
            [
                new VfPoint(1, 4.4, 1.0255, 62, 180, 30),
                new VfPoint(2, 4.3, 1.0185, 64, 180, 30),
                new VfPoint(4, 4.2, 1.0070, 66, 180, 30),
                new VfPoint(8, 4.0, 0.9915, 68, 180, 30),
                new VfPoint(18, 3.6, 0.9545, 70, 180, 30),
            ],
            IdlePowerW = 30, IdleTempC = 40, MaxTempC = 70, TempDriftC = 30,
            VoltFromMsr = false, ManualVoltage = true, ManualVoltageNote = "測試用的手動電壓說明。",
            Aborted = true, AbortReason = "測試用的中止原因。",
            StockAllCoreGhz = 3.6, BaseClockMhz = 100,
            VoltSource = "MSR 0x198", FreqSource = "MSR 0xE7／0xE8",
            PowerSource = "MSR 0x611", TempSource = "MSR 0x1B1",
        };
        var a = SiliconQuality.Evaluate(input);

        Check("Grade", a.Grade, bad);
        Check("Summary", a.Summary, bad);
        Check("MethodText", a.MethodText, bad);
        Check("PercentileText", a.PercentileText, bad);
        Check("ConfidenceText", a.ConfidenceText, bad);
        Check("NoPercentileReason", a.NoPercentileReason, bad);
        Check("CaveatText", a.CaveatText, bad);
        foreach (var m in a.Metrics)
        {
            Check($"Metric[{m.Name}].Name", m.Name, bad);
            Check($"Metric[{m.Name}].Value", m.Value, bad);
            Check($"Metric[{m.Name}].Note", m.Note, bad);
        }
        Assert.True(bad.Count == 0, string.Join("\n", bad));

        // 自我驗證：這一趟真的有走到「有內容」的分支，否則上面等於什麼都沒檢查
        Assert.True(a.Ok);
        Assert.NotEmpty(a.Metrics);
        Assert.NotEqual("", a.CaveatText);
    }

    /// <summary>自我驗證：這條規則的偵測器真的認得出標記，也不會誤殺正常的中文標點。</summary>
    [Fact]
    public void 偵測器抓得到標記且不誤殺中文標點()
    {
        var bad = new List<string>();
        Check("t", "這是<b>粗體</b>", bad);
        Check("t", "這是 **粗體**", bad);
        Check("t", "小於 &lt; 大於", bad);
        Assert.Equal(3, bad.Count);

        bad.Clear();
        Check("t", "這是「強調」——而且含有 < 與 > 這兩個裸符號，以及 V/F、0x611、100% 這些寫法。", bad);
        Check("t", @"路徑 \\.\PhysicalDriveN 與公式 P ＝ C·V²·f 都不算標記。", bad);
        Assert.Empty(bad);
    }
}
