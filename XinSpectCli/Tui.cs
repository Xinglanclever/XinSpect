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
    private const string CarriageReturn = "\r";
    private const string Esc = "\x1b";

    /// <summary>旋轉子幀（braille 點字；Windows Terminal 原生支援）。</summary>
    private static readonly string[] SpinnerFrames = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];

    // ── 寬度計算 ──────────────────────────────────────────────────────────

    /// <summary>可見寬度：剝掉 ANSI escape，CJK／全形字記 2 格。</summary>
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
            char c = s[i];
            // CJK 與全形字一律 2 格：OtherLetter（漢字、假名）＋全形標點 FF01-FF60、CJK 標點 3000-303F。
            // 少算這些會讓歡迎框的右框線被擠出對位（實測：全形「／」算 1 格、畫 2 格，框線飄出）。
            w += char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.OtherLetter
                 || (c >= '\uFF01' && c <= '\uFF60')
                 || (c >= '\u3000' && c <= '\u303F')
                 || (c >= '\u2190' && c <= '\u2199')   // ↑↓ 箭頭：WT＋CJK 字型下渲染 2 格
                 || (c >= '\u2733' && c <= '\u2743')   // ✢✶✻✽ 星芒：East Asian Wide
                 ? 2 : 1;
        }
        return w;
    }

    internal static string Pad(string s, int width)
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

    /// <summary>品牌紅（#E30613 的 truecolor；歡迎框框線、✻、⏺、提示符專用，不作內文著色）。</summary>
    public const string BrandRed = "[38;2;227;6;19m";

    /// <summary>Claude Code 式星芒 spinner 的幀序列（往返循環）。</summary>
    private static readonly string[] SparkFrames = ["∙", "∘", "○", "◎", "☼", "◎", "○", "∘"];   // 破曉序列：訊號點擴張為日輪再收回

    /// <summary>採集用俏皮動詞（繁中・硬體語境・每次隨機抽一個）。</summary>
    private static readonly string[] Verbs =
    [
        "走訪 1,178 把靜態事實鍵中", "展開 69 個動態硬體家族中", "隔窗端詳 Ring0 暫存器中",
        "沿著 PCI 組態空間巡邏中", "旁聽 WMI 與 SMBIOS 事實中", "屏息讀取 LHM 感測器事實中",
        "敲門詢問 Bridge IPC 管道中", "探詢 Redfish 帶外管理介面中", "逐項核對安全性合規基準中",
        "計算 Merkle 事實證據樹雜湊中", "對齊 GUI 65 頁結構索引中", "檢索單一硬體事實路徑中",
        "清點匯流排拓撲與裝置樹中", "確認零寫入唯讀封條狀態中",
    ];

    /// <summary>
    /// 歡迎框定稿（Gemini 依 xsc 真實指令集設計）：吉祥物 ☼（U+263C 白日光芒＝「曦」），
    /// 瑞士紅唯一強調色、無紅底徽章（紅字封印）、快速開始 4 行對應真實指令集、
    /// 標題行右對齊口號、框外誠信註記。右框線以 ANSI CHA 絕對定位釘死。
    /// </summary>
    public static void Welcome(string version, int staticKeys, int dynamicFamilies)
    {
        const int width = 66;
        int inner = width - 2;

        string Top() => $"{BrandRed}╭{new string('─', inner)}╮{Reset}";
        string Bot() => $"{BrandRed}╰{new string('─', inner)}╯{Reset}";
        string Row(string body) => $"{BrandRed}│{Reset} {body}[{width}G{BrandRed}│{Reset}";

        string cwd = Environment.CurrentDirectory;
        if (cwd.Length > 46) cwd = cwd[..20] + "…" + cwd[^24..];

        Console.WriteLine();
        Console.WriteLine(Top());

        // 標題行：☼ 品牌符號＋白粗體品牌名＋dim 版號；口號右對齊（CHA 定位，不受字寬計算影響）
        string slogan = "硬體的事實，不編故事。";
        int sloganCol = width - VisibleLength(slogan);   // 口號末格在 width-1
        Console.Write($"{BrandRed}│{Reset} {BrandRed}{Bold}☼{Reset} {Bold}曦覽終端機{Reset} {Dim}xsc v{version}{Reset}" + Esc + "[" + sloganCol + "G" + Dim + slogan + Reset + Esc + "[" + width + "G" + BrandRed + "│" + Reset + Environment.NewLine);

        Console.WriteLine(Row($"   {BrandRed}{Bold}唯讀{Reset}{Dim} · {Reset}{BrandRed}{Bold}不寫硬體{Reset}{Dim} · {Reset}{BrandRed}{Bold}不連網{Reset}"));
        Console.WriteLine(Row(""));
        Console.WriteLine(Row($"{Bold}facts{Reset}{new string(' ', 7)}{Dim}採集 {staticKeys:N0} 靜態鍵＋{dynamicFamilies} 動態家族 (或 fact <關鍵字>){Reset}"));
        Console.WriteLine(Row($"{Bold}dash{Reset}{new string(' ', 8)}{Dim}全螢幕即時儀表板 (q 離開 · 另有 sys 與 top [秒]){Reset}"));
        Console.WriteLine(Row($"{Bold}pages{Reset}{new string(' ', 7)}{Dim}對照 GUI 全頁 (另有 page · domain · keys){Reset}"));
        Console.WriteLine(Row($"{Bold}book{Reset}{new string(' ', 7)}{Dim}曦覽百科全書：15 部 362 節，離線可讀{Reset}"));
        Console.WriteLine(Row($"{Bold}help{Reset}{new string(' ', 8)}{Dim}指令書與百科全書 (help book){Reset}"));
        Console.WriteLine(Row(""));
        Console.WriteLine(Row($"{Dim}cwd: {cwd}{Reset}"));
        Console.WriteLine(Bot());
        Console.WriteLine($"  {Dim}註：所有探針嚴格唯讀；通道未就緒時必顯示 [ -- ]，絕不推測或編造。{Reset}");
        Console.WriteLine();
        Console.WriteLine($"  {Dim}輸入 facts 採集全機事實，或輸入 sys、dash、pages、msr <索引>…{Reset}");
        Console.WriteLine($"  {Dim}唯讀 · 不寫硬體 · 不連網  │  ↑↓ 歷史 · Tab 補全 · help 指令 · exit 離開{Reset}");
        Console.WriteLine();
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
    public static void Prompt() => Console.Write($"{BrandRed}{Bold}>{Reset} ");

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

    // ── 5.5) 走勢圖・獨立螢幕・徽章 ────────────────────────────────────────

    private const string SparkChars = "▁▂▃▄▅▆▇█";

    /// <summary>迷你走勢圖：最後 width 個讀值畫成 ▁▂▃▅▇。</summary>
    public static string Sparkline(IReadOnlyList<double?> history, double max, int width)
    {
        var take = history.Skip(Math.Max(0, history.Count - width)).ToList();
        if (take.Count == 0) return new string('·', width);
        var sb = new StringBuilder();
        foreach (var v in take)
        {
            if (v is null) { sb.Append('·'); continue; }
            int idx = Math.Clamp((int)(Math.Clamp(v.Value, 0, max) / max * (SparkChars.Length - 1)), 0, SparkChars.Length - 1);
            sb.Append(SparkChars[idx]);
        }
        return sb.ToString();
    }

    /// <summary>進入獨立螢幕（不留捲動殘跡）。</summary>
    public static void AltScreenEnter() => Console.Write("[?1049h[2J[H" + HideCursor);

    /// <summary>離開獨立螢幕，回到原本的捲動內容。</summary>
    public static void AltScreenExit() => Console.Write(ShowCursor + "[?1049l");

    /// <summary>色塊徽章。</summary>
    public static string Badge(string text, string bg, string fg) => $"{bg}{fg} {text} {Reset}";

    /// <summary>細分隔線。</summary>
    public static void Separator()
        => Console.WriteLine($"  {Dim}{new string('·', Math.Min(72, ConsoleWidth() - 4))}{Reset}");

    // ── 6) Spinner（Claude Code 式：星芒往返＋俏皮動詞＋計時） ────────────

    private static CancellationTokenSource? _cts;
    private static Task? _spinnerTask;
    private static readonly object Sync = new();
    private static readonly System.Diagnostics.Stopwatch SpinnerClock = new();

    /// <summary>開始轉圈：星芒往返循環＋隨機動詞；2 秒起顯示經過秒數與 esc 提示。label 留空時抽動詞。</summary>
    public static void SpinnerStart(string? label = null)
    {
        lock (Sync)
        {
            if (_cts is not null) return;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            string verb = label ?? Verbs[Random.Shared.Next(Verbs.Length)];
            SpinnerClock.Restart();
            Console.Write(HideCursor);
            _spinnerTask = Task.Run(async () =>
            {
                // 幀序列往返：✢→✶→✻→✽→✻→✶，120ms 一幀
                int[] seq = [0, 1, 2, 3, 2, 1];
                int i = 0;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        string frame = SparkFrames[seq[i++ % seq.Length]];
                        double secs = sw.Elapsed.TotalSeconds;
                        string tail = secs >= 2 ? $" ({secs:0}s{Dim} · esc 中斷{Reset})" : "";
                        Console.Write(CarriageReturn + ClearLine + $"{BrandRed}{frame}{Reset} {BrandRed}{verb}{Reset}{Dim}…{Reset}{Dim}{tail}{Reset}");
                        await Task.Delay(100, token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { }
            }, token);
        }
    }

    /// <summary>停止轉圈；doneMessage 非空時以品牌紅 ⏺ 開頭輸出結果行（Claude Code 式）。</summary>
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
            SpinnerClock.Stop();
            Console.Write(CarriageReturn + ClearLine + ShowCursor);
            if (!string.IsNullOrEmpty(doneMessage))
                Console.WriteLine($"{BrandRed}⏺{Reset} {doneMessage}");
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
