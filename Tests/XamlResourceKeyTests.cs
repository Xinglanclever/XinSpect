using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// XAML 裡引用的每一個資源鍵，都要真的有人定義過。
///
/// <para>
/// 這條規則的來歷是一個把整頁打掉的錯誤：<c>Views/BenchView.xaml</c> 的延遲曲線偏差表，
/// 轉換器寫成了 <c>{StaticResource SeverityToBrushConverter}</c>——那是 C# 的<b>類別名稱</b>，
/// 而 <c>Themes/Theme.xaml</c> 登記的鍵叫 <c>SeverityToBrush</c>。<c>StaticResource</c> 找不到鍵
/// 會丟例外，被 XAML 包成 <c>XamlParseException</c>，效能測試頁一量完延遲曲線就畫不出來。
/// </para>
/// <para>
/// 和 <see cref="RunTextBindingTests"/> 同一個道理，也用原始碼掃描而不是行為測試：這種錯只在
/// 該樣板<b>真的被具體化</b>時才炸。偏差表平常是空清單，煙霧測試量版面時根本不會建出
/// ItemTemplate，於是測試全綠、使用者一按「開始量測」就爆。靜態檢查抓得住每一處，
/// 包含還沒有資料的樣板。
/// </para>
/// <para>
/// 認定的查找範圍刻意收緊到「全域字典（<c>App.xaml</c> ＋ <c>Themes/Theme.xaml</c>）或本檔自己」，
/// 不是「專案裡任何一處」——這樣連「鍵定義在別的 View 底下、實際查不到」也一起擋掉。
/// </para>
/// </summary>
public class XamlResourceKeyTests
{
    /// <summary>資源定義：<c>x:Key="Name"</c>。<c>{x:Type …}</c> 這種型別鍵不是字串鍵，排除。</summary>
    private static readonly Regex KeyDef = new(@"x:Key=""([^""{}]+)""", RegexOptions.Compiled);

    /// <summary>資源引用：<c>{StaticResource Name}</c>／<c>{DynamicResource Name}</c>。</summary>
    private static readonly Regex KeyRef = new(
        @"\{(?:Static|Dynamic)Resource\s+(?:ResourceKey=)?([A-Za-z_][A-Za-z0-9_.]*)\s*\}",
        RegexOptions.Compiled);

    /// <summary>程式碼後置的字面鍵：<c>FindResource("Name")</c>／<c>TryFindResource("Name")</c>。</summary>
    private static readonly Regex CodeRef = new(
        @"\b(?:Try)?FindResource\(""([^""]+)""\)", RegexOptions.Compiled);

