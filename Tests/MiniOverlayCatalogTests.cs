using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 懸浮小窗的項目目錄與風格切換：<b>目錄、XAML、設定三邊必須對得上</b>。
/// </summary>
/// <remarks>
/// 這一組守的是「加了一列卻沒接上」的缺陷：項目清單現在只有一份
/// （<see cref="MiniOverlayCatalog.Items"/>），齒輪選單由它產生，
/// 而 XAML 的每一列與設定的每一個開關都必須指回同一個屬性名。
/// 三邊任一漏接，症狀都是「選單打開了、畫面沒出現」——不會當掉，也不會有人發現。
/// </remarks>
public class MiniOverlayCatalogTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "XinSpect.csproj")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void 每一個項目都有對應的布林設定屬性()
    {
        var props = typeof(SettingsService).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var missing = new List<string>();
        foreach (var item in MiniOverlayCatalog.Items)
        {
            var p = props.FirstOrDefault(x => x.Name == item.SettingsProperty);
            if (p is null) { missing.Add($"{item.Key}：SettingsService 沒有 {item.SettingsProperty}"); continue; }
            if (p.PropertyType != typeof(bool)) missing.Add($"{item.Key}：{item.SettingsProperty} 不是 bool");
            if (p.SetMethod is null || !p.SetMethod.IsPublic) missing.Add($"{item.Key}：{item.SettingsProperty} 不能寫入");
        }
        Assert.True(missing.Count == 0, string.Join("\n", missing));
    }

    [Fact]
    public void 項目的鍵不得重複()
    {
        var dups = MiniOverlayCatalog.Items.GroupBy(i => i.Key).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.Empty(dups);
    }

    [Fact]
    public void 每一個項目在懸浮窗的XAML裡都有一條可見性綁定()
    {
        string xaml = File.ReadAllText(Path.Combine(RepoRoot(), "MiniOverlayWindow.xaml"));
        var missing = MiniOverlayCatalog.Items
            .Where(i => !xaml.Contains("Settings." + i.SettingsProperty, StringComparison.Ordinal))
            .Select(i => $"MiniOverlayWindow.xaml 沒有 Settings.{i.SettingsProperty}（{i.Key}）")
            .ToList();
        Assert.True(missing.Count == 0, string.Join("\n", missing));
    }

    /// <summary>
    /// 使用者 2026-10-27 的要求：資料項<b>預設全部開啟</b>，想看少一點是自己關。
    /// 這裡直接掃設定欄位的初始器——新建一台機器時拿到的是這些預設值。
    /// </summary>
    [Fact]
    public void 所有項目預設都是開啟的()
    {
        string source = File.ReadAllText(Path.Combine(RepoRoot(), "Services", "SettingsService.cs"));
        var offByDefault = new List<string>();
        foreach (var item in MiniOverlayCatalog.Items)
        {
            string field = "_" + char.ToLowerInvariant(item.SettingsProperty[0]) + item.SettingsProperty[1..];
            // 欄位宣告可能是 `private bool _a = true, _b = true;`，所以只找「欄位 = true」這個片段。
            if (!Regex.IsMatch(source, Regex.Escape(field) + @"\s*=\s*true\b"))
                offByDefault.Add($"{item.SettingsProperty}（欄位 {field}）不是預設 true");
        }
        Assert.True(offByDefault.Count == 0, string.Join("\n", offByDefault));
    }

    // ── 風格 ──────────────────────────────────────────────────────────

    [Fact]
    public void 風格值收斂到兩種且說明不為空()
    {
        Assert.Equal(MiniOverlayCatalog.StyleXinSpect, MiniOverlayCatalog.NormalizeStyle(0));
        Assert.Equal(MiniOverlayCatalog.StyleAfterburner, MiniOverlayCatalog.NormalizeStyle(1));
        Assert.Equal(MiniOverlayCatalog.StyleXinSpect, MiniOverlayCatalog.NormalizeStyle(7));   // 設定檔被亂改也不出半套樣式
        Assert.Equal(MiniOverlayCatalog.StyleXinSpect, MiniOverlayCatalog.NormalizeStyle(-3));

        foreach (int style in new[] { MiniOverlayCatalog.StyleXinSpect, MiniOverlayCatalog.StyleAfterburner })
        {
            Assert.False(string.IsNullOrWhiteSpace(MiniOverlayCatalog.StyleName(style)));
            Assert.Contains("外觀", MiniOverlayCatalog.StyleDescription(style));   // 說明裡要講清楚只換外觀
        }
        Assert.NotEqual(MiniOverlayCatalog.StyleName(0), MiniOverlayCatalog.StyleName(1));
    }

    [Fact]
    public void 風格切換在XAML裡是資料觸發而不是兩份版面()
    {
        string xaml = File.ReadAllText(Path.Combine(RepoRoot(), "MiniOverlayWindow.xaml"));
        Assert.Contains("Settings.MiniStyle", xaml);
        Assert.Contains("DataTrigger", xaml);
    }
}
