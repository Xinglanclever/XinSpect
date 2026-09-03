using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 設定檔的完整性：<b>凡是 setter 會呼叫 <c>Save()</c> 的屬性，都必須真的被寫進檔案</b>。
///
/// 這條規則來自一個安靜壞掉很久的錯誤：「簡易模式」（<c>SimpleMode</c>）的 setter 一直有呼叫
/// <c>Save()</c>，看起來一切正常，但 <c>Persist</c> 類別裡從來沒有對應欄位——於是使用者勾了
/// 簡易模式、關掉程式、再開起來又變回詳細，畫面上沒有任何線索說明為什麼。
/// 1.9.0 把首次啟動的版本選擇做進去之後，這個漏洞會直接讓「選過的版本記不住」。
///
/// 用原始碼掃描而不是行為測試，理由是 <c>SettingsService</c> 的檔案路徑寫死在
/// <c>%APPDATA%\XinSpect\settings.json</c>：真的去存取它會蓋掉使用者自己的設定。
/// 靜態檢查抓得到同一類錯，而且不動到任何真實檔案。
/// </summary>
public class SettingsPersistenceTests
{
    /// <summary>setter 裡呼叫 Save() 的公開屬性——這就是「打算持久化」的定義。</summary>
    private static readonly Regex SavedProperty = new(
        @"public\s+[\w\?<>,\s\[\]]+?\s(\w+)\s*\{\s*get\s*=>[^}]*?set\s*\{[^}]*?Save\(\)",
        RegexOptions.Compiled | RegexOptions.Singleline);

    [Fact]
    public void 每一個會存檔的設定都要出現在Persist與載入存檔兩段程式裡()
    {
        string src = File.ReadAllText(Path.Combine(RepoRoot(), "Services", "SettingsService.cs"));
        string persist = Block(src, "private sealed class Persist", "\n    }");
        string load = Block(src, "private void Load()", "private void Save()");
        string save = Block(src, "private void Save()", "\n    private ");

        var problems = new List<string>();
        foreach (Match m in SavedProperty.Matches(src))
        {
            string name = m.Groups[1].Value;
            if (!persist.Contains(name)) problems.Add($"{name}：Persist 類別裡沒有對應欄位，存檔時整個被漏掉");
            else if (!load.Contains(name)) problems.Add($"{name}：Load() 沒有讀回來，重開程式會回到預設值");
            else if (!save.Contains(name)) problems.Add($"{name}：Save() 沒有寫出去");
        }

        Assert.True(problems.Count == 0,
            "以下設定的 setter 會呼叫 Save()，但實際上沒有被持久化：\n" + string.Join("\n", problems));
    }

    /// <summary>
    /// 自我驗證：上面三段程式若抓錯（例如抓成整個檔案），檢查就會永遠通過。
    /// 這裡確認切出來的區塊真的互不相同、而且抓到了預期的內容。
    /// </summary>
    [Fact]
    public void 切出來的三段程式真的是三段不同的東西()
    {
        string src = File.ReadAllText(Path.Combine(RepoRoot(), "Services", "SettingsService.cs"));
        string persist = Block(src, "private sealed class Persist", "\n    }");
        string load = Block(src, "private void Load()", "private void Save()");
        string save = Block(src, "private void Save()", "\n    private ");

        Assert.Contains("SchemaVersion", persist);
        Assert.Contains("JsonSerializer.Deserialize", load);
        Assert.Contains("AtomicWrite.AllText", save);
        Assert.DoesNotContain("AtomicWrite.AllText", load);
        Assert.True(persist.Length < src.Length / 2, "Persist 區塊抓得太大，八成把整個檔案吃進來了");
        // 而且這個掃描規則要真的抓得到東西——抓到 0 個屬性等同沒有測試
        Assert.True(SavedProperty.Matches(src).Count >= 20,
            $"只抓到 {SavedProperty.Matches(src).Count} 個會存檔的屬性，掃描規則八成失效了");
    }

    private static string Block(string src, string from, string to)
    {
        int a = src.IndexOf(from, StringComparison.Ordinal);
        Assert.True(a >= 0, $"原始碼裡找不到「{from}」");
        int b = src.IndexOf(to, a + from.Length, StringComparison.Ordinal);
        return b > a ? src[a..b] : src[a..];
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Services", "SettingsService.cs"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("找不到原始碼樹");
    }
}
