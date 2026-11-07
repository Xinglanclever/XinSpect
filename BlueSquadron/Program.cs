// ⚠ 未編譯——需作者機驗證（dotnet build 命令見 patch-24.md）
//
// Program.cs（BlueSquadronBridge 端）— 完整替換檔（patch-24，P0-12 誠實化＋P0-13/14 接線）
// 覆蓋目標：BlueSquadron/Program.cs（原 371 行）
// 套用順序：patch-23（EtwWatcher 新 API）→ 本檔 → patch-25（DriverManager.QueryCaps）。
// 本檔的 "caps" 指令呼叫 QueryCaps()，該方法由 patch-25 的 DriverManager.cs 提供——
// 兩檔需一起套用（同一修復包、一次建置）。
//
// 修復內容（對照原檔）：
//   P0-12（誠實化）：
//   - setPolicy：原版任何 line/mode 一律回 ok:true（未知 line 靜默無操作）——「block」
//     回 ok 但全 Bridge 沒有執行路徑。本版：驗證 line/mode（未知回 Err）；
//     「block」回應帶 implemented:false ＋ 說明「Phase 1 僅偵測；阻擋需 Phase 2」；
//     detect/warn 帶 implemented:true。
//   - status：每條防線帶 modeImplemented；頂層帶 blockingImplemented（false 是 Phase 1
//     的代碼事實）——GUI／腳本可據此標示「未實施」，不再被 ok:true 掩蓋。
//   P0-13（接線）：init 指令接受 blocklistPath（主程式傳它自己的
//   Rules\byovd-blocklist.xml），轉交 EtwWatcher.ApplyBlocklistPath；啟動與 standalone
//   模式先以 Bridge 自己的候選路徑載入（降級也不沉默）。init/status 回帶 byovdBlocklist
//   三態資訊。
//   P0-14（接線）：新增 "caps" 指令——DeviceIoControl(IOCTL_BS_GET_CAPS) 的能力帳單
//   （FeatureMask＋逐步 NTSTATUS＋MSR 快照活體健康），供主程式 DriverReadyService
//   狀態卡判「部分就緒」。驅動未載入時如實回 ok:false＋原因，不造假全量。

using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace BlueSquadron;

/// <summary>
/// BlueSquadronBridge 進入點。
///   無參數：JSON stdin/stdout 服務迴圈（由 XinSpect 驅動）
///   --standalone：系統匣模式（獨立運行，無 XinSpect）
///   --probe：人類可讀的自我檢測
/// </summary>
internal static class Program
{
    private static readonly PolicyEngine Policy = new();
    private static readonly BaselineStore Baseline = new();
    private static readonly WmiSecurityReader Reader = new();
    private static EtwWatcher? _etw;
    private static DriverManager? _driver;

    // stdout 寫入鎖——命令回應與警告推送共用 stdout，需序列化
    private static readonly object StdoutLock = new();
    private static StreamWriter? _stdout;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    /// <summary>
    /// 使用者直接點開 exe（主控台可見）時給友善提示：這是背景服務、沒有自己的畫面，
    /// 應與主程式放同一資料夾、由主程式拉起。由 XinSpect 背景啟動時主控台是隱藏的
    /// （CreateNoWindow），IsWindowVisible 為 false，完全不彈窗、不干擾 IPC。
    /// 〔複驗更正〕2026-11-07 紅隊複驗：撤除原稿保留的 WinForms MessageBox.Show——
    /// 零彈窗紀律下提示只走主控台文字（本方法既有輸出已完整承載同一段資訊）；
    /// 原樹雖有此彈窗，但本補丁整檔重寫，正是清掉的時機，不應原樣搬入。
    /// </summary>
    private static void ShowInteractiveBanner()
    {
        try
        {
            var cw = GetConsoleWindow();
            if (cw == IntPtr.Zero || !IsWindowVisible(cw)) return;

            try { Console.Title = "XinSpect BlueSquadron 守護進程"; } catch { }
            Console.OutputEncoding = new UTF8Encoding(false);
            Console.WriteLine();
            Console.WriteLine("  XinSpect BlueSquadron 已開啟");
            Console.WriteLine("  ─────────────────────────────────────────");
            Console.WriteLine("  這是背景守護進程，本身沒有操作畫面。");
            Console.WriteLine("  正確用法：把它與主程式 XinSpect.exe 放在同一個");
            Console.WriteLine("  資料夾，啟動主程式後即自動連線。");
            Console.WriteLine("  本視窗可以直接關閉，不影響其他功能。");
            Console.WriteLine();
        }
        catch { }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--probe")
            return Probe();

        if (args.Length > 0 && args[0] == "--standalone")
            return Standalone();

        return Server();
    }

