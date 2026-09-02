using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 量測期間必須讓動畫停下來。
///
/// 這件事在 README 與 <c>Motion</c> 的註解裡都寫著理由：重繪要花 GPU 與封裝功耗，而那會進到
/// 量測結果裡。跑分那幾支早就這樣做了，但 1.8.1／1.8.2 新加的四個量測一開始全都漏了——
/// 其中「隱形停頓」最嚴重：它量的就是封裝 C-state 駐留，而動畫持續重繪本身就會讓封裝
/// 進不了深層省電，等於把要量的東西自己破壞掉。
///
/// 這裡用原始碼掃描而不是行為測試，理由很實際：這四個量測有三個需要系統管理員權限或特殊授權，
/// 在測試環境裡會提早退出，跑不到暫停動畫那一段；而「有沒有掛上這個機制」是一次寫好就不該退化的事，
/// 靜態檢查抓得住，也不會因為環境而漏掉。
/// </summary>
public class MeasurementQuietTests
{
    /// <summary>（檔案，方法名）→ 該方法的實作必須取得一份 Motion.Suspend()。</summary>
    private static readonly (string File, string Method, string Why)[] Required =
    [
        ("Services/InvisibleStallService.cs", "Run",
         "量的是 C-state 駐留：動畫重繪會讓封裝進不了深層省電，直接汙染要量的數字"),
        ("Services/LargePageService.cs", "Chase",
         "指標追逐量單筆存取延遲，旁邊有動畫在重繪會混進爭用成本"),
        ("Services/NvmePowerService.cs", "Sweep",
         "整趟大半時間是刻意的閒置，動畫會把機器叫著，變成量「有活動時的閒置」"),
        ("Services/DramTrafficService.cs", "Run",
         "動畫的重繪本身會製造記憶體流量與快取壓力，會動到自我驗證的比值"),
    ];

    [Fact]
    public void 四個新量測都要在量測期間暫停動畫()
    {
        var missing = new List<string>();
        foreach (var (file, method, why) in Required)
        {
            string body = MethodBody(Path.Combine(RepoRoot(), file), method);
            if (!body.Contains("Motion.Suspend()"))
                missing.Add($"{file} 的 {method}()：{why}");
        }

        Assert.True(missing.Count == 0,
            "以下量測沒有在量測期間暫停動畫（應加上 using var quiet = Motion.Suspend();）：\n"
            + string.Join("\n", missing));
    }

    /// <summary>
    /// 自我驗證：上面那條若把方法主體抓錯（例如抓到整個檔案），就會變成永遠通過的空檢查。
    /// 這裡確認抓出來的主體真的只有那個方法——拿一個明知不含該呼叫的方法來對。
    /// </summary>
    [Fact]
    public void 抓方法主體的方式不會誤抓到整個檔案()
    {
        string path = Path.Combine(RepoRoot(), "Services/InvisibleStallService.cs");

        // Run() 裡有；Delta() 這個小工具方法裡不可能有
        Assert.Contains("Motion.Suspend()", MethodBody(path, "Run"));
        Assert.DoesNotContain("Motion.Suspend()", MethodBody(path, "Delta"));
    }

    /// <summary>
    /// 取出某個方法的主體文字（從簽章那一行到大括號配對結束）。
    /// 只用在原始碼檢查上，不必是完整的 C# 剖析器。
    /// </summary>
    private static string MethodBody(string path, string method)
    {
        string src = File.ReadAllText(path);
        var m = Regex.Match(src, @"^\s*(?:private|public|internal|protected).*\b" + Regex.Escape(method) + @"\s*\(",
                            RegexOptions.Multiline);
        Assert.True(m.Success, $"{Path.GetFileName(path)} 裡找不到方法 {method}()");

        int open = src.IndexOf('{', m.Index);
        Assert.True(open > 0, $"{method}() 之後找不到主體的左大括號");

        int depth = 0;
        for (int i = open; i < src.Length; i++)
        {
            if (src[i] == '{') depth++;
            else if (src[i] == '}')
            {
                depth--;
                if (depth == 0) return src[open..(i + 1)];
            }
        }
        throw new InvalidOperationException($"{method}() 的大括號沒有配對成功");
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null)
        {
            if (File.Exists(Path.Combine(d.FullName, "XinSpect.csproj"))) return d.FullName;
            d = d.Parent;
        }
        throw new DirectoryNotFoundException("找不到原始碼樹（往上找不到 XinSpect.csproj）");
    }
}
