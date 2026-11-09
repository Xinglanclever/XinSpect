using System.IO;
using System.Text;
using BlueSquadron;
using XinSpect;

namespace XinSpectCli;

/// <summary>
/// 曦覽終端機（xsc）：Claude Code 式的互動 REPL，跑在使用者的真終端裡。
/// </summary>
/// <remarks>
/// <para>
/// 與主程式的關係：<b>獨立 exe</b>（像 BlueSquadronBridge 一樣是倉庫裡的第二個執行檔），
/// 以 ProjectReference 復用主倉庫的唯讀服務；它不是主程式的一部分，也沒有 WPF 視窗。
/// </para>
/// <para>
/// 終端體驗：ANSI 上色、↑↓ 歷史、Tab 補全、`top` 即時刷新（Ctrl+C 停）。
/// 所有硬體通路與主程式同一套唯讀服務——不新增寫入、不常駐、不連網。
/// </para>
/// </remarks>
internal static class Program
{
    // ── ANSI ──────────────────────────────────────────────────────────────
    // 顏色跟著 Tui 走：legacy 純文字版全部為空字串，呼叫端寫法不變
    private static string Dim => Tui.Dim;
    private static string Red => Tui.SwissRed;
    private static string Green => Tui.SwissGreen;
    private static string Blue => Tui.SwissBlue;
    private static string Bold => Tui.Bold;
    private static string Reset => Tui.Reset;

    // 補全清單從 CommandBook 推導（單一事實來源——此前寫死 23 個、分派已 45+，體驗與
    // 「兩處寫死會漂移」的同款病）。別名與文檔外的可用指令在此補列。
    private static readonly string[] Commands = BuildCommands();

    private static string[] BuildCommands()
    {
        var names = new List<string>();
        foreach (var c in CommandBook.Commands)
        {
            var name = c.Name.Split(' ')[0];           // 「fact <關鍵字>」→ fact
            if (!names.Contains(name)) names.Add(name);
        }
        foreach (var alias in new[] { "refresh", "mascot", "吉祥物", "百科", "quit", "q", "?", "language" })
            if (!names.Contains(alias)) names.Add(alias);
        return names.OrderBy(n => n, StringComparer.Ordinal).ToArray();
    }

    private static readonly List<string> History = [];
    private static IReadOnlyList<HardwareFact>? _cache;
    private static int _frameWidth = 100;   // 輸入框兩條長線的固定寬度（啟動時定，resize 才更新）

