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
        var enc = new UTF8Encoding(false);
        TextReader stdin = new StreamReader(Console.OpenStandardInput(), enc);
        _stdout = new StreamWriter(Console.OpenStandardOutput(), enc) { AutoFlush = true, NewLine = "\n" };
        try { Console.SetOut(TextWriter.Null); Console.SetError(TextWriter.Null); } catch { }

        Baseline.Load();

        // 啟動 ETW 監控（警告透過 stdout 推送）
        _etw = new EtwWatcher(Policy, alertJson =>
        {
            lock (StdoutLock) _stdout?.WriteLine(alertJson);
        });
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
                    });
                return new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["driverLoaded"] = _driver?.IsLoaded ?? false,
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
                string line = GetStr(req, "line") ?? "";
                string mode = GetStr(req, "mode") ?? "detect";
                Policy.SetMode(line, mode);
                return Ok();
            }

            case "enableDefense":
            {
                string line = GetStr(req, "line") ?? "";
                Policy.Enable(line);
                return Ok();
            }

            case "disableDefense":
            {
                string line = GetStr(req, "line") ?? "";
                Policy.Disable(line);
                return Ok();
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

    // ── 獨立運行模式（系統匣） ───────────────────────────────────────────
    private static int Standalone()
    {
        Baseline.Load();
        _etw = new EtwWatcher(Policy);
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

        Baseline.Load();
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