    [Fact]
    public void 每一個XAML資源引用都找得到定義()
    {
        var global = GlobalKeys();
        var offenders = new List<string>();
        foreach (string path in XamlFiles())
        {
            string text = File.ReadAllText(path);
            var local = Keys(text);
            foreach (Match m in KeyRef.Matches(text))
            {
                string key = m.Groups[1].Value;
                if (global.Contains(key) || local.Contains(key)) continue;
                offenders.Add($"{Rel(path)}:{LineOf(text, m.Index)} → {key}");
            }
        }

        Assert.True(offenders.Count == 0,
            "StaticResource／DynamicResource 找不到鍵會在樣板載入時丟 XamlParseException。"
            + "以下引用在全域字典與本檔裡都查不到（注意鍵名區分大小寫，"
            + "而且轉換器的鍵名不一定等於類別名稱）：\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void 程式碼後置的FindResource字面鍵也找得到定義()
    {
        var global = GlobalKeys();
        var offenders = new List<string>();
        int seen = 0;
        foreach (string path in CodeFiles())
        {
            string text = File.ReadAllText(path);
            var scope = global;
            // Foo.xaml.cs 查得到 Foo.xaml 自己宣告的資源（查找會沿邏輯樹往上走）
            string paired = path[..^3];
            if (paired.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase) && File.Exists(paired))
            {
                scope = new HashSet<string>(global, StringComparer.Ordinal);
                scope.UnionWith(Keys(File.ReadAllText(paired)));
            }

            foreach (Match m in CodeRef.Matches(text))
            {
                seen++;
                if (scope.Contains(m.Groups[1].Value)) continue;
                offenders.Add($"{Rel(path)}:{LineOf(text, m.Index)} → {m.Groups[1].Value}");
            }
        }

        Assert.True(offenders.Count == 0,
            "FindResource 找不到鍵會直接丟 ResourceReferenceKeyNotFoundException：\n"
            + string.Join("\n", offenders));
        // 自我驗證：真的有掃到東西，否則上面等於空檢查
        Assert.True(seen >= 5, $"只掃到 {seen} 處 FindResource，掃描器可能已經失效。");
    }

    /// <summary>
    /// 自我驗證：兩條正規式若抓不到東西，上面就會退化成永遠通過的空檢查。
    /// 這裡確認它們認得出該認的寫法，也不會去咬無從靜態比對的型別鍵。
    /// </summary>
    [Fact]
    public void 掃描規則本身認得出鍵的定義與引用()
    {
        Assert.Equal("Card", KeyDef.Match(@"<Style x:Key=""Card"" TargetType=""Border"">").Groups[1].Value);
        Assert.Empty(KeyDef.Matches(@"<Style x:Key=""{x:Type TextBlock}""/>"));

        Assert.Equal("SeverityToBrush", KeyRef.Match("{StaticResource SeverityToBrush}").Groups[1].Value);
        Assert.Equal("AccentBrush", KeyRef.Match("{DynamicResource AccentBrush}").Groups[1].Value);
        Assert.Empty(KeyRef.Matches("{StaticResource {x:Type Button}}"));

        Assert.Equal("GoodBrush", CodeRef.Match(@"(Brush)FindResource(""GoodBrush"")").Groups[1].Value);
        Assert.Empty(CodeRef.Matches(@"fe.TryFindResource(""Tile."" + t.Id)"));

        // 全域字典真的讀進來了：路徑寫錯的話上面每一條都會變成空檢查
        var global = GlobalKeys();
        Assert.Contains("SeverityToBrush", global);
        Assert.True(global.Count > 20, $"全域資源只讀到 {global.Count} 個，字典路徑可能不對。");
    }

    /// <summary>全域查找範圍：<c>App.xaml</c> 的資源，以及它合併進來的 <c>Themes/Theme.xaml</c>。</summary>
    private static HashSet<string> GlobalKeys()
    {
        string root = RepoRoot();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (string rel in new[] { "App.xaml", Path.Combine("Themes", "Theme.xaml") })
            keys.UnionWith(Keys(File.ReadAllText(Path.Combine(root, rel))));
        return keys;
    }

    private static HashSet<string> Keys(string xaml)
        => new(KeyDef.Matches(xaml).Select(m => m.Groups[1].Value), StringComparer.Ordinal);

    private static int LineOf(string text, int index) => text.Take(index).Count(c => c == '\n') + 1;

    private static IEnumerable<string> XamlFiles()
        => Directory.EnumerateFiles(RepoRoot(), "*.xaml", SearchOption.AllDirectories).Where(IsOurs);

    /// <summary>只掃本體的程式碼後置；測試自己與 net48 橋接程式不算。</summary>
    private static IEnumerable<string> CodeFiles()
        => Directory.EnumerateFiles(RepoRoot(), "*.cs", SearchOption.AllDirectories)
            .Where(p => IsOurs(p) && !Rel(p).StartsWith("Tests/", StringComparison.Ordinal));

    /// <summary>排除建置產出、發佈輸出與 net48 橋接程式。</summary>
    private static bool IsOurs(string path)
    {
        string rel = Rel(path);
        foreach (string skip in new[] { "bin/", "obj/", "Bridge/", "publish" })
            if (rel.StartsWith(skip, StringComparison.Ordinal)) return false;
        return !rel.Contains("/bin/", StringComparison.Ordinal)
            && !rel.Contains("/obj/", StringComparison.Ordinal);
    }

    private static string Rel(string path)
        => Path.GetRelativePath(RepoRoot(), path).Replace('\\', '/');

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Views", "SettingsView.xaml"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("找不到原始碼樹");
    }
}