    private static int Main(string[] args)
    {
        if (!CliLocalization.TryConsumeOption(args, out string[] remainingArgs, out string? languageError))
        {
            Console.Error.WriteLine(languageError);
            Console.Error.WriteLine("Usage: xsc [--lang zh-TW|zh-CN] [command]");
            return 2;
        }
        args = remainingArgs;

        try { Console.OutputEncoding = Encoding.UTF8; }
        catch { /* 管線斷開（stdout 關閉）時設定編碼會丟例外——啟動不能死在這（審計 #22） */ }
        CliLocalization.InstallConsoleWriters(CliLocalization.IsMachineFormat(args));
        Tui.EnableAnsi();   // Win10 傳統 conhost 預設不開 VT，不開的話整個界面印成原始 ESC 序列
        try { Console.CursorVisible = true; } catch { /* 重導輸出時沒有游標可管 */ }

        // 明示的系統修復動作（審計 #1 的配套）：刪除死指針的 WinRing0 服務。需要管理員。
        // 〔修復 v3.21〕審計 D4：成敗如實回報——非管理員 1、sc.exe 失敗 2、成功 0。
        if (args.Length == 1 && args[0].Equals("--repair-ring0", StringComparison.OrdinalIgnoreCase))
        {
            return RepairRing0Service();
        }

        // ── 提權（ring0 對齊桌面端 DriverReadyService.RestartElevated）────────
        // xsc --elevate [ --repair-ring0 ]：以 runas 重啟自身，提權實例完成 WinRing0 服務
        // 健檢（唯讀）後停留為提權 REPL（或帶 --repair-ring0 時修完即退）。
        // 三態如實回報：成功 / UAC 被取消或被拒 / 其他失敗——退出碼 0／1／2 對應。
        // 分工：--elevate 只負責「取得權限」，健檢只讀不動手；刪死指針服務是
        // --repair-ring0 的明示動作（審計 #1 教訓：系統寫入絕不藏在別的旗標底下）。
        if (args.Length >= 1 && args[0].Equals("--elevate", StringComparison.OrdinalIgnoreCase))
        {
            string[] rest = args[1..];
            bool repair = rest.Length == 1 && rest[0].Equals("--repair-ring0", StringComparison.OrdinalIgnoreCase);
            if (rest.Length > 0 && !repair)
            {
                Console.WriteLine($"{Red}用法：xsc --elevate [--repair-ring0]{Reset}");
                return 2;
            }
            if (IsElevated())
            {
                // 已是管理員：不重啟、不重彈 UAC（防「提權了還一直彈」的迴圈），如實告知後就地繼續。
                Console.WriteLine($"{Dim}已是系統管理員——不需要提權，直接繼續。{Reset}");
                if (repair) return RepairRing0Service();
                AnnounceElevatedRing0Check();
                // 〔修復 v3.21〕審計 D1：此處 args 仍帶 "--elevate"，下方一次性分派會把它送進
                // CliService 報「未知引數」後 exit 1——承諾的提權 REPL 從未出現過。清空引數
                // 讓流程落到 REPL（此時已是提權 REPL），與 Main 註解／CommandBook 的宣稱一致。
                args = Array.Empty<string>();
            }
            else
            {
                // 〔複驗更正〕2026-11-07 紅隊複驗：原稿傳 `rest`（args[1..]）——提權實例收到的
                // 參數**不含 "--elevate"**，Main 的分派永遠進不了 --elevate 分支：
                // `xsc --elevate` 的提權實例會落成無參數的普通 REPL，第 1 節宣稱的
                // 「提權實例先做一次 WinRing0 服務健檢」與 `[ring0 健檢]` 一行都不會出現
                //（驗證路徑 1 必敗）。子行程必須帶回 "--elevate"，讓提權實例重進本分支、
                // 走 IsElevated()==true 路徑（健檢＋進 REPL；帶 --repair-ring0 時修復退出）。
                // 提權實例必是管理員，不會再彈 UAC、無遞迴風險。
                string[] childArgs = repair
                    ? new[] { "--elevate", "--repair-ring0" }
                    : new[] { "--elevate" };
                if (CliLocalization.Current == CliLanguage.Simplified)
                    childArgs = [.. childArgs, "--lang", "zh-CN"];
                return RelaunchElevated(childArgs);
            }
        }

        if (args.Length > 0)
        {
            // xsc compliance --format sarif …：直接轉發既有 CLI 通路後退出
            return CliService.Run(args, Collect, Console.Out, Console.Error);
        }

        // 重導輸入（管線／批次檔）：ReadKey 在這裡會丟例外，被主迴圈的防護吞掉後回空字串＝無限空轉。
        // 這條路改用 Console.ReadLine，EOF（null）正常收工；跳過歡迎框——管線輸出要乾淨。
        if (Console.IsInputRedirected)
        {
            // 管線／批次：腳本與代理要能偵測失敗——未知指令、指令例外與 RunCli 非零退出碼都計
            // 失敗，退出碼 1；整條管線崩到無法繼續才是 2。〔修復 v3.21〕審計 D3：原本 Execute
            // 永遠回 true、RunCli 回傳值被丟棄，失敗計數是空的，最常見的兩種失敗都 exit 0。
            int failures = 0;
            try
            {
            while (true)
            {
                try
                {
                    string? piped = Console.ReadLine();
                    if (piped is null) break;              // EOF
                    piped = piped.Trim();
                    if (piped.Length == 0) continue;
                    var first = piped.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (first.Length == 0) continue;
                    var head = first[0].ToLowerInvariant();
                    if (!Commands.Contains(head) && head != "help" && head != "?" && head != "exit"
                        && head != "quit" && head != "q")
                    {
                        Console.WriteLine($"{Red}未知指令：{head}{Reset}（help 看清單）");
                        failures++;
                        continue;
                    }
                    int r = Execute(piped);
                    if (r == -1) break;                    // exit／quit：如實收工
                    if (r != 0) failures++;
                }
                catch (Exception ex)
                {
                    failures++;
                    Console.WriteLine($"{Red}內部錯誤（已攔下，繼續讀下一行）：{ex.GetType().Name} {ex.Message}{Reset}");
                }
            }
            }
            catch (Exception fatal)
            {
                // 輸入流本身崩了（ReadLine 連續拋、主控台句柄失效）＝管線無法繼續——如實回 2。
                Console.WriteLine($"{Red}管線無法繼續：{fatal.GetType().Name} {fatal.Message}{Reset}");
                return 2;
            }
            return failures > 0 ? 1 : 0;
        }

        try
        {
            Tui.Welcome(AppInfo.ShortVersion,
                staticKeys: FactKeyCatalog.Count,
                dynamicFamilies: FactKeyDynamicCatalog.Families.Count);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{Red}歡迎框渲染失敗（不影響指令）：{ex.Message}{Reset}");
        }
        _frameWidth = Tui.CurrentWidth();   // 提交後分隔線的固定寬度
        try { _lastWidth = Console.WindowWidth; } catch { }
        Tui.Footer("● READY ・ 唯讀模式", "曦覽 XinSpect Console v" + AppInfo.ShortVersion);
        var startupStatus = StatusLine();
        int scol = Math.Max(1, _frameWidth - Tui.VisibleLength(startupStatus) - 1);
        if (Tui.Legacy) Console.WriteLine(Tui.Dim + startupStatus + Tui.Reset);
        else Console.Write(Tui.Esc + "[" + scol + "G" + Tui.Dim + startupStatus + Tui.Reset + Environment.NewLine);
        string? line;
        while (true)
        {
            try
            {
                Tui.InputFrame();
                line = ReadLineWithHistory();
                if (line is null) break;              // Ctrl+Z／EOF
                line = line.Trim();
                if (line.Length == 0) continue;
                // 歷史：連續重複不入（↑ 體驗）＋容量上限（長會話記憶體不緩漲）
                if (History.Count == 0 || History[^1] != line) History.Add(line);
                if (History.Count > 200) History.RemoveAt(0);
                if (Execute(line) == -1) break;
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
        return 0;
    }

    // ── 指令分派 ──────────────────────────────────────────────────────────

    // 〔修復 v3.20〕審計 D3：Execute 改回「成敗可觀察」——回傳 0＝成功（繼續）、1＝失敗（繼續）、
    // -1＝結束（exit/quit/提權接手）。原本永遠回 true，管線模式的失敗計數對指令例外與
    // RunCli 退出碼全盲，「批次可偵測失敗」宣稱空洞。
    private static int Execute(string line)
    {
        string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string cmd = parts[0].ToLowerInvariant();
        string rest = parts.Length > 1 ? line[(line.IndexOf(' ') + 1)..].Trim() : "";
        try
        {
            switch (cmd)
            {
                case "help" or "?": CommandBook.Run(rest.Length > 0 ? rest : null); return 0;
                case "book" or "百科": Book.Run(rest.Length > 0 ? rest : null); return 0;
                case "手冊":
                    Console.WriteLine($"{Dim}手冊.md 是 v2.91 時代的歷史文件；目前版文件請用 book（曦覽百科）。{Reset}");
                    return 1;
                case "mascot" or "吉祥物": MascotPreview(rest); return 0;
                case "lang" or "language":
                    if (string.IsNullOrWhiteSpace(rest))
                    {
                        Console.WriteLine(CliLocalization.Current == CliLanguage.Simplified
                            ? "目前語言：简体中文。用 lang zh-TW 或 lang zh-CN 切换。"
                            : "目前語言：繁體中文。用 lang zh-TW 或 lang zh-CN 切換。");
                        return 0;
                    }
                    if (!CliLocalization.TrySet(rest))
                    {
                        Console.WriteLine("用法：lang zh-TW | zh-CN");
                        return 1;
                    }
                    Console.WriteLine(CliLocalization.Current == CliLanguage.Simplified
                        ? "已切换到简体中文。"
                        : "已切換至繁體中文。");
                    return 0;
                case "ver":
                    Tui.Frame($"曦覽 XinSpect v{AppInfo.Version}",
                    [
                        $"代號：{AppInfo.Codename}",
                        "形式：便攜單一執行檔（GUI）＋ xsc 終端機（本程式）",
                        "承諾：唯讀 ・ 不寫硬體 ・ 不連網",
                    ]);
                    return 0;

                case "pages": ListPages(); return 0;
                case "page": PageFacts(rest); return 0;
                case "domain": DomainFacts(rest); return 0;
                case "keys": ListKeys(rest); return 0;
                case "settings": ShowSettings(); return 0;
                case "changelog": ShowChangelog(rest); return 0;
                case "clear":
                    if (Tui.Legacy) { try { Console.Clear(); } catch { } }
                    else Console.Write("\x1b[2J\x1b[H");
                    return 0;
                case "elevate":
                    // REPL 內提權：對齊桌面端「以系統管理員重新啟動」按鈕。
                    if (Console.IsInputRedirected)
                    {
                        Console.WriteLine($"{Red}elevate 會彈 UAC——管線模式不適用，請改用 xsc --elevate。{Reset}");
                        return 0;
                    }
                    if (IsElevated())
                    {
                        Console.WriteLine($"{Dim}已經是系統管理員——Ring0 通路應已可用（見狀態行）。{Reset}");
                        return 0;
                    }
                    Console.WriteLine($"{Dim}以系統管理員重新啟動 xsc（會彈 UAC）……{Reset}");
                    // 〔複驗更正〕子行程必須帶 "--elevate"（原稿傳空陣列——提權實例會落成
                    // 無參數普通 REPL，缺 [ring0 健檢] 行）；同時避免 C#12 集合運算式
                    // `[]` 在舊 LangVersion 下的相容性疑慮，改 Array.Empty<string>()。
                    if (RelaunchElevated(new[] { "--elevate" }) == 0)
                        return -1;   // 提權實例接手，本 REPL 收工——對齊桌面端 Application.Current?.Shutdown()
                    // 取消／失敗：訊息已如實印出，留在原 REPL 繼續（唯讀功能不受影響）
                    return 0;

                case "exit" or "quit" or "q": return -1;

                case "facts":
                    if (rest.Contains("--refresh", StringComparison.Ordinal) ||
                        rest.Contains("refresh", StringComparison.Ordinal))
                        _cache = null;                     // 明確要求重採，快取作廢
                    EnsureFacts();
                    Console.WriteLine($"{Green}{_cache!.Count}{Reset} 筆事實（本機一輪採集）。");
                    Console.WriteLine($"{Dim}用 fact <關鍵字> 查細節；快取過期用 facts --refresh 重採。{Reset}");
                    return 0;

                case "refresh":
                    _cache = null;
                    EnsureFacts();
                    Console.WriteLine($"{Green}已重新採集：{_cache!.Count}{Reset} 筆事實。");
                    return 0;

                case "fact":
                    EnsureFacts();
                    SearchFacts(rest);
                    return 0;

                case "health": HealthSummary(); return 0;
                case "battery": DomainReport("電池健康", ["pw.battery"],
                    "電池資料來自 root\\WMI BatteryStaticData／BatteryFullChargedCapacity／BatteryCycleCount；沒有電池的機器會如實標 NotApplicable。"); return 0;
                case "storage": DomainReport("儲存總表", ["st", "ata"],
                    "SMART 原始屬性只有 ata.smart.attr.lookup（ATA 碟；NVMe 一律不讀——第 263 輪實證會卡核心不可中斷等待，理由在百科第 11 部）。"); return 0;
                case "net": DomainReport("網路", ["net", "nw"],
                    "net.* ＝ 傾聽埠／offload/WMI 缺口；nw.* ＝ 藍牙／DNS／Wi-Fi／WoL／USB 等週邊摘要。"); return 0;
                case "security": DomainReport("安全姿態", ["sec"],
                    "sec.* ＝ VBS／Credential Guard 等安全事實。即時防護分數走 ipc posture（藍色中隊引擎）。"); return 0;
                case "bsod": BsodScan(); return 0;
                case "posture": PostureReport(); return 0;
                case "firmware": DomainReport("韌體安全", ["fw", "uefi", "spi", "tpm"],
                    "fw.* ＝ BIOS 寫入保護／SMRAM／ME；uefi.* ＝ 開機鏈；spi.* ＝ BIOS 映像；tpm.* ＝ 平台信任根。裁決卡在 GUI 韌體安全頁。"); return 0;
                case "os": DomainReport("作業系統", ["os"],
                    "os.* ＝ 版本、授權、更新與安全態勢（Secure Boot／VBS／HVCI／UAC）。"); return 0;
                case "power": DomainReport("電源與睡眠", ["pw"],
                    "pw.* ＝ 電池（battery 指令看細節）、睡眠能力、喚醒來源、電源計劃漂移。"); return 0;
                case "catalog": CatalogQuery(rest); return 0;
                case "history": HistoryReport(); return 0;
                case "usb": UsbReport(); return 0;
                case "pcie": PcieReport(); return 0;
                case "drivers": DriverReport(); return 0;

                case "sys": LiveLine(); return 0;
                case "fan": Fans(); return 0;
                case "dash":
                    if (Console.IsInputRedirected)
                    {
                        Console.WriteLine($"{Red}dash 需要互動終端（按鍵退出）——管線模式請用 sys。{Reset}");
                        return 0;
                    }
                    Dashboard(); return 0;

                case "top":
                    if (Console.IsInputRedirected)
                    {
                        Console.WriteLine($"{Red}top 需要互動終端（按鍵退出）——管線模式請用 sys。{Reset}");
                        return 0;
                    }
                    LiveTop(rest); return 0;

                case "msr":
                    if (rest.Length == 0) { Console.WriteLine($"{Red}用法：msr <十六進位索引>，例如 msr 0x19C{Reset}"); return 0; }
                    ReadMsr(rest);
                    return 0;

                case "pci":
                    Pci(rest);
                    return 0;

                case "ipc":
                    if (rest.Length == 0) { Console.WriteLine($"{Red}用法：ipc ping|status|posture|threats|baseline|snapshot|probe{Reset}"); return 0; }
                    Ipc(rest);
                    return 0;

                case "redfish":
                    if (rest.Length == 0) { Console.WriteLine($"{Red}用法：redfish <離線目錄>{Reset}"); return 0; }
                    return RunCli(["--redfish", rest]) == 0 ? 0 : 1;

                case "compliance":
                    // 使用者寫「compliance --format sarif」與「compliance sarif」都要能動——
                    // rest 原樣拆詞往後送，不再整串塞給 --format（那個腳槍已拆除）。
                    return RunCli(rest.Length == 0
                        ? ["--compliance"]
                        : ["--compliance", .. rest.Split(' ', StringSplitOptions.RemoveEmptyEntries)]) == 0 ? 0 : 1;

                case "merkle":
                    // P4-19：原版寫死 ["--merkle"]——merkle 後面跟什麼都無聲消失。
                    // 多餘字元如實拒絕，不吞。
                    if (rest.Length > 0)
                    {
                        Console.WriteLine($"{Red}merkle 不接受任何參數（收到：{rest}）。{Reset}");
                        return 1;
                    }
                    return RunCli(["--merkle"]) == 0 ? 0 : 1;

                default:
                    Console.WriteLine($"{Red}未知指令：{cmd}{Reset}（help 看清單）");
                    return 1;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{Red}{ex.GetType().Name}：{ex.Message}{Reset}");
            return 1;
        }
    }

    // ── 事實採集與查詢 ────────────────────────────────────────────────────

    /// <summary>吉祥物候選預覽：mascot（全部依序）／mascot a／mascot b，每個候選播一輪動畫並標名。</summary>
    private static void MascotPreview(string arg)
    {
        var names = new[] { "A ・ 我的設計（日核菱形）", "B ・ Gemini 設計" };
        int choice = arg.Trim().ToLowerInvariant() switch
        {
            "a" => 0,
            "b" => 1,
            _ => -1,
        };
        var list = choice >= 0 ? new[] { choice } : new[] { 0, 1 };
        foreach (var c in list)
        {
            Console.WriteLine($"{Tui.BrandRed}{Tui.Bold}候選 {names[c]}{Tui.Reset}");
            Tui.MascotChoice = c;
            if (!Console.IsInputRedirected)
            {
                for (int round = 0; round < 4; round++)
                {
                    Console.WriteLine(Tui.MascotBox(round % 2));
                    System.Threading.Thread.Sleep(350);
                    try { Console.Clear(); } catch { }
                }
            }
            else
            {
                foreach (var lineText in Tui.MascotFrames[0]) Console.WriteLine("  " + lineText);
            }
            Console.WriteLine();
        }
        Console.WriteLine($"{Tui.Dim}跟我說選 A 還是 B（或再看一次：mascot）。選定後我把吉祥物換上。{Tui.Reset}");
    }

    private static void EnsureFacts()
    {
        if (_cache is not null)
        {
            Console.WriteLine($"{Dim}（使用現有快取；重採用 facts --refresh 或 refresh）{Reset}");
            return;
        }
        Tui.SpinnerStart();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            _cache = Collect();
        }
        catch
        {
            Tui.SpinnerStop("採集中斷——沒有事實進快取。", ok: false);
            throw;
        }
        sw.Stop();
        Tui.SpinnerStop($"facts ({FactKeyCatalog.Count:N0} 靜態鍵 · {FactKeyDynamicCatalog.Families.Count} 動態家族 · 耗時 {sw.Elapsed.TotalSeconds:0.00}s)");
        // ⎿ 結果行：快取筆數是真實收集到的數量（含動態展開），與靜態鍵數是兩個口徑，都如實報
        Console.WriteLine($"  {Dim}⎿{Reset}  已進快取 {_cache.Count:N0} 筆事實 · fact <關鍵字> 查單一 · dash 開全螢幕儀表板");
        Console.WriteLine();
    }

    private static void SearchFacts(string query)
    {
        if (_cache is null) { Console.WriteLine($"{Red}先跑 facts。{Reset}"); return; }
        IEnumerable<HardwareFact> rows = _cache;
        if (query.Length > 0)
            rows = rows.Where(f => f.Key.Contains(query, StringComparison.OrdinalIgnoreCase)
                                || f.Value.Contains(query, StringComparison.OrdinalIgnoreCase));
        var list = rows.Take(30).ToList();
        // P4-12：單鍵全文出口——查詢與某鍵名完全相等（不分大小寫）時，全文印出該鍰
        // （值＋單位＋可得性＋不可得原因），不受 56 字截斷。DomainReport 尾註
        // 「fact <關鍵字> 看單鍵全文」從此是真的。
        var exact = query.Length > 0
            ? _cache.FirstOrDefault(f => f.Key.Equals(query.Trim(), StringComparison.OrdinalIgnoreCase))
            : null;
        if (exact is not null)
        {
            Console.WriteLine($"{Bold}{exact.Key}{Reset} {Dim}〔單鍵全文〕{Reset}");
            Console.WriteLine($"  狀態：{exact.Availability}{(string.IsNullOrEmpty(exact.UnavailableReason) ? "" : $"（{exact.UnavailableReason}）")}");
            Console.WriteLine($"  值：{exact.Value}{(exact.Unit.Length > 0 ? " " + exact.Unit : "")}");
            if (exact.Name.Length > 0 && exact.Name != exact.Key) Console.WriteLine($"  名稱：{exact.Name}");
            Console.WriteLine();
        }
        Tui.Table(
            ["", "KEY", "狀態", "值"],
            list.Select(f => new[]
            {
                f.Availability switch
                {
                    FactAvailability.Present => $"{Tui.SwissGreen}✓{Tui.Reset}",
                    FactAvailability.Unknown => $"{Tui.Dim}?{Tui.Reset}",
                    _ => $"{Tui.Amber}⚠{Tui.Reset}",
                },
                f.Key,
                f.Availability.ToString(),
                Trim(f.Value, 56),
            }));
        int total = query.Length == 0 ? _cache.Count
            : _cache.Count(f => f.Key.Contains(query, StringComparison.OrdinalIgnoreCase)
                             || f.Value.Contains(query, StringComparison.OrdinalIgnoreCase));
        Console.WriteLine($"{Dim}… {list.Count} / {total} 筆（✓ 讀到 ・ ⚠ 讀取失敗/權限 ・ ? 未確認 ・ 值欄截 56 字，單鍵全文：fact <完整鍵名>）{Reset}");
    }

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

    // ── GUI 全功能對照：頁面清單、逐頁資料、域過濾、鍵目錄、設定、沿革 ────

    private static string HintOf(XinSpect.PageDef p)
    {
        string h = p.Hint ?? "";
        return h.Length > 44 ? h[..44] + "…" : h;
    }

    private static void ListPages()
    {
        Console.WriteLine();
        Console.WriteLine($"{Bold}主頁面（{PageRegistry.Pages.Count}）{Reset}");
        Tui.Table(["KEY", "頁面", "說明"],
            PageRegistry.Pages.Select(p => new[] { p.Key, p.Title, HintOf(p) }));
        Console.WriteLine();
        Console.WriteLine($"{Bold}實用工具（{PageRegistry.Utilities.Count}）{Reset}");
        Tui.Table(["KEY", "工具", "說明"],
            PageRegistry.Utilities.Select(p => new[] { p.Key, p.Title, HintOf(p) }));
        Console.WriteLine($"{Dim}用 page <key> 看該頁對應的資料（先跑 facts 建快取；說明欄截 44 字）。{Reset}");
    }

    private static void PageFacts(string key)
    {
        if (_cache is null) { Console.WriteLine($"{Red}先跑 facts。{Reset}"); return; }
        var def = PageRegistry.Pages.Concat(PageRegistry.Utilities)
            .FirstOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase)
                              || p.Title.Contains(key, StringComparison.OrdinalIgnoreCase));
        if (def is null) { Console.WriteLine($"{Red}找不到頁面：{key}（pages 看清單）{Reset}"); return; }

        Console.WriteLine($"{Bold}{def.Title}{Reset} {Dim}[{def.Key}・{def.Group}] {def.Hint}{Reset}");
        // 頁面關鍵字＋標題＋鍵名套到事實搜尋；這就是「那一頁」在終端裡的等價資料。
        var terms = new List<string> { def.Key, def.Title };
        terms.AddRange(def.Keywords);
        var hits = _cache.Where(f => terms.Any(t =>
                f.Key.Contains(t, StringComparison.OrdinalIgnoreCase)
                || f.Name.Contains(t, StringComparison.OrdinalIgnoreCase)
                || f.Value.Contains(t, StringComparison.OrdinalIgnoreCase)))
            .Take(30).ToList();
        if (hits.Count == 0)
            Console.WriteLine($"{Dim}這一頁的資料不在事實快取的關鍵字範圍；domain <前綴> 或 keys <字> 更精準。{Reset}");
        foreach (var f in hits)
        {
            string avail = f.Availability == FactAvailability.Present ? Green : Red;
            Console.WriteLine($"  {Blue}{f.Key,-34}{Reset} {avail}{f.Availability,-10}{Reset} {Trim(f.Value, 56)}");
        }
        Console.WriteLine($"{Dim}{hits.Count} 筆（上限 30）。{Reset}");
    }

