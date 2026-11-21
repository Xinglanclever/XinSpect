using System.Text;
using LibreHardwareMonitor.Hardware;
using XinSpect;

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
    internal readonly record struct LiveCpuPower(double? VerifiedWatts, double? ReportedWatts)
    {
        internal double? Watts => VerifiedWatts ?? ReportedWatts;

        internal string SourceText => (CliLocalization.Current, VerifiedWatts, ReportedWatts) switch
        {
            (CliLanguage.English, not null, _) => "CPU power: RAPL (verified)",
            (CliLanguage.English, _, not null) => "CPU power: LHM package (unverified)",
            (CliLanguage.English, _, _) => "CPU power: no usable reading",
            (CliLanguage.Simplified, not null, _) => "CPU 功率：RAPL（已验证）",
            (CliLanguage.Simplified, _, not null) => "CPU 功率：LHM 封装（未验证）",
            (CliLanguage.Simplified, _, _) => "CPU 功率：无可用读数",
            (_, not null, _) => "CPU 功率：RAPL（已驗證）",
            (_, _, not null) => "CPU 功率：LHM 封裝（未驗證）",
            _ => "CPU 功率：無可用讀值",
        };
    }

    internal static double? ReadPackageWatts(string name, SensorType type, float? raw) =>
        type == SensorType.Power && (name.Equals("CPU Package", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Package", StringComparison.OrdinalIgnoreCase))
            ? SensorSanity.Plausible(type, raw) : null;

    internal static LiveCpuPower SelectCpuPower(double? direct, double? reported, RaplGateVerdict verdict) =>
        verdict == RaplGateVerdict.Trusted && direct is > 0 and < 1500 ? new(direct, null)
            : new(null, verdict == RaplGateVerdict.NotResponding ? null : reported);

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
        s = CliLocalization.Translate(s);
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
        s = CliLocalization.Translate(s);
        int w = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\x1b')
            {
                // ECMA-48：ESC 後若為 CSI（ESC [），參數與中間位元組落在 0x20–0x3F、
                // 終止位元組落在 0x40–0x7E——m（SGR）只是終止字元之一，G（CHA）、K（清行）、
                // J（清屏）都是。舊寫法只認 'm'，非 SGR 序列會把後續正文一起吃掉。
                i++;
                if (i < s.Length && s[i] == '[')
                {
                    i++;
                    while (i < s.Length && s[i] >= '\u0020' && s[i] <= '\u003f') i++;
                    if (i < s.Length && s[i] >= '\u0040' && s[i] <= '\u007e') i++;
                }
                else
                {
                    // 非 CSI（本程式不輸出）：保守吃到 BEL 或字串尾
                    while (i < s.Length && s[i] != '\u0007') i++;
                }
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

    /// <summary>Formats live metric cells into explicit continuation lines without clipping values.</summary>
    internal static string[] FormatLiveMetricCells(string prefix, IReadOnlyList<string> cells, IReadOnlyList<int> widths, int consoleWidth, bool header)
    {
        if (cells.Count != widths.Count) throw new ArgumentException("Cell and width counts must match.");
        int width = Math.Max(1, consoleWidth);
        int indent = Math.Min(12, Math.Max(2, width / 5));
        int prefixWidth = Math.Min(10, Math.Max(0, width - 1));
        string first = Pad(prefix, prefixWidth) + (width - prefixWidth >= 2 ? "  " : "");
        var lines = new List<string>();
        var line = new StringBuilder(first);
        int lineWidth = VisibleLength(first);
        bool hasCell = false;

        for (int i = 0; i < cells.Count; i++)
        {
            string cell = cells[i] ?? string.Empty;
            int requestedWidth = Math.Max(widths[i], VisibleLength(cell));
            int continuationCapacity = Math.Max(1, width - indent);

            if (requestedWidth > continuationCapacity)
            {
                if (lineWidth > 0)
                {
                    lines.Add(line.ToString());
                    line = new StringBuilder(new string(' ', Math.Min(indent, Math.Max(0, width - 1))));
                    lineWidth = VisibleLength(line.ToString());
                    hasCell = false;
                }

                foreach (string piece in SplitVisible(cell, continuationCapacity))
                {
                    string continuation = new string(' ', Math.Min(indent, Math.Max(0, width - 1))) + piece;
                    lines.Add(continuation);
                }
                line.Clear();
                lineWidth = 0;
                continue;
            }

            string padded = header ? Pad(cell, requestedWidth) : PadLeftVisible(cell, requestedWidth);
            int paddedWidth = VisibleLength(padded);
            const int gap = 2;
            if (hasCell && lineWidth + gap + paddedWidth > width)
            {
                lines.Add(line.ToString());
                line = new StringBuilder(new string(' ', Math.Min(indent, Math.Max(0, width - 1))));
                lineWidth = VisibleLength(line.ToString());
                hasCell = false;
            }
            else if (!hasCell && lineWidth + gap + paddedWidth > width && lineWidth > 0)
            {
                // The prefix itself may consume a tiny terminal's whole first line.
                lines.Add(line.ToString());
                line = new StringBuilder(new string(' ', Math.Min(indent, Math.Max(0, width - 1))));
                lineWidth = VisibleLength(line.ToString());
            }

            if (lineWidth + gap + paddedWidth > width)
            {
                foreach (string piece in SplitVisible(padded, continuationCapacity))
                    lines.Add(new string(' ', Math.Min(indent, Math.Max(0, width - 1))) + piece);
                line.Clear();
                lineWidth = 0;
                continue;
            }

            line.Append(' ', gap).Append(padded);
            lineWidth += gap + paddedWidth;
            hasCell = true;
        }

        if (line.Length > 0) lines.Add(line.ToString());
        return lines.ToArray();
    }

    private static string PadLeftVisible(string text, int width)
    {
        int gap = width - VisibleLength(text);
        return gap <= 0 ? text : new string(' ', gap) + text;
    }

    private static IEnumerable<string> SplitVisible(string text, int maxWidth)
    {
        var pieces = new List<string>();
        var current = new StringBuilder();
        int currentWidth = 0;
        string activeSgr = string.Empty;

        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\x1b')
            {
                int end = i + 1;
                if (end < text.Length && text[end] == '[')
                {
                    end++;
                    while (end < text.Length && text[end] >= '\u0020' && text[end] <= '\u003f') end++;
                    if (end < text.Length && text[end] >= '\u0040' && text[end] <= '\u007e') end++;
                }
                else
                {
                    while (end < text.Length && text[end] != '\u0007') end++;
                    if (end < text.Length) end++;
                }
                string escape = text[i..end];
                current.Append(escape);
                if (escape.StartsWith("\x1b[", StringComparison.Ordinal) && escape.EndsWith('m'))
                    activeSgr = escape.Contains("[0m", StringComparison.Ordinal) ? string.Empty : escape;
                i = end - 1;
                continue;
            }

            int charWidth = VisibleCharWidth(text[i]);
            if (currentWidth > 0 && currentWidth + charWidth > maxWidth)
            {
                if (activeSgr.Length > 0) current.Append("\x1b[0m");
                pieces.Add(current.ToString());
                current.Clear();
                if (activeSgr.Length > 0) current.Append(activeSgr);
                currentWidth = 0;
            }
            current.Append(text[i]);
            currentWidth += charWidth;
        }

        if (current.Length > 0) pieces.Add(current.ToString());
        return pieces;
    }

    private static int VisibleCharWidth(char c)
        => char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.OtherLetter
           || (c >= '\uFF01' && c <= '\uFF60')
           || (c >= '\u3000' && c <= '\u303F')
           || (c >= '\u2190' && c <= '\u2199')
           || (c >= '\u2733' && c <= '\u2743') ? 2 : 1;

    private static int ConsoleWidth()
    {
        // 下限 40＝CJK 表格最小可用寬，兩把尺同一口徑（P4-24）
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
        $"走訪 {FactKeyCatalog.Count:N0} 把靜態事實鍵中", $"展開 {FactKeyDynamicCatalog.Families.Count} 個動態硬體家族中", "隔窗端詳 Ring0 暫存器中",
        "沿著 PCI 組態空間巡邏中", "旁聽 WMI 與 SMBIOS 事實中", "屏息讀取 LHM 感測器事實中",
        "敲門詢問 Bridge IPC 管道中", "探詢 Redfish 帶外管理介面中", "逐項核對安全性合規基準中",
        "計算 Merkle 事實證據樹雜湊中", "對齊 GUI 65 頁結構索引中", "檢索單一硬體事實路徑中",
        "清點匯流排拓撲與裝置樹中", "確認零寫入唯讀封條狀態中",
    ];

    /// <summary>吉祥物候選。A＝我的設計（原版 9×5 破曉日輪的精修）；B＝Gemini 設計；
    /// C＝旋轉地球完整一圈＋Global Village 打字機（Usenet 1997 取樣＋本專案疊加 27 幀）。
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
        // ── 候選 C：旋轉地球（完整一圈）→ 地球下方打字機逐字打出 Global Village。
        //    旋轉原作：Triamme（John A. Lawson III）1997-09-30 張貼於 alt.ascii-art.animation
        //    「Rotating globe (HTML)」（t0dac8d9e3b，JavaScript 殼為 KANGAROO 所作），49 幀＝完整一圈，
        //    本專案每 4 幀取 1 幀（0→44）；「Global Village」打字機為本專案疊加──
        new[]
        {
            new[]
            {
                "                _v->#H#P? \"':o<>\\_",
                "          .,dP` `''  \"'-o.+H6&MMMHo_",
                "        oHMH9'         `?&bHMHMMMMMMHo.",
                "      oMP\"' '           ooMP*#&HMMMMMMM?.",
                "    ,M*          -     `*MSdob//`^&##MMMH\\",
                "   d*'                .,MMMMMMH#o>#ooMMMMMb",
                "  HM-                :HMMMMMMMMMMMMMMM&HM[R\\",
                " d\"Z\\.               9MMMMMMMMMMMMMMMMM[HMM|:",
                "-H    -              MMMMMMMMMMMMMMMMMMMbMP' :",
                ":??Mb#               `9MMMMMMMMMMMMMMMMMMH#! .",
                ": MMMMH#,              \"*\"\"\"\"`#HMMMMMMMMMMH  -",
                "||MMMMMM6\\.                    {MMMMMMMMMH'  :",
                ":|MMMMMMMMMMHo                 `9MMMMMMMM'   .",
                ". HMMMMMMMMMMP'                 !MMMMMMMM    `",
                "- `#MMMMMMMMM                   HMMMMMMM*,/  :",
                " :  ?MMMMMMMF                   HMMMMMM',P' :",
                "  .  HMMMMR'                    {MMMMP' ^' -",
                "   : `HMMMT                     iMMH'     .'",
                "    -.`HMH                               .",
                "      -:*H                            . '",
                "        -`\\,,    .                  .-",
                "          ' .  _                 .-`",
                "              '`~\\.__,obb#q==~'''",
            },
            new[]
            {
                "                .oo>#&H6?*MMH:-.._",
                "          .oHMMMR:\"&MZ\\ `\"'  \"  |$-_",
                "       ..dMMMMMMMMdMMM#9\\        `'HHo.",
                "     . ,dMMMMMMMMMMM\"`' `           ?MP?.",
                "    . |MMMMMMMMMMM'                 `\"$b&\\",
                "   -  |MMMMHH##M'                     HMMH?",
                "  -   TTMM|    >..                   \\MMMMMH",
                " :     |MM\\,#-\"\"$~b\\.                `MMMMMM+",
                ".       ``\"H&#        -               &MMMMMM|",
                ":            *\\v,#MHddc.              `9MMMMMb",
                ".               MMMMMMMM##\\             `\"\":HM",
                "-          .  .HMMMMMMMMMMRo_.              |M",
                ":             |MMMMMMMMMMMMMMMM#\\           :M",
                "-              `HMMMMMMMMMMMMMMM'           |T",
                ":               `*HMMMMMMMMMMMM'            H'",
                " :                 MMMMMMMMMMM|            |T",
                "  .                MMMMMMMM?'             ./",
                "  `.               MMMMMMH'              ./",
                "    -.            |MMMH#'                .",
                "      .           `MM*                . '",
                "        -.         #M: .    .       .-",
                "          ` .         .,         .-",
                "              '-.-~ooHH__,,v~--`",
            },
            new[]
            {
                "                _\\oo\\?ddk9MRbS>v\\_",
                "          ..:>*\"\"MMMMMMMMM:?|H?$?-.",
                "       ..- -     \"HMMMMMMMMMMHMMMH\\_-.",
                "     .            dMMMMMMMMMMMMMMT\"    .",
                "    .             TMMMMMMMMMMMMMM       `.",
                "   -               `&HMMMMMM#H#H:         .",
                "  -                 `\\7HMMH     |\\.        .",
                " :    `                 HMM\\_?c`\"\"+?\\..     :",
                "-                         \"``#&#|      .     -",
                ":                              `?,\\#MHdb.    .",
                ":                                 |MMMMMH#.  :",
                ":                            .   ,HMMMMMMMb, -",
                ": '                              4MMMMMMMMMMH`",
                ":   .                             9MMMMMMMMMT-",
                ":.`                               `#MMMMMMMH '",
                " :      '                           HMMMMMH':",
                "  -                                |MMMMH\" -",
                "  `:                              |MMMH*' .'",
                "    '?                           dMM#'   .",
                "      \\.                       .dH\"    .'",
                "        -.                    ,M'-  ..'",
                "          ` .                .. ..-`",
                "              '-. .\\ooooboo<^.-`",
            },
            new[]
            {
                "                _,oc>?_:b?o?HH#b\\_",
                "          .v/99*\"\"\" '*H#\"\"*HMMMMMZ,_",
                "        oH* /\"   -   '      \"`#MMMMM#o.",
                "     ./*>-                     `MMMMMMMb",
                "    ,b/'                        `#MMMMMMM\\",
                "   :'                             ``HMMMMb:",
                "  /-                                `|&MH `\\",
                " /                   `-.               |Hb??\\",
                ",-  '                                    \"`&,.",
                "1                                           \\}",
                "!.                                           T",
                "$,.                                        . 1",
                "?`M??.                                       M",
                "?.::| '\\        -                            ?",
                " M?&.    .   .  -                           ,'",
                " 9MMH\\   ..  '           `                  .",
                "  HMMM#.                                   :'",
                "   9#MMb                                 ..",
                "    -:\"#     `b.                        .-",
                "      . `    {!                        /",
                "        -                           ,-'",
                "          ' .                    .-",
                "             ```^==\\_.,,,ov--\\-`",
            },
            new[]
            {
                "                _oo#H&d#b?\\b:_>>\\_",
                "          .oHMMMMMMH*\"*9R\"'-``*#P\\-_",
                "        oHMMMMMMMMM$  .\"       '   `^-",
                "     .dMMMMMMMMH*\",?-                 '\\.",
                "    ,MMMMMMM:?}.,d'                     `.",
                "   dMMMMMMMH  /''                         :",
                "  HMMMMMMM&' -                             -",
                " dPTMMP>' :                           -.    :",
                "|? -MM}  .\\                                  .",
                "J' ::*'  -$L                                 .",
                ":  ?b .,H- '                                 :",
                "-  |6.&MP:: !.,_.                            -",
                ":   `\\:: \"' \"`:\"MM#,-^,            -         :",
                "-     ````:' _.:\"?``\\   `-                   .",
                ":         .?bMML.]#        -   _  `      .  .'",
                " -      .o#MMMMMMMMH\\     \\.          .     .",
                "  -     `HMMMMMMMMMMMH                     :",
                "  `.     `HMM#*#MMMMMH'                   -",
                "    -.     '    ``##*'      i+           :",
                "      -            `'     v/'          .'",
                "       `-                           ..'",
                "          ' .                    .-",
                "              \"`*##HMH##:__,-.-`",
            },
            new[]
            {
                "                _,,>#b&HMHd&&bb>\\_",
                "          _oHMMMMMMMMMMMMMMMMH**H:.",
                "        oHMMMMMMMMMMMMMMMMMMMM#v`?  `.",
                "     .dMMMMMMMMMMMMMMMMMMMMMMH*`+|     .",
                "    ,MMMMMMMMMMMMMMMMMMMMMb|?+.,H       -.",
                "   ddHMMMMMMMMMMMMMMMMMMMMMb  `'          .",
                "  HMMkZ**HMMMMMMMMMMMMMMMMH\\  -   .        :",
                " dTMMM*  `9MMMP'\"*MMMMPT\"` ..               :",
                "|M6H''    4MP'   `\"HMMM|   !|.      .        .",
                "1MHp'      #L      $ *\"'  .-:&.              .",
                "MMM'        \"     q:H.  .o#-``'              :",
                "MM'                ?H?.|MMH::::-o,_.         -",
                "M[                  `*?:::'|` `\"`:9MH\\~-.    `",
                "&M.                     \"\"'`'^'.:.`?'`. '|  -:",
                "`M|d,                       .dbHM[.1?     .. :",
                " 9||| .                  _obMMMMMMMMH,   .  :",
                "  H.^                    MMMMMMMMMMMM}     -",
                "   \\                     |MMH#*HMMMMH'    .'",
                "    .                    `      `#*'   ,:-",
                "     `                           '' .-'.",
                "       `.                           .-",
                "          '- .                   .-`",
                "              '`\\bqHMMMMMMHHb--`",
            },
            new[]
            {
                "                .-:?,Z?:&$dHH##b\\_",
                "           ,:bqRMMMMMMMMMMMMMMMMMHo.",
                "        .?HHHMMMMMMMMMMMMMMMMMMMMMMMHo.",
                "      -o/*M9MMMMMMMMMMMMMMMMMMMMMMMMMMMv",
                "    .:H\\b\\'|?#HHMMMMMMMMMMMMMMMMMMMMMM6?Z\\",
                "   .?MMMHbdbbodMMMMHMMMMMMMMMMMMMMMMMMMM\\':",
                "  :MMMMMMMMMMM7MMMMb?6P**#MMMMMMMMMMMMMMM_ :",
                " \\MMMMMMMMMMMMb^MMMMMM?   `*MMMM*\"`MMMR<' . -",
                ".1MMMMMMMMMMMMMb]M#\"\"       9MR'   `?MMb  \\. :",
                "-MMMMMMMMMMMMMMMH##|`        *&.     |`*' .\\ .",
                "-?\"\"*MMMMMMMMMMMMM'            '    |?b  ,}\" :",
                ":    MMMMMMMMMMH'                    `M_|M}r\\?",
                ".    `MMMMMMMMM'                      `$_:`'\"H",
                "-     TMMMMMMMM,                        '\"``::",
                ":     {MMMMMMMM| oH|                      .#M-",
                " :    `9MMMMMM' .MP   .                 ,oMMT",
                "  .     HMMMMP'  `'                    ,MMMP",
                "   -     `MMH'                         HH9*",
                "    '.    `                           ` .'",
                "      -                               . '",
                "       ` .               -          .-",
                "          ` .                    .-",
                "              ' -==pHMMH##HH#\"\"\"",
            },
            new[]
            {
                "                _ooq=\"\"''$b$_&?b\\_",
                "          .-`^\"  \"'o |&M:MMMMMMMMHo_",
                "        o/'      -$Mb#MMMMMMMMMMMMMMHo.",
                "      /'        .ooHP*&R&MMMMMMMMMMMMMM?.",
                "    .'          `MRbod?|'`+?##9MMMMMMMMMH\\",
                "  .`          .,MMMMMMH#od##obdMMMMHMMMMMMb",
                "  -          ?MMMMMMMMMMMMMMMMM$HMMH$ZP*HMMb",
                " ?          |MMMMMMMMMMMMMMMMMMM|9MMMMP  \"M6\\",
                ".-          dMMMMMMMMMMMMMMMMMMMMb]M*'    |R |",
                "1|          `HMMMMMMMMMMMMMMMMMMMMMd#|     ?,:",
                "MH,          ``*\"\"'\"*#MMMMMMMMMMMMMM*       '`",
                "MM6_                 |MMMMMMMMMMMMH\"         :",
                "MMMMMb.               \"MMMMMMMMMMT           -",
                "&MMMMM'                |MMMMMMMMMH           `",
                "!MMMMb                .HMMMMMMMMM+.?&        :",
                " TMMMM                 *MMMMMMMP  dH' .     :",
                "  9MM'                 `MMMMMMP'  \"'       .",
                "   9ML                  `MMM#'            -",
                "    `H                   `               :",
                "     `\\.                               .'",
                "       `-\\  .                       .-",
                "          ' ._                   .-`",
                "              '-\\. ,b#####p&**^`",
            },
            new[]
            {
                "                _oo##'9MMHb':'-,o_",
                "          .oH\":HH$' \"\"'  \"' -\\7*R&o_",
                "       .oHMMMHMH#9:          \"\\bMMMMHo.",
                "      dMMMMMM*\"\"'`'           .oHM\"H9MM?.",
                "    ,MMMMMM'                   \"HLbd<|?&H\\",
                "   JMMH#H'                     |MMMMM#b>bHb",
                "  :MH  .\"\\                   `|MMMMMMMMMMMM&",
                " .:M:d-\"|:b..                 9MMMMMMMMMMMMM+",
                ":  \"*H|      -                &MMMMMMMMMMMMMH:",
                ".    `LvdHH#d?                `?MMMMMMMMMMMMMb",
                ":      iMMMMMMH#b               `\"*\"'\"#HMMMMMM",
                ".   . ,MMMMMMMMMMb\\.                   {MMMMMH",
                "-     |MMMMMMMMMMMMMMHb,               `MMMMM|",
                ":      |MMMMMMMMMMMMMMH'                &MMMM,",
                "-       `#MMMMMMMMMMMM                 |MMMM6-",
                " :        `MMMMMMMMMM+                 ]MMMT/",
                "  .       `MMMMMMMP\"                   HMM*`",
                "   -       |MMMMMH'                   ,M#'-",
                "    '.     :MMMH|                       .-",
                "      .     |MM                        -",
                "       ` .   `#?..    .             ..'",
                "           -.     _.             .-",
                "              '-|.#qo__,,ob=~~-''",
            },
            new[]
            {
                "                _,\\?dZkMHF&$*q#b..",
                "          .//9MMMMMMM?:'HM\\\\\"`-''`..",
                "       ..`  :MMMMMMMMMMHMMMMH?_    `-\\",
                "     .     .dMMMMMMMMMMMMMM'\"'\"       `\\.",
                "    .      |MMMMMMMMMMMMMR              \\\\",
                "   -        T9MMMMMHH##M\"                `?",
                "  :          (9MMM'    !':.               &k",
                " .:            HMM\\_?p \"\":-b\\.            `ML",
                "-                \"'\"H&#,       :           |M|",
                ":                     ?\\,\\dMH#b#.           9b",
                ":                        |MMMMMMM##,        `*",
                ":                   .   +MMMMMMMMMMMo_       -",
                ":                       HMMMMMMMMMMMMMM#,    :",
                ":                        9MMMMMMMMMMMMMH'    .",
                ": .                       *HMMMMMMMMMMP     .'",
                " :                          MMMMMMMMMH'     .",
                "  -                        :MMMMMMM'`      .",
                "  `.                       9MMMMM*'       -",
                "    -.                    {MMM#'         :",
                "      -                  |MM\"          .'",
                "       `.                &M'..  .   ..'",
                "          ' .             ._     .-",
                "              '-. -voboo#&:,-.-`",
            },
            new[]
            {
                "                _o\\:,??\\??MR9#cb\\_",
                "          .v/''':&#\"\"#HMMMMMMM$?*d\\.",
                "       ..~' - -`      `\"#MMMMMMMMMMMHv.",
                "     .-'                 HMMMMMMMMMMMR!.",
                "    :                    `9MMMMMMMMMMM| -.",
                "   .                       `*9MMMMMH##|   .",
                "  -                          `(#MMH   `:,  .",
                " :           '|                 `HMb_>/\"|\\,.:",
                ".'                                `\"'#&b   - .",
                ":                                      ?\\oHH?.",
                ":                                        !MMM&",
                ":  .                                  .  HMMMM",
                "/.      -                               -MMMMM",
                "\\`.                                      9MMMP",
                ":. .  . -                                |MMM'",
                " \\... '                                  .MMT",
                "  &.                                    .dMP",
                "   \\,                                  .HM*",
                "    \\. `\\.                            ,H&'",
                "     `- `| -                        ,&':",
                "       `.                         ,/\\ '",
                "          '-..                  _.-",
                "              \"---.._\\o,oov+--'\"",
            },
            new[]
            {
                "                _,o#bH\\??::?o?cbo_",
                "          .o#MH#**SH\"\"' \"`*H#\"*#MHo_",
                "        oHMMMH^  ^\"    -  `      '*HHo.",
                "     .dMMM#\">>-                     `HM?.",
                "    ,MH:R_o/                         `*MH\\",
                "   dMM' '                               \"ML",
                "  HMR! '                                 `#k",
                " d&'.                          -.          `L",
                ":M ::     `                                 `-",
                "/| !|                                        -",
                "k.$-\"                                        :",
                "}9R:!,,_.                                    .",
                "\\::\\':`*M#\\-'.                               -",
                ": \"''..:\"!`\\  '-          -                  `",
                "-   ,HMb.H|      .    _   -                 .'",
                " : ,MMMMMMMb.    ..                         .",
                "  .`HMMMMMMMM?                             .",
                "  `.`9M#*HMMMM                            :",
                "    -.'   \"##*      `b,                  .",
                "      .      `     ,/'                 .'",
                "       ` .                          ..'",
                "           - .                  ..-",
                "              \"`*#d##c.._\\v----`",
            },
            new[]
            {
                "                _,dd#HMb&dHo?\\?:\\_",
                "          .oHMMMMMMMMMMMH***9P'`\"\\v.",
                "        oHMMMMMMMMMMMMMMM>  `'      -.",
                "     .dMMMMMMMMMMMMMMMH*'|~-'          .",
                "    ,MMMMMMMMMMMMM6>`H._,&              -.",
                "   dMMMMMMMMMMMMMMM|  `\"                  .",
                "  H*MMMMMMMMMMMMMH&. -                     .",
                " d' HMM\"\"&MMMPT'' :.                      `.-",
                ",'  MP   `TMMM,   |:        .                -",
                "|   #:    ? *\"   : &L                        :",
                "!   `'   /?H   ,#r `'                        :",
                ".         ?M: HMM^<~->,o._                   :",
                ":          `9:::'`*-``':`9MHb,|-,         '  :",
                ".             `\"''':' :_ \"\"!\"^.  `|          :",
                "`.                 _dbHM6_|H.      .   . '  .'",
                " \\              _odHMMMMMMMMH,    ..  `     :",
                " `-             |MMMMMMMMMMMMM|            :",
                "  `.             9MMH**#MMMMMH'           :",
                "    -.            '     \"?##\"      d     :",
                "      .                    '    ,/\"    .'",
                "       `..                          ..'",
                "          `  .                   .-",
                "              '`\"[B#HHMMMMM#<>..-`",
            },
            new[]
            {
                "                _,dd#HMb&dHo?\\?:\\_",
                "          .oHMMMMMMMMMMMH***9P'`\"\\v.",
                "        oHMMMMMMMMMMMMMMM>  `'      -.",
                "     .dMMMMMMMMMMMMMMMH*'|~-'          .",
                "    ,MMMMMMMMMMMMM6>`H._,&              -.",
                "   dMMMMMMMMMMMMMMM|  `\"                  .",
                "  H*MMMMMMMMMMMMMH&. -                     .",
                " d' HMM\"\"&MMMPT'' :.                      `.-",
                ",'  MP   `TMMM,   |:        .                -",
                "|   #:    ? *\"   : &L                        :",
                "!   `'   /?H   ,#r `'                        :",
                ".         ?M: HMM^<~->,o._                   :",
                ":          `9:::'`*-``':`9MHb,|-,         '  :",
                ".             `\"''':' :_ \"\"!\"^.  `|          :",
                "`.                 _dbHM6_|H.      .   . '  .'",
                " \\              _odHMMMMMMMMH,    ..  `     :",
                " `-             |MMMMMMMMMMMMM|            :",
                "  `.             9MMH**#MMMMMH'           :",
                "    -.            '     \"?##\"      d     :",
                "      .                    '    ,/\"    .'",
                "       `..                          ..'",
                "          `  .                   .-",
                "              '`\"[B#HHMMMMM#<>..-`",
                "",
                "                G_",
            },
            new[]
            {
                "                _,dd#HMb&dHo?\\?:\\_",
                "          .oHMMMMMMMMMMMH***9P'`\"\\v.",
                "        oHMMMMMMMMMMMMMMM>  `'      -.",
                "     .dMMMMMMMMMMMMMMMH*'|~-'          .",
                "    ,MMMMMMMMMMMMM6>`H._,&              -.",
                "   dMMMMMMMMMMMMMMM|  `\"                  .",
                "  H*MMMMMMMMMMMMMH&. -                     .",
                " d' HMM\"\"&MMMPT'' :.                      `.-",
                ",'  MP   `TMMM,   |:        .                -",
                "|   #:    ? *\"   : &L                        :",
                "!   `'   /?H   ,#r `'                        :",
                ".         ?M: HMM^<~->,o._                   :",
                ":          `9:::'`*-``':`9MHb,|-,         '  :",
                ".             `\"''':' :_ \"\"!\"^.  `|          :",
                "`.                 _dbHM6_|H.      .   . '  .'",
                " \\              _odHMMMMMMMMH,    ..  `     :",
                " `-             |MMMMMMMMMMMMM|            :",
                "  `.             9MMH**#MMMMMH'           :",
                "    -.            '     \"?##\"      d     :",
                "      .                    '    ,/\"    .'",
                "       `..                          ..'",
                "          `  .                   .-",
                "              '`\"[B#HHMMMMM#<>..-`",
                "",
                "                Gl_",
            },
            new[]
            {
                "                _,dd#HMb&dHo?\\?:\\_",
                "          .oHMMMMMMMMMMMH***9P'`\"\\v.",
                "        oHMMMMMMMMMMMMMMM>  `'      -.",
                "     .dMMMMMMMMMMMMMMMH*'|~-'          .",
                "    ,MMMMMMMMMMMMM6>`H._,&              -.",
                "   dMMMMMMMMMMMMMMM|  `\"                  .",
                "  H*MMMMMMMMMMMMMH&. -                     .",
                " d' HMM\"\"&MMMPT'' :.                      `.-",
                ",'  MP   `TMMM,   |:        .                -",
                "|   #:    ? *\"   : &L                        :",
                "!   `'   /?H   ,#r `'                        :",
                ".         ?M: HMM^<~->,o._                   :",
                ":          `9:::'`*-``':`9MHb,|-,         '  :",
                ".             `\"''':' :_ \"\"!\"^.  `|          :",
                "`.                 _dbHM6_|H.      .   . '  .'",
                " \\              _odHMMMMMMMMH,    ..  `     :",
                " `-             |MMMMMMMMMMMMM|            :",
                "  `.             9MMH**#MMMMMH'           :",
                "    -.            '     \"?##\"      d     :",
                "      .                    '    ,/\"    .'",
                "       `..                          ..'",
                "          `  .                   .-",
                "              '`\"[B#HHMMMMM#<>..-`",
                "",
                "                Glo_",
            },
            new[]
            {
                "                _,dd#HMb&dHo?\\?:\\_",
                "          .oHMMMMMMMMMMMH***9P'`\"\\v.",
                "        oHMMMMMMMMMMMMMMM>  `'      -.",
                "     .dMMMMMMMMMMMMMMMH*'|~-'          .",
                "    ,MMMMMMMMMMMMM6>`H._,&              -.",
                "   dMMMMMMMMMMMMMMM|  `\"                  .",
                "  H*MMMMMMMMMMMMMH&. -                     .",
                " d' HMM\"\"&MMMPT'' :.                      `.-",
                ",'  MP   `TMMM,   |:        .                -",
                "|   #:    ? *\"   : &L                        :",
                "!   `'   /?H   ,#r `'                        :",
                ".         ?M: HMM^<~->,o._                   :",
                ":          `9:::'`*-``':`9MHb,|-,         '  :",
                ".             `\"''':' :_ \"\"!\"^.  `|          :",
                "`.                 _dbHM6_|H.      .   . '  .'",
                " \\              _odHMMMMMMMMH,    ..  `     :",
                " `-             |MMMMMMMMMMMMM|            :",
                "  `.             9MMH**#MMMMMH'           :",
                "    -.            '     \"?##\"      d     :",
                "      .                    '    ,/\"    .'",
                "       `..                          ..'",
                "          `  .                   .-",
                "              '`\"[B#HHMMMMM#<>..-`",
                "",
                "                Glob_",
            },
            new[]
            {
                "                _,dd#HMb&dHo?\\?:\\_",
                "          .oHMMMMMMMMMMMH***9P'`\"\\v.",
                "        oHMMMMMMMMMMMMMMM>  `'      -.",
                "     .dMMMMMMMMMMMMMMMH*'|~-'          .",
                "    ,MMMMMMMMMMMMM6>`H._,&              -.",
                "   dMMMMMMMMMMMMMMM|  `\"                  .",
                "  H*MMMMMMMMMMMMMH&. -                     .",
                " d' HMM\"\"&MMMPT'' :.                      `.-",
                ",'  MP   `TMMM,   |:        .                -",
                "|   #:    ? *\"   : &L                        :",
                "!   `'   /?H   ,#r `'                        :",
                ".         ?M: HMM^<~->,o._                   :",
                ":          `9:::'`*-``':`9MHb,|-,         '  :",
                ".             `\"''':' :_ \"\"!\"^.  `|          :",
                "`.                 _dbHM6_|H.      .   . '  .'",
                " \\              _odHMMMMMMMMH,    ..  `     :",
                " `-             |MMMMMMMMMMMMM|            :",
                "  `.             9MMH**#MMMMMH'           :",
                "    -.            '     \"?##\"      d     :",
                "      .                    '    ,/\"    .'",
                "       `..                          ..'",
                "          `  .                   .-",
                "              '`\"[B#HHMMMMM#<>..-`",
                "",
                "                Globa_",
            },
            new[]
            {
                "                _,dd#HMb&dHo?\\?:\\_",
                "          .oHMMMMMMMMMMMH***9P'`\"\\v.",
                "        oHMMMMMMMMMMMMMMM>  `'      -.",
                "     .dMMMMMMMMMMMMMMMH*'|~-'          .",
                "    ,MMMMMMMMMMMMM6>`H._,&              -.",
                "   dMMMMMMMMMMMMMMM|  `\"                  .",
                "  H*MMMMMMMMMMMMMH&. -                     .",
                " d' HMM\"\"&MMMPT'' :.                      `.-",
                ",'  MP   `TMMM,   |:        .                -",
                "|   #:    ? *\"   : &L                        :",
                "!   `'   /?H   ,#r `'                        :",
                ".         ?M: HMM^<~->,o._                   :",
                ":          `9:::'`*-``':`9MHb,|-,         '  :",
                ".             `\"''':' :_ \"\"!\"^.  `|          :",
                "`.                 _dbHM6_|H.      .   . '  .'",
                " \\              _odHMMMMMMMMH,    ..  `     :",
                " `-             |MMMMMMMMMMMMM|            :",
                "  `.             9MMH**#MMMMMH'           :",
                "    -.            '     \"?##\"      d     :",
                "      .                    '    ,/\"    .'",
                "       `..                          ..'",
                "          `  .                   .-",
                "              '`\"[B#HHMMMMM#<>..-`",
                "",
                "                Global_",
            },
            new[]
            {
                "                _,dd#HMb&dHo?\\?:\\_",
                "          .oHMMMMMMMMMMMH***9P'`\"\\v.",
                "        oHMMMMMMMMMMMMMMM>  `'      -.",
                "     .dMMMMMMMMMMMMMMMH*'|~-'          .",
                "    ,MMMMMMMMMMMMM6>`H._,&              -.",
                "   dMMMMMMMMMMMMMMM|  `\"                  .",
                "  H*MMMMMMMMMMMMMH&. -                     .",
                " d' HMM\"\"&MMMPT'' :.                      `.-",
                ",'  MP   `TMMM,   |:        .                -",
                "|   #:    ? *\"   : &L                        :",
                "!   `'   /?H   ,#r `'                        :",
                ".         ?M: HMM^<~->,o._                   :",
                ":          `9:::'`*-``':`9MHb,|-,         '  :",
                ".             `\"''':' :_ \"\"!\"^.  `|          :",
                "`.                 _dbHM6_|H.      .   . '  .'",
                " \\              _odHMMMMMMMMH,    ..  `     :",
                " `-             |MMMMMMMMMMMMM|            :",
                "  `.             9MMH**#MMMMMH'           :",
                "    -.            '     \"?##\"      d     :",
                "      .                    '    ,/\"    .'",
                "       `..                          ..'",
                "          `  .                   .-",
                "              '`\"[B#HHMMMMM#<>..-`",
                "",
                "                Global _",
            },
            new[]
            {
                "                _,dd#HMb&dHo?\\?:\\_",
                "          .oHMMMMMMMMMMMH***9P'`\"\\v.",
                "        oHMMMMMMMMMMMMMMM>  `'      -.",
                "     .dMMMMMMMMMMMMMMMH*'|~-'          .",
                "    ,MMMMMMMMMMMMM6>`H._,&              -.",
                "   dMMMMMMMMMMMMMMM|  `\"                  .",
                "  H*MMMMMMMMMMMMMH&. -                     .",
                " d' HMM\"\"&MMMPT'' :.                      `.-",
                ",'  MP   `TMMM,   |:        .                -",
                "|   #:    ? *\"   : &L                        :",
                "!   `'   /?H   ,#r `'                        :",
                ".         ?M: HMM^<~->,o._                   :",
                ":          `9:::'`*-``':`9MHb,|-,         '  :",
                ".             `\"''':' :_ \"\"!\"^.  `|          :",
                "`.                 _dbHM6_|H.      .   . '  .'",
                " \\              _odHMMMMMMMMH,    ..  `     :",
                " `-             |MMMMMMMMMMMMM|            :",
                "  `.             9MMH**#MMMMMH'           :",
                "    -.            '     \"?##\"      d     :",
                "      .                    '    ,/\"    .'",
                "       `..                          ..'",
                "          `  .                   .-",
                "              '`\"[B#HHMMMMM#<>..-`",
                "",
                "                Global V_",
            },
            new[]
            {
                "                _,dd#HMb&dHo?\\?:\\_",
                "          .oHMMMMMMMMMMMH***9P'`\"\\v.",
                "        oHMMMMMMMMMMMMMMM>  `'      -.",
                "     .dMMMMMMMMMMMMMMMH*'|~-'          .",
                "    ,MMMMMMMMMMMMM6>`H._,&              -.",
                "   dMMMMMMMMMMMMMMM|  `\"                  .",
                "  H*MMMMMMMMMMMMMH&. -                     .",
                " d' HMM\"\"&MMMPT'' :.                      `.-",
                ",'  MP   `TMMM,   |:        .                -",
                "|   #:    ? *\"   : &L                        :",
                "!   `'   /?H   ,#r `'                        :",
                ".         ?M: HMM^<~->,o._                   :",
                ":          `9:::'`*-``':`9MHb,|-,         '  :",
                ".             `\"''':' :_ \"\"!\"^.  `|          :",
                "`.                 _dbHM6_|H.      .   . '  .'",
                " \\              _odHMMMMMMMMH,    ..  `     :",
                " `-             |MMMMMMMMMMMMM|            :",
                "  `.             9MMH**#MMMMMH'           :",
                "    -.            '     \"?##\"      d     :",
                "      .                    '    ,/\"    .'",
                "       `..                          ..'",
                "          `  .                   .-",
                "              '`\"[B#HHMMMMM#<>..-`",
                "",
                "                Global Vi_",
            },
            new[]
            {
                "                _,dd#HMb&dHo?\\?:\\_",
                "          .oHMMMMMMMMMMMH***9P'`\"\\v.",
                "        oHMMMMMMMMMMMMMMM>  `'      -.",
                "     .dMMMMMMMMMMMMMMMH*'|~-'          .",
                "    ,MMMMMMMMMMMMM6>`H._,&              -.",
                "   dMMMMMMMMMMMMMMM|  `\"                  .",
                "  H*MMMMMMMMMMMMMH&. -                     .",
                " d' HMM\"\"&MMMPT'' :.                      `.-",
                ",'  MP   `TMMM,   |:        .                -",
                "|   #:    ? *\"   : &L                        :",
                "!   `'   /?H   ,#r `'                        :",
                ".         ?M: HMM^<~->,o._                   :",
                ":          `9:::'`*-``':`9MHb,|-,         '  :",
                ".             `\"''':' :_ \"\"!\"^.  `|          :",
                "`.                 _dbHM6_|H.      .   . '  .'",
                " \\              _odHMMMMMMMMH,    ..  `     :",
                " `-             |MMMMMMMMMMMMM|            :",
                "  `.             9MMH**#MMMMMH'           :",
                "    -.            '     \"?##\"      d     :",
                "      .                    '    ,/\"    .'",
                "       `..                          ..'",
                "          `  .                   .-",
                "              '`\"[B#HHMMMMM#<>..-`",
                "",
                "                Global Vil_",
            },
            new[]
            {
                "                _,dd#HMb&dHo?\\?:\\_",
                "          .oHMMMMMMMMMMMH***9P'`\"\\v.",
                "        oHMMMMMMMMMMMMMMM>  `'      -.",
                "     .dMMMMMMMMMMMMMMMH*'|~-'          .",
                "    ,MMMMMMMMMMMMM6>`H._,&              -.",
                "   dMMMMMMMMMMMMMMM|  `\"                  .",
                "  H*MMMMMMMMMMMMMH&. -                     .",
                " d' HMM\"\"&MMMPT'' :.                      `.-",
                ",'  MP   `TMMM,   |:        .                -",
                "|   #:    ? *\"   : &L                        :",
                "!   `'   /?H   ,#r `'                        :",
                ".         ?M: HMM^<~->,o._                   :",
                ":          `9:::'`*-``':`9MHb,|-,         '  :",
                ".             `\"''':' :_ \"\"!\"^.  `|          :",
                "`.                 _dbHM6_|H.      .   . '  .'",
                " \\              _odHMMMMMMMMH,    ..  `     :",
                " `-             |MMMMMMMMMMMMM|            :",
                "  `.             9MMH**#MMMMMH'           :",
                "    -.            '     \"?##\"      d     :",
                "      .                    '    ,/\"    .'",
                "       `..                          ..'",
                "          `  .                   .-",
                "              '`\"[B#HHMMMMM#<>..-`",
                "",
                "                Global Vill_",
            },
            new[]
            {
                "                _,dd#HMb&dHo?\\?:\\_",
                "          .oHMMMMMMMMMMMH***9P'`\"\\v.",
                "        oHMMMMMMMMMMMMMMM>  `'      -.",
                "     .dMMMMMMMMMMMMMMMH*'|~-'          .",
                "    ,MMMMMMMMMMMMM6>`H._,&              -.",
                "   dMMMMMMMMMMMMMMM|  `\"                  .",
                "  H*MMMMMMMMMMMMMH&. -                     .",
                " d' HMM\"\"&MMMPT'' :.                      `.-",
                ",'  MP   `TMMM,   |:        .                -",
                "|   #:    ? *\"   : &L                        :",
                "!   `'   /?H   ,#r `'                        :",
                ".         ?M: HMM^<~->,o._                   :",
                ":          `9:::'`*-``':`9MHb,|-,         '  :",
                ".             `\"''':' :_ \"\"!\"^.  `|          :",
                "`.                 _dbHM6_|H.      .   . '  .'",
                " \\              _odHMMMMMMMMH,    ..  `     :",
                " `-             |MMMMMMMMMMMMM|            :",
                "  `.             9MMH**#MMMMMH'           :",
                "    -.            '     \"?##\"      d     :",
                "      .                    '    ,/\"    .'",
                "       `..                          ..'",
                "          `  .                   .-",
                "              '`\"[B#HHMMMMM#<>..-`",
                "",
                "                Global Villa_",
            },
            new[]
            {
                "                _,dd#HMb&dHo?\\?:\\_",
                "          .oHMMMMMMMMMMMH***9P'`\"\\v.",
                "        oHMMMMMMMMMMMMMMM>  `'      -.",
                "     .dMMMMMMMMMMMMMMMH*'|~-'          .",
                "    ,MMMMMMMMMMMMM6>`H._,&              -.",
                "   dMMMMMMMMMMMMMMM|  `\"                  .",
                "  H*MMMMMMMMMMMMMH&. -                     .",
                " d' HMM\"\"&MMMPT'' :.                      `.-",
                ",'  MP   `TMMM,   |:        .                -",
                "|   #:    ? *\"   : &L                        :",
                "!   `'   /?H   ,#r `'                        :",
                ".         ?M: HMM^<~->,o._                   :",
                ":          `9:::'`*-``':`9MHb,|-,         '  :",
                ".             `\"''':' :_ \"\"!\"^.  `|          :",
                "`.                 _dbHM6_|H.      .   . '  .'",
                " \\              _odHMMMMMMMMH,    ..  `     :",
                " `-             |MMMMMMMMMMMMM|            :",
                "  `.             9MMH**#MMMMMH'           :",
                "    -.            '     \"?##\"      d     :",
                "      .                    '    ,/\"    .'",
                "       `..                          ..'",
                "          `  .                   .-",
                "              '`\"[B#HHMMMMM#<>..-`",
                "",
                "                Global Villag_",
            },
            new[]
            {
                "                _,dd#HMb&dHo?\\?:\\_",
                "          .oHMMMMMMMMMMMH***9P'`\"\\v.",
                "        oHMMMMMMMMMMMMMMM>  `'      -.",
                "     .dMMMMMMMMMMMMMMMH*'|~-'          .",
                "    ,MMMMMMMMMMMMM6>`H._,&              -.",
                "   dMMMMMMMMMMMMMMM|  `\"                  .",
                "  H*MMMMMMMMMMMMMH&. -                     .",
                " d' HMM\"\"&MMMPT'' :.                      `.-",
                ",'  MP   `TMMM,   |:        .                -",
                "|   #:    ? *\"   : &L                        :",
                "!   `'   /?H   ,#r `'                        :",
                ".         ?M: HMM^<~->,o._                   :",
                ":          `9:::'`*-``':`9MHb,|-,         '  :",
                ".             `\"''':' :_ \"\"!\"^.  `|          :",
                "`.                 _dbHM6_|H.      .   . '  .'",
                " \\              _odHMMMMMMMMH,    ..  `     :",
                " `-             |MMMMMMMMMMMMM|            :",
                "  `.             9MMH**#MMMMMH'           :",
                "    -.            '     \"?##\"      d     :",
                "      .                    '    ,/\"    .'",
                "       `..                          ..'",
                "          `  .                   .-",
                "              '`\"[B#HHMMMMM#<>..-`",
                "",
                "                Global Village",
            },
        },
    ];

    /// <summary>目前選用的候選（預設旋轉地球＋Global Village）。</summary>
    public static int MascotChoice = 2;

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
            // 純文字開場：零色彩、零框線、零定位——右對齊用空格補，不用 CHA。
            // 字標也不用方塊字（█╗║╚）：Win10 傳統主控台的點陣字型把它們渲染成碎塊
            // （2026-11-07 使用者實測截圖），間隔大寫字母是任何字型都安全的寫法。
            int w = 100;
            try { w = Math.Clamp(Console.WindowWidth - 1, 40, 118); } catch { }
            try { Console.Clear(); } catch { }
            if (!Console.IsInputRedirected)
            {
                string wm = "X  I  N  S  P  E  C  T";
                Console.WriteLine(new string(' ', Math.Max(0, (w - wm.Length) / 2)) + wm);
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
            Console.WriteLine("  book     曦覽百科全書：離線嵌入，部節實數 book 目錄自報");
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
                Bold + "book" + Reset + Dim + "     曦覽百科全書：離線嵌入，部節實數 book 目錄自報",
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
        return TruncToWidth(tip, max);
    }

    private static string ShortCwd()
    {
        string cwd = Environment.CurrentDirectory;
        return cwd.Length > 30 ? cwd[..14] + "…" + cwd[^14..] : cwd;
    }

    /// <summary>靜態吉祥物框（預覽用）：單幀＋品牌名。框寬隨候選作品自適應（下限 46，上限 64）。</summary>
    public static string MascotBox(int frame)
    {
        var f = MascotFrames[frame];
        int artW = 0;
        foreach (var lineText in f) artW = Math.Max(artW, VisibleLength(lineText));
        int width = Math.Clamp(artW + 3, 46, 64);
        var sb = new System.Text.StringBuilder();
        sb.Append(BoxTop(width, $"{Dim}曦覽終端機{Reset}") + Environment.NewLine);
        foreach (var lineText in f) sb.Append(BoxRow(width, BrandRed + lineText + Reset) + Environment.NewLine);
        sb.Append(BoxBot(width));
        return sb.ToString();
    }

    /// <summary>輸入框：紅色提示符；提交後由 ReadLineWithHistory 畫一條全寬分隔線。</summary>
    public static void InputFrame()
    {
        Console.Write($"{BrandRed}{Bold}>{Reset} ");
    }

    /// <summary>目前可用的行寬（重導或讀不到時 100）——給固定寬度初次取值用。
    /// 下限 40＝CJK 表格最小可用寬，與 <see cref="ConsoleWidth"/> 同一口徑（P4-24）。</summary>
    public static int CurrentWidth()
    {
        try { return Math.Max(40, Console.WindowWidth - 1); }
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

    // ── 5.5) 走勢圖・獨立螢幕 ──────────────────────────────────────────────

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
    // 〔修復 v3.21〕審計 D7：重導輸出（xsc facts > log）時 spinner 的 ESC 幀會汙染 log——
    // 動畫與游標控制全部跳過（取樣照跑，狀態語意不變），真正的結果行照印。
    private static bool SpinnerVisuals => !Console.IsOutputRedirected;

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
            if (SpinnerVisuals) Console.Write(HideCursor);
            _spinnerTask = Task.Run(async () =>
            {
                // 幀序列往返：✢→✶→✻→✽→✻→✶，100ms 一幀（Task.Delay(100)）
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
                        if (SpinnerVisuals)
                            Console.Write(CarriageReturn + ClearLine + $"{BrandRed}{frame}{Reset} {BrandRed}{verb}{Reset}{Dim}…{Reset} "
                                + $"[{bar}] {Bold}{secs,5:0.0}s{Reset}{Dim} · Ctrl+C 結束{Reset}");
                        // 示波器線：真實 CPU 負載取樣（PerformanceCounter；不可用時整行略過，不造假波形）
                        scopeTick++;
                        if (hasScope && SpinnerVisuals)
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
                                if (SpinnerVisuals)
                                    Console.Write(Environment.NewLine + $"  {Dim}CPU ╶{Reset}" + trace + $"{Dim}╴{Reset} {Bold}{load:0}%{Reset}");
                            }
                        }
                        // legacy：游標上移（[1F）在舊主控台印不出來，示波器線整段略過
                        hasScope = drewScope && !Legacy;
                        if (Legacy && SpinnerVisuals)
                        {
                            // legacy：ClearLine 是空字串——補空白擦掉上一拍的殘尾
                            string plain = $"{frame} {verb}… [{bar}] {secs,5:0.0}s · Ctrl+C 結束";
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
    public static void SpinnerStop(string? doneMessage = null, bool ok = true)
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
            if (SpinnerVisuals)
            {
                if (Legacy)
                    Console.Write(CarriageReturn + new string(' ', _spinPad + 2) + CarriageReturn);
                else
                {
                    Console.Write(Esc + "[1F" + CarriageReturn + ClearLine);
                    Console.Write(CarriageReturn + ClearLine);
                }
            }
            _spinPad = 0;
            double done = SpinnerClock.Elapsed.TotalSeconds;
            // 審計 #7：採集無法預知百分比——「100.0%」只有真正跑完才印；失敗印中斷條，不冒充完成
            if (SpinnerVisuals)
            {
                if (ok)
                    Console.Write($"[{BrandRed}{new string('▓', 22)}{Reset}] {Bold}100.0%{Reset} {Dim}({done:0.00}s){Reset}" + Environment.NewLine);
                else
                    Console.Write($"[{Dim}{new string('░', 10)}{SwissRed}✕{Dim}{new string('░', 11)}{Reset}] {Dim}中斷於 {done:0.00}s{Reset}" + Environment.NewLine);
                Console.Write(ShowCursor);
            }
            if (!string.IsNullOrEmpty(doneMessage))
                Console.WriteLine($"{BrandRed}⏺{Reset} {doneMessage}");
        }
    }

    /// <summary>
    /// 以「失敗」狀態收掉轉圈：印中斷條（✕），<b>絕不印 100.0%</b>。
    /// 審計 #7 的規則是「失敗印中斷條，不冒充完成」——但決定權原本留在
    /// SpinnerStop 的選擇性參數上，呼叫端忘了傳 <c>ok: false</c> 就把失敗印成 100%
    /// （實例：pci scan 失敗時「掃描失敗：…」配著 100.0% 同屏）。此方法讓失敗態
    /// 有自己的名字，不必依賴呼叫端記得參數。
    /// 轉圈已停（<see cref="SpinnerStop"/> 已執行過）時是無操作——可在 finally 裡無腦呼叫。
    /// </summary>
    public static void SpinnerFail(string? failMessage = null)
        => SpinnerStop(failMessage, ok: false);

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
