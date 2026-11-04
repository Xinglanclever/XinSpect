import io

p_tui = r"C:\Users\Administrator\XinSpect\XinSpectCli\Tui.cs"
p_prog = r"C:\Users\Administrator\XinSpect\XinSpectCli\Program.cs"

# ── Tui.cs ──
s = io.open(p_tui, encoding="utf-8").read()

old1 = "Console.WriteLine(Bot());"
assert s.count(old1) == 1, f"Bot count={s.count(old1)}"
s = s.replace(old1, 'Console.WriteLine($"{BrandRed}┬{new string(\'─\', width - 3)}╯{Reset}");')

old2 = 'Console.WriteLine($"  {Dim}輸入 facts 採集全機事實，或輸入 sys、dash、pages、msr <索引>…{Reset}");'
assert old2 in s, "hint1 not found"
s = s.replace(old2, 'Console.WriteLine($"{BrandRed}│{Reset}  {Dim}輸入 facts 採集全機事實，或輸入 sys、dash、pages、msr <索引>…{Reset}");')

old3 = 'Console.WriteLine($"  {Dim}唯讀 · 不寫硬體 · 不連網  │  ↑↓ 歷史 · Tab 補全 · help 指令書 · exit 離開{Reset}");'
assert old3 in s, "hint2 not found"
s = s.replace(old3, 'Console.WriteLine($"{BrandRed}│{Reset}  {Dim}唯讀 · 不寫硬體 · 不連網  │  ↑↓ 歷史 · Tab 補全 · help 指令書 · exit 離開{Reset}");')

old4 = 'Console.WriteLine($"{Dim}{new string(\'─\', width)}{Reset}");\n        Console.Write($"{BrandRed}{Bold}>{Reset} ");'
assert old4 in s, "InputFrame not found"
s = s.replace(old4, 'Console.WriteLine($"{BrandRed}╭{new string(\'─\', width - 2)}╮{Reset}");\n        Console.Write($"{BrandRed}│{Reset} {BrandRed}{Bold}>{Reset} ");')

io.open(p_tui, "w", encoding="utf-8", newline="").write(s)
print("tui patched")

# ── Program.cs ──
s = io.open(p_prog, encoding="utf-8").read()

old5 = """            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                int w;
                try { w = Math.Max(60, Console.WindowWidth - 1); } catch { w = 100; }
                Console.WriteLine($"{Tui.Dim}{new string('─', w)}{Tui.Reset}");   // 輸入欄底線（全寬，與上線成對）"""
assert old5 in s, "Enter block not found"
new5 = """            if (key.Key == ConsoleKey.Enter)
            {
                int w;
                try { w = Math.Max(60, Console.WindowWidth - 1); } catch { w = 100; }
                Console.Write(Tui.Esc + "[" + w + "G" + Tui.BrandRed + "│" + Tui.Reset + Environment.NewLine);
                Console.WriteLine($"{Tui.BrandRed}╰{new string('─', w - 2)}╯{Tui.Reset}");   // 長方框底線（閉合）"""
s = s.replace(old5, new5)

old6 = '''        Console.Write("\\r\\x1b[K");
        Console.Write($"{Tui.BrandRed}{Tui.Bold}>{Tui.Reset} " + buf.ToString());
        if (pos < buf.Length) Console.Write($"\\x1b[{buf.Length - pos}D");'''
assert old6 in s, "Redraw not found"
new6 = '''        int w;
        try { w = Math.Max(60, Console.WindowWidth - 1); } catch { w = 100; }
        Console.Write("\\r\\x1b[K");
        Console.Write($"{Tui.BrandRed}│{Tui.Reset} {Tui.BrandRed}{Tui.Bold}>{Tui.Reset} " + buf.ToString());
        Console.Write(Tui.Esc + "[" + w + "G" + Tui.BrandRed + "│" + Tui.Reset);
        Console.Write(Tui.Esc + "[" + (5 + pos) + "G");   // 游標回輸入位置'''
s = s.replace(old6, new6)

io.open(p_prog, "w", encoding="utf-8", newline="").write(s)
print("program patched")
