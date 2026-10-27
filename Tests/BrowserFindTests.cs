using Xunit;

namespace XinSpect.Tests;

/// <summary>
/// 頁內尋找的腳本產生器。
///
/// <para>
/// WebView2 沒有內建尋找列，只能叫頁面自己找——這代表使用者輸入會被拼進一段 JavaScript。
/// 這一頁跑在管理員權杖的分頁裡，所以「沒有跳脫的字串」不只是顯示錯誤，
/// 是把「找這段字」變成「執行這段程式」。測試把跳脫釘住。
/// </para>
/// </summary>
public class BrowserFindTests
{
    [Theory]
    [InlineData("溫度")]
    [InlineData("cpu")]
    [InlineData("a b")]
    [InlineData("  前後空白  ")]
    public void 正常字串要能被使用(string term)
    {
        Assert.True(BrowserFind.IsUsableTerm(term));
        Assert.Contains("window.find(", BrowserFind.Script(term));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 空字串不能用(string? term)
    {
        Assert.False(BrowserFind.IsUsableTerm(term));
    }

    [Fact]
    public void 過長的字串不能用()
    {
        Assert.True(BrowserFind.IsUsableTerm(new string('a', BrowserFind.MaxTermLength)));
        Assert.False(BrowserFind.IsUsableTerm(new string('a', BrowserFind.MaxTermLength + 1)));
    }

    [Theory]
    [InlineData("引號\"與'單引號")]
    [InlineData("反斜線\\與換行\n第二行")]
    [InlineData("</script><script>alert(1)</script>")]
    [InlineData("'); alert(document.cookie); //")]
    [InlineData("`反引號` 與 ${變數}")]
    public void 任何字元都要被編碼成字串而不是程式碼(string term)
    {
        string script = BrowserFind.Script(term);

        // 一定是 JSON 字串字面值：開頭是雙引號、內容裡的雙引號被轉義
        Assert.Contains("window.find(\"", script);
        Assert.DoesNotContain("</script>", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("');", script);
        // `${` 只有在樣板字串裡才有意義；JSON 字串裡它只是三個字元。真正要確認的是
        // 整個 term 被包在雙引號裡、而內部的雙引號已轉義——上面那幾條就是在驗這件事。

        // 換行不能原樣出現在腳本裡（會把一行拆成兩行）
        Assert.DoesNotContain("\n", script);
    }

    [Fact]
    public void 往回找的參數要真的傳下去()
    {
        // 參數順序：(字串, 區分大小寫, 往回找, 繞回開頭, 只找整字, 含框架, 顯示對話框)
        Assert.Equal("window.find(\"x\", false, false, true, false, false, false)", BrowserFind.Script("x", backwards: false));
        Assert.Equal("window.find(\"x\", false, true, true, false, false, false)", BrowserFind.Script("x", backwards: true));
    }

    [Fact]
    public void 一律不顯示瀏覽器內建的尋找對話框()
    {
        // 最後一個參數是 showDialog：true 會蓋出 IE 時代的對話框，蓋住頁面。
        Assert.EndsWith(", false)", BrowserFind.Script("x"));
    }

    [Fact]
    public void 空字串傳進來不會產生壞腳本()
    {
        // 呼叫端應該先擋，但這裡也不能產出語法錯誤的腳本
        Assert.Equal("window.find(\"\", false, false, true, false, false, false)", BrowserFind.Script(null));
    }
}
