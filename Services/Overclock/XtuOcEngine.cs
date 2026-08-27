using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace XinSpect;

// ───────────────────────────────────────────────────────────────────────────
// 真正的超頻寫入引擎（.NET 10 端）：以獨立程序 XtuBridge.exe（net48）承載 Intel XTU
// 的 IntelOverclockingSDK.dll，透過 newline-delimited JSON 管線（stdin/stdout）驅動之。
//
// 為何走程序外：.NET 10 已移除 SDK 建構所需的舊版 WCF（System.ServiceModel 4.0.0.0），
// 在程序內以反射載入 SDK 會於建構子擲出 TargetInvocationException；改由原生 .NET Framework
// 4.8 執行階段的橋接程式代為呼叫，本體僅負責 IPC 與語意判定。
//
// 誠實原則（與舊版程序內引擎一致，判定全留在本端）：
//   • 可調項一律來自橋接端的 SDK 實測列舉，不硬編 Id、不臆造。
//   • 每次寫入後回讀 ActiveValue 作為「真的寫進硬體了」的證據，並以容差判定是否生效。
//   • 需重開機才生效的項目，據實標記為「已排入、重開機後生效」，不假裝即時成功。
//   • 找不到 SDK / 平台不支援 / 初始化失敗 → 據實回報，交由上層改用 NullOcEngine。
// ───────────────────────────────────────────────────────────────────────────

public sealed class XtuOcEngine : IOcEngine
{
    private readonly object _lock = new();          // 序列化 IPC 往返（一問一答）
    private readonly List<OcKnob> _knobs = new();

    private Process? _proc;
    private StreamWriter? _stdin;
    private StreamReader? _stdout;
    private volatile bool _alive;
    private bool _disposed;
    private int _soState;                            // Speed Optimizer 狀態快取（避免屬性 getter 阻塞式 IPC）

    public string Name => "Intel XTU 超頻引擎";
    public OcEngineStatus Status { get; private set; } = OcEngineStatus.NotInitialized;
    public string StatusMessage { get; private set; } = "尚未初始化";

    public bool CoreTunable { get; private set; }
    public bool BclkTunable { get; private set; }
    public bool MemoryTunable { get; private set; }
    public bool CacheTunable { get; private set; }
    public bool SpeedOptimizerSupported { get; private set; }
    public int ProcessorFamily { get; private set; }

    public bool WatchdogPresent { get; private set; }
    public bool WatchdogRunning { get; private set; }
    public bool WatchdogFailed { get; private set; }

    public IReadOnlyList<OcKnob> Knobs => _knobs;
    public int SpeedOptimizerState => _soState;

    // ── 初始化 ─────────────────────────────────────────────────────────────

