import io

p = r"C:\Users\Administrator\XinSpect\XinSpectCli\Program.cs"
s = io.open(p, encoding="utf-8").read()

# 1) 欄位：記住上次終端寬度
old0 = """    private static string? ReadLineWithHistory()"""
assert old0 in s
new0 = """    private static int _lastWidth;

    /// <summary>終端寬度變了就整屏重繪：拖窄時右側自動隱藏，拉寬時重新出現。</summary>
    private static bool CheckResize()
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

    private static string? ReadLineWithHistory()"""
s = s.replace(old0, new0, 1)

# 2) 按鍵迴圈開頭：先查 resize——變了就 Clear＋重印歡迎框＋目前輸入行
old1 = """        var buf = new StringBuilder();
        int hist = History.Count;
        int pos = 0;
        while (true)
        {
            var key = Console.ReadKey(intercept: true);"""
assert old1 in s, "key loop not found"
new1 = """        var buf = new StringBuilder();
        int hist = History.Count;
        int pos = 0;
        while (true)
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
            var key = Console.ReadKey(intercept: true);"""
s = s.replace(old1, new1, 1)

# 3) 啟動時記錄初始寬度
old2 = """        Tui.Welcome(AppInfo.ShortVersion,
            staticKeys: FactKeyCatalog.Count,
            dynamicFamilies: FactKeyDynamicCatalog.Families.Count);"""
assert old2 in s, "welcome call not found"
new2 = """        Tui.Welcome(AppInfo.ShortVersion,
            staticKeys: FactKeyCatalog.Count,
            dynamicFamilies: FactKeyDynamicCatalog.Families.Count);
        try { _lastWidth = Console.WindowWidth; } catch { }"""
s = s.replace(old2, new2, 1)

io.open(p, "w", encoding="utf-8", newline="").write(s)
print("live resize redraw wired")