    private static void DomainFacts(string prefix)
    {
        if (_cache is null) { Console.WriteLine($"{Red}先跑 facts。{Reset}"); return; }
        if (prefix.Length == 0)
        {
            var domains = _cache.Select(f => f.Key.Split('.')[0]).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
            Console.WriteLine($"{Bold}本機採集到的域（{domains.Count}）：{Reset}" + string.Join("  ", domains));
            Console.WriteLine($"{Dim}用法：domain <前綴>，例如 domain cpu{Reset}");
            return;
        }
        // P4-13：域查詢就該是「鍵的網域前綴」——原版轉手 SearchFacts 變成鍵或值全文包含，
        // 與用法頁承諾的前綴過濾不符（domain nvme 會撈出一堆值裡提到 nvme 的別域事實）。
        // 現在按前綴過濾（不分大小寫、帶不帶尾點都通）；要全文搜尋請用 fact。
        string dot = prefix.EndsWith('.') ? prefix : prefix + ".";
        var hit = _cache.Where(f => f.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                                 || f.Key.StartsWith(dot, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.Key, StringComparer.Ordinal).ToList();
        var list = hit.Take(30).ToList();
        if (list.Count == 0)
        {
            Console.WriteLine($"{Dim}快取裡沒有 {prefix}* 前綴的事實——domain（無參數）看有哪些域。{Reset}");
            return;
        }
        Tui.Table(["事實鍵", "值", "狀態"],
            list.Select(f => new[]
            {
                f.Key,
                f.Availability == FactAvailability.Present ? Trim(f.Value + (f.Unit.Length > 0 ? " " + f.Unit : ""), 44) : "——",
                f.Availability == FactAvailability.Present ? "✓" : $"{f.Availability}{(string.IsNullOrEmpty(f.UnavailableReason) ? "" : "：" + Trim(f.UnavailableReason, 30))}",
            }));
        Console.WriteLine($"{Dim}… {list.Count} / {hit.Count} 筆（值欄截 44 字；單鍵全文：fact <完整鍵名>）{Reset}");
    }

    private static void ListKeys(string query)
    {
        var keys = FactKeyCatalog.Keys;
        IEnumerable<string> rows = keys;
        if (query.Length > 0) rows = rows.Where(k => k.Contains(query, StringComparison.OrdinalIgnoreCase));
        var list = rows.Take(40).ToList();
        foreach (var k in list) Console.WriteLine("  " + k);
        Console.WriteLine($"{Dim}{list.Count} / {keys.Count} 把靜態鍵（{FactKeyDynamicCatalog.Families.Count} 個動態家族另計）。{Reset}");
    }

    private static void ShowSettings()
    {
        string path = XinSpect.SettingsService.FilePath;
        if (!File.Exists(path)) { Console.WriteLine($"{Dim}還沒有設定檔（{path}）。{Reset}"); return; }
        Console.WriteLine($"{Dim}{path}{Reset}");
        Console.WriteLine(File.ReadAllText(path));
    }

    private static void ShowChangelog(string nText)
    {
        int n = int.TryParse(nText, out int v) && v > 0 ? v : 3;
        foreach (var e in XinSpect.ChangelogCatalog.Entries.Take(n))
            Tui.Frame($"v{e.Version} ・ {e.DateText}",
                new[] { e.Title, "" }.Concat(e.Items.Select(i => "・ " + Trim(i, 76))));
    }

    // ── 即時遙測 ──────────────────────────────────────────────────────────

    private static readonly List<double?> _cpuHist = [], _gpuHist = [], _memHist = [];

    private static void PushHistory((double? CpuT, double? GpuT, double? CpuL, double? MemL, double? CpuClock, double? CpuPower, double? MemUsed) r)
    {
        void Push(List<double?> h, double? v) { h.Add(v); if (h.Count > 40) h.RemoveAt(0); }
        Push(_cpuHist, r.CpuT); Push(_gpuHist, r.GpuT); Push(_memHist, r.MemL);
    }

    /// <summary>域總表：把快取裡屬於指定前綴的事實攤成一張表——GUI 某頁的資料在終端機的對照視圖。</summary>
    private static void DomainReport(string title, string[] prefixes, string? note = null)
    {
        EnsureFacts();
        var rows = _cache!.Where(f => prefixes.Any(p => f.Key == p || f.Key.StartsWith(p + ".", StringComparison.Ordinal)))
            .OrderBy(f => f.Key, StringComparer.Ordinal).ToList();
        Tui.Section(title, string.Join("・", prefixes) + ".* ・ fact <關鍵字> 看單鍵全文");
        if (rows.Count == 0)
        {
            Console.WriteLine($"{Dim}快取裡沒有這個域的事實——先 facts 採一輪；沒有也可能是真的沒有（如桌上型沒有電池）。{Reset}");
            return;
        }
        Tui.Table(["事實鍵", "值", "狀態"],
            rows.Select(f => new[]
            {
                Trim(f.Key, 34),
                f.Availability == FactAvailability.Present ? Trim(f.Value + (f.Unit.Length > 0 ? " " + f.Unit : ""), 44) : "——",
                f.Availability == FactAvailability.Present ? "✓" : $"{f.Availability}{(string.IsNullOrEmpty(f.UnavailableReason) ? "" : "：" + Trim(f.UnavailableReason, 30))}",
            }));
        Console.WriteLine($"  {Dim}共 {rows.Count} 把鍵（值欄截 44 字；單鍵全文：fact <完整鍵名>）。{Reset}");
        if (note != null) Console.WriteLine($"  {Dim}{note}{Reset}");
    }

    /// <summary>健康摘要：六態分布與對帳矛盾——只陳述、不判決（判決在 GUI 的健康頁與 --errors）。</summary>
    private static void HealthSummary()
    {
        EnsureFacts();
        var sum = HomeHealthSummaryService.Aggregate(_cache!);
        if (!sum.HasFacts)
        {
            Console.WriteLine($"{Dim}快取裡沒有事實可彙整——先 facts 採一輪。{Reset}");
            return;
        }
        Tui.Section("健康摘要", "六態分布 ・ 缺源不扣分，只如實說有幾個");
        Tui.Table(["狀態", "筆數"],
        [
            new[] { "總計（快取裡的事實）", sum.Total.ToString("N0") },
            new[] { "Present（讀到了）", sum.Present.ToString("N0") },
            new[] { "NotSupported（這台沒有）", sum.NotSupported.ToString("N0") },
            new[] { "InsufficientPrivilege（要提權）", sum.InsufficientPrivilege.ToString("N0") },
            new[] { "ReadError（讀取失敗）", sum.ReadError.ToString("N0") },
            new[] { "NotApplicable（不適用）", sum.NotApplicable.ToString("N0") },
            new[] { "Unknown（無法判定）", sum.Unknown.ToString("N0") },
            new[] { "對帳規則交叉數", sum.ReconcileCount.ToString("N0") },
        ]);
        if (sum.ContradictionRules.Count > 0)
            foreach (var rule in sum.ContradictionRules)
                Console.WriteLine($"  {Red}對帳矛盾：{rule}{Reset}");
        else
            Console.WriteLine($"  {Dim}沒有對帳矛盾。{Reset}");
    }

    /// <summary>歷史型號查詢：2006–2026 CPU／GPU 索引（使用者提供的內建目錄，離線）。</summary>
    private static void CatalogQuery(string query)
    {
        var svc = new HardwareCatalogService();
        Tui.Section("歷史型號", svc.Status + " ・ " + svc.Limitations);
        if (query.Length == 0)
        {
            Console.WriteLine($"{Dim}用法：catalog <關鍵字>，例如 catalog 5800X、catalog rtx 3080。{Reset}");
            return;
        }
        var rows = svc.Search(query, limit: 20);
        if (rows.Count == 0)
        {
            Console.WriteLine($"{Dim}「{query}」沒有查到——目錄不含主機板（理由見 attribution）。{Reset}");
            return;
        }
        Tui.Table(["型號", "類別", "年份", "摘要"],
            rows.Select(r => new[]
            {
                Trim(r.DisplayName, 34), Trim(r.Kind, 8), r.Year?.ToString() ?? "—", Trim(r.Summary, 44),
            }));
        Console.WriteLine($"  {Dim}{svc.Attribution}{Reset}");
    }

    /// <summary>歷史回放摘要：讀 HistoryStore 持久化檔，13 指標各給 min／avg／max／P95。</summary>
    private static void HistoryReport()
    {
        using var store = new HistoryStore();
        if (store.MinuteCount == 0)
        {
            Console.WriteLine($"{Dim}沒有歷史記錄（GUI 的歷史記錄功能沒開，或這台還沒累積）。{Reset}");
            return;
        }
        var series = store.Query(DateTime.UtcNow - TimeSpan.FromDays(30), DateTime.UtcNow);
        Tui.Section("歷史回放摘要", $"近 30 天 ・ {store.MinuteCount:N0} 分鐘取樣 ・ 檔 {store.SizeText}");
        var rows = new List<string[]>();
        for (int m = 0; m < HistoryMetrics.Count; m++)
        {
            if (!series.HasData(m)) continue;
            var (min, avg, max, p95) = series.Summarize(m);
            rows.Add(new[]
            {
                Trim(HistoryMetrics.Titles[m], 18),
                $"{min:0.##}", $"{avg:0.##}", $"{max:0.##}", $"{p95:0.##}",
                HistoryMetrics.Units[m],
            });
        }
        if (rows.Count == 0)
        {
            Console.WriteLine($"{Dim}有檔但這 30 天沒有有效取樣。{Reset}");
            return;
        }
        Tui.Table(["指標", "最小", "平均", "最大", "P95", "單位"], rows);
        Console.WriteLine($"  {Dim}最早取樣：{store.OldestUtc:yyyy-MM-dd HH:mm} UTC。{Reset}");
    }

    /// <summary>安全姿態對帳表：13 把鍵逐一攤開（值 → 域），null 如實寫「未回報」——不猜、不編。</summary>
    private static void PostureReport()
    {
        Tui.Section("安全姿態對帳表", "WMI 直讀 13 把鍵 ・ null＝來源沒回報");
        var f = new WmiSecurityReader().ReadAll();
        string B(bool? v) => v is null ? "未回報" : v.Value ? "true" : "false";
        Tui.Table(["鍵", "值", "域"],
        [
            new[] { "dma.hvci_running", B(f.HvciRunning), "DMA" },
            new[] { "dma.vbs_running", B(f.VbsRunning), "DMA" },
            new[] { "dma.iommu_available", B(f.IommuAvailable), "DMA" },
            new[] { "dma.dma_protection", B(f.DmaProtection), "DMA" },
            new[] { "dma.credential_guard", B(f.CredentialGuardRunning), "DMA" },
            new[] { "dma.cet_enabled", B(f.CetEnabled), "DMA" },
            new[] { "fw.secureboot_enabled", B(f.SecureBootEnabled), "韌體" },
            new[] { "fw.testsigning_enabled", B(f.TestSigningEnabled), "韌體" },
            new[] { "cpu.spec_mitigations", B(f.SpecMitigationsActive), "CPU" },
            new[] { "drivers.total", f.TotalDriverCount.ToString("N0"), "驅動" },
            new[] { "drivers.unsigned", f.UnsignedDriverCount.ToString("N0"), "驅動" },
            new[] { "drivers.blocklist_present", B(f.VulnerableDriverBlocklistPresent), "驅動" },
            new[] { "tb.security_level", f.ThunderboltSecurityLevel is { } tb ? tb.ToString() : "未回報", "雷電" },
        ]);
        Console.WriteLine($"  {Dim}帶權重的防護分數與威脅事件流：ipc posture／ipc threats。{Reset}");
    }

    /// <summary>USB 鏈路：埠樹、版本、供電與判定——GUI「USB 鏈路」工具的對照視圖。</summary>
    private static void UsbReport()
    {
        var svc = new UsbLinkService();
        Tui.SpinnerStart("列舉 USB 控制器與埠");
        try
        {
            svc.RefreshAsync().GetAwaiter().GetResult();   // fire-and-forget 的話 Rows 還是空的
            Tui.SpinnerStop($"{svc.Rows.Count} 個埠。");
        }
        finally
        {
            Tui.SpinnerFail("USB 列舉被例外中斷——沒有結果，不印完成。");
        }
        if (svc.Rows.Count == 0)
        {
            Console.WriteLine($"{Dim}沒有列到埠（需要管理員權限讀 USB 節點資訊）。{Reset}");
            return;
        }
        Tui.Table(["位置", "裝置", "版本", "現況", "判定"],
            svc.Rows.Select(r => new[]
            {
                Trim(r.Location, 8), Trim(r.Name, 30), r.UsbVersion,
                Trim(r.OperatingText, 10), Trim(r.Verdict, 10),
            }));
    }

    /// <summary>PCIe 鏈路：各裝置的鏈路速度／寬度現值與最大值、協商落差判定。</summary>
    private static void PcieReport()
    {
        var svc = new PcieLinkService();
        Tui.SpinnerStart("讀 PCIe 鏈路");
        try
        {
            if (!svc.CanRefresh) { /* 上一次載入還在跑 */ }
            svc.RefreshAsync().GetAwaiter().GetResult();
            Tui.SpinnerStop($"{svc.Rows.Count} 條鏈路。");
        }
        finally
        {
            Tui.SpinnerFail("PCIe 讀取被例外中斷——沒有結果，不印完成。");
        }
        if (svc.Rows.Count == 0)
        {
            Console.WriteLine($"{Dim}沒有讀到鏈路（需要管理員權限）。{Reset}");
            return;
        }
        Tui.Table(["裝置", "位置", "目前", "最大", "判定"],
            svc.Rows.Select(r => new[]
            {
                Trim(r.Name, 26), Trim(r.Location, 10), r.CurrentText,
                PcieLinkDecoder.LinkText(r.MaxSpeed, r.MaxWidth), Trim(r.Verdict, 10),
            }));
    }

    /// <summary>驅動稽核：裝置→驅動、版本、日期、簽章——唯讀盤點。</summary>
    private static void DriverReport()
    {
        var svc = new DriverAuditService();
        Tui.SpinnerStart("盤點驅動");
        try
        {
            svc.RefreshAsync().GetAwaiter().GetResult();
            Tui.SpinnerStop($"{svc.Rows.Count} 支驅動。");
        }
        finally
        {
            Tui.SpinnerFail("驅動盤點被例外中斷——沒有結果，不印完成。");
        }
        if (svc.Rows.Count == 0)
        {
            Console.WriteLine($"{Dim}沒有盤點到驅動（需要管理員權限讀 WMI）。{Reset}");
            return;
        }
        int unsigned = svc.Rows.Count(r => !r.Signed);
        Tui.Table(["裝置", "類別", "版本", "簽章"],
            svc.Rows.Take(40).Select(r => new[]
            {
                Trim(r.Device, 34), Trim(r.DeviceClass, 8), Trim(r.Version, 18),
                r.Signed ? "✓" : $"{Red}✗{Reset}",
            }));
        Console.WriteLine($"  {Dim}顯示前 40 支（共 {svc.Rows.Count} 支，未簽章 {unsigned} 支）。{Reset}");
    }

    /// <summary>藍屏記錄：掃 Minidump、解 BugCheck 代碼——唯讀，GUI「藍屏分析」工具的對照視圖。</summary>
    private static void BsodScan()
    {
        var svc = new BsodService();
        Tui.SpinnerStart("掃描 Minidump");
        try
        {
            svc.Scan();
            Tui.SpinnerStop(svc.Status);
        }
        finally
        {
            Tui.SpinnerFail("Minidump 掃描被例外中斷——沒有結果，不印完成。");
        }
        if (!svc.HasDumps)
        {
            Console.WriteLine($"{Dim}沒有藍屏記錄。不是壞事。{Reset}");
            return;
        }
        Tui.Table(["時間", "代碼", "名稱", "參數"],
            svc.Rows.OrderByDescending(r => r.Time).Take(20).Select(r => new[]
            {
                r.TimeText, r.CodeHex, Trim(r.Name, 34), Trim(r.Params, 30),
            }));
        Console.WriteLine($"  {Dim}檔案在 C:\\Windows\\Minidump；顯示最近 20 筆（上限）；bsod 只列表，解讀看 GUI 工具頁。{Reset}");
    }

    /// <summary>風扇轉速：LHM SuperIO 感測器唯讀——寫入（調速曲線）只在 GUI 風扇頁，終端機不做。</summary>
    private static void Fans()
    {
        var svc = new LibreHardwareMonitorLibWrapperOrNull();
        if (svc is null)
        {
            Console.WriteLine($"{Red}這台機器拿不到感測器（LHM 初始化失敗或沒有管理員）。{Reset}");
            return;
        }
        using (svc)
        {
            var fans = svc.ReadFans();
            Tui.Section("風扇轉速", "唯讀 ・ 調速曲線是寫入路徑，只在 GUI 風扇頁");
            if (fans.Count == 0)
            {
                Console.WriteLine($"{Dim}讀不到風扇感測器（SuperIO 晶片沒被 LHM 認出，或轉速為 0）。{Reset}");
                return;
            }
            Tui.Table(["風扇", "轉速"],
                fans.Select(f => new[] { Trim(f.Name, 36), $"{f.Rpm:0} RPM" }));
            Console.WriteLine($"  {Dim}共 {fans.Count} 顆。{Reset}");
        }
    }

    private static void LiveLine()
    {
        var svc = new LibreHardwareMonitorLibWrapperOrNull();
        if (svc is null)
        {
            Console.WriteLine($"{Red}這台機器拿不到感測器（LHM 初始化失敗或沒有管理員）。{Reset}");
            return;
        }
        using (svc)
        {
            Tui.Section("即時快照", "sys 單拍 ・ top 2 持續 ・ dash 全螢幕");
            var r = svc.Read();
            PushHistory(r);
            Tui.Kpi("CPU 溫度", r.CpuT, " °C", 100, warnAbove: 75, critAbove: 85);
            Console.WriteLine($"      {Dim}走勢{Reset} {Tui.Sparkline(_cpuHist, 100, 24)}");
            Tui.Kpi("CPU 負載", r.CpuL, " %", 100);
            Tui.Kpi("GPU 溫度", r.GpuT, " °C", 100, warnAbove: 75, critAbove: 85);
            Console.WriteLine($"      {Dim}走勢{Reset} {Tui.Sparkline(_gpuHist, 100, 24)}");
            Tui.Kpi("記憶體", r.MemL, " %", 100, warnAbove: 85, critAbove: 95);
            Console.WriteLine($"      {Dim}走勢{Reset} {Tui.Sparkline(_memHist, 100, 24)}");
            Tui.Separator();
            Console.WriteLine($"  {Pad("CPU 時脈", 12)}{Bold}{(r.CpuClock is null ? "——" : $"{r.CpuClock:0.00}")}{Reset}{Dim} GHz{Reset}   "
                + $"{Pad("CPU 功率", 12)}{Bold}{(r.CpuPower is null ? "——" : $"{r.CpuPower:0.#}")}{Reset}{Dim}{(r.CpuPower is null && RaplEnergyGate.Verdict == RaplGateVerdict.NotResponding ? $" 能量計未隨負載（原始計數 0x{RaplEnergyGate.LastRawCount:X}）" : r.CpuPower is null && RaplEnergyGate.Verdict == RaplGateVerdict.NoCounter ? " 無能量計" : "")}{(LastPowerWasRapl && RaplEnergyGate.Verdict == RaplGateVerdict.Trusted ? "（RAPL）" : "")}{Reset}   "
                + $"{Pad("記憶體用量", 12)}{Bold}{(r.MemUsed is null ? "——" : $"{r.MemUsed:0.0}")}{Reset}{Dim} GB{Reset}");
        }
    }

    private static string Pad(string s, int w) => s + new string(' ', Math.Max(0, w - Tui.VisibleLength(s)));

    // ── 全螢幕儀表板：獨立螢幕、原地重繪、q 離開 ────────────────────────────

    private static void Dashboard()
    {
        var svc = new LibreHardwareMonitorLibWrapperOrNull();
        if (svc is null)
        {
            Console.WriteLine($"{Red}這台機器拿不到感測器。{Reset}");
            return;
        }
        using (svc)
        {
            Tui.AltScreenEnter();
            try
            {
                while (true)
                {
                    var r = svc.Read();
                    PushHistory(r);
                    if (Tui.Legacy) { try { Console.SetCursorPosition(0, 0); } catch { } }
                    else Console.Write("\x1b[H");
                    Console.WriteLine($"{Tui.Dim}┌─ {Tui.Bold}XSC 即時儀表板{Tui.Reset} {Tui.Dim}{new string('─', 44)}{DateTime.Now:HH:mm:ss} ─┐{Tui.Reset}");
                    Console.WriteLine();
                    DashRow("CPU 溫度", r.CpuT, "°C", 100, 75, 85, _cpuHist, ref _prevCpuT);
                    DashRow("CPU 負載", r.CpuL, "%", 100, null, null, null, ref _prevCpuL);
                    DashRow("GPU 溫度", r.GpuT, "°C", 100, 75, 85, _gpuHist, ref _prevGpuT);
                    DashRow("記憶體", r.MemL, "%", 100, 85, 95, _memHist, ref _prevMem);
                    DashRow("CPU 時脈", r.CpuClock, "GHz", 6, null, null, null, ref _prevClock);
                    DashRow("CPU 功率", r.CpuPower, "W", 253, 200, 240, null, ref _prevPower);
                    Console.WriteLine();
                    // P4-21：Bridge 狀態如實——_ipcEngine 是會話級快取（ipc 指令建立過就在），
                    // 不再寫死「未連線」。
                    Console.WriteLine($"  {Tui.Dim}記憶體用量 {(r.MemUsed is null ? "——" : r.MemUsed.Value.ToString("0.0") + " GB")} ・ PCI：{(_pci?.Count.ToString() ?? "未掃（pci scan）")} 台 ・ Bridge：{(_ipcEngine is { Connected: true } ? "已連線（ipc 會話存續中）" : "本程序未連線（ipc 可建立）")}{Tui.Reset}");
                    Console.WriteLine();
                    Console.WriteLine($"{Tui.Dim}  q 離開 ・ 1 秒刷新{Tui.Reset}");
                    Console.Write($"{Tui.SwissRed}{Tui.Bold}xs{Tui.Reset}{Tui.SwissBlue}▸{Tui.Reset} dash");
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    while (sw.ElapsedMilliseconds < 1000)
                    {
                        if (Console.KeyAvailable)
                        {
                            var k = Console.ReadKey(true);
                            if (k.Key == ConsoleKey.Q || (k.Key == ConsoleKey.C && k.Modifiers.HasFlag(ConsoleModifiers.Control)))
                            {
                                Tui.AltScreenExit();
                                Console.WriteLine();
                                return;
                            }
                        }
                        Thread.Sleep(20);
                    }
                }
            }
            finally { Tui.AltScreenExit(); }
        }
    }

    // 漲落箭頭用：與上一拍比較（溫度升＝紅▲、降＝綠▼；負載升＝黃▲）
    private static double? _prevCpuT, _prevCpuL, _prevGpuT, _prevMem, _prevClock, _prevPower;

    private static void DashRow(string label, double? v, string unit, double max,
        double? warn, double? crit, List<double?>? hist, ref double? prev)
    {
        string vt = v is null ? "——" : $"{v:0.#}{unit}";
        string vc = v is null ? Tui.Dim
            : v >= (crit ?? double.MaxValue) ? Tui.SwissRed
            : v >= (warn ?? double.MaxValue) ? Tui.Amber
            : Tui.Bold;
        string bar = Tui.Bar(v, max, 24, warn, crit);
        string spark = hist is null ? "" : $"  {Tui.Dim}{Tui.Sparkline(hist, max, 18)}{Tui.Reset}";
        // 漲落：與上一拍比。溫度上升是壞事（紅▲）；下降是好事（綠▼）。
        string trend = v is null || prev is null ? $"{Tui.Dim}●{Reset}"
            : v > prev + 0.05 ? $"{Tui.SwissRed}▲{Reset}"
            : v < prev - 0.05 ? $"{Tui.SwissGreen}▼{Reset}"
            : $"{Tui.Dim}●{Reset}";
        prev = v;
        Console.WriteLine("  " + Tui.Pad(label + " " + trend, 12) + Tui.Pad(vc + vt, 14) + bar + spark);
    }

    private static void LiveTop(string secondsText)
    {
        double interval = double.TryParse(secondsText, out double s) && s >= 1 ? s : 2;
        var svc = new LibreHardwareMonitorLibWrapperOrNull();
        if (svc is null)
        {
            Console.WriteLine($"{Red}這台機器拿不到感測器。{Reset}");
            return;
        }
        using (svc)
        {
            Console.WriteLine($"{Dim}Q 或 Ctrl+C 停止。{Reset}");
            Console.WriteLine($"  {"時間",-10}{"CPU 溫度",10}{"CPU 負載",10}{"GPU 溫度",10}{"MEM",8}");
            // 審計 #17：TreatControlCAsInput 未開時 Ctrl+C 是 SIGINT 直接殺進程——
            // 「提示符會好好回到你手上」的承諾在這裡是假的。開了之後 Ctrl+C 是普通按鍵。
            bool prevCtrlC = false;
            try { prevCtrlC = Console.TreatControlCAsInput; } catch { }
            try { Console.TreatControlCAsInput = true; } catch { }
            try
            {
                while (true)
                {
                    var (cpuT, gpuT, cpuL, memL, _, _, _) = svc.Read();
                    Console.WriteLine($"  {DateTime.Now.ToString("HH:mm:ss"),-10}"
                        + $"{Heat(cpuT, 75, 85),10}{Fmt(cpuL),10}{Heat(gpuT, 75, 85),10}{Heat(memL, 85, 95),8}");
                    // 有按鍵就檢查離開（重導輸入下 KeyAvailable 會丟例外＝沒有按鍵）
                    try
                    {
                        if (Console.KeyAvailable)
                        {
                            var k = Console.ReadKey(true);
                            if (k.Key == ConsoleKey.Q ||
                                (k.Key == ConsoleKey.C && k.Modifiers.HasFlag(ConsoleModifiers.Control)))
                                break;
                        }
                    }
                    catch { }
                    // 〔修復 v3.21〕審計 D7：睡眠改 250ms 切片——大間隔不再 (int) 溢位炸，
                    // 按鍵也在切片間提早醒（不用等完整 interval）。
                    int totalMs = (int)Math.Min(interval * 1000d, int.MaxValue);
                    for (int waited = 0; waited < totalMs; waited += 250)
                    {
                        Thread.Sleep(Math.Min(250, totalMs - waited));
                        bool keyHit = false;
                        try { keyHit = Console.KeyAvailable; } catch { }
                        if (keyHit) break;
                    }
                }
            }
            finally
            {
                try { Console.TreatControlCAsInput = prevCtrlC; } catch { }
            }
        }
    }

    /// <summary>超過警告／臨界門檻時把數字染黃／染紅（top 表用）。</summary>
    private static string Heat(double? v, double warn, double crit)
        => v is null ? "—"
         : v >= crit ? $"{Tui.SwissRed}{v:0.#}{Tui.Reset}"
         : v >= warn ? $"{Tui.Amber}{v:0.#}{Tui.Reset}"
         : v.Value.ToString("0.#");

    private static string Fmt(double? v) => v is null ? "—" : v.Value.ToString("0.#");

    /// <summary>LibreHardwareMonitor 的薄包裝；失敗回 null（缺驅動／非管理員是環境事實）。</summary>
    private static bool _ring0DiagShown;

    /// <summary>
    /// WinRing0 服務健檢（<b>唯讀診斷</b>）：服務鍵指向的 .sys 不存在（死指針）時——只回報，不動手。
    /// 審計 #1（2026-11-07）：此前的版本會在「唯讀模式」橫幅下靜默執行 sc.exe delete——
    /// 對系統狀態的寫入藏在唯讀承諾底下，行為與同屏宣稱直接矛盾。修復改為使用者明示的動作：
    /// <c>xsc --repair-ring0</c>（刪除死指針服務，讓 LHM 下次用自己的內嵌驅動重建）。
    /// 實例：服務被別家工具註冊到已刪除的桌面資料夾（ImagePath 指向不存在的 .sys），
    /// StartService 永遠失敗 → 時脈整排消失、RAPL 功率卡垃圾值。
    /// </summary>
    private static (bool DeadPointer, string Diag) DiagnoseRing0Service()
    {
        const string svc = "WinRing0_1_2_0";
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + svc);
        if (key is null) return (false, $"服務 {svc} 未安裝（LHM 會在需要時自建）。");
        string? image = (key.GetValue("ImagePath") as string)?.Trim();
        if (string.IsNullOrEmpty(image)) return (false, $"服務 {svc} 沒有 ImagePath。");
        // ImagePath 形如 \??\C:\...——剝掉裝置前綴再驗存在性
        string path = image.StartsWith(@"\??\", StringComparison.Ordinal) ? image[4..] : image;
        if (File.Exists(path)) return (false, $"服務 {svc} 指向 {path}（存在）。");
        return (true, $"服務 {svc} 是死指針：ImagePath 指向不存在的 {path}——StartService 必敗，" +
                      "時脈／RAPL 功率會全是垃圾值。修復：xsc --repair-ring0（刪除死服務，LHM 下次自建）。");
    }

    /// <summary>每個 wrapper 實例只顯示一次診斷（死指針才顯示）。</summary>
    private static void DiagnoseRing0Once()
    {
        if (_ring0DiagShown) return;
        _ring0DiagShown = true;
        try
        {
            var (dead, diag) = DiagnoseRing0Service();
            if (dead) Console.WriteLine($"{Dim}[{diag}]{Reset}");
        }
        catch { }
    }

    /// <summary>
    /// 明示的修復動作（--repair-ring0）：刪除死指針的 WinRing0 服務。需要管理員。
    /// 〔修復 v3.21〕審計 D4：回傳 0／1／2（成功／非管理員／sc.exe 失敗）——原本不查
    /// sc.exe 退出碼，服務不存在（1060）或存取被拒（5）也印綠字成功。
    /// </summary>
    private static int RepairRing0Service()
    {
        const string svc = "WinRing0_1_2_0";
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            if (!new System.Security.Principal.WindowsPrincipal(id)
                    .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
            {
                Console.WriteLine($"{Red}需要管理員權限（以系統管理員身分執行再跑 --repair-ring0）。{Reset}");
                return 1;
            }
            var psi = new System.Diagnostics.ProcessStartInfo("sc.exe", "delete " + svc)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null)
            {
                Console.WriteLine($"{Red}無法啟動 sc.exe——刪除動作沒有發生。{Reset}");
                return 2;
            }
            if (!p.WaitForExit(5000))
            {
                try { p.Kill(); } catch { /* 掛死的 sc.exe 只能殺 */ }
                Console.WriteLine($"{Red}sc.exe 五秒內沒有結束（已終止）——刪除結果未知。{Reset}");
                return 2;
            }
            if (p.ExitCode != 0)
            {
                Console.WriteLine($"{Red}sc.exe delete 失敗（代碼 {p.ExitCode}）——服務可能不存在（1060）或存取被拒（5），沒有東西被刪除。{Reset}");
                return 2;
            }
            Console.WriteLine($"{Green}已刪除死指針服務 {svc}——重開機後 LHM 會以自己的內嵌驅動重建。{Reset}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{Red}修復失敗：{ex.Message}{Reset}");
            return 2;
        }
    }

    // ── 提權（對齊桌面端 DriverReadyService.RestartElevated，L131-148）──────

    /// <summary>目前行程是否以系統管理員執行（與 StatusLine／RepairRing0Service 同一口徑）。</summary>
    private static bool IsElevated()
    {
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(id)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    /// <summary>
    /// 以 runas 重啟自身（childArgs 原樣轉交給提權實例；〔複驗更正〕childArgs 必須含
    /// "--elevate"——見 Main 分派處的更正注記）。三態如實回報，絕不靜默：
    /// <list type="bullet">
    /// <item>0 ＝ UAC 核准，提權實例已啟動——本（標準權限）實例隨即收工，由提權實例接手。</item>
    /// <item>1 ＝ 提權被取消或被拒（UAC 按了「否」，Win32 1223 ERROR_CANCELLED）——未重新啟動，仍是標準權限，Ring0 通路如實顯示未就緒。</item>
    /// <item>2 ＝ 其他失敗（拿不到執行檔路徑、殼層拒絕……）。</item>
    /// </list>
    /// </summary>
    private static int RelaunchElevated(string[] childArgs)
    {
        string? exe = Environment.ProcessPath;
        if (exe is null)
        {
            Console.WriteLine($"{Red}提權失敗：無法取得執行檔路徑。{Reset}");
            return 2;
        }
        try
        {
            // childArgs 只會是 ["--elevate"] 或 ["--elevate", "--repair-ring0"]（Main 已驗），不含空格；仍以保守方式拼。
            string arguments = string.Join(" ", childArgs.Select(a => a.Contains(' ') ? "\"" + a + "\"" : a));
            // 〔修復 v3.21〕審計 D7：提權子行程的 Process 句柄用完即還。
            using var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = exe,
                Arguments = arguments,
                UseShellExecute = true,
                Verb = "runas",
            });
            Console.WriteLine($"{Green}UAC 已核准——已啟動提權的 xsc；本視窗收工，提權實例接手。{Reset}");
            Console.WriteLine($"{Dim}提權實例會先做一次 WinRing0 服務健檢（唯讀）；刪除死指針服務仍需明示的 --repair-ring0。{Reset}");
            return 0;
        }
        catch (System.ComponentModel.Win32Exception ex)
            when (ex.NativeErrorCode == 1223)   // ERROR_CANCELLED：使用者在 UAC 按了「否」
        {
            Console.WriteLine($"{Tui.Amber}提權被取消或被拒——未重新啟動，仍是標準權限。{Reset}");
            Console.WriteLine($"{Dim}Ring0 通路（msr／pci 直讀）會如實顯示未就緒，不會冒充可用。{Reset}");
            return 1;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Console.WriteLine($"{Red}提權失敗（Win32 {ex.NativeErrorCode}）：{ex.Message}{Reset}");
            return 2;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{Red}提權失敗：{ex.GetType().Name} {ex.Message}{Reset}");
            return 2;
        }
    }