    public bool Initialize()
    {
        try
        {
            if (!StartBridge(out string startErr))
            {
                Status = OcEngineStatus.Failed;
                StatusMessage = startErr;
                return false;
            }

            var resp = Send(Req("init"), 60000);
            if (resp is null)
            {
                Status = OcEngineStatus.Failed;
                StatusMessage = "超頻橋接程序無回應（可能已結束、遭安全軟體阻擋，或缺少必要權限）。";
                Diag("init 無回應");
                return false;
            }
            var r = resp.Value;

            ProcessorFamily = GetInt(r, "family");
            CoreTunable = GetBool(r, "coreTunable");
            BclkTunable = GetBool(r, "bclkTunable");
            MemoryTunable = GetBool(r, "memoryTunable");
            CacheTunable = GetBool(r, "cacheTunable");
            SpeedOptimizerSupported = GetBool(r, "speedOptimizerSupported");
            WatchdogPresent = GetBool(r, "watchdogPresent");
            WatchdogRunning = GetBool(r, "watchdogRunning");
            WatchdogFailed = GetBool(r, "watchdogFailed");

            _knobs.Clear();
            if (r.TryGetProperty("knobs", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var k in arr.EnumerateArray())
                {
                    var knob = BuildKnob(k);
                    if (knob is not null) _knobs.Add(knob);
                }

            StatusMessage = GetStr(r, "message") ?? "";
            Status = MapStatus(GetStr(r, "status"));

            // 就緒後補抓一次 Speed Optimizer 狀態（後續由本端快取供 UI 讀取）
            if (Status == OcEngineStatus.Ready)
            {
                var so = Send(Req("soState"), 8000);
                if (so is not null) _soState = GetInt(so.Value, "value");
            }

            return Status == OcEngineStatus.Ready;
        }
        catch (Exception ex)
        {
            // 取最內層例外作為對使用者顯示的真正原因
            var root = ex;
            while (root.InnerException is not null) root = root.InnerException;
            Status = OcEngineStatus.Failed;
            StatusMessage = "超頻引擎初始化失敗：" + root.Message + "（" + root.GetType().Name + "）";
            Diag("初始化擲出例外：\n" + ex);
            return false;
        }
    }

    // 由 init 回應的旋鈕 DTO 重建 OcKnob；分類（Kind）在本端以 OcNaming.Classify 判定，與舊版一致。
    private static OcKnob? BuildKnob(JsonElement k)
    {
        try
        {
            uint id = (uint)GetLong(k, "id");
            string name = GetStr(k, "name") ?? $"控制項#{id}";
            string category = GetStr(k, "category") ?? "";
            string unit = GetStr(k, "unit") ?? "";
            double min = GetDouble(k, "min");
            double max = GetDouble(k, "max");
            double def = GetDouble(k, "default");
            double boot = GetDouble(k, "boot");
            double active = GetDouble(k, "active");
            bool rt = GetBool(k, "realTime");
            bool rr = GetBool(k, "requiresReboot");
            bool ro = GetBool(k, "readOnly");
            bool en = GetBool(k, "enabled");

            var kind = OcNaming.Classify(name, category);
            return new OcKnob(id, name, category, unit, kind, min, max, def, boot, active, rt, rr, ro, en);
        }
        catch { return null; }
    }

    private static OcEngineStatus MapStatus(string? s) => s switch
    {
        "Ready" => OcEngineStatus.Ready,
        "Unsupported" => OcEngineStatus.Unsupported,
        "Missing" => OcEngineStatus.Missing,
        _ => OcEngineStatus.Failed,
    };

    // ── 讀取 ───────────────────────────────────────────────────────────────

    public double? ReadCoreVoltage()
    {
        var resp = Send(Req("readVcore"), 3000);
        if (resp is null) return null;
        double? v = GetDoubleN(resp.Value, "value");
        return v is > 0 ? v : null;
    }

    public double? ReadMonitor(params string[] nameContains)
    {
        var req = Req("readMonitor");
        req["keys"] = nameContains ?? Array.Empty<string>();
        var resp = Send(req, 3000);
        if (resp is null) return null;
        return GetDoubleN(resp.Value, "value");
    }

    public void RefreshActives()
    {
        var resp = Send(Req("refresh"), 15000);
        if (resp is not null) ApplyActives(resp.Value);
    }

    // 把回應中的 actives（{"<id>":現值}）套回對應旋鈕的 Active。
    private void ApplyActives(JsonElement r)
    {
        if (!r.TryGetProperty("actives", out var m) || m.ValueKind != JsonValueKind.Object) return;
        foreach (var prop in m.EnumerateObject())
        {
            if (!uint.TryParse(prop.Name, out uint id)) continue;
            if (prop.Value.ValueKind != JsonValueKind.Number || !prop.Value.TryGetDouble(out double v)) continue;
            var knob = _knobs.FirstOrDefault(x => x.Id == id);
            if (knob is not null) knob.Active = v;
        }
    }

    // ── 寫入 ───────────────────────────────────────────────────────────────

    public OcApplyResult Apply(OcKnob knob, double value)
    {
        if (Status != OcEngineStatus.Ready) return OcApplyResult.Fail("引擎未就緒：" + StatusMessage);
        if (!knob.Writable) return OcApplyResult.Fail($"「{knob.Label}」在此平台為唯讀，無法調整。");

        value = Math.Clamp(value, knob.Min, knob.Max);
        double before = knob.Active;

        var req = Req("apply");
        req["id"] = knob.Id;
        req["value"] = value;
        req["requiresReboot"] = knob.RequiresReboot;
        var resp = Send(req, 30000);
        if (resp is null)
            return OcApplyResult.Fail($"寫入「{knob.Label}」失敗：超頻橋接程序無回應（連線可能中斷）。");
        var r = resp.Value;

        bool ok = GetBool(r, "ok");
        string? code = GetStr(r, "code");
        bool activeKnown = GetBool(r, "activeKnown");
        double after = activeKnown ? GetDouble(r, "active") : before;
        knob.Active = after;

        if (!ok)
        {
            // 橋接端 Tune / ApplyChanges 未成功；code 內含真正原因（EX:… / NOT_READY）
            if (code == "NOT_READY")
                return OcApplyResult.Fail($"寫入「{knob.Label}」失敗：引擎未就緒。");
            string why = code is not null && code.StartsWith("EX:") ? code.Substring(3) : (code ?? "未知原因");
            return OcApplyResult.Fail($"寫入「{knob.Label}」時發生例外：{why}");
        }

        // 需重開機的項目：無法以即時回讀驗證（現值本就不會馬上變），據實回報為「已排入」
        if (knob.RequiresReboot)
            return OcApplyResult.Success($"「{knob.Label}」已排入設定，需重新開機後生效。", after);

        double tol = Tolerance(knob);
        if (Math.Abs(after - value) <= tol)
            return OcApplyResult.Success($"「{knob.Label}」已寫入硬體：{knob.Fmt(after)}", after);

        // 硬體回讀與目標不符：可能被 BIOS/驅動夾限或拒絕。誠實回報現值與診斷碼。
        return new OcApplyResult(false,
            $"「{knob.Label}」未如預期生效：硬體現值為 {knob.Fmt(after)}"
            + (string.IsNullOrEmpty(code) ? "" : $"（回報碼 {code}）"), after);
    }

    public bool Discard()
    {
        var resp = Send(Req("discard"), 15000);
        if (resp is null) return false;
        bool ok = GetBool(resp.Value, "ok");
        if (ok) RefreshActives();   // 橋接端已 DiscardChanges，這裡把最新現值拉回本端旋鈕
        return ok;
    }

    public OcApplyResult RestoreDefaults()
    {
        if (Status != OcEngineStatus.Ready) return OcApplyResult.Fail("引擎未就緒：" + StatusMessage);

        var resp = Send(Req("restoreDefaults"), 60000);
        if (resp is null) return OcApplyResult.Fail("還原預設值失敗：超頻橋接程序無回應。");
        var r = resp.Value;

        ApplyActives(r);
        foreach (var k in _knobs) k.ResetTargetToActive();

        string? error = GetStr(r, "error");
        int count = GetInt(r, "count");
        if (string.IsNullOrEmpty(error))
            return OcApplyResult.Success($"已將 {count} 個可調項還原為預設值。", null);
        return OcApplyResult.Fail("還原預設值時發生錯誤：" + error);
    }

    public void SetBootRestore(bool on)
    {
        var req = Req("setBootRestore");
        req["on"] = on;
        try { Send(req, 8000); } catch { /* 開機還原偏好寫入失敗不影響本次操作 */ }
    }

    // ── Intel Speed Optimizer（可逆一鍵自動超頻）──────────────────────────

    public OcApplyResult SetSpeedOptimizer(bool on, bool extreme)
    {
        if (!SpeedOptimizerSupported) return OcApplyResult.Fail("此平台不支援 Intel Speed Optimizer。");

        var req = Req("setSO");
        req["on"] = on;
        req["extreme"] = extreme;
        var resp = Send(req, 60000);
        if (resp is null) return OcApplyResult.Fail("Speed Optimizer 操作失敗：超頻橋接程序無回應。");
        var r = resp.Value;

        bool ok = GetBool(r, "ok");
        string? error = GetStr(r, "error");
        if (!ok) return OcApplyResult.Fail("Speed Optimizer 操作失敗：" + (error ?? "未知原因"));

        // 更新現值與 SO 狀態快取
        RefreshActives();
        var so = Send(Req("soState"), 8000);
        if (so is not null) _soState = GetInt(so.Value, "value");

        return OcApplyResult.Success(
            on ? $"已啟用 Intel Speed Optimizer{(extreme ? "（Extreme）" : "")}，此為可還原的自動超頻。"
               : "已關閉 Intel Speed Optimizer。", null);
    }

    // ── IPC 管線 ───────────────────────────────────────────────────────────

    private bool StartBridge(out string error)
    {
        error = "";
        string exe;
        try { exe = BridgeBootstrap.EnsureExtracted(); }
        catch (Exception ex)
        {
            error = "無法佈署超頻橋接程式：" + ex.Message;
            Diag(error);
            return false;
        }

        try
        {
            // 以 UTF-8（無 BOM）雙向溝通，確保中文旋鈕名稱與訊息不亂碼。
            var enc = new UTF8Encoding(false);
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                // stderr 不重導：橋接程式啟動後即把 Console.Error 導向 Null，不會污染協定；
                // 不重導可免除「未讀取而塞滿管線」導致子程序阻塞的風險。
                StandardOutputEncoding = enc,
                StandardInputEncoding = enc,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory,
            };
            _proc = Process.Start(psi);
            if (_proc is null) { error = "超頻橋接程式無法啟動。"; return false; }
            _stdin = _proc.StandardInput;
            _stdout = _proc.StandardOutput;
            _alive = true;
            Diag($"已啟動橋接程式：{exe}（PID {_proc.Id}）");
            return true;
        }
        catch (Exception ex)
        {
            error = "啟動超頻橋接程式失敗：" + ex.Message;
            Diag(error);
            return false;
        }
    }

