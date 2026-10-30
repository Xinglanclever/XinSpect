using System.Reflection;

namespace XinSpect;

/// <summary>
/// 版本與品名的單一來源：一律從組件版本讀取，避免版號散落在各個畫面的字面字串裡。
/// 版號本身定義於 XinSpect.csproj 的 &lt;Version&gt;，改一處即可全體同步。
/// </summary>
public static class AppInfo
{
    /// <summary>純版號（如 2.0.0；已去除 Git 雜湊後綴）。</summary>
    public static string Version { get; } = Read();

    /// <summary>短版號（僅主版與次版，如 2.0），供標題列等空間有限處使用。</summary>
    public static string ShortVersion
    {
        get
        {
            var p = Version.Split('.');
            return p.Length >= 2 ? $"{p[0]}.{p[1]}" : Version;
        }
    }

    public static string Name => "曦覽 XinSpect";
    public static string VersionText => $"版本 {ShortVersion}";

    /// <summary>
    /// 官方網站的位址。<b>這是唯一來源</b>——關於頁、內建瀏覽器起始頁、三份 README 都指到這裡。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 為什麼要集中成一個常數：官網換網域時，散落在 XAML、HTML 與三份 README 的字串一定會漏掉一處，
    /// 而漏掉的那一處<b>不會紅燈</b>——它只會是一個點不開的連結。所以位址寫在這裡，
    /// <c>AppInfoTests</c> 逐檔比對「這些地方寫的是同一個字串」。
    /// </para>
    /// <para>
    /// 只有 <c>https</c>：站上沒有需要 <c>http</c> 的東西，而本專案對外連線一律走加密通道。
    /// 結尾不帶斜線，讓字串比對與顯示都是同一個形狀。
    /// </para>
    /// </remarks>
    public const string OfficialSiteUrl = "https://xinspect-browser-preview.wild-chimpanzee.workers.dev";

    /// <summary>官網位址的顯示形狀（去掉 <c>https://</c>，給空間有限處用）。</summary>
    public static string OfficialSiteDisplay => OfficialSiteUrl.Replace("https://", "", StringComparison.Ordinal);

    /// <summary>
    /// 版本代號：2.5 起為 Olympus（奧林帕斯山／奧林匹斯山）。
    /// 舊代號 Everest（額菲爾士峰／珠穆朗玛峰）已於 2026-10-03 更名——繁中彩蛋顯示
    /// 「奧林帕斯山」、簡中轉換表顯示「奧林匹斯山」（見 MainWindow 與 ZhTerms）。
    /// </summary>
    public static string VersionCodename(bool simplified)
        => "Olympus";

    public static string WindowTitle(bool simplified)
        => $"XinSpect v{ShortVersion} {VersionCodename(simplified)}";

    public static string HeaderTitle(bool simplified)
        => $"XinSpect v{Version} {VersionCodename(simplified)}";

    public static string VersionMetadata(bool simplified)
        => $"版本 {Version} ・ {VersionCodename(simplified)} ・ 便攜單一執行檔";

    private static string Read()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            string? info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info))
            {
                int plus = info.IndexOf('+');
                return plus > 0 ? info[..plus] : info;
            }
            var v = asm.GetName().Version;
            return v is null ? "—" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
        catch { return "—"; }
    }
}
