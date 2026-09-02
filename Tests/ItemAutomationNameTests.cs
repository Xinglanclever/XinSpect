using Xunit;
using XinSpect;

namespace XinSpect.Tests;

/// <summary>
/// 清單項目的自動化名稱（螢幕閱讀器唸的那一個）。
/// <para>
/// 為什麼要有這一支：<c>ListBoxItem</c>／<c>ComboBoxItem</c> 的 UIA Name 取自資料項目的
/// <see cref="object.ToString"/>，而不是 <c>DataTemplate</c> 或 <c>DisplayMemberPath</c> 畫出來的字。
/// 少了 <c>ToString</c> 覆寫，畫面上明明寫著「儲存裝置」，讀螢幕的人聽到的卻是「XinSpect.PageDef」。
/// 這種缺陷在截圖上完全看不出來，只有測試分得出來。
/// </para>
/// </summary>
public class ItemAutomationNameTests
{
    [Fact]
    public void 每一頁的導覽項目名稱都等於頁面標題()
    {
        foreach (var p in PageRegistry.Pages.Concat(PageRegistry.Utilities))
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Title), $"{p.Key} 沒有標題");
            Assert.Equal(p.Title, p.ToString());
            Assert.DoesNotContain("XinSpect.", p.ToString());
        }
    }

    [Fact]
    public void 磁碟下拉項目名稱等於標籤()
    {
        var row = new SmartDriveRow(3, "磁碟 3：INTEL SSDPELKX010T8（931.5 GB）");
        Assert.Equal("磁碟 3：INTEL SSDPELKX010T8（931.5 GB）", row.ToString());
    }

    [Fact]
    public void 磁碟區下拉項目名稱等於標題文字()
    {
        var v = new VolumeInfo("C:", "C:\\") { Label = "系統" };
        Assert.Equal(v.CaptionText, v.ToString());
        Assert.Contains("C:", v.ToString());

        // 沒有磁碟區標籤時退回代號本身，不能變成型別名稱
        var bare = new VolumeInfo("D:", "D:\\");
        Assert.Equal("D:", bare.ToString());
    }
}