    /// <summary>
    /// 提權實例進 REPL 前的一次性 WinRing0 服務健檢（<b>唯讀</b>，復用 DiagnoseRing0Service）。
    /// 桌面端提權後要按「檢測驅動」才看得到狀態；終端機沒有按鈕，就在歡迎框之後主動報一次。
    /// 三態：未安裝（LHM 會自建）／指向存在的 .sys／死指針（只回報，修復走 --repair-ring0）。
    /// </summary>
    private static void AnnounceElevatedRing0Check()
    {
        try
        {
            var (dead, diag) = DiagnoseRing0Service();
            Console.WriteLine(dead ? $"{Tui.Amber}[ring0 健檢] {diag}{Reset}" : $"{Dim}[ring0 健檢] {diag}{Reset}");
        }
        catch (Exception ex)
        {
            // 健檢本身失敗也要說出來，不靜默跳過
            Console.WriteLine($"{Dim}[ring0 健檢] 健檢無法執行：{ex.Message}{Reset}");
        }
    }

    /// <summary>
    /// Ring0 直讀 CPU 時脈與封裝功率——走 XinSpect 自己的 XsRegProbe（msr 指令同一條路，allowlist 內）。
    /// 時脈：IA32_PERF_STATUS（0x198）位元 15:8 ＝ 目前倍頻，× bus(≈100MHz)。
    /// 功率：RAPL 能量計數差分——0x606 給能量單位，0x611（PKG_ENERGY_STATUS）兩拍相除；
    ///       LHM 0.9.6 已無內核驅動（實證：dll 裡沒有驅動資源），它的功率／時脈在沒有別家驅動的機器上是垃圾值。
    /// 失敗一律回 null，由呼叫端退回 LHM 的值——兩邊都讀不到就顯示「——」，不編。
    /// </summary>
    /// <summary>上一次取樣的功率是否來自 RAPL（顯示端用來標來源）。</summary>
    public static bool LastPowerWasRapl;

