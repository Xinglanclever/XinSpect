import io

p = r"C:\Users\Administrator\XinSpect\XinSpectCli\Program.cs"
s = io.open(p, encoding="utf-8").read()

# 1) 主迴圈：任何例外都攔下顯示，永不退回 cmd（使用者看到的 C:/xxx> 就是炸出去的 cmd 提示符）
old1 = """        string? line;
        while (true)
        {
            Tui.InputFrame();
            line = ReadLineWithHistory();
            if (line is null) break;              // Ctrl+Z／EOF
            line = line.Trim();
            if (line.Length == 0) continue;
            History.Add(line);
            if (!Execute(line)) break;
        }
        return 0;"""
assert old1 in s, "main loop not found"
new1 = """        string? line;
        while (true)
        {
            try
            {
                Tui.InputFrame();
                line = ReadLineWithHistory();
                if (line is null) break;              // Ctrl+Z／EOF
                line = line.Trim();
                if (line.Length == 0) continue;
                History.Add(line);
                if (!Execute(line)) break;
            }
            catch (Exception ex)
            {
                // REPL 永不崩：任何例外（控制台 IO、句柄、重導邊緣案例）攔下顯示後繼續，
                // 使用者絕不被踢回 cmd 提示符——那個 C:/xxx> 就是炸出去的殘骸。
                try { Console.CursorVisible = true; } catch { }
                Console.WriteLine();
                Console.WriteLine($"{Red}內部錯誤（已攔下，直接輸入下一個指令繼續）：{ex.GetType().Name} {ex.Message}{Reset}");
            }
        }
        return 0;"""
s = s.replace(old1, new1)

# 2) ReadLineWithHistory：按鍵迴圈也要防護——例外時把已輸入的內容如實帶回，不輸掉
old2 = """        var buf = new StringBuilder();
        int hist = History.Count;
        int pos = 0;
        while (true)
        {
            var key = Console.ReadKey(intercept: true);"""
assert old2 in s, "key loop not found"
new2 = """        var buf = new StringBuilder();
        int hist = History.Count;
        int pos = 0;
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            try
            {"""
s = s.replace(old2, new2)

# 3) 迴圈尾端補 catch：Enter 分支 return 前的例外一併攔；非 Enter 例外繼續
old3 = """            if (key.KeyChar == '\\0') continue;
            if (char.IsControl(key.KeyChar)) continue;
            buf.Insert(pos, key.KeyChar);
            pos++;
            Redraw(buf, pos);
        }
    }"""
assert old3 in s, "loop tail not found"
new3 = """            if (key.KeyChar == '\\0') continue;
            if (char.IsControl(key.KeyChar)) continue;
            buf.Insert(pos, key.KeyChar);
            pos++;
            Redraw(buf, pos);
            }
            catch (Exception ex)
            {
                try { Console.CursorVisible = true; } catch { }
                Console.WriteLine();
                Console.WriteLine($"{Red}輸入行錯誤（已攔下）：{ex.GetType().Name} {ex.Message}{Reset}");
                return buf.ToString();   // 已輸入的內容如實帶回，不輸掉
            }
        }
    }"""
s = s.replace(old3, new3)

io.open(p, "w", encoding="utf-8", newline="").write(s)
print("never-crash wired")
