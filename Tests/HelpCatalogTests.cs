using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 全站圓圈問號（<c>HelpDot</c>）的靜態檢查：版面上寫的每一個 <c>HelpKey</c> 都要在
/// <see cref="HelpCatalog"/> 找得到說明，說明表裡也不能留下沒人用的孤兒條目。
/// <para>
/// 為什麼要有這一支：<c>HelpDot</c> 查不到鍵值時會把自己藏起來（一個點下去甚麼都沒有的問號
/// 比沒有問號更糟）。這個設計很安全，但代價是「打錯字」在畫面上長得跟「這裡本來就沒有問號」
/// 一模一樣——只有測試分得出來。所以本測試直接讀 XAML 原始碼比對，不走執行期視覺樹。
/// </para>
/// </summary>
public class HelpCatalogTests
{
    /// <summary>版面上寫死的 HelpKey（略過 <c>{Binding …}</c>——那是控制項自己轉手的）。</summary>
    private static readonly Regex KeyPattern = new("HelpKey=\"(?!\\{)([^\"]+)\"", RegexOptions.Compiled);

    /// <summary>不在 <see cref="PageRegistry"/> 裡的合法前綴：每核心細節是獨立視窗，不是分頁。</summary>
    private static readonly string[] NonPagePrefixes = ["coredetail"];

    [Fact]
    public void 版面上每一個問號都查得到說明()
    {
        var missing = new List<string>();
        foreach ((string file, string key) in KeysInXaml())
            if (HelpCatalog.Find(key) is null)
                missing.Add($"{file} → {key}");

        Assert.Empty(missing);
    }

    [Fact]
    public void 說明表沒有孤兒條目()
    {
        var used = KeysInXaml().Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        // 孤兒＝說明寫了但版面上沒有任何問號指到它：通常是標題改過字而說明忘了跟著改。
        string[] orphans = [.. HelpCatalog.All.Keys.Where(k => !used.Contains(k)).Order()];

        Assert.Empty(orphans);
    }

    [Fact]
    public void 每一條說明都有實際內容()
    {
        foreach ((string key, HelpEntry e) in HelpCatalog.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(e.Title), $"{key}：缺標題");
            Assert.False(string.IsNullOrWhiteSpace(e.What), $"{key}：缺「這是什麼」");
            Assert.False(string.IsNullOrWhiteSpace(e.Does), $"{key}：缺「有什麼用」");
            // 風險那一行永遠有內容（沒填 Safety 就用該等級的預設說法）。
            Assert.False(string.IsNullOrWhiteSpace(e.RiskLine), $"{key}：缺風險說明");
        }
    }

    [Fact]
    public void 會寫入硬體的條目必須自己寫清楚最壞情況()
    {
        // 預設說法只夠用在唯讀項目。真的會動硬體的地方，必須指名道姓寫出後果
        // （當機、藍屏、需要清除 CMOS、資料可能不一致），不能用一句「請小心使用」帶過。
        foreach ((string key, HelpEntry e) in HelpCatalog.All)
        {
            if (e.Risk == HelpRisk.ReadOnly) continue;
            Assert.False(string.IsNullOrWhiteSpace(e.Safety), $"{key}：非唯讀卻沒有自己的安全說明");
            Assert.DoesNotContain("請小心使用", e.Safety!);
        }
    }

    [Fact]
    public void 鍵值前綴要對得上分頁代號()
    {
        var known = PageRegistry.Pages.Concat(PageRegistry.Utilities)
            .Select(p => p.Key).Concat(NonPagePrefixes).ToHashSet(StringComparer.Ordinal);

        foreach (string key in HelpCatalog.All.Keys)
        {
            int slash = key.IndexOf('/');
            Assert.True(slash > 0, $"{key}：鍵值必須是「分頁代號/標題」的形式");
            Assert.Contains(key[..slash], known);
        }
    }

    /// <summary>掃出 Views 與 Controls 兩處 XAML 裡所有寫死的 HelpKey。</summary>
    private static IEnumerable<(string File, string Key)> KeysInXaml()
    {
        string root = RepoRoot() ?? throw new DirectoryNotFoundException("找不到原始碼樹");
        foreach (string dir in new[] { "Views", "Controls" })
            foreach (string path in Directory.EnumerateFiles(Path.Combine(root, dir), "*.xaml"))
                foreach (Match m in KeyPattern.Matches(File.ReadAllText(path)))
                    yield return (Path.GetFileName(path), m.Groups[1].Value);
    }

    /// <summary>從測試輸出目錄往上找到含 Views\AiView.xaml 的原始碼根目錄。</summary>
    private static string? RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Views", "AiView.xaml"))) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}
