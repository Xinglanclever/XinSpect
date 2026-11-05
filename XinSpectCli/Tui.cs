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
    // ── 色票：瑞士紅／瑞士藍／灰墨（深底終端上不刺眼的層級）——定義在上面 Legacy 區塊 ──

    /// <summary>
    /// 純文字相容版（Windows 10／Server 2016/2019/2022 的傳統主控台）：
    /// 以 <c>-p:DefineConstants=XINSPECT_LEGACY</c> 編譯。無色彩、無框線、無 CHA 定位、
    /// ASCII spinner——只靠 \r 與退格控版面，任何主控台都印得出來。
    /// </summary>
#if XINSPECT_LEGACY
    public const bool Legacy = true;
#else
    public const bool Legacy = false;
#endif

    // 以下「色彩與游標控制」全部走屬性而非 const：legacy 版一律回空字串，
    // 呼叫端的寫法（Tui.Red、$"{Dim}…"）完全不用改。
    public static string Reset => Legacy ? "" : "\x1b[0m";
    public static string Bold => Legacy ? "" : "\x1b[1m";
    public static string Dim => Legacy ? "" : "\x1b[90m";
    public static string SwissRed => Legacy ? "" : "\x1b[91m";
    public static string SwissBlue => Legacy ? "" : "\x1b[94m";
    public static string SwissGreen => Legacy ? "" : "\x1b[92m";
    public static string Amber => Legacy ? "" : "\x1b[93m";
    public static string RedBg => Legacy ? "" : "\x1b[101m";
    public static string Invert => Legacy ? "" : "\x1b[7m";
    public static string BrandRed => Legacy ? "" : "\x1b[38;2;227;6;19m";

    private static string HideCursor => Legacy ? "" : "\x1b[?25l";
    private static string ShowCursor => Legacy ? "" : "\x1b[?25h";
    private static string ClearLine => Legacy ? "" : "\x1b[2K";
    private const string CarriageReturn = "\r";
    public const string Esc = "\x1b";

    /// <summary>旋轉子幀（braille 點字；Windows Terminal 原生支援）。</summary>
    private static readonly string[] SpinnerFrames = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];

    // ── 舊版 conhost 的 VT 開關 ────────────────────────────────────────────
    // Windows 10 傳統主控台（conhost）預設不處理 ANSI/VT 序列，所有 ESC[...m、ESC[nG
    // 會原樣印在畫面上（Windows Terminal 與 Win11 預設開啟，所以開發機看不到）。
    // 這裡在啟動時主動打開 ENABLE_VIRTUAL_TERMINAL_PROCESSING；打不開（重導輸出、
    // 極舊系統）就靜默放棄——不是每個環境都救得動，但不假裝成功。
    private const int StdOutputHandle = -11;
    private const uint EnableVirtualTerminalProcessing = 0x0004;

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern System.IntPtr GetStdHandle(int nStdHandle);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(System.IntPtr hConsoleHandle, out uint lpMode);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(System.IntPtr hConsoleHandle, uint dwMode);

    /// <summary>在輸出任何帶色字串之前呼叫；必須在設定 OutputEncoding 之後——改編碼會重造螢幕緩衝，把 VT 旗標洗掉。</summary>
    public static void EnableAnsi()
    {
        try
        {
            if (Console.IsOutputRedirected) return;
            System.IntPtr h = GetStdHandle(StdOutputHandle);
            if (h == System.IntPtr.Zero || h == (System.IntPtr)(-1)) return;
            if (!GetConsoleMode(h, out uint mode)) return;
            SetConsoleMode(h, mode | EnableVirtualTerminalProcessing);
        }
        catch { /* 非 Windows 或無主控台：序列會印出來，但程式不該因此死 */ }
    }

    // ── 寬度計算 ──────────────────────────────────────────────────────────

    /// <summary>依可見寬度裁字串（超出以 … 收尾）；給表格窄寬度防折行用。</summary>
    private static string TruncToWidth(string s, int max)
    {
        if (VisibleLength(s) <= max) return s;
        var sb = new System.Text.StringBuilder();
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
    // BrandRed 已上移到 Legacy 區塊（屬性）

    /// <summary>Claude Code 式星芒 spinner 的幀序列（往返循環）。</summary>
    // 破曉序列：訊號點擴張為日輪再收回；legacy 版用 ASCII（舊主控台字型未必有這些字形）
    private static readonly string[] SparkFrames = Legacy
        ? ["*", "o", ".", "o"]
        : ["∙", "∘", "○", "◎", "☼", "◎", "○", "∘"];

    /// <summary>採集用俏皮動詞（繁中・硬體語境・每次隨機抽一個）。</summary>
    private static readonly string[] Verbs =
    [
        "走訪 1,178 把靜態事實鍵中", "展開 69 個動態硬體家族中", "隔窗端詳 Ring0 暫存器中",
        "沿著 PCI 組態空間巡邏中", "旁聽 WMI 與 SMBIOS 事實中", "屏息讀取 LHM 感測器事實中",
        "敲門詢問 Bridge IPC 管道中", "探詢 Redfish 帶外管理介面中", "逐項核對安全性合規基準中",
        "計算 Merkle 事實證據樹雜湊中", "對齊 GUI 65 頁結構索引中", "檢索單一硬體事實路徑中",
        "清點匯流排拓撲與裝置樹中", "確認零寫入唯讀封條狀態中",
    ];

    /// <summary>吉祥物候選。A＝我的設計（原版 9×5 破曉日輪的精修）；B＝Gemini 設計。
    /// mascot 指令可逐一預覽動畫，選定後把 MascotFrames 指向該候選。</summary>
    public static readonly string[][][] MascotCandidates =
    [
        // ── 候選 A（原版，採用）：日核＋八向光線，破曉兩幀 ──
        new[]
        {
            new[] { "   ▄█▄   ", "▄▀ ███ ▀▄", "█ ▐███▌ █", "▀▄ ███ ▄▀", "   ▀█▀   " },
            new[] { "    █    ", "  ▄███▄  ", " ▐█████▌ ", "  ▀███▀  ", "    █    " },
        },
        // ── 候選 B（Gemini 設計）：「曦輪」核心＋留白雙眼；半格位移＋單雙線切換＝日珥呼吸＋眨眼 ──
        new[]
        {
            new[] { "   ▄ ▄ ▄   ", "  ▄█████▄  ", " ▐███████▌ ", "─▐█ ███ █▌─", " ▐███████▌ ", "  ▀█████▀  ", "   ▀ ▀ ▀   " },
            new[] { " ▀  ▀█▀  ▀ ", " ▀▄█████▄▀ ", "─▐███████▌─", "═▐█▀███▀█▌═", "─▐███████▌─", " ▄▀█████▀▄ ", " ▄  ▄█▄  ▄ " },
        },
    ];

    /// <summary>目前選用的候選（預設 A）。</summary>
    public static int MascotChoice;

    public static string[][] MascotFrames => MascotCandidates[MascotChoice];

    /// <summary>大框字 XINSPECT（cmd 大字 5 行組合字，全寬 51 欄；寬度不足時自動縮為 X＋inspect）。</summary>
    /// <summary>大框字 XINSPECT（ANSI Shadow 圓角雙線風：╗╔╝╚ 自帶圓弧感），7 行 × 8 字母。
    /// 字母寬度：X9 I4 N11 S8 P8 E8 C8 T9；組合時字母間 3 格間距。</summary>
    private static readonly string[][] BannerLetters =
    [
        new[] { "██╗   ██╗", " ╚██╗ ██╔╝", "  ╚███╔╝ ", " ██╔██╗ ", "╚██╝ ╚██╝" },  // X
        new[] { "██╗", "██║", "██║", "██║", "██║" },                                                            // I
        new[] { "███╗   ██╗", "████╗  ██║", "██╔██╗ ██║", "██║╚██╗██║", "██║ ╚████║" },                        // N
        new[] { "███████╗", "██╔════╝", "███████╗", "╚════██║", "███████║" },                                  // S
        new[] { "██████╗ ", "██╔══██╗", "██████╔╝", "██╔═══╝ ", "██║     " },                                  // P
        new[] { "███████╗", "██╔════╝", "█████╗  ", "██╔══╝  ", "███████╗" },                                  // E
        new[] { " ██████╗", "██╔════╝", "██║     ", "██║     ", "╚██████╗" },                                  // C
        new[] { "████████╗", "╚══██╔══╝", "   ██║   ", "   ██║   ", "   ██║   " },                             // T
    ];

    private const string BannerWord = "XINSPECT";

    /// <summary>開場字標 5 行（ANSI Shadow 圓角體＋寬間距）。空隙由呼叫端以 CHA 對齊，不受字寬影響。</summary>
    public static (string Line, int Width)[] XinspectBanner(int gap)
    {
        var rows = new (string Line, int Width)[5];
        for (int r = 0; r < 5; r++)
        {
            var sb = new System.Text.StringBuilder();
            int total = 0;
            for (int i = 0; i < BannerWord.Length; i++)
            {
                var letter = BannerLetters[BannerWord.IndexOf(BannerWord[i])][r];
                sb.Append(letter);
                total += letter.Length;
                if (i < BannerWord.Length - 1)
                {
                    sb.Append(new string(' ', gap));
                    total += gap;
                }
            }
            rows[r] = (sb.ToString(), total);
        }
        return rows;
    }

    /// <summary>動畫太陽吉祥物（兩幀往返數輪後定格）＋雙欄全寬歡迎框（Claude Code 佈局）。
    /// 左欄：吉祥物＋品牌名＋cwd；右欄：快速開始（真實指令集）＋小知識。
    /// 中欄分隔線與右框線都用 CHA 絕對定位——CJK 寬度計算在框線上不可信。</summary>
    public static void Welcome(string version, int staticKeys, int dynamicFamilies)
    {
        if (Legacy)
        {
            // 純文字開場：保留大字 XINSPECT（方塊字元在舊主控台照樣渲染）與全部資訊，
            // 但零色彩、零框線、零定位——右對齊用空格補，不用 CHA。
            int w = 100;
            try { w = Math.Clamp(Console.WindowWidth - 1, 40, 118); } catch { }
            try { Console.Clear(); } catch { }
            if (!Console.IsInputRedirected && w >= 79)
            {
                var banner = XinspectBanner(w >= 88 ? 3 : 2);
                int bannerPad = Math.Max(0, (w - banner[0].Width) / 2);
                foreach (var (lineText, _) in banner)
                    Console.WriteLine(new string(' ', bannerPad) + lineText);
                string by = "by: xinglanclever";
                Console.WriteLine(new string(' ', Math.Max(1, w - by.Length)) + by);
            }
            Console.WriteLine();
            Console.WriteLine($"  曦覽終端機 v{version}（純文字相容版：Windows 10／Server 2016、2019、2022）");
            Console.WriteLine();
            Console.WriteLine($"  facts    採集 {staticKeys.ToString("N0")} 靜態鍵＋{dynamicFamilies} 動態家族");
            Console.WriteLine("  dash     全螢幕即時儀表板 (q 離開 · sys 與 top [秒])");
            Console.WriteLine("  pages    GUI 全頁對照 (page · domain · keys)");
            Console.WriteLine("  help     指令書：分類功能表＋help <指令> 詳解");
            Console.WriteLine("  book     曦覽百科全書：15 部 362 節，離線可讀");
            Console.WriteLine();
            Console.WriteLine("  cwd: " + ShortCwd());
            Console.WriteLine("  硬體的事實，不編故事。");
            Console.WriteLine();
            Console.WriteLine("  輸入 facts 採集全機事實，或輸入 sys、dash、pages、msr <索引>…");
            Console.WriteLine("  唯讀 · 不寫硬體 · 不連網  │  ↑↓ 歷史 · Tab 補全 · help 指令書 · exit 離開");
            Console.WriteLine();
            return;
        }
        int width;
        try { width = Math.Clamp(Console.WindowWidth - 1, 78, 118); }
        catch { width = 110; }
        bool wide = width >= 104;
        int mid = width / 2;

        try { Console.Clear(); } catch { }

        // ── 大框字 XINSPECT（ANSI Shadow 圓角體）＋ by: xinglanclever ──
        if (!Console.IsInputRedirected)
        {
            if (width >= 79)
            {
                int gap = width >= 88 ? 3 : 2;
                var banner = XinspectBanner(gap);
                int bannerPad = Math.Max(0, (width - banner[0].Width) / 2);
                foreach (var (lineText, _) in banner)
                    Console.WriteLine(new string(' ', bannerPad) + BrandRed + lineText + Reset);
                string by = "by: xinglanclever";
                Console.Write(Esc + $"[{width - VisibleLength(by)}G" + Dim + by + Reset);
                Console.WriteLine();
            }
        }

        Console.WriteLine(BoxTop(width, $"{BrandRed}{Bold}☼{Reset} {Bold}曦覽終端機{Reset} {Dim}v{version}{Reset}"));

        var tip = Tips[Random.Shared.Next(Tips.Length)];
        if (wide)
        {
            var left = new List<string>
            {
                "",
                Bold + "Welcome back!" + Reset,
                Dim + "Type facts to scan · help for the book." + Reset,
                "",
                Bold + "曦覽終端機" + Reset,
                Dim + "曦覽 XinSpect Console" + Reset,
                "",
                Dim + "cwd: " + ShortCwd() + Reset,
                Dim + "硬體的事實，不編故事。" + Reset,
                "",
                Dim + "Ready when you are." + Reset,
            };
            var right = new List<string>
            {
                SwissRed + Bold + "快速開始" + Reset + Dim + " ・ 全部指令見 help 指令書",
                Bold + "facts" + Reset + Dim + "    採集 " + staticKeys.ToString("N0") + " 靜態鍵＋" + dynamicFamilies + " 動態家族",
                Bold + "dash" + Reset + Dim + "     全螢幕即時儀表板 (q 離開 · sys 與 top [秒])",
                Bold + "pages" + Reset + Dim + "    GUI 全頁對照 (page · domain · keys)",
                Bold + "help" + Reset + Dim + "     指令書：分類功能表＋help <指令> 詳解",
                Bold + "book" + Reset + Dim + "     曦覽百科全書：15 部 362 節，離線可讀",
                "",
                Dim + new string('─', Math.Max(8, mid - 8)) + Reset,
                SwissRed + Bold + "小知識" + Reset,
                Dim + TruncateTip(tip, mid - 4) + Reset,
            };
            int rows = Math.Max(left.Count, right.Count);
            for (int i = 0; i < rows; i++)
            {
                string l = i < left.Count ? left[i] : "";
                string r = i < right.Count ? right[i] : "";
                Console.WriteLine(BoxTwoCol(width, mid, l, r));
            }
        }
        else
        {
            Console.WriteLine(BoxRow(width, ""));
            Console.WriteLine(BoxRow(width, Bold + "曦覽終端機" + Reset + Dim + " v" + version + Reset));
            Console.WriteLine(BoxRow(width, ""));
            Console.WriteLine(BoxRow(width, Bold + "facts" + Reset + Dim + "  採集 " + staticKeys.ToString("N0") + " 靜態鍵＋" + dynamicFamilies + " 家族"));
            Console.WriteLine(BoxRow(width, Bold + "book" + Reset + Dim + "   曦覽百科全書 ・ help 指令書"));
            Console.WriteLine(BoxRow(width, ""));
            Console.WriteLine(BoxRow(width, Dim + "cwd: " + ShortCwd() + Reset));
            Console.WriteLine(BoxRow(width, Dim + "硬體的事實，不編故事。" + Reset));
        }

        Console.WriteLine(BoxBot(width));
        Console.WriteLine();
        Console.WriteLine($"  {Dim}輸入 facts 採集全機事實，或輸入 sys、dash、pages、msr <索引>…{Reset}");
        Console.WriteLine($"  {Dim}唯讀 · 不寫硬體 · 不連網  │  ↑↓ 歷史 · Tab 補全 · help 指令書 · exit 離開{Reset}");
        Console.WriteLine();
    }

    /// <summary>小知識超出右欄可用寬時以 … 截斷。</summary>
    private static string TruncateTip(string tip, int max)
    {
        if (VisibleLength(tip) <= max) return tip;
        var sb = new System.Text.StringBuilder();
        int w = 0;
        foreach (char c in tip)
        {
            int cw = char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.OtherLetter
                  || (c >= '！' && c <= '｠') ? 2 : 1;
            if (w + cw > max - 1) break;
            sb.Append(c);
            w += cw;
        }
        return sb + "…";
    }

    private static string ShortCwd()
    {
        string cwd = Environment.CurrentDirectory;
        return cwd.Length > 30 ? cwd[..14] + "…" + cwd[^14..] : cwd;
    }

    /// <summary>靜態吉祥物框（預覽用）：單幀＋品牌名，居中。</summary>
    public static string MascotBox(int frame)
    {
        const int width = 46;
        var f = MascotFrames[frame];
        var sb = new System.Text.StringBuilder();
        sb.Append(BoxTop(width, $"{Dim}曦覽終端機{Reset}") + Environment.NewLine);
        foreach (var lineText in f) sb.Append(BoxRow(width, BrandRed + lineText + Reset) + Environment.NewLine);
        sb.Append(BoxBot(width));
        return sb.ToString();
    }

    /// <summary>Claude Code 式輸入框：上方全寬細線＋右對齊狀態行＋紅色 > 提示符。</summary>
    /// <summary>輸入框：兩條全寬長線。width 傳入時用固定寬——長線是長直線，
    /// 不隨每次重繪時的視窗讀值漂移（兩條線不一樣長就是這麼來的）。</summary>
    public static void InputFrame(int? width = null)
    {
        int w = width ?? CurrentWidth();
        Console.WriteLine($"{Dim}{new string('─', w)}{Reset}");
        Console.Write($"{BrandRed}{Bold}>{Reset} ");
    }

    /// <summary>目前可用的行寬（重導或讀不到時 100）——給固定寬度初次取值用。</summary>
    public static int CurrentWidth()
    {
        try { return Math.Max(60, Console.WindowWidth - 1); }
        catch { return 100; }
    }

    // ── 1.9) 框線 helpers（唯一正解：邊框位置一律 CHA 絕對定位，零寬度計算）──
    // 歷史教訓：所有框線錯位都源於「用 VisibleLength 算補白」——全形標點、模糊寬度字元等
    // 模糊寬度字元、使用者終端字型回退，都會讓計算值與實際渲染差 1–2 格。
    // CHA（\x1b[<col>G）把游標釘到絕對欄位，畫不出來就 clamp 到行尾，永不折行、永不錯位。
    // 內文超寬時會被右框線壓住——那是可接受的降級；框線錯位不可接受。

    /// <summary>框頂：╭ + 破折號 + 標題（CHA 覆寫）+ ╮（CHA 釘在 width 列）。</summary>
    public static string BoxTop(int width, string title = "")
    {
        if (Legacy) return title;   // 純文字版：沒有框，標題一行
        var sb = new System.Text.StringBuilder();
        sb.Append(BrandRed).Append('╭').Append(new string('─', Math.Max(1, width - 3)));
        sb.Append(Esc).Append('[').Append(width).Append('G').Append('╮').Append(Reset);
        if (title.Length > 0)
            sb.Append(Esc).Append("[4G").Append(title).Append(Reset);
        return sb.ToString();
    }

    /// <summary>框底：╰ + 破折號 + 附註（CHA 覆寫）+ ╯（CHA 釘在 width 列）。</summary>
    public static string BoxBot(int width, string note = "")
    {
        if (Legacy) return note;   // 純文字版：只留附註
        var sb = new System.Text.StringBuilder();
        sb.Append(BrandRed).Append('╰').Append(new string('─', Math.Max(1, width - 3)));
        sb.Append(Esc).Append('[').Append(width).Append('G').Append('╯').Append(Reset);
        if (note.Length > 0)
            sb.Append(Esc).Append("[").Append(width - VisibleLength(note) - 2).Append('G')
              .Append(Dim).Append(note).Append(Reset);
        return sb.ToString();
    }

    /// <summary>框行：│ + 內文 + │（右框線 CHA 釘在 width 列；內文超寬被框線壓住）。</summary>
    public static string BoxRow(int width, string body)
        => Legacy ? body : $"{BrandRed}│{Reset} {body}{Esc}[{width}G{BrandRed}│{Reset}";

    /// <summary>雙欄框行：│ 左欄 │ 右欄 │（中欄與右框線都 CHA 釘死）。</summary>
    public static string BoxTwoCol(int width, int mid, string left, string right)
        => Legacy ? (left.Length == 0 ? right : right.Length == 0 ? left : left + "　" + right)
                  : $"{BrandRed}│{Reset} {left}{Esc}[{mid}G{BrandRed}│{Reset} {right}{Esc}[{width}G{BrandRed}│{Reset}";

    // ── 2) 帶標題的方框面板 ───────────────────────────────────────────────

    /// <summary>帶標題面板：標題嵌在上邊線（┌── TITLE ──…），內容行左對齊。</summary>
    public static void Frame(string title, IEnumerable<string> lines)
    {
        const int width = 78;
        var body = lines.ToList();

        Console.WriteLine(BoxTop(width, $"{Bold} {title.ToUpperInvariant()} {Reset}"));
        foreach (var l in body)
            Console.WriteLine(BoxRow(width, l));
        Console.WriteLine(BoxBot(width));
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

        // 窄終端防折行：整行超出可用寬時把最後一欄裁短（說明欄被裁好過整表錯位折行）。
        // CHA 釘的是框線不是表格——表格靠等寬字元的 Padding 對齊，折行就是全表錯位。
        int avail;
        try { avail = Math.Max(40, Console.WindowWidth - 3); }
        catch { avail = 117; }
        int overhead = 3 * (cols - 1) + 2;   // 欄間 " ┆ " 與行首縮排
        int sum = widths.Sum() + overhead;
        if (sum > avail)
        {
            int excess = sum - avail;
            int last = cols - 1;
            int shrink = Math.Min(excess, Math.Max(0, widths[last] - 6));
            widths[last] -= shrink;
            excess -= shrink;
            if (excess > 0)
            {
                // 還不夠就從最寬的欄均攤（各留 4 格下限）
                for (int c = 0; c < last && excess > 0; c++)
                {
                    int take = Math.Min(excess, Math.Max(0, widths[c] - 4));
                    widths[c] -= take;
                    excess -= take;
                }
            }
        }

        Console.WriteLine("  " + string.Join($"{Dim} ┆ {Reset}", headers.Select((h, c) => Bold + Pad(h, widths[c]) + Reset)));
        Console.WriteLine("  " + string.Join($"{Dim}+{Reset}", widths.Select(w => new string('─', w + 2))));
        foreach (var r in data)
            Console.WriteLine("  " + string.Join($"{Dim} ┆ {Reset}",
                Enumerable.Range(0, cols).Select(c => Pad(TruncToWidth(c < r.Length ? r[c] : "", widths[c]), widths[c]))));
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
        int padding = width - leftW - rightW - 3;   // 頭尾各留 1 格餘量：整行剛好等於終端寬度會折行
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
    public static void AltScreenEnter()
    {
        if (Legacy) { try { Console.Clear(); } catch { } return; }
        Console.Write("\x1b[?1049h\x1b[2J\x1b[H" + HideCursor);
    }

    /// <summary>離開獨立螢幕，回到原本的捲動內容。</summary>
    public static void AltScreenExit()
    {
        if (Legacy) return;
        Console.Write(ShowCursor + "\x1b[?1049l");
    }

    /// <summary>色塊徽章。</summary>
    public static string Badge(string text, string bg, string fg) => $"{bg}{fg} {text} {Reset}";

    /// <summary>細分隔線。</summary>
    public static void Separator()
        => Console.WriteLine($"  {Dim}{new string('·', Math.Min(72, ConsoleWidth() - 4))}{Reset}");

    // ── 6) Spinner（Claude Code 式：星芒往返＋俏皮動詞＋計時） ────────────

    private static CancellationTokenSource? _cts;
    private static Task? _spinnerTask;
    private static int _spinPad;   // legacy 擦行用：上一拍的可見寬度
    private static readonly object Sync = new();
    private static readonly System.Diagnostics.Stopwatch SpinnerClock = new();
    private const int ScopeWidth = 24;
    private static readonly List<double> ScopeSamples = [];
    private static System.Diagnostics.PerformanceCounter? _cpuCounter;
    private static bool _cpuCounterBroken;

    /// <summary>取樣 CPU 總負載（0–100）；PerformanceCounter 不可用時回 null，不造假波形。</summary>
    private static double? SampleCpuLoad()
    {
        if (_cpuCounterBroken) return null;
        try
        {
            _cpuCounter ??= new System.Diagnostics.PerformanceCounter("Processor", "% Processor Time", "_Total", true);
            float v = _cpuCounter.NextValue();
            return double.IsNaN(v) ? null : Math.Clamp(v, 0, 100);
        }
        catch
        {
            _cpuCounterBroken = true;
            return null;
        }
    }

    /// <summary>示波器線：最後 width 個取樣畫成高度條（▁▂▃…█），示波器掃描感。</summary>
    private static string ScopeLine(IReadOnlyList<double> samples, int width)
    {
        const string levels = " ▁▂▃▄▅▆▇█";
        var sb = new System.Text.StringBuilder();
        sb.Append(new string(' ', Math.Max(0, width - samples.Count)));
        foreach (var v in samples)
        {
            int idx = Math.Clamp((int)Math.Round(v / 100.0 * (levels.Length - 1)), 1, levels.Length - 1);
            sb.Append(SwissRed);
            sb.Append(levels[idx]);
            sb.Append(Reset);
        }
        return sb.ToString();
    }

    /// <summary>開始轉圈：星芒往返循環＋隨機動詞；2 秒起顯示經過秒數與 esc 提示。label 留空時抽動詞。</summary>
    public static void SpinnerStart(string? label = null)
    {
        lock (Sync)
        {
            if (_cts is not null) return;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            string verb = label ?? Verbs[Random.Shared.Next(Verbs.Length)];
            ScopeSamples.Clear();
            SpinnerClock.Restart();
            _spinPad = 0;
            Console.Write(HideCursor);
            _spinnerTask = Task.Run(async () =>
            {
                // 幀序列往返：✢→✶→✻→✽→✻→✶，120ms 一幀
                int[] seq = [0, 1, 2, 3, 2, 1];
                int i = 0;
                bool hasScope = false;
                int scopeTick = 0;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        string frame = SparkFrames[seq[i++ % seq.Length]];
                        double secs = sw.Elapsed.TotalSeconds;
                        // 掃描式進度條（借部署器經驗）：採集無法預知百分比，亮窗滑動＋計時如實呈現
                        int track = 22;
                        int win = 5;
                        int pos = (int)(sw.Elapsed.TotalMilliseconds / 180) % (track - win + 1);
                        string bar = "";
                        for (int t = 0; t < track; t++)
                            bar += (t >= pos && t < pos + win) ? $"{BrandRed}▓{Reset}" : Dim + "·" + Reset;
                        Console.Write(CarriageReturn + ClearLine + $"{BrandRed}{frame}{Reset} {BrandRed}{verb}{Reset}{Dim}…{Reset} "
                            + $"[{bar}] {Bold}{secs,5:0.0}s{Reset}{Dim} · esc 中斷{Reset}");
                        // 示波器線：真實 CPU 負載取樣（PerformanceCounter；不可用時整行略過，不造假波形）
                        scopeTick++;
                        if (hasScope)
                            Console.Write(Esc + "[1F" + CarriageReturn + ClearLine);   // 回到 spinner 行（scope 行佔了下一行）
                        bool drewScope = false;
                        if (scopeTick % 3 == 0)
                        {
                            double? load = SampleCpuLoad();
                            if (load.HasValue)
                            {
                                drewScope = true;
                                ScopeSamples.Add(load.Value);
                                if (ScopeSamples.Count > ScopeWidth) ScopeSamples.RemoveAt(0);
                                string trace = ScopeLine(ScopeSamples, ScopeWidth);
                                Console.Write(Environment.NewLine + $"  {Dim}CPU ╶{Reset}" + trace + $"{Dim}╴{Reset} {Bold}{load:0}%{Reset}");
                            }
                        }
                        // legacy：游標上移（[1F）在舊主控台印不出來，示波器線整段略過
                        hasScope = drewScope && !Legacy;
                        if (Legacy)
                        {
                            // legacy：ClearLine 是空字串——補空白擦掉上一拍的殘尾
                            string plain = $"{frame} {verb}… [{bar}] {secs,5:0.0}s · esc 中斷";
                            int w = VisibleLength(plain);
                            if (w < _spinPad) Console.Write(new string(' ', _spinPad - w));
                            _spinPad = w;
                        }
                        await Task.Delay(100, token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception) { /* 示波器線繪製等 IO 例外：動畫停了就好，程序不能死 */ }
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
            if (Legacy)
                Console.Write(CarriageReturn + new string(' ', _spinPad + 2) + CarriageReturn);
            else
            {
                Console.Write(Esc + "[1F" + CarriageReturn + ClearLine);
                Console.Write(CarriageReturn + ClearLine);
            }
            _spinPad = 0;
            double done = SpinnerClock.Elapsed.TotalSeconds;
            Console.Write($"[{BrandRed}{new string('▓', 22)}{Reset}] {Bold}100.0%{Reset} {Dim}({done:0.00}s){Reset}" + Environment.NewLine);
            Console.Write(ShowCursor);
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