    // 送出一行 JSON 請求、讀回一行 JSON 回應。全程持鎖，確保管線一問一答不交錯。
    private JsonElement? Send(Dictionary<string, object> req, int timeoutMs)
    {
        lock (_lock)
        {
            if (!Alive || _stdin is null || _stdout is null) return null;
            try
            {
                string line = JsonSerializer.Serialize(req);
                _stdin.Write(line);
                _stdin.Write('\n');
                _stdin.Flush();
            }
            catch (Exception ex) { Diag("IPC 送出失敗：" + ex.Message); MarkDead(); return null; }

            return ReadResponse(_stdout, timeoutMs);
        }
    }

    // 讀取直到取得一個完整 JSON 物件；逾時則視同斷線並中止橋接程序。
    private JsonElement? ReadResponse(StreamReader reader, int timeoutMs)
    {
        var task = Task.Run(() =>
        {
            string? line;
            int guard = 0;
            while ((line = reader.ReadLine()) is not null && guard++ < 500)
            {
                line = line.Trim();
                if (line.Length == 0 || line[0] != '{') continue;   // 跳過非協定雜訊行
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    return (JsonElement?)doc.RootElement.Clone();     // Clone 脫離 doc，dispose 後仍可用
                }
                catch { /* 尚非完整 JSON 物件，續讀下一行 */ }
            }
            return (JsonElement?)null;
        });

