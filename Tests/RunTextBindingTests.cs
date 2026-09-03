using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// <c>Run.Text</c> 的繫結一律要明寫 <c>Mode=OneWay</c>。
///
/// 這條規則的來歷是一個實際把畫面打掉的錯誤：<c>Run.Text</c> 在 WPF 裡的中繼資料帶
/// <c>BindsTwoWayByDefault</c>，所以 <c>&lt;Run Text="{Binding Type}"/&gt;</c> 是 TwoWay。
/// 一旦來源是唯讀屬性（本專案的資料列幾乎都是唯讀 record／init-only），繫結在<b>樣板載入的那一刻</b>
/// 就丟 <c>InvalidOperationException</c>，被 XAML 包成 <c>XamlParseException</c>，
/// 整個分頁開不起來——1.9.0 的設定頁就是這樣壞在診斷紀錄那一列上。
///
/// 用原始碼掃描而不是行為測試，理由是這種錯只在「該樣板真的被具體化」時才炸：
/// 診斷紀錄那一列平常是空清單，煙霧測試量測版面時根本不會建出 ItemTemplate，
/// 所以跑得過測試卻在使用者手上爆。靜態檢查抓得住每一處，包含還沒有資料的樣板。
/// </summary>
public class RunTextBindingTests
{
    /// <summary>抓 <c>&lt;Run ... Text="{Binding …}"</c>，把整個繫結運算式括起來。</summary>
    private static readonly Regex RunTextBinding = new(
        "<Run\\b[^>]*?Text=\"(\\{Binding[^\"]*)\"", RegexOptions.Compiled | RegexOptions.Singleline);

    [Fact]
    public void 每一個Run的文字繫結都要明寫OneWay()
    {
        var offenders = new List<string>();
        foreach (var (file, path) in XamlFiles())
        {
            string text = File.ReadAllText(path);
            foreach (Match m in RunTextBinding.Matches(text))
            {
                string expr = m.Groups[1].Value;
                if (expr.Contains("Mode=OneWay") || expr.Contains("Mode=OneTime")) continue;
                int line = text.Take(m.Index).Count(c => c == '\n') + 1;
                offenders.Add($"{file}:{line} → {expr}");
            }
        }

        Assert.True(offenders.Count == 0,
            "Run.Text 預設是 TwoWay，繫到唯讀屬性上會在樣板載入時丟 XamlParseException。"
            + "以下位置要補 Mode=OneWay：\n" + string.Join("\n", offenders));
    }

    /// <summary>
    /// 自我驗證：上面那條若正規式抓不到東西，就會變成永遠通過的空檢查。
    /// 這裡確認它真的認得出「缺 Mode」與「有 Mode」兩種寫法。
    /// </summary>
    [Fact]
    public void 掃描規則本身分得出有沒有寫Mode()
    {
        Assert.Single(RunTextBinding.Matches("<Run Text=\"{Binding Foo}\"/>"));
        Assert.Single(RunTextBinding.Matches("<Run Text=\"{Binding Foo, Mode=OneWay}\"/>"));
        Assert.Contains("Mode=OneWay",
            RunTextBinding.Match("<Run Text=\"{Binding Foo, Mode=OneWay}\"/>").Groups[1].Value);
        // 純字面的 Run 不該被抓進來（那沒有繫結，也就沒有方向問題）
        Assert.Empty(RunTextBinding.Matches("<Run Text=\"目前 DNS：\"/>"));
    }

    /// <summary>掃 Views、Controls、Dialogs 三處的 XAML，以及根目錄的視窗。</summary>
    private static IEnumerable<(string File, string Path)> XamlFiles()
    {
        string root = RepoRoot();
        foreach (string dir in new[] { "Views", "Controls", "Dialogs", "" })
        {
            string full = Path.Combine(root, dir);
            if (!Directory.Exists(full)) continue;
            foreach (string path in Directory.EnumerateFiles(full, "*.xaml", SearchOption.TopDirectoryOnly))
                yield return (Path.GetFileName(path), path);
        }
    }

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
