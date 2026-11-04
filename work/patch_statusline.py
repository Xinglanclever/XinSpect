import io

p_tui = r"C:\Users\Administrator\XinSpect\XinSpectCli\Tui.cs"
p_prog = r"C:\Users\Administrator\XinSpect\XinSpectCli\Program.cs"

# ── Tui.cs：輸入框改回兩條長線（去長方框）──
s = io.open(p_tui, encoding="utf-8").read()
old = 'Console.WriteLine($"{BrandRed}╭{new string(\'─\', width - 2)}╮{Reset}");\n        Console.Write($"{BrandRed}│{Reset} {BrandRed}{Bold}>{Reset} ");'
assert old in s, "InputFrame box not found"
s = s.replace(old, 'Console.WriteLine($"{Dim}{new string(\'─\', width)}{Reset}");\n        Console.Write($"{BrandRed}{Bold}>{Reset} ");')
io.open(p_tui, "w", encoding="utf-8", newline="").write(s)
print("tui input frame -> two lines")

# ── Program.cs ──
s = io.open(p_prog, encoding="utf-8").read()

# 1) Enter：底線＋右對齊狀態行（權限＋內核）
old1 = """            if (key.Key == ConsoleKey.Enter)
            {
                int w;
                try { w = Math.Max(60, Console.WindowWidth - 1); } catch { w = 100; }
                Console.Write(Tui.Esc + "[" + w + "G" + Tui.BrandRed + "│" + Tui.Reset + Environment.NewLine);
                Console.WriteLine($"{Tui.BrandRed}╰{new string('─', w - 2)}╯{Tui.Reset}");   // 長方框底線（閉合）
                string status = "● 唯讀 · 不寫硬體 · 不連網  │  ↑↓ 歷史 · Tab 補全 · help 指令書 · book 百科";
                int scol = Math.Max(1, w - status.Length - 1);
                Console.Write(Tui.Esc + "[" + scol + "G" + Tui.Dim + status + Tui.Reset + Environment.NewLine);
                return buf.ToString();
            }"""
assert old1 in s, "Enter block not found"
new1 = """            if (key.Key == ConsoleKey.Enter)
            {
                int w;
                try { w = Math.Max(60, Console.WindowWidth - 1); } catch { w = 100; }
                Console.WriteLine($"{Tui.Dim}{new string('─', w)}{Tui.Reset}");   // 輸入欄底線
                var st = StatusLine();
                int scol = Math.Max(1, w - Tui.VisibleLength(st) - 1);
                Console.Write(Tui.Esc + "[" + scol + "G" + Tui.Dim + st + Tui.Reset + Environment.NewLine);"""
s = s.replace(old1, new1)

# 2) Redraw 回復簡式（無框線）
old2 = '''        int w;
        try { w = Math.Max(60, Console.WindowWidth - 1); } catch { w = 100; }
        Console.Write("\\r\\x1b[K");
        Console.Write($"{Tui.BrandRed}│{Tui.Reset} {Tui.BrandRed}{Tui.Bold}>{Tui.Reset} " + buf.ToString());
        Console.Write(Tui.Esc + "[" + w + "G" + Tui.BrandRed + "│" + Tui.Reset);
        Console.Write(Tui.Esc + "[" + (5 + pos) + "G");   // 游標回輸入位置'''
assert old2 in s, "Redraw not found"
new2 = '''        Console.Write("\\r\\x1b[K");
        Console.Write($"{Tui.BrandRed}{Tui.Bold}>{Tui.Reset} " + buf.ToString());
        if (pos < buf.Length) Console.Write($"\\x1b[{buf.Length - pos}D");'''
s = s.replace(old2, new2)

# 3) StatusLine()：真實偵測——權限（SYSTEM／administrator／標準）＋Ring0 驅動註冊狀態
anchor = "    private static string? ReadLineWithHistory()"
assert anchor in s
helper = '''    /// <summary>狀態行：面前是什麼權限（SYSTEM／administrator／標準）＋Ring0 驅動是否已註冊。
    /// 全部如實偵測：登錄檔查 WinRing0／XsRegProbe 服務機碼，查無就是未註冊，不編造就緒。</summary>
    private static string StatusLine()
    {
        string priv;
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            if (id.User == new System.Security.Principal.SecurityIdentifier("S-1-5-18"))
                priv = "SYSTEM";
            else if (new System.Security.Principal.WindowsPrincipal(id)
                     .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
                priv = "administrator";
            else
                priv = "標準權限";
        }
        catch { priv = "權限不明"; }

        string ring0 = "ring0 未註冊";
        try
        {
            using var services = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\\CurrentControlSet\\Services");
            if (services is not null)
                foreach (var name in services.GetSubKeyNames())
                    if (name.StartsWith("WinRing0", StringComparison.OrdinalIgnoreCase)
                        || name.StartsWith("XsRegProbe", StringComparison.OrdinalIgnoreCase))
                    {
                        ring0 = "ring0 驅動已註冊";
                        break;
                    }
        }
        catch { ring0 = "ring0 —"; }

        return $"{Tui.BrandRed}●{Tui.Reset} {priv} · {ring0}";
    }

''' + anchor
s = s.replace(anchor, helper, 1)

# 4) 啟動時：歡迎框後右對齊印一次狀態行（讓使用者立刻看到權限）
old3 = '''        string? line;
        while (true)
        {
            Tui.InputFrame();'''
new3 = '''        var startupStatus = StatusLine();
        int sw;
        try { sw = Math.Max(60, Console.WindowWidth - 1); } catch { sw = 100; }
        int scol = Math.Max(1, sw - Tui.VisibleLength(startupStatus) - 1);
        Console.Write(Tui.Esc + "[" + scol + "G" + Tui.Dim + startupStatus + Tui.Reset + Environment.NewLine);
        string? line;
        while (true)
        {
            Tui.InputFrame();'''
assert old3 in s
s = s.replace(old3, new3)

io.open(p_prog, "w", encoding="utf-8", newline="").write(s)
print("program status line wired")