        if (!task.Wait(timeoutMs))
        {
            Diag($"IPC 逾時（{timeoutMs}ms），中止橋接程序。");
            MarkDead();
            return null;
        }
        return task.Result;
    }

    private bool Alive
    {
        get
        {
            if (_disposed || !_alive || _proc is null) return false;
            try { return !_proc.HasExited; } catch { return false; }
        }
    }

    private void MarkDead()
    {
        _alive = false;
        try { if (_proc is not null && !_proc.HasExited) _proc.Kill(); } catch { }
    }

    // 診斷用：把訊息寫入 OC 設定目錄的 oc_engine_error.log，供離線分析（與舊版一致）。
    private static void Diag(string msg)
    {
        try
        {
            Directory.CreateDirectory(OcSettings.RootDir);
            File.AppendAllText(Path.Combine(OcSettings.RootDir, "oc_engine_error.log"),
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine + Environment.NewLine);
        }
        catch { /* 診斷紀錄失敗不影響主流程 */ }
    }

    // ── 小工具 ─────────────────────────────────────────────────────────────

    private static Dictionary<string, object> Req(string cmd) => new() { { "cmd", cmd } };

    private static double Tolerance(OcKnob k) => k.Kind switch
    {
        OcKnobKind.Voltage => 0.008,
        OcKnobKind.MemoryVoltage => 0.02,
        OcKnobKind.CoreRatio or OcKnobKind.CacheRatio or OcKnobKind.MemoryRatio or OcKnobKind.Offset => 0.5,
        OcKnobKind.Bclk => 0.06,
        OcKnobKind.PowerLimit or OcKnobKind.Current => 0.6,
        _ => Math.Max(k.Step, 0.01),
    };

    private static string? GetStr(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool GetBool(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static int GetInt(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? (int)l : 0;

    private static long GetLong(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : 0;

    private static double GetDouble(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : 0;

    private static double? GetDoubleN(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return d;
        return null;   // null / 缺值 → 不可用
    }

    // ── 收尾 ───────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // 客氣地請橋接程序自行結束：它會 Dispose SDK、Reset 硬體控制代碼後退出。
        try
        {
            lock (_lock)
            {
                if (_alive && _stdin is not null)
                {
                    try { _stdin.Write("{\"cmd\":\"quit\"}\n"); _stdin.Flush(); } catch { }
                }
            }
        }
        catch { }
        _alive = false;

        try
        {
            if (_proc is not null)
            {
                if (!_proc.WaitForExit(3000)) { try { _proc.Kill(); } catch { } }
                _proc.Dispose();
            }
        }
        catch { }
    }
}