    private static (double? Clock, double? Power, double? Temp) Ring0CpuSample()
    {
        try
        {
            double? clock = null;
            var perf = PowerConsoleRing0Service.ReadMsr(0x198);
            if (perf.Error is null)
            {
                uint ratio = (uint)((perf.Value >> 8) & 0xFF);
                if (ratio is > 4 and < 120) clock = ratio * 100.0 / 1000.0;   // × bus(100MHz) → GHz
            }

            var unit = PowerConsoleRing0Service.ReadMsr(0x606);
            var e1 = PowerConsoleRing0Service.ReadMsr(0x611);
            if (unit.Error is not null || e1.Error is not null) return (clock, null, ReadCpuTemp());
            int div = (int)((unit.Value >> 8) & 0x1F);
            double joule = 1.0 / Math.Pow(2, div);

            // 誠實閘門（一次性）：0x611 的速率得先證明會隨負載動，才敢換算成瓦——
            // 本機（Skylake-X）實測計數器前進但與功耗脫鉤，硬換算得出「18 核滿載 1.2 W」。
            RaplEnergyGate.RunOnce(
                () => { var r = PowerConsoleRing0Service.ReadMsr(0x611); return r.Error is null ? r.Value : (ulong?)null; },
                ReadCpuTemp);

            if (RaplEnergyGate.Verdict == RaplGateVerdict.Trusted)
            {
                System.Threading.Thread.Sleep(400);   // 能量差分窗（僅在閘門通過後才值得量）
                var e2 = PowerConsoleRing0Service.ReadMsr(0x611);
                if (e2.Error is not null) return (clock, null, ReadCpuTemp());
                ulong delta = e2.Value >= e1.Value ? e2.Value - e1.Value : e2.Value + (ulong.MaxValue - e1.Value) + 1;
                double power = delta * joule / 0.4;
                if (power is > 0 and < 1500)
                {
                    LastPowerWasRapl = true;
                    return (clock, power, ReadCpuTemp());
                }
            }
            // 閘門未過（計數器與負載脫鉤）或差分超帶：不硬編功率，溫度照樣走 MSR
            LastPowerWasRapl = false;
            return (clock, null, ReadCpuTemp());
        }
        catch { LastPowerWasRapl = false; return (null, null, null); }
    }

