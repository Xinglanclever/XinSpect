import io

p = r"C:\Users\Administrator\XinSpect\XinSpectCli\Tui.cs"
s = io.open(p, encoding="utf-8").read()

# ── 1) 在 Tui 加入通用框線 helpers（CHA 絕對定位，零寬度計算）──
anchor = "    // ── 2) 帶標題的方框面板 ───────────────────────────────────────────────"
assert anchor in s
helpers = '''    // ── 1.9) 框線 helpers（唯一正解：邊框位置一律 CHA 絕對定位，零寬度計算）──
    // 歷史教訓：所有框線錯位都源於「用 VisibleLength 算補白」——全形標點、✻↑↓☼ 等
    // 模糊寬度字元、使用者終端字型回退，都會讓計算值與實際渲染差 1–2 格。
    // CHA（\\x1b[<col>G）把游標釘到絕對欄位，畫不出來就 clamp 到行尾，永不折行、永不錯位。
    // 內文超寬時會被右框線壓住——那是可接受的降級；框線錯位不可接受。

    /// <summary>框頂：╭ + 破折號 + 標題（CHA 覆寫）+ ╮（CHA 釘在 width 列）。</summary>
    public static string BoxTop(int width, string title = "")
    {
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
        => $"{BrandRed}│{Reset} {body}{Esc}[{width}G{BrandRed}│{Reset}";

    /// <summary>雙欄框行：│ 左欄 │ 右欄 │（中欄與右框線都 CHA 釘死）。</summary>
    public static string BoxTwoCol(int width, int mid, string left, string right)
        => $"{BrandRed}│{Reset} {left}{Esc}[{mid}G{BrandRed}│{Reset} {right}{Esc}[{width}G{BrandRed}│{Reset}";

''' + anchor
s = s.replace(anchor, helpers, 1)

# ── 2) Welcome 改用 helpers ──
w_start = s.index("    public static void Welcome(string version, int staticKeys, int dynamicFamilies)")
w_end = s.index("    private static string ShortCwd()", w_start)
new_welcome = '''    public static void Welcome(string version, int staticKeys, int dynamicFamilies)
    {
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

'''
s = s[:w_start] + new_welcome + s[w_end:]

# ── 3) MascotBox 改用 helpers ──
m_start = s.index("    public static string MascotBox(int frame)")
m_end = s.index("    /// <summary>Claude Code 式輸入框", m_start)
new_mbox = '''    public static string MascotBox(int frame)
    {
        const int width = 46;
        var f = MascotFrames[frame];
        var sb = new System.Text.StringBuilder();
        sb.Append(BoxTop(width, $"{Dim}曦覽終端機{Reset}") + Environment.NewLine);
        foreach (var lineText in f) sb.Append(BoxRow(width, BrandRed + lineText + Reset) + Environment.NewLine);
        sb.Append(BoxBot(width));
        return sb.ToString();
    }

'''
s = s[:m_start] + new_mbox + s[m_end:]

# ── 4) Frame 改用 helpers（changelog 等共用）──
f_start = s.index('    public static void Frame(string title, IEnumerable<string> lines)')
f_end = s.index("    // ── 3) 表格 ─", f_start)
new_frame = '''    public static void Frame(string title, IEnumerable<string> lines)
    {
        const int width = 78;
        var body = lines.ToList();

        Console.WriteLine(BoxTop(width, $"{Bold} {title.ToUpperInvariant()} {Reset}"));
        foreach (var l in body)
            Console.WriteLine(BoxRow(width, l));
        Console.WriteLine(BoxBot(width));
    }

'''
s = s[:f_start] + new_frame + s[f_end:]

io.open(p, "w", encoding="utf-8", newline="").write(s)
print("refactored to CHA helpers")
