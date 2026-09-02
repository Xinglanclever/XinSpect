namespace XinSpect;

/// <summary>
/// 規格摘要要用到的欄位。刻意做成一個扁平的紀錄，讓產生文字這件事變成可測的純函式，
/// 也讓「哪些欄位會被複製出去」在型別上就看得一清二楚。
/// </summary>
/// <remarks>
/// <b>刻意不含任何可識別這台機器的東西</b>：沒有電腦名稱、使用者名稱、主機板序號、
/// 系統 UUID、磁碟序號。這份摘要的用途是貼到論壇或聊天室問人，那些欄位對回答問題毫無幫助，
/// 卻會被一起貼出去。
/// </remarks>
public sealed record SpecFacts
{
    public string Os { get; init; } = "";
    public string OsVersion { get; init; } = "";
    public string Cpu { get; init; } = "";
    public int Cores { get; init; }
    public int Threads { get; init; }
    public double CpuMaxMHz { get; init; }
    public string Board { get; init; } = "";
    public string Bios { get; init; } = "";
    public double RamGB { get; init; }
    public string RamDetail { get; init; } = "";
    public string Gpu { get; init; } = "";
    public string SystemDisk { get; init; } = "";
    public string Display { get; init; } = "";
}

/// <summary>
/// 「複製規格摘要」的文字產生器（純函式）。
/// </summary>
/// <remarks>
/// 為什麼要有這個：整機報告匯出（HTML／Markdown／純文字）是給自己留存的完整檔案；
/// 而人在問問題時要的是<b>七八行貼得進聊天室</b>的東西。兩者用途不同，不該互相湊合。
/// <para>
/// 寫作原則與全站一致：讀不到的欄位寫「—」，不猜、不省略成空白。省略會讓看的人以為
/// 那一項不存在（例如沒有獨立顯示卡與「讀不到顯示卡」是兩件事）。
/// </para>
/// </remarks>
public static class SpecSummary
{
    /// <summary>讀不到時一律用這個，而不是空字串或 0。</summary>
    public const string Unknown = "—";

    public static string Build(SpecFacts f)
    {
        var lines = new List<string>
        {
            "【曦覽 XinSpect 規格摘要】",
            $"作業系統：{Text(f.Os)}{Suffix(f.OsVersion)}",
            $"處理器：{Text(f.Cpu)}{CoreText(f.Cores, f.Threads)}{ClockText(f.CpuMaxMHz)}",
            $"記憶體：{RamText(f.RamGB)}{Suffix(f.RamDetail)}",
            $"顯示卡：{Text(f.Gpu)}",
            $"主機板：{Text(f.Board)}{Suffix(f.Bios, "BIOS ")}",
            $"系統碟：{Text(f.SystemDisk)}",
            $"顯示器：{Text(f.Display)}",
        };
        return string.Join(Environment.NewLine, lines);
    }

    private static string Text(string? s)
        => string.IsNullOrWhiteSpace(s) || s == Unknown ? Unknown : s.Trim();

    /// <summary>括號補述；沒有內容就整段不出現（而不是留一對空括號）。</summary>
    private static string Suffix(string? s, string prefix = "")
        => string.IsNullOrWhiteSpace(s) || s == Unknown ? "" : $"（{prefix}{s.Trim()}）";

    private static string CoreText(int cores, int threads)
        => cores > 0 && threads > 0 ? $" ・ {cores} 核 {threads} 執行緒"
         : cores > 0 ? $" ・ {cores} 核"
         : "";

    private static string ClockText(double mhz)
        => mhz > 0 ? $" ・ 最高 {mhz / 1000.0:0.0#} GHz" : "";

    private static string RamText(double gb)
        => gb > 0 ? $"{gb:0.#} GB" : Unknown;
}
