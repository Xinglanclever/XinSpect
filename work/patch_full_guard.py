import io

p_tui = r"C:\Users\Administrator\XinSpect\XinSpectCli\Tui.cs"
p_prog = r"C:\Users\Administrator\XinSpect\XinSpectCli\Program.cs"

# ── 1) Tui：spinner 後台執行緒整體 try/catch(Exception)——IO 例外不再炸進程 ──
s = io.open(p_tui, encoding="utf-8").read()
old1 = """                try
                {
                    while (!token.IsCancellationRequested)
                    {"""
assert old1 in s, "spinner try not found"
new1 = """                try
                {
                    while (!token.IsCancellationRequested)
                    {"""
# spinner 內部已有 OperationCanceled catch；把 catch 範圍擴到 Exception
old_catch = """                catch (OperationCanceledException) { }
            }, token);"""
assert old_catch in s, "spinner catch not found"
new_catch = """                catch (OperationCanceledException) { }
                catch (Exception) { /* 示波器線繪製等 IO 例外：動畫停了就好，程序不能死 */ }
            }, token);"""
s = s.replace(old_catch, new_catch)
io.open(p_tui, "w", encoding="utf-8", newline="").write(s)
print("spinner guarded")

# ── 2) Program：CheckResize 整體防護（Welcome 渲染例外不再炸）──
s = io.open(p_prog, encoding="utf-8").read()
old2 = """    private static bool CheckResize()
    {
        if (Console.IsInputRedirected) return false;
        int w;
        try { w = Console.WindowWidth; } catch { return false; }
        if (w == _lastWidth) return false;
        _lastWidth = w;
        try { Console.Clear(); } catch { }
        Tui.Welcome(AppInfo.ShortVersion,
            staticKeys: FactKeyCatalog.Count,
            dynamicFamilies: FactKeyDynamicCatalog.Families.Count);
        return true;
    }"""
assert old2 in s, "CheckResize not found"
new2 = """    private static bool CheckResize()
    {
        try
        {
            if (Console.IsInputRedirected) return false;
            int w;
            try { w = Console.WindowWidth; } catch { return false; }
            if (w == _lastWidth) return false;
            _lastWidth = w;
            try { Console.Clear(); } catch { }
            Tui.Welcome(AppInfo.ShortVersion,
                staticKeys: FactKeyCatalog.Count,
                dynamicFamilies: FactKeyDynamicCatalog.Families.Count);
            return true;
        }
        catch (Exception)
        {
            // resize 重繪途中任何例外（控制台 IO 等）：放棄這次重繪，程序絕不因此死掉
            return false;
        }
    }"""
s = s.replace(old2, new2)

# ── 3) ReadLineWithHistory 迴圈內的 CheckResize 呼叫加防護 ──
old3 = """            if (CheckResize())
            {
                int w;
                try { w = Math.Max(60, Console.WindowWidth - 1); } catch { w = 100; }
                Console.WriteLine($"{Tui.Dim}{new string('─', w)}{Tui.Reset}");
                Console.Write($"{Tui.BrandRed}{Tui.Bold}>{Tui.Reset} " + buf.ToString());
                if (pos < buf.Length) Console.Write($"\\x1b[{buf.Length - pos}D");
                continue;
            }"""
assert old3 in s, "loop CheckResize not found"
new3 = """            try
            {
                if (CheckResize())
                {
                    int w;
                    try { w = Math.Max(60, Console.WindowWidth - 1); } catch { w = 100; }
                    Console.WriteLine($"{Tui.Dim}{new string('─', w)}{Tui.Reset}");
                    Console.Write($"{Tui.BrandRed}{Tui.Bold}>{Tui.Reset} " + buf.ToString());
                    if (pos < buf.Length) Console.Write($"\\x1b[{buf.Length - pos}D");
                    continue;
                }
            }
            catch { /* resize 重繪失敗不影響輸入 */ }"""
s = s.replace(old3, new3)

# ── 4) Main 裡的啟動 Welcome 呼叫加防護 ──
old4 = """        Tui.Welcome(AppInfo.ShortVersion,
            staticKeys: FactKeyCatalog.Count,
            dynamicFamilies: FactKeyDynamicCatalog.Families.Count);
        try { _lastWidth = Console.WindowWidth; } catch { }"""
assert old4 in s, "startup Welcome not found"
new4 = """        try
        {
            Tui.Welcome(AppInfo.ShortVersion,
                staticKeys: FactKeyCatalog.Count,
                dynamicFamilies: FactKeyDynamicCatalog.Families.Count);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{Red}歡迎框渲染失敗（不影響指令）：{ex.Message}{Reset}");
        }
        try { _lastWidth = Console.WindowWidth; } catch { }"""
s = s.replace(old4, new4)

io.open(p_prog, "w", encoding="utf-8", newline="").write(s)
print("all crash paths guarded")
