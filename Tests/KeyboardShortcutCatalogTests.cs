using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 全域鍵盤快捷鍵表（U10-UI-0001／ui.keys.map）的守門：<b>表與實際綁定一致</b>。
/// 直接掃原始碼字面——MainWindow 的按鍵判斷、CommandPalette 的面板內導航、
/// 以及「全站沒有任何 XAML KeyBinding／InputBinding」這個盤點結論本身。
/// 日後新增快捷鍵而忘了更新表（或反過來），這裡紅燈。
/// </summary>
public class KeyboardShortcutCatalogTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "XinSpect.csproj")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([RepoRoot(), .. parts]));

    [Fact]
    public void 表列的每一個組合鍵_在主視窗原始碼裡真的有綁定()
    {
        string shell = Read("MainWindow.xaml.cs");
        string palette = Read("Controls", "CommandPalette.xaml.cs");

        // 開啟：Ctrl+K 為主、Ctrl+P 沿用編輯器慣例——兩個都要在程式碼裡真的被攔
        Assert.Contains("Key.K", shell, StringComparison.Ordinal);
        Assert.Contains("Key.P", shell, StringComparison.Ordinal);
        Assert.Contains("ModifierKeys.Control", shell, StringComparison.Ordinal);

        // 面板內導航：Esc／↑／↓／Enter 由 HandleKey 處理
        Assert.Contains("Key.Escape", palette, StringComparison.Ordinal);
        Assert.Contains("Key.Down", palette, StringComparison.Ordinal);
        Assert.Contains("Key.Up", palette, StringComparison.Ordinal);
        Assert.Contains("Key.Enter", palette, StringComparison.Ordinal);

        // 表裡的按鍵字面與程式碼對得上（表是單一來源，別處抄錯會在這裡現形）
        Assert.Equal(5, KeyboardShortcutCatalog.Global.Count);
        Assert.Contains(KeyboardShortcutCatalog.Global, r => r.Keys == "Ctrl+K");
        Assert.Contains(KeyboardShortcutCatalog.Global, r => r.Keys == "Ctrl+P");
        Assert.Contains(KeyboardShortcutCatalog.Global, r => r.Keys == "Esc");
    }

    [Fact]
    public void 全站沒有任何XAML按鍵綁定_盤點結論本身被釘住()
    {
        // 表只列了命令面板一組（程式碼攔截）。若有人在 XAML 加了 KeyBinding／InputBinding，
        // 這個「沒有」就不再成立——屆時請把新快捷鍵補進 KeyboardShortcutCatalog 並更新本測試。
        var xamlFiles = Directory.EnumerateFiles(Path.Combine(RepoRoot()), "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToList();
        Assert.True(xamlFiles.Count > 50, $"只掃到 {xamlFiles.Count} 個 XAML——掃描路徑可能壞了");

        var offenders = new List<string>();
        foreach (string f in xamlFiles)
        {
            string text;
            try { text = File.ReadAllText(f); } catch (IOException) { continue; }
            if (Regex.IsMatch(text, "<(KeyBinding|InputBinding|MouseBinding)\\b"))
                offenders.Add(Path.GetFileName(f));
        }
        Assert.Empty(offenders);
    }

    [Fact]
    public void 表的每一列都有按鍵_說明與作用範圍_不留空列()
    {
        Assert.NotEmpty(KeyboardShortcutCatalog.Global);
        foreach (var r in KeyboardShortcutCatalog.Global)
        {
            Assert.False(string.IsNullOrWhiteSpace(r.Keys), $"{r.Keys}：缺按鍵組合");
            Assert.False(string.IsNullOrWhiteSpace(r.Description), $"{r.Keys}：缺說明");
            Assert.False(string.IsNullOrWhiteSpace(r.Scope), $"{r.Keys}：缺作用範圍");
        }
    }

    [Fact]
    public void 追蹤鍵字面落在Services原始碼裡()
    {
        string catalog = Read("Services", "KeyboardShortcutCatalog.cs");
        Assert.Contains("ui.keys.map", catalog, StringComparison.Ordinal);
    }
}
