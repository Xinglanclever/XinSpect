using System.IO;

namespace XinSpect;

/// <summary>網址列的解析結果。</summary>
public enum BrowserInputKind
{
    /// <summary>空輸入 → 回首頁。</summary>
    Home,
    /// <summary>直接開啟的 http／https 網址。</summary>
    Url,
    /// <summary>不是網址 → 丟給搜尋引擎。</summary>
    Search,
    /// <summary>不是網頁：本機路徑、javascript:、data:、自訂協定等，一律不載入。</summary>
    Blocked,
}

/// <param name="Query">Search 時要搜的關鍵字；其他情形為空字串。</param>
public readonly record struct BrowserInput(BrowserInputKind Kind, Uri? Target, string Query, string Reason);

/// <summary>
/// 內建瀏覽器的規則，全部放在這裡而不是散在事件處理常式裡——這樣它們能被測試釘住。
///
/// <para>
/// 三件事在這裡定義：<b>什麼可以載入</b>（只有 http／https）、<b>什麼要擋下來並說清楚原因</b>
/// （本機路徑、javascript:、data:、自訂協定）、以及<b>下載檔案放哪裡、叫什麼名字</b>。
/// </para>
/// <para>
/// 為什麼要擋 <c>file://</c>：網頁（甚至是首頁上的連結）只要能載入本機路徑，
/// 就能把這台機器上的檔案內容顯示在瀏覽器裡——一個「硬體檢測工具」沒有理由提供那條路。
/// 真的要看本機檔案，用檔案總管或系統瀏覽器，那裡才有完整的權限提示。
/// </para>
/// </summary>
public static class BrowserPolicy
{
    /// <summary>唯一允許在內建瀏覽器裡導覽的協定。</summary>
    public static readonly string[] AllowedSchemes = ["http", "https"];

    /// <summary>搜尋引擎（與起始頁一致）。</summary>
    public const string SearchBase = "https://www.bing.com/search?q=";

