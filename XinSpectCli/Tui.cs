using System.Text;

namespace XinSpectCli;

/// <summary>
/// xsc 的終端介面件：Claude Code 式的「有頭有臉」＋瑞士現代主義平面語彙。
/// </summary>
/// <remarks>
/// <para>
/// <b>畫風裁決（使用者欽定）：</b>瑞士平面——直角（不用圓角框，那不是瑞士）、
/// 紅白黑三色、等寬大寫小標籤、大量留白；歡迎面板參考 Claude Code 的開場框，
/// 但邊角是方的、左側帶一道瑞士紅短規。
/// </para>
/// <para>
/// <b>對齊規則：</b>ANSI escape 不佔顯示寬度；所有需要計寬的字串先過
/// <see cref="VisibleLength"/>（CJK 記 2 格）。表格欄寬由內容自動撐開。
/// </para>
/// </remarks>
internal static class Tui
{
    // ── 色票：瑞士紅／瑞士藍／灰墨（深底終端上不刺眼的層級） ─────────────
    public const string Reset = "\x1b[0m";
    public const string Bold = "\x1b[1m";
    public const string Dim = "\x1b[90m";
    public const string SwissRed = "\x1b[91m";
    public const string SwissBlue = "\x1b[94m";
    public const string SwissGreen = "\x1b[92m";
    public const string Amber = "\x1b[93m";
    public const string RedBg = "\x1b[101m";
    public const string Invert = "\x1b[7m";

    private const string HideCursor = "\x1b[?25l";
    private const string ShowCursor = "\x1b[?25h";
    private const string ClearLine = "\x1b[2K";

