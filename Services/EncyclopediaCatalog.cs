using System;
using System.IO;
using System.Reflection;
using System.Text;

namespace XinSpect;

/// <summary>
/// 百科全書的程式內版本：把倉庫根的 <c>百科.html</c>（由 Tools/gen-docs-html.py 從
/// <c>README.md</c> 產生，自足、含目錄與錨點）內嵌成資源，供「關於」頁在程式內翻閱。
///
/// <para>
/// 為什麼是 HTML 而不是在程式裡再寫一個 Markdown 渲染器：同一份文件只能有一個渲染器。
/// HTML 由產生器從 .md 產出、版本控管、`--check` 對帳；程式只負責「把位元組交給 WebView2」。
/// 兩邊各寫一套渲染，遲早會出現「同一個標題在 GitHub 與程式裡長得不一樣」的漂移，
/// 而那種漂移沒有任何守門抓得到。
/// </para>
///
/// <para>
/// 讀取順序與 <see cref="ThirdPartyNoticesCatalog"/> 同一慣例：資源優先、檔案其次、
/// 皆無＝如實標缺並帶原因（單檔發佈版走資源；開發機資源不在時退回 exe 旁或倉庫根）。
/// <b>不用空字串冒充百科</b>，也不丟例外。
/// </para>
/// </summary>
public static class EncyclopediaCatalog
{
    /// <summary>csproj 內嵌時用的資源邏輯名（與 <c>&lt;EmbeddedResource LogicalName&gt;</c> 一致）。</summary>
    public const string ResourceName = "XinSpect.Encyclopedia.html";

    /// <summary>檔案名（磁碟退路與倉庫根查找都用它）。</summary>
    public const string FileName = "百科.html";

    /// <summary>讀不到時的固定原因（繁中原文；UI 顯示前過 LanguageService.T）。</summary>
    public const string MissingReason = "百科檔讀不到";

    /// <summary>百科內容。<c>Available = false</c> 時 <see cref="Text"/> 為 null。</summary>
    public sealed record EncyclopediaReport(bool Available, string? UnavailableReason, string? Text, string Source);

    public static EncyclopediaReport Load() => Load(
        () => ReadResource(Assembly.GetExecutingAssembly(), ResourceName),
        path => File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null);

    /// <summary>測試注入點：資源與磁碟讀取都可替換。</summary>
    internal static EncyclopediaReport Load(Func<string?> readResource, Func<string, string?> readFile)
    {
        string? embedded = readResource();
        if (!string.IsNullOrEmpty(embedded))
            return new EncyclopediaReport(true, null, embedded, "embedded:" + ResourceName);

        string? beside = readFile(Path.Combine(AppContext.BaseDirectory, FileName));
        if (!string.IsNullOrEmpty(beside))
            return new EncyclopediaReport(true, null, beside, "file:" + Path.Combine(AppContext.BaseDirectory, FileName));

        // 開發機：從輸出目錄往上找倉庫根（與 ThirdPartyNoticesCatalog 相同做法）。
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, FileName);
            string? text = readFile(candidate);
            if (!string.IsNullOrEmpty(text))
                return new EncyclopediaReport(true, null, text, "repo:" + candidate);
            dir = dir.Parent;
        }

        return new EncyclopediaReport(false, MissingReason, null, "none");
    }

    /// <summary>
    /// 把百科寫到本機快取（給 WebView2 以虛擬主機載入；WebView2 讀不了內嵌資源）。
    /// 內容沒變就不重寫——每次開啟都寫檔會讓防毒與索引器一直掃同一個大檔。
    /// </summary>
    public static string EnsureLocalCopy(string html)
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "XinSpect", "docs");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, FileName);
        var bytes = Encoding.UTF8.GetBytes(html);
        if (File.Exists(path) && new FileInfo(path).Length == bytes.LongLength)
            return path;
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static string? ReadResource(Assembly asm, string name)
    {
        try
        {
            using var stream = asm.GetManifestResourceStream(name);
            if (stream is null) return null;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch
        {
            return null;
        }
    }
}
