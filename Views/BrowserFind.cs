using System.Text.Json;

namespace XinSpect;

/// <summary>
/// 頁內尋找（Ctrl＋F）。WebView2 沒有內建的尋找列，只能叫頁面自己找——
/// 這代表<b>使用者的輸入會被拼進一段 JavaScript</b>，所以字串一定要走 JSON 編碼，
/// 不能自己加引號。
///
/// <para>
/// 這一頁跑在管理員權杖的分頁裡；雖然輸入的人就是使用者本人，但一個沒有跳脫的字串
/// 會讓「找這段字」變成「執行這段程式」——那不是使用者的意圖，也不該有可能發生。
/// </para>
/// </summary>
public static class BrowserFind
{
    /// <summary>太長的字串不像在找東西，也讓腳本變得很大。</summary>
    public const int MaxTermLength = 200;

    /// <summary>可以拿去搜尋的字串（空、全空白、過長都不行）。</summary>
    public static bool IsUsableTerm(string? term)
        => !string.IsNullOrEmpty(term) && term.Trim().Length > 0 && term.Length <= MaxTermLength;

    /// <summary>
    /// 產生呼叫 <c>window.find</c> 的腳本。參數順序：
    /// (字串, 區分大小寫, 往回找, 繞回開頭, 只找整字, 含框架, 顯示對話框)。
    /// 一律<b>不顯示對話框</b>——那是 IE 時代的 UI，會蓋住頁面。
    /// </summary>
    public static string Script(string term, bool backwards = false)
    {
        string literal = JsonSerializer.Serialize(term ?? "");
        string back = backwards ? "true" : "false";
        return $"window.find({literal}, false, {back}, true, false, false, false)";
    }
}