    /// <summary>旋轉子幀（braille 點字；Windows Terminal 原生支援）。</summary>
    private static readonly string[] SpinnerFrames = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];

    // ── 寬度計算 ──────────────────────────────────────────────────────────

    /// <summary>可見寬度：剝掉 ANSI escape，CJK 記 2 格。</summary>
    public static int VisibleLength(string s)
    {
        int w = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\x1b')
            {
                while (i < s.Length && s[i] != 'm') i++;   // 吃到 escape 序列結尾
                continue;
            }
            w += char.GetUnicodeCategory(s[i]) == System.Globalization.UnicodeCategory.OtherLetter ? 2 : 1;
        }
        return w;
    }

    private static string Pad(string s, int width)
    {
        int gap = width - VisibleLength(s);
        return gap <= 0 ? s : s + new string(' ', gap);
    }

    private static int ConsoleWidth()
    {
        try { return Math.Max(40, Console.WindowWidth - 1); }
        catch { return 100; }
    }

    // ── 1) 開場歡迎面板 ───────────────────────────────────────────────────

    /// <summary>開場：直角框＋左側瑞士紅短規＋紅底徽章＋旋轉小知識。參考 Claude Code 的開場框。</summary>
    private static readonly string[] Tips =
    [
        "facts 之後，page cpu、domain nvme 都走同一份快取，不必重掃",
        "msr 0x19C 是 IA32_THERM_STATUS——CPU 溫度目標的源頭（唯讀）",
        "pci dump 0 看的是 Host Bridge 的 256B 設定空間原貌",
        "top 2 會持續刷新；Ctrl+C 停下來，提示符會好好回到你手上",
        "changelog 1 只看最新一版改了什麼",
    ];

    public static void Welcome(string version)
    {
        int width = Math.Min(78, ConsoleWidth());
        string line = new('─', width - 2);
        var tip = Tips[Random.Shared.Next(Tips.Length)];

        Console.WriteLine();
        Console.WriteLine($"{Dim}┌{line}┐{Reset}");

        Row("");
        Row($"  {SwissRed}▌{Reset}{SwissRed}{Bold}✻{Reset} {Bold}曦覽終端機{Reset} {Dim}XINSPECT CONSOLE{Reset}");
        Row("");
        Row($"  {RedBg}{Bold} 唯讀 {Reset} {RedBg}{Bold} 不寫硬體 {Reset} {RedBg}{Bold} 不連網 {Reset}    {Bold}v{version}{Reset}");
        Row($"  {new string('─', 26)}");
        Row($"  {Bold}快速開始{Reset}");
        Row($"    {SwissGreen}facts{Reset}        採集本機硬體事實（一切查詢的底料）");
        Row($"    {SwissGreen}sys{Reset} / {SwissGreen}top 2{Reset}    即時一行／持續刷新表");
        Row($"    {SwissGreen}pages{Reset}        GUI 65 頁的終端對照表");
        Row($"    {SwissGreen}help{Reset}         完整指令清單");
        Row("");
        Row($"  {Dim}小知識：{tip}{Reset}");
        Row($"  {Dim}↑↓ 歷史 ・ Tab 補全 ・ exit 離開{Reset}");

        Console.WriteLine($"{Dim}└{line}┘{Reset}");
        Console.WriteLine();

        void Row(string content)
        {
            int pad = Math.Max(0, width - 2 - 2 - VisibleLength(content) - 2);
            Console.WriteLine($"{Dim}│{Reset}  {content}{new string(' ', pad)}  {Dim}│{Reset}");
        }
    }

    // ── 2) 帶標題的方框面板 ───────────────────────────────────────────────

    /// <summary>帶標題面板：標題嵌在上邊線（┌── TITLE ──…），內容行左對齊。</summary>
    public static void Frame(string title, IEnumerable<string> lines)
    {
        int width = Math.Min(78, ConsoleWidth());
        var body = lines.ToList();
        int inner = width - 2;

        string titleText = $" {title.ToUpperInvariant()} ";
        int titleStart = 3;
        string top = "┌──" + titleText + new string('─', Math.Max(1, inner - 3 - titleText.Length)) + "┐";
        Console.WriteLine($"{Dim}{top}{Reset}");
        foreach (var l in body)
        {
            int pad = Math.Max(0, inner - 2 - VisibleLength(l));
            Console.WriteLine($"{Dim}│{Reset} {l}{new string(' ', pad)} {Dim}│{Reset}");
        }
        Console.WriteLine($"{Dim}└{new string('─', inner)}┘{Reset}");
    }

    // ── 3) 表格 ───────────────────────────────────────────────────────────

    /// <summary>自動欄寬表格：表頭反白、 直角細線；ANSI 不破壞對齊。</summary>
    public static void Table(string[] headers, IEnumerable<string[]> rows)
    {
        var data = rows.ToList();
        int cols = headers.Length;
        var widths = new int[cols];
        for (int c = 0; c < cols; c++)
            widths[c] = Math.Max(VisibleLength(headers[c]), data.Count == 0 ? 0 : data.Max(r => c < r.Length ? VisibleLength(r[c]) : 0));
        for (int c = 0; c < cols; c++) widths[c] = Math.Min(widths[c], 60);

        Console.WriteLine("  " + string.Join($"{Dim} ┆ {Reset}", headers.Select((h, c) => Bold + Pad(h, widths[c]) + Reset)));
        Console.WriteLine("  " + string.Join($"{Dim}+{Reset}", widths.Select(w => new string('─', w + 2))));
        foreach (var r in data)
            Console.WriteLine("  " + string.Join($"{Dim} ┆ {Reset}",
                Enumerable.Range(0, cols).Select(c => Pad(c < r.Length ? r[c] : "", widths[c]))));
    }

    // ── 4) 提示符與輸入 ───────────────────────────────────────────────────

    /// <summary>彩色提示符：紅 xs ＋ 藍 ▸。</summary>
    public static void Prompt() => Console.Write($"{SwissRed}{Bold}xs{Reset}{SwissBlue}▸{Reset} ");

    /// <summary>印提示符並讀一行（重導向時退回 ReadLine）。</summary>
    public static string? ReadInput()
    {
        if (Console.IsInputRedirected) return Console.ReadLine();
        Prompt();
        return Console.ReadLine();
    }

    // ── 5) 底部狀態列 ─────────────────────────────────────────────────────

    /// <summary>整行反白狀態列：左狀態、右署名；寬度不足先裁左側。</summary>
    public static void Footer(string left, string right)
    {
        int width = ConsoleWidth();
        int leftW = VisibleLength(left), rightW = VisibleLength(right);
        int padding = width - leftW - rightW - 2;
        if (padding < 1)
        {
            left = Truncate(left, Math.Max(10, width - rightW - 6));
            leftW = VisibleLength(left);
            padding = Math.Max(1, width - leftW - rightW - 2);
        }
        Console.WriteLine($"{Invert} {left}{new string(' ', padding)}{right} {Reset}");
    }

    private static string Truncate(string s, int max)
    {
        if (VisibleLength(s) <= max) return s;
        var sb = new StringBuilder();
        int w = 0;
        foreach (char ch in s)
        {
            int cw = char.GetUnicodeCategory(ch) == System.Globalization.UnicodeCategory.OtherLetter ? 2 : 1;
            if (w + cw > max - 1) break;
            sb.Append(ch);
            w += cw;
        }
        return sb.ToString() + "…";
    }

    // ── 6) Spinner ────────────────────────────────────────────────────────

    private static CancellationTokenSource? _cts;
    private static Task? _spinnerTask;
    private static readonly object Sync = new();

    /// <summary>開始轉圈（幕後執行緒每 80ms 重寫同一行）。</summary>
    public static void SpinnerStart(string label)
    {
        lock (Sync)
        {
            if (_cts is not null) return;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            Console.Write(HideCursor);
            _spinnerTask = Task.Run(async () =>
            {
                int i = 0;
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        string frame = SpinnerFrames[i++ % SpinnerFrames.Length];
                        Console.Write($"\r{ClearLine}{SwissBlue}{frame}{Reset} {label}");
                        await Task.Delay(80, token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { }
            }, token);
        }
    }

    /// <summary>停止轉圈；doneMessage 非空時以綠 ✔ 取代該行。</summary>
    public static void SpinnerStop(string? doneMessage = null)
    {
        lock (Sync)
        {
            if (_cts is null) return;
            _cts.Cancel();
            try { _spinnerTask?.Wait(250); } catch { }
            _cts.Dispose();
            _cts = null;
            _spinnerTask = null;
            Console.Write($"\r{ClearLine}{ShowCursor}");
            if (!string.IsNullOrEmpty(doneMessage))
                Console.WriteLine($"{SwissGreen}✔{Reset} {doneMessage}");
        }
    }

    // ── 7) 進度條與 KPI ───────────────────────────────────────────────────

    /// <summary>文字進度條：████░░░░，超過門檻整條轉警示色。</summary>
    public static string Bar(double? value, double max, int width, double? warnAbove = null, double? critAbove = null)
    {
        if (value is null) return $"{Dim}{new string('·', width)}{Reset}";
        double v = Math.Clamp(value.Value, 0, max);
        int filled = (int)Math.Round(v / max * width);
        string color = critAbove is not null && value >= critAbove ? SwissRed
                     : warnAbove is not null && value >= warnAbove ? Amber
                     : SwissGreen;
        return $"{color}{new string('█', filled)}{Dim}{new string('░', width - filled)}{Reset}";
    }

    /// <summary>KPI 列：標題、帶色數值、進度條——sys／top 共用。</summary>
    public static void Kpi(string label, double? value, string unit, double max,
        int barWidth = 24, double? warnAbove = null, double? critAbove = null)
    {
        string v = value is null ? $"{Dim}——{Reset}" : $"{Bold}{value:0.#}{Reset}{Dim}{unit}{Reset}";
        Console.WriteLine($"  {Pad(label, 12)}{v}");
        Console.WriteLine($"  {Pad("", 12)}{Bar(value, max, barWidth, warnAbove, critAbove)}");
    }

    // ── 8) 節區標頭 ───────────────────────────────────────────────────────

    /// <summary>節區標頭：瑞士紅方塊 ＋ 大寫等寬標籤＋右側灰說明。</summary>
    public static void Section(string label, string? note = null)
    {
        string n = note is null ? "" : $"  {Dim}{note}{Reset}";
        Console.WriteLine();
        Console.WriteLine($"{SwissRed}{Bold} ■{Reset} {Bold}{label.ToUpperInvariant()}{Reset}{n}");
    }
}
