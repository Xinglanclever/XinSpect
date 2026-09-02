using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 簡易模式：側邊欄只留一般使用者用得到的頁面。
///
/// 這一份守兩件事。其一，<b>簡易模式不能讓任何功能消失</b>——它只是不列在側邊欄，
/// 命令面板（Ctrl+K）必須照樣搜得到每一頁，否則就是把功能藏起來而不是簡化。
/// 其二，分類要合理：一般人真正會用的那幾頁（總覽、健康、儲存、網路、關於、設定…）
/// 不得被標成進階，而直讀暫存器、編程效能計數器那類頁面必須是進階。
/// </summary>
public class SimpleModeTests
{
    /// <summary>不論簡易或進階，這些頁面都必須留在側邊欄——它們是「這台電腦是什麼、健不健康」的答案。</summary>
    private static readonly string[] MustStayBasic =
    [
        "overview", "cpu", "memory", "mainboard", "gpu", "storage", "network",
        "health", "sensors", "bench", "toolbox", "utilities", "setup", "settings", "about",
    ];

    /// <summary>這些頁面對一般使用者只是雜訊，簡易模式下該收起來。</summary>
    private static readonly string[] MustBeAdvanced =
    [
        "ceiling", "oc", "gpuoc", "pcie", "usb", "nvmepower", "displaylink", "netadv", "dpc", "frametime",
    ];

    [Fact]
    public void 一般使用者用得到的頁面不得被標成進階()
    {
        foreach (string key in MustStayBasic)
        {
            var def = PageRegistry.FindAny(key);
            Assert.NotNull(def);
            Assert.False(def!.Advanced, $"「{def.Title}」是一般使用者的基本頁面，不該被簡易模式收起來");
        }
    }

    [Fact]
    public void 直讀暫存器那類頁面必須是進階()
    {
        foreach (string key in MustBeAdvanced)
        {
            var def = PageRegistry.FindAny(key);
            Assert.NotNull(def);
            Assert.True(def!.Advanced, $"「{def.Title}」對一般使用者是雜訊，應標為進階頁");
        }
    }

    [Fact]
    public void 簡易模式下側邊欄要少一截但不能空掉()
    {
        int all = PageRegistry.Pages.Count;
        int simple = PageRegistry.Pages.Count(p => !p.Advanced);

        Assert.True(simple < all, "簡易模式沒有少掉任何頁面，等於這個模式沒有作用");
        Assert.True(simple >= 10, $"簡易模式只剩 {simple} 頁，砍太多了——連基本資訊都看不到就不是簡化");
    }

    [Fact]
    public void 被收起來的頁面在命令面板仍然找得到()
    {
        // 這是「不能讓功能消失」那一條的實際驗證：進階頁必須仍在註冊表裡、仍可依鍵值取得
        foreach (var def in PageRegistry.Pages.Where(p => p.Advanced))
        {
            Assert.NotNull(PageRegistry.FindAny(def.Key));
            Assert.False(string.IsNullOrWhiteSpace(def.Title));
        }
    }

    [Fact]
    public void 每一個進階頁都要有提示文字_使用者才知道自己收起了什麼()
    {
        foreach (var def in PageRegistry.Pages.Concat(PageRegistry.Utilities).Where(p => p.Advanced))
            Assert.False(string.IsNullOrWhiteSpace(def.Hint), $"{def.Title}：進階頁缺少一句說明");
    }
}