    /// <summary>核心溫度：TjMax（0x1A2 位元 23:16）− DTS（0x19C 位元 22:16）。XsRegProbe 直讀，實測與負載同步。</summary>
    private static double? ReadCpuTemp()
    {
        var therm = PowerConsoleRing0Service.ReadMsr(0x19C);
        var target = PowerConsoleRing0Service.ReadMsr(0x1A2);
        if (therm.Error is not null || target.Error is not null) return null;
        int dts = (int)((therm.Value >> 16) & 0x7F);
        int tjMax = (int)((target.Value >> 16) & 0xFF);
        double t = tjMax - dts;
        return t is > 0 and < 150 ? t : null;
    }

    private sealed class LibreHardwareMonitorLibWrapperOrNull : IDisposable
    {
        private readonly LibreHardwareMonitor.Hardware.Computer? _computer;
        public LibreHardwareMonitorLibWrapperOrNull()
        {
            DiagnoseRing0Once();   // 唯讀診斷：死指針只回報不動手（審計 #1）
            try
            {
                var c = new LibreHardwareMonitor.Hardware.Computer
                {
                    IsCpuEnabled = true,
                    IsGpuEnabled = true,
                    IsMemoryEnabled = true,
                    // 主板與 SuperIO 控制器要開：fan 指令讀風扇轉速靠這兩類硬體。
                    // 仍然全部唯讀——Control 感測器只讀現值，不設值；調速是 GUI 風扇頁的寫入路徑。
                    IsMotherboardEnabled = true,
                    IsStorageEnabled = false,
                    IsNetworkEnabled = false,
                    IsControllerEnabled = true,
                    IsPsuEnabled = false,
                };
                c.Open();
                _computer = c;
                // 暖機兩拍：RAPL 功率是兩次更新間的能量差，第一拍沒有基準（會跑出 1.4W 這種垃圾值）；
                // 時脈與部分感測器也要第二拍才有值。GUI 持續輪詢沒這問題，單拍式的終端機必須自己暖。
                try
                {
                    foreach (var hw in _computer.Hardware) hw.Update();
                    System.Threading.Thread.Sleep(600);
                    foreach (var hw in _computer.Hardware) hw.Update();
                }
                catch { }
            }
            catch { _computer = null; }
        }
        public (double? CpuT, double? GpuT, double? CpuL, double? MemL, double? CpuClock, double? CpuPower, double? MemUsed) Read()
        {
            double? cpuT = null, gpuT = null, cpuL = null, memL = null;
            double? cpuClock = null, cpuPower = null, memUsed = null;
            if (_computer is null) return (null, null, null, null, null, null, null);
            foreach (var hw in _computer.Hardware)
            {
                hw.Update();
                if (hw.HardwareType == LibreHardwareMonitor.Hardware.HardwareType.Cpu)
                {
                    // 比照 GUI SensorService 的選法：溫度取第一顆、負載取 CPU Total；
                    // 功率優先 Package，找不到就取 Power 感測器最大值（不挑第一個——第一個可能是小軌道）；
                    // 時脈取 Core #* 最大，沒有就取非 Bus 時脈最大（MHz→GHz）。
                    double? pkgPower = null, maxPower = null, coreClock = null, anyClock = null;
                    foreach (var s in hw.Sensors)
                    {
                        if (s.Value is not { } v) continue;
                        if (s.SensorType == LibreHardwareMonitor.Hardware.SensorType.Temperature && cpuT is null) cpuT = v;
                        if (s.SensorType == LibreHardwareMonitor.Hardware.SensorType.Load && cpuL is null && s.Name == "CPU Total") cpuL = v;
                        if (s.SensorType == LibreHardwareMonitor.Hardware.SensorType.Clock)
                        {
                            if (s.Name.StartsWith("Core", StringComparison.Ordinal))
                            {
                                if (v > (coreClock ?? 0)) coreClock = v;
                            }
                            else if (!s.Name.Contains("Bus", StringComparison.OrdinalIgnoreCase) && v > (anyClock ?? 0)) anyClock = v;
                        }
                        if (s.SensorType == LibreHardwareMonitor.Hardware.SensorType.Power)
                        {
                            if (s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase))
                            {
                                if (v > (pkgPower ?? 0)) pkgPower = v;
                            }
                            else if (v > (maxPower ?? 0)) maxPower = v;
                        }
                    }
                    cpuClock = coreClock ?? anyClock;
                    cpuPower = pkgPower ?? maxPower;
                }
                if (hw.HardwareType is LibreHardwareMonitor.Hardware.HardwareType.GpuNvidia or LibreHardwareMonitor.Hardware.HardwareType.GpuAmd or LibreHardwareMonitor.Hardware.HardwareType.GpuIntel)
                    foreach (var s in hw.Sensors)
                        if (s.SensorType == LibreHardwareMonitor.Hardware.SensorType.Temperature && gpuT is null && s.Value is { } v) gpuT = v;
                if (hw.HardwareType == LibreHardwareMonitor.Hardware.HardwareType.Memory)
                    foreach (var s in hw.Sensors)
                    {
                        if (s.Value is not { } v) continue;
                        if (s.SensorType == LibreHardwareMonitor.Hardware.SensorType.Load && memL is null) memL = v;
                        if (s.SensorType == LibreHardwareMonitor.Hardware.SensorType.Data && s.Name == "Used Memory") memUsed = v;
                    }
            }
            // 功率與時脈優先用 XsRegProbe 的 Ring0 直讀（LHM 0.9.6 無驅動時這兩項是垃圾值）；
            // XsRegProbe 讀不到才退回 LHM 的值，兩邊都沒有就是 null——顯示「——」，不編。
            var (r0Clock, r0Power, r0Temp) = Ring0CpuSample();
            // Ring0 的時脈已是 GHz；wrapper 對外介面是 MHz（回傳時再除 1000）——別二次除
            cpuClock = r0Clock.HasValue ? r0Clock.Value * 1000.0 : cpuClock;
            cpuPower = r0Power ?? cpuPower;
            cpuT = r0Temp ?? cpuT;   // MSR 溫度是權威值（LHM 0.9.6 無驅動時它的溫度來源不明）
            return (cpuT, gpuT, cpuL, memL, cpuClock is null ? null : cpuClock / 1000.0, cpuPower, memUsed);
        }
        /// <summary>風扇轉速：主板／SuperIO 的 Fan 感測器，附最小濾噪（0 RPM 的不列）。</summary>
        public List<(string Name, double Rpm)> ReadFans()
        {
            var fans = new List<(string, double)>();
            if (_computer is null) return fans;
            foreach (var hw in _computer.Hardware)
            {
                hw.Update();
                foreach (var sub in hw.SubHardware)
                {
                    sub.Update();
                    foreach (var s in sub.Sensors)
                        if (s.SensorType == LibreHardwareMonitor.Hardware.SensorType.Fan && s.Value is { } v && v > 0)
                            fans.Add(($"{hw.Name} {sub.Name} {s.Name}".Trim(), v));
                }
                foreach (var s in hw.Sensors)
                    if (s.SensorType == LibreHardwareMonitor.Hardware.SensorType.Fan && s.Value is { } v && v > 0)
                        fans.Add(($"{hw.Name} {s.Name}".Trim(), v));
            }
            return fans;
        }

