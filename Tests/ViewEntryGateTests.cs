using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 檢視入口守門：<b>Views/ 裡的每一頁都要有人指得到它。</b>
/// <para>
/// 這一條的存在理由是一個真的發生過的例子：`Views/TroubleshootView.xaml`——整頁故障排查嚮導
/// （六個症狀、可能原因、檢查步驟、建議動作）寫完、服務與測試都在，卻沒有出現在
/// <see cref="PageRegistry"/> 裡、也沒有任何其他檔案提到它。它會編譯、它會通過煙霧測試
/// （沒人建構它就不會壞），而使用者的世界裡它<b>不存在</b>——
/// 「做了但看不到」與「沒做」在畫面上長得一模一樣。
/// </para>
/// <para>
/// 與 <see cref="ServiceOrphanGateTests"/> 的分工：那一條管服務有沒有接進管線，
/// 這一條管<b>畫面</b>有沒有入口（分頁註冊、被某一頁 host、或獨立視窗）。
/// 兩者都是「一整個東西做完但沒人用」那一類缺陷的機械化版本。
/// </para>
/// <para>
/// <b>不算引用的是「自己的那兩個檔」</b>（<c>X.xaml</c> 與 <c>X.xaml.cs</c>）：一個只被自己
/// 的程式碼碼提到的頁面，正是這條網要抓的孤兒。這與那一條服務守門「跳過自己的宣告行」同一個道理。
/// </para>
/// </summary>
public class ViewEntryGateTests
{
    private static readonly Regex ViewElement =
        new(@"<local:(\w+View)\b", RegexOptions.Compiled);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "XinSpect.csproj")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("找不到倉庫根（XinSpect.csproj）。");
    }

    /// <summary>Views/ 頂層的檢視名稱（不含分頁 XAML 自己的程式碼檔——引用判斷要排除它）。</summary>
    internal static List<string> DeclaredViews(string root)
        => [.. Directory.EnumerateFiles(Path.Combine(root, "Views"), "*.xaml", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => n.EndsWith("View", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)];

    private static IEnumerable<string> ReferencingFiles(string root)
    {
        // Nav/ 在這裡**要**收（側邊欄註冊就是最主要的入口來源），與服務守門排除 Nav/ 的口徑不同。
        string[] skipDirs = ["obj", "bin", "bin2", "Tests", "StrykerOutput", "lang-shots", "verify-shots",
                             "verify-shots-210", "verify-shots-omni", "_verify_probe", ".orphaned-tests", "work"];
        foreach (string path in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
        {
            if (!path.EndsWith(".cs", StringComparison.Ordinal) && !path.EndsWith(".xaml", StringComparison.Ordinal))
                continue;
            var parts = path[(root.Length + 1)..].Split(Path.DirectorySeparatorChar);
            if (parts.Any(p => skipDirs.Contains(p) || p.StartsWith("publish-", StringComparison.Ordinal)))
                continue;
            yield return path;
        }
    }

    /// <summary>
    /// 檔案對應的檢視名稱：一個檢視抵兩個檔（<c>X.xaml</c> 與 <c>X.xaml.cs</c>），
    /// <b>兩個都要算「自己」</b>——用 <c>GetFileNameWithoutExtension</c> 一次只去一層副檔名，
    /// 後置程式碼會被讀成「TempOrphanView.xaml」而不是「TempOrphanView」，
    /// 於是<b>它自己的類別名變成別的檔案在引用它</b>，這條網就名存實亡（2026-10-18 正對照照出來的）。
    /// </summary>
    internal static string OwnViewName(string path)
    {
        string name = Path.GetFileName(path);
        if (name.EndsWith(".xaml.cs", StringComparison.OrdinalIgnoreCase))
            name = name[..^3];                      // X.xaml.cs → X.xaml
        return Path.GetFileNameWithoutExtension(name);
    }

    /// <summary>某個檢視是否被「別的檔案」指到（元素寫法或程式碼建構都算）。</summary>
    internal static bool ReferencedOutsideItsOwnFiles(string viewName,
        IEnumerable<(string Path, string Text)> files)
    {
        foreach (var (path, text) in files)
        {
            if (string.Equals(OwnViewName(path), viewName, StringComparison.Ordinal))
                continue;   // 自己的 XAML 與自己的後置程式碼都不算別人指到它
            if (Regex.IsMatch(text, $@"\b{Regex.Escape(viewName)}\b"))
                return true;
        }
        return false;
    }

    [Fact]
    public void 每個檢視都要有人指得到它()
    {
        string root = RepoRoot();
        var views = DeclaredViews(root);
        Assert.True(views.Count > 50, $"只掃到 {views.Count} 個檢視——掃描慣例改了？這條網就失效了");

        var files = ReferencingFiles(root).Select(p => (Path: p, Text: File.ReadAllText(p))).ToList();
        var orphans = views.Where(v => !ReferencedOutsideItsOwnFiles(v, files)).ToList();

        Assert.True(orphans.Count == 0,
            "以下檢視沒有任何入口（不在 PageRegistry、沒有被其他頁面 host、也不是獨立視窗）：\n"
            + string.Join("\n", orphans.Select(o => "  • " + o))
            + "\n二選一：(a) 在 Nav/PageRegistry.cs 註冊成頁面（或掛進既有頁面的分頁／開成視窗）；"
            + "(b) 它已經被別的東西取代就刪掉。做完的頁面沒有入口，對使用者等於沒有做。");
    }

    [Fact]
    public void 掃描器抓得到沒有入口的檢視_正對照()
    {
        // 合成一個「自己講自己」的檢視：只有自己的兩個檔提到它 → 必須被點名。
        var files = new List<(string Path, string Text)>
        {
            (Path.Combine("Views", "TempOrphanView.xaml"), "<UserControl x:Class=\"XinSpect.TempOrphanView\"/>"),
            (Path.Combine("Views", "TempOrphanView.xaml.cs"), "namespace XinSpect; public partial class TempOrphanView { }"),
        };
        Assert.False(ReferencedOutsideItsOwnFiles("TempOrphanView", files));
        Assert.Equal("TempOrphanView", OwnViewName(Path.Combine("Views", "TempOrphanView.xaml.cs")));

        // 反面：別的檔案提到它就算有入口，且邊界要對（TempOrphanView2 不該讓 TempOrphanView 通過）。
        files.Add(("Nav/PageRegistry.cs", "Factory = () => new TempOrphanView2(),"));
        Assert.False(ReferencedOutsideItsOwnFiles("TempOrphanView", files));
        files.Add(("Nav/PageRegistry.cs", "Factory = () => new TempOrphanView(),"));
        Assert.True(ReferencedOutsideItsOwnFiles("TempOrphanView", files));
    }

    [Fact]
    public void 被host的檢視是用元素寫法掛上去的()
    {
        // 這一條是上一條的補充：被 host 的頁面（不在 PageRegistry 裡的那些）在 XAML 裡
        // 應該以 <local:某某View .../> 出現。若它們改成純程式碼 new 出來，這條會點名——
        // 因為那種寫法在畫面上找不到對應的元素，下一個人得讀完整個後置程式碼才知道它長在哪。
        string root = RepoRoot();
        var files = ReferencingFiles(root).Select(p => (Path: p, Text: File.ReadAllText(p))).ToList();
        var registered = File.ReadAllText(Path.Combine(root, "Nav", "PageRegistry.cs"));

        var hostedWithoutElement = new List<string>();
        foreach (string view in DeclaredViews(root))
        {
            if (registered.Contains($"new {view}(", StringComparison.Ordinal)) continue;
            bool element = files.Any(f => ViewElement.IsMatch(f.Text)
                && f.Text.Contains("<local:" + view, StringComparison.Ordinal));
            if (!element) hostedWithoutElement.Add(view);
        }

        // 獨立視窗（不以 View 命名、掛在 *Window.xaml）不在本清單的範圍；這裡只列 View 結尾者。
        Assert.True(hostedWithoutElement.Count == 0,
            "以下檢視沒有出現在 PageRegistry、也沒有用元素寫法被 host：\n"
            + string.Join("\n", hostedWithoutElement.Select(v => "  • " + v)));
    }
}