    // ── JSON 服務迴圈 ────────────────────────────────────────────────────
    private static int Server()
    {
        ShowInteractiveBanner();

        var enc = new UTF8Encoding(false);
        TextReader stdin = new StreamReader(Console.OpenStandardInput(), enc);
        _stdout = new StreamWriter(Console.OpenStandardOutput(), enc) { AutoFlush = true, NewLine = "\n" };
        try { Console.SetOut(TextWriter.Null); Console.SetError(TextWriter.Null); } catch { }

        Baseline.Load();

        // patch-10 §1.3(a)：基線完整性驗證失敗時如實上報威脅事件——壞檔已隔離、
        // 未靜默重校，主體端藍色中隊威脅清單要能看到這一條。
        if (Baseline.IntegrityFailure)
            Policy.RecordThreat("warning", "firmware",
                "基線檔完整性驗證失敗",
                Baseline.LoadStatus ?? "baseline.json 驗證失敗（壞檔已隔離，未靜默重校）。");

        // 啟動 ETW 監控（警告透過 stdout 推送）
        _etw = new EtwWatcher(Policy, alertJson =>
        {
            lock (StdoutLock) _stdout?.WriteLine(alertJson);
        });
        // P0-13：先用 Bridge 自己找得到的候選路徑載入封鎖清單（standalone／開發模式；
        // 正式模式由 init 的 blocklistPath 升級為主程式目錄的清單）。缺檔→降級模式，
        // 降級會經 RecordThreat 如實申報，不沉默。
        _etw.ApplyBlocklistPath(EtwWatcher.ResolveDefaultBlocklistPath());
        _etw.Start();

        // 嘗試載入核心驅動（失敗不阻斷）
        _driver = new DriverManager();
        bool driverOk = _driver.TryLoad();

        try
        {
            string? line;
            while ((line = stdin.ReadLine()) is not null)
            {
                line = line.Trim();
                if (line.Length == 0) continue;

                Dictionary<string, JsonElement>? req;
                try { req = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(line); }
                catch { WriteResponse(Err("JSON 解析失敗")); continue; }
                if (req is null) { WriteResponse(Err("請求非 JSON 物件")); continue; }

                string cmd = req.TryGetValue("cmd", out var c) ? c.GetString() ?? "" : "";
                if (cmd == "quit") { WriteResponse(Ok()); break; }

                object resp;
                try { resp = Dispatch(cmd, req); }
                catch (Exception ex)
                {
                    var root = ex;
                    while (root.InnerException is not null) root = root.InnerException;
                    resp = Err($"{root.Message}（{root.GetType().Name}）");
                }
                WriteResponse(resp);
            }
        }
        finally
        {
            _etw?.Dispose();
            _driver?.Dispose();
        }
        return 0;
    }

    // ── 指令分派 ─────────────────────────────────────────────────────────
    private static object Dispatch(string cmd, Dictionary<string, JsonElement> req)
    {
        switch (cmd)
        {
            case "ping":
                return Ok();

            case "init":
            {
                // P0-13：主程式可帶自己的封鎖清單路徑（它知道 XinSpect.exe 的目錄；
                // Bridge 從 %LOCALAPPDATA% 解壓目錄跑，看不到主程式目錄）。
                string? blocklistPath = GetStr(req, "blocklistPath");
                if (!string.IsNullOrEmpty(blocklistPath) && _etw is not null)
                    _etw.ApplyBlocklistPath(blocklistPath);

                var facts = Reader.ReadAll();
                var changes = Baseline.Snapshot(facts);
                Baseline.Save();

                foreach (var (key, old, @new) in changes)
                    Policy.RecordThreat("warning", "firmware",
                        $"基線變更：{key}",
                        $"{key} 從 {old} 變為 {@new}");

                return new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["driverLoaded"] = _driver?.IsLoaded ?? false,
                    ["driverError"] = _driver?.LastError ?? "",
                    ["baselineChanges"] = changes.Count,
                    // patch-10 §1.3(b)：完整性三態隨 init 回應帶給主體（多兩欄不破壞既有解析）。
                    ["baselineIntegrityFailed"] = Baseline.IntegrityFailure,
                    ["baselineStatus"] = Baseline.LoadStatus ?? "",
                    ["byovdBlocklist"] = BlocklistInfo(),
                    ["blockingImplemented"] = PolicyEngine.BlockingImplemented,
                    ["facts"] = FactsToDict(facts),
                };
            }