        public void Dispose() => _computer?.Close();
    }

    // ── Ring0／IPC／既有 CLI ──────────────────────────────────────────────

    private static void ReadMsr(string hexText)
    {
        string t = hexText.Replace("0x", "").Replace("0X", "");
        if (!uint.TryParse(t, System.Globalization.NumberStyles.HexNumber, null, out uint index))
        {
            Console.WriteLine($"{Red}索引格式不對（十六進位）。{Reset}");
            return;
        }
        var r = PowerConsoleRing0Service.ReadMsr(index);
        if (r.Error is { } err) { Console.WriteLine($"{Red}MSR 0x{index:X8}：{err}{Reset}"); return; }
        Console.WriteLine($"MSR {Bold}0x{r.Index:X8}{Reset} = {Bold}0x{r.Value:X16}{Reset}");
        Console.WriteLine($"{Dim}EDX 0x{r.Edx:X8} ・ EAX 0x{r.Eax:X8}{Reset}");
    }

    private static List<PciConsoleDevice>? _pci;
    private static void Pci(string rest)
    {
        if (rest is "scan" or "")
        {
            Tui.SpinnerStart("掃描 bus 0–255（唯讀）");
            try
            {
                var (devices, error) = PowerConsoleRing0Service.ScanPci();
                if (devices is null)
                {
                    // 失敗走失敗態：印 ✕ 中斷條，絕不先印 100.0% 再說掃描失敗（P0-7 病例本尊）
                    Tui.SpinnerFail("掃描失敗：" + error);
                    return;
                }
                _pci = devices.OrderBy(d => d.Bus).ThenBy(d => d.Device).ThenBy(d => d.Function).ToList();
                Tui.SpinnerStop($"找到 {_pci.Count} 台裝置。");
            }
            finally
            {
                Tui.SpinnerFail("PCI 掃描被例外中斷——沒有結果，不印完成。");
            }
            Tui.Table(
                ["#", "BDF", "VEN:DEV", "裝置"],
                _pci!.Select((d, i) => new[]
                {
                    i.ToString(), $"{d.Bus:X2}:{d.Device:X2}.{d.Function:X0}",
                    $"{d.VendorId:X4}:{d.DeviceId:X4}", Trim(d.Name, 46),
                }));
            return;
        }
        if (rest.StartsWith("dump", StringComparison.Ordinal))
        {
            string nText = rest["dump".Length..].Trim();
            if (_pci is null) { Console.WriteLine($"{Red}先 pci scan。{Reset}"); return; }
            if (!int.TryParse(nText, out int n) || n < 0 || n >= _pci.Count) { Console.WriteLine($"{Red}編號範圍 0–{(_pci?.Count ?? 1) - 1}。{Reset}"); return; }
            var d = _pci[n];
            Console.WriteLine($"{Bold}{d.Bus:X2}:{d.Device:X2}.{d.Function:X0}{Reset}  {d.Name}");
            Console.WriteLine($"{Dim}VEN {d.VendorId:X4} ・ DEV {d.DeviceId:X4} ・ 類別 {d.BaseClass:X2}{d.SubClass:X2}{Reset}");
            for (int row = 0; row < 16; row++)
            {
                Console.Write($"  {row * 16:X2}  ");
                for (int col = 0; col < 16; col++)
                    Console.Write(((d.ConfigSpace[row] >> ((col % 4) * 8)) & 0xFF).ToString("X2") + " ");
                Console.WriteLine();
            }
            return;
        }
        Console.WriteLine($"{Red}用法：pci scan｜pci dump <n>{Reset}");
    }

    private static BlueSquadronEngine? _ipcEngine;
    private static int _ipcTimeoutMs = 15000;

    private static void Ipc(string rest)
    {
        // 逾時可調：ipc timeout <毫秒>（會話級，存到 settings 的路徑留給下一版）
        if (rest.StartsWith("timeout", StringComparison.OrdinalIgnoreCase))
        {
            var msText = rest["timeout".Length..].Trim();
            if (int.TryParse(msText, out int ms) && ms is > 500 and <= 120000)
            {
                _ipcTimeoutMs = ms;
                Console.WriteLine($"{Dim}ipc 逾時改為 {ms} ms（本會話）。{Reset}");
                return;
            }
            Console.WriteLine($"{Red}用法：ipc timeout <毫秒>（500–120000）{Reset}");
            return;
        }

        // 引擎會話級快取：連續查 status／threats／baseline 不再每次重啟守護進程握手。
        if (_ipcEngine is null)
        {
            _ipcEngine = new BlueSquadronEngine();
            if (!_ipcEngine.Start())
            {
                Console.WriteLine($"{Red}Bridge 啟動失敗：{_ipcEngine.LastError}{Reset}");
                _ipcEngine = null;
                return;
            }
            // 〔修復 v3.21〕審計 D6：行程退出時優雅收尾（bridge 平時靠 stdin EOF 自行退出）。
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                try { _ipcEngine?.Dispose(); } catch { }
            };
        }
        var resp = _ipcEngine.SendCmd(rest, _ipcTimeoutMs);
        if (resp is { } json)
        {
            Console.WriteLine(json.GetRawText());
            return;
        }
        if (!_ipcEngine.Connected)
        {
            // 〔修復 v3.21〕審計 D6：引擎已標死——如實說「斷線」，不是「逾時」；下次重建。
            Console.WriteLine($"{Red}Bridge 已斷線（可能已退出）——下次 ipc 指令會自動重新啟動。{Reset}");
            try { _ipcEngine.Dispose(); } catch { }
            _ipcEngine = null;
            return;
        }
        Console.WriteLine($"{Red}無回應（逾時 {_ipcTimeoutMs} ms——可用 ipc timeout 調整）。{Reset}");
    }

    private static int RunCli(string[] args)
    {
        // REPL 裡剛跑過 facts 時，轉發指令復用快取——不讓使用者再等一整輪採集。
        // 快取存在時如實告知來源；沒有快取走全量採集（與主程式 CLI 同一路）。
        if (_cache is not null)
        {
            Console.WriteLine($"{Dim}（使用現有快取 {_cache.Count:N0} 筆；重採用 facts --refresh 後再跑）{Reset}");
            return CliService.Run(args, () => _cache, Console.Out, Console.Error);
        }
        return CliService.Run(args, Collect, Console.Out, Console.Error);
    }

    /// <summary>與主程式 CLI 同一條採集路徑（EvidenceLabService＋使用者模式事實）。</summary>
    private static IReadOnlyList<HardwareFact> Collect()
    {
        var svc = new EvidenceLabService();
        EvidenceCollection.ReloadInto(svc);
        EvidenceCollection.LoadUsermodeFacts(svc);
        return svc.AllFacts;
    }

    // ── 行編輯：↑↓ 歷史、Tab 補全 ─────────────────────────────────────────

    /// <summary>狀態行：面前是什麼權限（SYSTEM／administrator／標準）＋Ring0 驅動是否已註冊。
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
            using var services = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
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

    private static int _lastWidth;

    /// <summary>終端寬度變了就整屏重繪：拖窄時右側自動隱藏，拉寬時重新出現。</summary>
    private static bool CheckResize()
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
    }

    private static string? ReadLineWithHistory()
    {
        // 輸入被重導向（管線／指令稿）時沒有按鍵事件可拿：退回普通 ReadLine，
        // 讓 `echo "help" | xsc` 這種批次用法也能跑。歷史與 Tab 只在真 TTY 有。
        if (Console.IsInputRedirected) return Console.ReadLine();

        var buf = new StringBuilder();
        int hist = History.Count;
        int pos = 0;
        while (true)
        {
            try
            {
                if (CheckResize())
                {
                    _frameWidth = Tui.CurrentWidth();
                    Console.Write($"{Tui.BrandRed}{Tui.Bold}>{Tui.Reset} " + buf.ToString());
                    if (pos < buf.Length)
                    {
                        if (Tui.Legacy) Console.Write(new string('\b', buf.Length - pos));
                        else Console.Write($"\x1b[{buf.Length - pos}D");
                    }
                    continue;
                }
            }
            catch { /* resize 重繪失敗不影響輸入 */ }
            try
            {
                // 〔修復 v3.21〕審計 D5：ReadKey 移進 try——主控台句柄失效時在 try 外拋
                // IOException，被外層主迴圈 catch 接住後下一輪再拋，無限紅字轟炸。
                var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine($"{Tui.Dim}{new string('─', _frameWidth)}{Tui.Reset}");   // 輸入欄底線（固定長直線）
                var st = StatusLine();
                int scol = Math.Max(1, _frameWidth - Tui.VisibleLength(st) - 1);
                if (Tui.Legacy) Console.WriteLine(Tui.Dim + st + Tui.Reset);
                else Console.Write(Tui.Esc + "[" + scol + "G" + Tui.Dim + st + Tui.Reset + Environment.NewLine);
                return buf.ToString();
            }
            if (key.Key == ConsoleKey.Backspace)
            {
                if (pos > 0)
                {
                    buf.Remove(pos - 1, 1);
                    pos--;
                    Redraw(buf, pos);
                }
                continue;
            }
            if (key.Key == ConsoleKey.LeftArrow) { if (pos > 0) { pos--; Console.Write("\b"); } continue; }
            if (key.Key == ConsoleKey.RightArrow)
            {
                if (pos < buf.Length)
                {
                    pos++;
                    if (Tui.Legacy) Redraw(buf, pos);   // 純文字版沒有右移控制碼，整行重畫
                    else Console.Write("\x1b[1C");
                }
                continue;
            }
            if (key.Key == ConsoleKey.Home) { pos = 0; Redraw(buf, pos); continue; }
            if (key.Key == ConsoleKey.End) { pos = buf.Length; Redraw(buf, pos); continue; }
            if (key.Key == ConsoleKey.UpArrow)
            {
                if (History.Count == 0) continue;
                hist = Math.Max(0, hist - 1);
                buf.Clear(); buf.Append(History[hist]); pos = buf.Length; Redraw(buf, pos);
                continue;
            }
            if (key.Key == ConsoleKey.DownArrow)
            {
                if (History.Count == 0) continue;
                hist = Math.Min(History.Count, hist + 1);
                buf.Clear();
                if (hist < History.Count) buf.Append(History[hist]);
                pos = buf.Length; Redraw(buf, pos);
                continue;
            }
            if (key.Key == ConsoleKey.Tab)
            {
                string prefix = buf.ToString(0, pos);
                var hits = Commands.Where(c => c.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
                if (hits.Count == 1)
                {
                    string done = hits[0];
                    buf.Clear(); buf.Append(done); pos = done.Length;
                    Redraw(buf, pos);
                }
                else if (hits.Count > 1)
                {
                    Console.WriteLine();
                    Console.WriteLine($"{Dim}" + string.Join("  ", hits) + Reset);
                    Console.Write($"{Tui.BrandRed}{Tui.Bold}>{Tui.Reset} ");
                    Console.Write(buf.ToString());
                }
                continue;
            }
            if (key.KeyChar == '\0') continue;
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
    }

    private static void Redraw(StringBuilder buf, int pos)
    {
        if (Tui.Legacy)
        {
            // 純文字相容版：只有退格（\b）可靠——整行擦掉重畫，再把游標推回定位
            Console.Write("\r" + new string(' ', 2 + Tui.VisibleLength(buf.ToString())) + "\r");
            Console.Write("> " + buf.ToString());
            if (pos < buf.Length) Console.Write(new string('\b', buf.Length - pos));
            return;
        }
        Console.Write("\r\x1b[K");
        Console.Write($"{Tui.BrandRed}{Tui.Bold}>{Tui.Reset} " + buf.ToString());
        if (pos < buf.Length) Console.Write($"\x1b[{buf.Length - pos}D");
    }
}