    private static readonly HashSet<string> BlockedSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "javascript", "data", "vbscript", "file", "blob", "filesystem", "view-source",
        "about", "chrome", "edge", "ms-settings", "ms-appx", "intent", "magnet", "res",
    };

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>下載存放資料夾：跟著程式自己的資料走，不往使用者的下載資料夾裡丟東西。</summary>
    public static string DownloadFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XinSpect", "Downloads");

    /// <summary>把網址列輸入解析成「直接開／丟去搜尋／擋下」。</summary>
    public static BrowserInput Resolve(string? raw)
    {
        string text = (raw ?? "").Trim();
        if (text.Length == 0) return new BrowserInput(BrowserInputKind.Home, null, "", "");

        // 有「協定:」的字串先判斷協定——不能直接當網域，否則 javascript:alert(1) 會被補成 https://javascript:…
        var schemeMatch = System.Text.RegularExpressions.Regex.Match(text, @"^(?<s>[A-Za-z][A-Za-z0-9+.\-]*):");
        if (schemeMatch.Success)
        {
            string scheme = schemeMatch.Groups["s"].Value;

            if (scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
                || scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
            {
                return Uri.TryCreate(text, UriKind.Absolute, out var abs)
                    ? new BrowserInput(BrowserInputKind.Url, abs, "", "")
                    : new BrowserInput(BrowserInputKind.Blocked, null, "", "網址格式不完整，無法開啟");
            }

            // 單一磁碟機代號（C:\…、D:/…）不是協定，是本機路徑。
            if (scheme.Length == 1 && text.Length > 2 && (text[2] == '\\' || text[2] == '/'))
                return new BrowserInput(BrowserInputKind.Blocked, null, "",
                    "這是本機檔案路徑。內建瀏覽器只開網頁，本機檔案請用檔案總管或系統瀏覽器。");

            if (BlockedSchemes.Contains(scheme))
                return new BrowserInput(BrowserInputKind.Blocked, null, "", DescribeBlocked(scheme));

            // localhost:8080 這類「主機:埠」會被誤認成協定——只有後面是純數字埠號時才當主機。
            string after = text[(scheme.Length + 1)..];
            if (after.Length > 0 && after.All(char.IsAsciiDigit) && !text.Contains(' ')
                && SafeUri("http://" + text) is { } local)
                return new BrowserInput(BrowserInputKind.Url, local, "", "");

            return new BrowserInput(BrowserInputKind.Blocked, null, "", DescribeBlocked(scheme));
        }

        // 看起來像網域（含點、不含空白）→ 補 https://
        if (!text.Contains(' ') && text.Contains('.') && SafeUri("https://" + text) is { } host)
            return new BrowserInput(BrowserInputKind.Url, host, "", "");

        return new BrowserInput(BrowserInputKind.Search, null, text, "");
    }

    /// <summary>
    /// 使用者自訂的首頁。留空／解析不出來／不是 http(s) 一律回 <c>null</c>＝用內建的離線起始頁。
    /// </summary>
    /// <remarks>
    /// <b>為什麼只在這裡判定「能不能當首頁」：</b>首頁是每次按「首頁」鍵與啟動時都會走的字串，
    /// 如果它被存成 <c>file:///…</c> 或 <c>javascript:…</c>，就會變成一個<b>每次啟動都失敗的首頁</b>——
    /// 使用者看到的是一頁空白，而不是「這個設定不合法」。所以設定頁寫入時不擋，
    /// 判定集中在這裡：不合法就安靜退回內建頁（設定頁會即時顯示判定結果）。
    /// </remarks>
    public static Uri? ResolveHomeUrl(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured)) return null;
        string text = configured.Trim();

        // 有「看起來像協定」的前綴就照它解析、不補 https：
        // 否則 magnet:?xt=… 會被補成 https://magnet/…，變成一個「合法但錯的」首頁。
        // 判斷條件不含小數點，才不會把 example.com:8080/x 這種「主機:埠」誤認成協定。
        int colon = text.IndexOf(':');
        int slash = text.IndexOf('/');
        bool hasScheme = colon > 0
                         && (slash < 0 || colon < slash)
                         && !text[..colon].Contains('.');

        if (!hasScheme) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return null;
        return uri.Scheme is "http" or "https" ? uri : null;
    }

    /// <summary>這個已經解析好的 URI 能不能在內建瀏覽器裡導覽。</summary>
    /// <remarks>
    /// <b>起始頁不是 about:blank。</b>WebView2 的 <c>NavigateToString</c> 實際導覽到的是
    /// <c>data:text/html;charset=utf-8;base64,…</c>（2026-10-26 在執行中的瀏覽器上實測確認），
    /// 而 <c>data:</c> 在政策裡是被擋的協定——第 291 輪加上導覽政策後，首頁就變成一片空白。
    /// 所以「我們自己剛送出的那一份文件」必須放行，但由呼叫端明確說出這一次是自己送出的
    /// （<paramref name="allowSelfDocument"/>）；頁面裡的 data: 連結照樣擋。
    /// </remarks>
    public static bool IsNavigationAllowed(Uri? uri, bool allowSelfDocument = false)
    {
        if (uri is null) return false;
        if (uri.Scheme is "http" or "https") return true;
        if (uri.Scheme == "about" && uri.AbsolutePath.Equals("blank", StringComparison.OrdinalIgnoreCase)) return true;
        return allowSelfDocument && IsSelfDocument(uri);
    }

    /// <summary>
    /// 是不是 <c>NavigateToString</c> 產生的 HTML 文件（<c>data:text/html…</c>）。
    /// 只認 HTML——<c>data:image/…</c> 之類一樣是「頁面把內容塞成網址」，不因此放行。
    /// </summary>
    public static bool IsSelfDocument(Uri? uri)
        => uri is { Scheme: "data" }
           && uri.AbsoluteUri.StartsWith("data:text/html", StringComparison.OrdinalIgnoreCase);

    /// <summary>被擋下時給人看的一句話（要說出「是什麼」與「去哪裡做」）。</summary>
    public static string DescribeBlocked(string scheme) => scheme.ToLowerInvariant() switch
    {
        "javascript" => "這是頁面內的 JavaScript 連結，不是網址——內建瀏覽器不執行。",
        "data" => "這是內嵌資料（data:）網址，不是網頁位址——內建瀏覽器不載入。",
        "file" => "這是本機檔案路徑（file:）。內建瀏覽器只開網頁，本機檔案請用檔案總管或系統瀏覽器。",
        "about" => "這是瀏覽器內部頁面（about:），內建瀏覽器不提供。",
        "blob" or "filesystem" => "這是頁面執行期產生的暫存物件（" + scheme + ":），無法直接開啟。",
        "view-source" => "這是原始碼檢視（view-source:），內建瀏覽器不提供；請用開發者工具。",
        "ms-settings" or "ms-appx" => "這是 Windows 設定／應用程式連結，內建瀏覽器不處理。",
        "magnet" => "這是種子連結（magnet:），內建瀏覽器不處理。",
        _ => $"這是「{scheme}:」自訂協定，不是網頁——內建瀏覽器只開 http／https。",
    };

    /// <summary>
    /// 開發者工具只在<b>明確要求</b>時打開：這個程式以管理員權杖執行，DevTools 是一個能對頁面
    /// 執行任意腳本、檢視 Cookie 與本機儲存的完整介面。使用者要除錯自己的網頁時可以開，
    /// 但預設不該擺在那裡——「預設開啟」等於把一個提權介面常駐在畫面上。
    ///
    /// <para>開啟方式：設定環境變數 <c>XINSPECT_BROWSER_DEVTOOLS=1</c> 再啟動程式（刻意不做成按鈕：
    /// 做成按鈕等於每個使用者都會看到它，而它只對除錯的人有意义）。</para>
    /// </summary>
    public const string DevToolsEnvVar = "XINSPECT_BROWSER_DEVTOOLS";

    /// <summary>解析環境變數：<c>1</c>／<c>true</c>／<c>yes</c>／<c>on</c> 視為開啟，其餘一律關閉（含未設定）。</summary>
    public static bool DevToolsAllowed(string? envValue)
    {
        if (string.IsNullOrWhiteSpace(envValue)) return false;
        string v = envValue.Trim().ToLowerInvariant();
        return v is "1" or "true" or "yes" or "on";
    }

    /// <summary>縮放下限：再小就只是把字變糊，不會多看到東西。</summary>
    public const double MinZoom = 0.5;

    /// <summary>縮放上限：再大就只剩兩三個字，導覽列還得靠捲動。</summary>
    public const double MaxZoom = 2.0;

    /// <summary>每次點按鈕的縮放級距。</summary>
    public const double ZoomStep = 0.1;

    /// <summary>
    /// 依方向調整縮放並夾在範圍內。<paramref name="direction"/> 為正放大、負縮小、0 不變。
    /// 直接乘加會累積浮點誤差（0.1 十次不等於 1.0），所以先取整到小數第一位再加。
    /// </summary>
    public static double NextZoom(double current, int direction)
    {
        double now = ClampZoom(current);
        if (direction == 0) return now;
        double steps = Math.Round((now - 1.0) / ZoomStep);        // 目前是幾格
        steps += Math.Sign(direction);
        return ClampZoom(1.0 + steps * ZoomStep);
    }

    /// <summary>把任意值夾進 [<see cref="MinZoom"/>, <see cref="MaxZoom"/>]；非有限值一律回 1.0（100%）。</summary>
    public static double ClampZoom(double value)
        => double.IsFinite(value) ? Math.Clamp(value, MinZoom, MaxZoom) : 1.0;

    /// <summary>
    /// 把網站建議的檔名洗成安全的本機檔名：去掉路徑（含 <c>..\..</c>）、換掉非法字元、
    /// 避開 Windows 保留名、限制長度。洗不出名字就用 <c>download</c>——不要用空字串去組路徑。
    /// </summary>
    public static string SanitizeFileName(string? suggested)
    {
        string name = "";
        try { name = Path.GetFileName((suggested ?? "").Replace('\\', '/')); }
        catch { name = ""; }
        if (string.IsNullOrWhiteSpace(name)) return "download";

        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (char c in name)
            sb.Append(Array.IndexOf(invalid, c) >= 0 || c < 32 ? '_' : c);
        name = sb.ToString().Trim().TrimEnd('.');

        // 保留名（CON、NUL、COM1…）就算帶副檔名也不行。
        string stem = Path.GetFileNameWithoutExtension(name);
        if (ReservedNames.Contains(stem)) name = "_" + name;

        if (name.Length == 0) return "download";
        if (name.Length > 120)
        {
            string ext = Path.GetExtension(name);
            if (ext.Length > 20) ext = "";
            name = name[..(120 - ext.Length)] + ext;
        }
        return name;
    }

    /// <summary>
    /// 組出不撞名的完整路徑：同名檔存在時依序嘗試「名稱 (1).ext」「名稱 (2).ext」。
    /// <paramref name="exists"/> 由呼叫端提供（測試可注入），避免這裡直接碰磁碟。
    /// </summary>
    public static string UniquePath(string folder, string fileName, Func<string, bool> exists)
    {
        string safe = SanitizeFileName(fileName);
        string candidate = Path.Combine(folder, safe);
        if (!exists(candidate)) return candidate;

        string stem = Path.GetFileNameWithoutExtension(safe);
        string ext = Path.GetExtension(safe);
        for (int i = 1; i < 1000; i++)
        {
            candidate = Path.Combine(folder, $"{stem} ({i}){ext}");
            if (!exists(candidate)) return candidate;
        }
        // 1,000 個同名檔還不罷休就加時間戳——寧可名字醜，也不要覆蓋別人的檔案。
        return Path.Combine(folder, $"{stem} ({DateTime.Now:HHmmssfff}){ext}");
    }

    private static Uri? SafeUri(string text)
        => Uri.TryCreate(text, UriKind.Absolute, out var u) ? u : null;
}
