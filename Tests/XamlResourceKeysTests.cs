using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// XAML 資源鍵的接線守門。
///
/// <para>
/// <c>{StaticResource 不存在的鍵}</c> 是<b>執行期</b>的 <c>XamlParseException</c>——那一頁會整頁載不起來，
/// 而編譯器不管（它不是型別錯誤）。<c>{DynamicResource 不存在的鍵}</c> 更安靜：控制項直接變成
/// 沒有樣式或透明，看起來像「設計成這樣」。
/// </para>
/// <para>
/// 這一輪之前沒有人數過這件事，所以新增一個控制項時打錯一個字，只有真的開到那一頁才會發現。
/// 這裡只問「用到的鍵有沒有定義」，不問「定義的鍵有沒有被用到」（後者是死資源，不影響行為）。
/// </para>
/// </summary>
public class XamlResourceKeysTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "XinSpect.csproj")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>專案裡「會被編進程式」的 XAML：排除建置產物。</summary>
    private static IEnumerable<string> XamlFiles()
    {
        string root = RepoRoot();
        foreach (string file in Directory.EnumerateFiles(root, "*.xaml", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (rel.StartsWith("obj/", StringComparison.OrdinalIgnoreCase)) continue;
            if (rel.StartsWith("bin/", StringComparison.OrdinalIgnoreCase)) continue;
            if (rel.StartsWith("publish-", StringComparison.OrdinalIgnoreCase)) continue;
            yield return file;
        }
    }

    [Fact]
    public void 用到的每個資源鍵都要有定義()
    {
        var defined = new HashSet<string>(StringComparer.Ordinal);
        var used = new List<(string Key, string File, int Line)>();

        foreach (string file in XamlFiles())
        {
            string[] lines = File.ReadAllLines(file);
            string rel = Path.GetRelativePath(RepoRoot(), file).Replace('\\', '/');

            for (int i = 0; i < lines.Length; i++)
            {
                foreach (Match m in Regex.Matches(lines[i], @"x:Key=""([^""]+)"""))
                    defined.Add(m.Groups[1].Value);

                foreach (Match m in Regex.Matches(lines[i], @"\{(?:StaticResource|DynamicResource)\s+([^}]+)\}"))
                {
                    string key = m.Groups[1].Value.Trim();
                    // {x:Type Button} 這類系統資源不是我們定義的鍵，跳過
                    if (key.StartsWith('{')) continue;
                    used.Add((key, rel, i + 1));
                }
            }
        }

        Assert.True(defined.Count > 0, "一個資源鍵都沒數到——掃描邏輯壞了");

        var missing = used.Where(u => !defined.Contains(u.Key))
                          .Select(u => $"{u.File}:{u.Line} 用到「{u.Key}」但沒有任何地方定義它")
                          .Distinct()
                          .ToList();

        Assert.True(missing.Count == 0,
            "XAML 資源鍵沒有定義（StaticResource 會在載入那一頁時直接拋 XamlParseException，"
            + "DynamicResource 則是安靜地沒有樣式）：\n" + string.Join("\n", missing));
    }

    /// <summary>
    /// 反過來：<c>Theme.xaml</c> 定義的鍵要被 <c>App.xaml</c> 併進來，否則整份字典都等於不存在。
    /// 這一條是上面那一條的前提——少了它，上面的掃描會把「有定義」當成「載得到」。
    /// </summary>
    [Fact]
    public void 佈景字典要被應用程式併進來()
    {
        string app = File.ReadAllText(Path.Combine(RepoRoot(), "App.xaml"));
        var merged = Regex.Matches(app, @"ResourceDictionary\s+Source=""([^""]+)""")
                          .Select(m => m.Groups[1].Value.Replace('\\', '/'))
                          .ToList();

        Assert.NotEmpty(merged);
        foreach (string src in merged)
            Assert.True(File.Exists(Path.Combine(RepoRoot(), src.Replace('/', Path.DirectorySeparatorChar))),
                $"App.xaml 併入了不存在的資源字典：{src}");

        Assert.Contains(merged, m => m.EndsWith("Themes/Theme.xaml", StringComparison.OrdinalIgnoreCase));
    }
}