            case "status":
            {
                var lines = new List<object>();
                foreach (var id in new[] { "dma", "firmware", "cpu", "storage", "drivers" })
                    lines.Add(new Dictionary<string, object>
                    {
                        ["id"] = id,
                        ["enabled"] = Policy.IsEnabled(id),
                        ["mode"] = Policy.GetMode(id),
                        // P0-12：該防線目前設定的模式是否真的有執行路徑——
                        // block 在 Phase 1 沒有，如實回 false。
                        ["modeImplemented"] = Policy.ModeImplemented(id),
                    });
                return new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["driverLoaded"] = _driver?.IsLoaded ?? false,
                    ["blockingImplemented"] = PolicyEngine.BlockingImplemented,
                    ["byovdBlocklist"] = BlocklistInfo(),
                    ["lines"] = lines,
                };
            }

            case "posture":
            {
                var facts = Reader.ReadAll();
                return new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["facts"] = FactsToDict(facts),
                };
            }

            case "threats":
            {
                int max = 50;
                if (req.TryGetValue("max", out var m) && m.ValueKind == JsonValueKind.Number)
                    max = m.GetInt32();
                var threats = Policy.RecentThreats(max);
                var list = new List<object>();
                foreach (var t in threats)
                    list.Add(new Dictionary<string, object>
                    {
                        ["ts"] = t.Timestamp.ToString("o"),
                        ["severity"] = t.Severity,
                        ["line"] = t.DefenseLine,
                        ["title"] = t.Title,
                        ["detail"] = t.Detail,
                    });
                return new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["threats"] = list,
                };
            }

            case "setPolicy":
            {
                // P0-12：如實申報。原版任何輸入都回 ok:true（未知 line 靜默無操作）——
                // 「阻擋」因此看起來生效了，實際上沒有任何執行路徑。
                // 現在：未知 line/mode 回 Err；block 回 ok 但帶 implemented:false 與說明。
                string line = GetStr(req, "line") ?? "";
                string mode = GetStr(req, "mode") ?? "detect";

                if (!PolicyEngine.KnownLines.Contains(line))
                    return Err($"未知防線：'{line}'（可用：{string.Join("/", PolicyEngine.KnownLines)}）");
                if (!PolicyEngine.KnownModes.Contains(mode))
                    return Err($"未知模式：'{mode}'（可用：{string.Join("/", PolicyEngine.KnownModes)}）");

                Policy.SetMode(line, mode);
                bool blocking = mode == "block";
                return new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["line"] = line,
                    ["mode"] = mode,
                    // 阻擋沒有執行路徑——意向已記錄，但現在的偵測不會因此變成攔截
                    ["implemented"] = !blocking,
                    ["note"] = blocking
                        ? "已記錄為意向。Phase 1 僅偵測：目前沒有任何執行路徑會阻止此防線上的活動（阻擋需 Phase 2 的核心執行路徑）。"
                        : "偵測模式已套用（ETW 與核心回呼持續記錄）。"
                };
            }

            case "enableDefense":
            {
                string line = GetStr(req, "line") ?? "";
                if (!PolicyEngine.KnownLines.Contains(line))
                    return Err($"未知防線：'{line}'（可用：{string.Join("/", PolicyEngine.KnownLines)}）");
                Policy.Enable(line);
                return Ok();
            }

            case "disableDefense":
            {
                string line = GetStr(req, "line") ?? "";
                if (!PolicyEngine.KnownLines.Contains(line))
                    return Err($"未知防線：'{line}'（可用：{string.Join("/", PolicyEngine.KnownLines)}）");
                Policy.Disable(line);
                return Ok();
            }

            case "caps":
            {
                // P0-14：能力帳單——四個回呼哪些真的註冊成功、MSR 快照活體健康。
                // 依賴 patch-25 的 DriverManager.QueryCaps（IOCTL_BS_GET_CAPS）。
                var caps = _driver?.QueryCaps();
                if (caps is null)
                    return new Dictionary<string, object?>
                    {
                        ["ok"] = false,
                        ["error"] = string.IsNullOrEmpty(_driver?.CapsError)
                            ? "驅動未載入——能力帳單不可用（user-mode 偵測仍在運行）"
                            : _driver!.CapsError,
                    };

                bool NtOk(int status) => status >= 0;
                var callbacks = new Dictionary<string, bool>
                {
                    ["loadImage"] = (caps.FeatureMask & 0x01) != 0,
                    ["process"]   = (caps.FeatureMask & 0x02) != 0,
                    ["registry"]  = (caps.FeatureMask & 0x04) != 0,
                    ["obProtect"] = (caps.FeatureMask & 0x08) != 0,
                };
                var missing = new List<string>();
                foreach (var (name, ok) in callbacks)
                    if (!ok) missing.Add(name);

                return new Dictionary<string, object?>
                {
                    ["ok"] = true,
                    ["featureMask"] = (int)caps.FeatureMask,
                    ["registeredCount"] = callbacks.Values.Count(v => v),
                    ["callbacks"] = callbacks,
                    ["missing"] = missing,
                    ["msrSnapshotOk"] = NtOk(caps.MsrSnapshotStatus),
                    ["msrSnapshotStatus"] = $"0x{(uint)caps.MsrSnapshotStatus:X8}",
                    ["msrActiveCpus"] = caps.ActiveCpus,
                    ["msrBaseEntries"] = caps.MsrBaseEntries,
                    ["msrDiffCount"] = caps.MsrDiffCount,
                    ["hashProviderOk"] = NtOk(caps.HashProviderStatus),
                    // DriverEntry 各步的原始 NTSTATUS（供狀態卡列「為什麼沒註冊上」）
                    ["statuses"] = new Dictionary<string, string>
                    {
                        ["loadImage"] = $"0x{(uint)caps.LoadImageStatus:X8}",
                        ["process"] = $"0x{(uint)caps.ProcessStatus:X8}",
                        ["registry"] = $"0x{(uint)caps.RegistryStatus:X8}",
                        ["obProtect"] = $"0x{(uint)caps.ObProtectStatus:X8}",
                        ["msrSnapshot"] = $"0x{(uint)caps.MsrSnapshotStatus:X8}",
                        ["hashProvider"] = $"0x{(uint)caps.HashProviderStatus:X8}",
                    },
                };
            }

            case "snapshot":
            {
                var facts = Reader.ReadAll();
                var changes = Baseline.Snapshot(facts);
                Baseline.Save();
                return new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["changes"] = changes.Count,
                };
            }

            case "baseline":
            {
                return new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["items"] = Baseline.All,
                };
            }

            default:
                return Err("未知指令：" + cmd);
        }
    }

    /// <summary>BYOVD 封鎖清單三態的對外呈現（init／status 共用）。</summary>
    private static object BlocklistInfo()
    {
        if (_etw is null)
            return new Dictionary<string, object?>
            {
                ["state"] = "NotLoaded", ["degraded"] = true, ["rules"] = 0,
                ["detail"] = "ETW 監控未啟動",
            };
        var (state, rules, detail) = _etw.BlocklistStatus;
        return new Dictionary<string, object?>
        {
            ["state"] = state.ToString(),
            ["degraded"] = state != ByovdBlocklistState.Full,
            ["rules"] = rules,
            ["detail"] = detail,
        };
    }

    // ── 獨立運行模式（系統匣） ───────────────────────────────────────────
    private static int Standalone()
    {
        Baseline.Load();
        _etw = new EtwWatcher(Policy);
        _etw.ApplyBlocklistPath(EtwWatcher.ResolveDefaultBlocklistPath());
        _etw.Start();
        _driver = new DriverManager();
        _driver.TryLoad();

        // Simple WinForms tray icon
        var tray = new System.Windows.Forms.NotifyIcon
        {
            Text = "Blue Squadron — 核心安全守護",
            Icon = System.Drawing.SystemIcons.Shield,
            Visible = true,
        };
        // patch-10 §1.3(c)：獨立運行（系統匣）模式也要讓完整性失敗被看見——氣泡如實申報。
        if (Baseline.IntegrityFailure)
            tray.ShowBalloonTip(8000, "藍色中隊",
                "基線檔完整性驗證失敗：" + (Baseline.LoadStatus ?? "壞檔已隔離。"),
                System.Windows.Forms.ToolTipIcon.Warning);
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("狀態：運行中").Enabled = false;
        menu.Items.Add("重新掃描", null, (_, _) =>
        {
            var facts = Reader.ReadAll();
            var changes = Baseline.Snapshot(facts);
            Baseline.Save();
            if (changes.Count > 0)
                tray.ShowBalloonTip(5000, "藍色中隊",
                    $"偵測到 {changes.Count} 項基線變更。",
                    System.Windows.Forms.ToolTipIcon.Warning);
            else
                tray.ShowBalloonTip(3000, "藍色中隊",
                    "所有安全設定與基線一致。",
                    System.Windows.Forms.ToolTipIcon.Info);
        });
        menu.Items.Add("-");
        menu.Items.Add("結束", null, (_, _) => System.Windows.Forms.Application.Exit());
        tray.ContextMenuStrip = menu;

        System.Windows.Forms.Application.Run();

        tray.Visible = false;
        _etw.Dispose();
        _driver.Dispose();
        return 0;
    }

    // ── 自我檢測 ────────────────────────────────────────────────────────
    private static int Probe()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("=== Blue Squadron Probe ===");

        var facts = Reader.ReadAll();
        Console.WriteLine($"HVCI={facts.HvciRunning} VBS={facts.VbsRunning}");
        Console.WriteLine($"SecureBoot={facts.SecureBootEnabled} TestSign={facts.TestSigningEnabled}");
        Console.WriteLine($"IOMMU={facts.IommuAvailable} DMA={facts.DmaProtection}");
        Console.WriteLine($"CredGuard={facts.CredentialGuardRunning} CET={facts.CetEnabled}");
        Console.WriteLine($"Drivers={facts.TotalDriverCount} Unsigned={facts.UnsignedDriverCount}");
        Console.WriteLine($"Thunderbolt={facts.ThunderboltSecurityLevel}");
        Console.WriteLine($"DriverBlocklist={facts.VulnerableDriverBlocklistPresent}");
        Console.WriteLine($"SpecMitigations={facts.SpecMitigationsActive}");

        using var driver = new DriverManager();
        bool driverOk = driver.TryLoad();
        Console.WriteLine($"Driver={driverOk} Error={driver.LastError}");

        // P0-14：能力帳單進 probe 輸出——「部分就緒」在自我檢測就得看見
        var caps = driver.QueryCaps();
        if (caps is null)
            Console.WriteLine($"Caps=不可用（{driver.CapsError}）");
        else
        {
            Console.WriteLine($"Caps FeatureMask=0x{caps.FeatureMask:X2} " +
                $"loadImage={(caps.FeatureMask & 0x01) != 0} process={(caps.FeatureMask & 0x02) != 0} " +
                $"registry={(caps.FeatureMask & 0x04) != 0} obProtect={(caps.FeatureMask & 0x08) != 0} " +
                $"msr=0x{(uint)caps.MsrSnapshotStatus:X8} activeCpus={caps.ActiveCpus}");
        }

        Baseline.Load();
        // patch-10 §1.3(d)：probe 模式完整性失敗直接印出——批次/腳本能看到三態結果。
        if (Baseline.IntegrityFailure)
            Console.WriteLine("[baseline] 完整性驗證失敗：" + Baseline.LoadStatus);
        var changes = Baseline.Snapshot(facts);
        Console.WriteLine($"BaselineChanges={changes.Count}");
        foreach (var (key, old, @new) in changes)
            Console.WriteLine($"  {key}: {old} -> {@new}");
        Baseline.Save();

        Console.WriteLine("=== Done ===");
        return 0;
    }

    // ── JSON 輔助 ────────────────────────────────────────────────────────
    private static void WriteResponse(object resp)
    {
        string json = JsonSerializer.Serialize(resp);
        lock (StdoutLock) _stdout?.WriteLine(json);
    }

    private static Dictionary<string, object> Ok() => new() { ["ok"] = true };
    private static Dictionary<string, object> Err(string msg) => new() { ["ok"] = false, ["error"] = msg };

    private static string? GetStr(Dictionary<string, JsonElement> d, string key)
        => d.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static Dictionary<string, object?> FactsToDict(SecurityFacts f) => new()
    {
        ["hvciRunning"] = f.HvciRunning,
        ["vbsRunning"] = f.VbsRunning,
        ["iommuAvailable"] = f.IommuAvailable,
        ["dmaProtection"] = f.DmaProtection,
        ["credentialGuard"] = f.CredentialGuardRunning,
        ["cetEnabled"] = f.CetEnabled,
        ["secureBootEnabled"] = f.SecureBootEnabled,
        ["testSigningEnabled"] = f.TestSigningEnabled,
        ["specMitigationsActive"] = f.SpecMitigationsActive,
        ["totalDrivers"] = f.TotalDriverCount,
        ["unsignedDrivers"] = f.UnsignedDriverCount,
        ["driverBlocklist"] = f.VulnerableDriverBlocklistPresent,
        ["thunderboltSecurity"] = f.ThunderboltSecurityLevel,
    };
}
