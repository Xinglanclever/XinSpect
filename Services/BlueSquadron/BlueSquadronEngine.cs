using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace XinSpect;

/// <summary>
/// 藍色中隊 IPC 引擎：以 stdin/stdout JSON 管線驅動 BlueSquadronBridge.exe。
/// 沿用 XtuOcEngine 的模式：一問一答 + lock 序列化，另加背景讀取執行緒處理即時警告推送。
/// </summary>
public sealed class BlueSquadronEngine : IDisposable
{
    private readonly object _lock = new();
    private Process? _proc;
    private StreamWriter? _stdin;
    private StreamReader? _stdout;
    private volatile bool _alive;
    private bool _disposed;
    private Thread? _alertThread;

    /// <summary>即時警告推送事件。在背景執行緒觸發，訂閱者需自行切回 UI 執行緒。</summary>
    public event Action<JsonElement>? AlertReceived;

    public bool Connected => _alive;
    public string LastError { get; private set; } = "";

    // ── 啟動 ────────────────────────────────────────────────────────────────

    public bool Start()
    {
        try
        {
            string exe = BlueSquadronBootstrap.EnsureExtracted();
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardInputEncoding = new UTF8Encoding(false),
            };
            _proc = Process.Start(psi);
            if (_proc is null)
            {
                LastError = "Process.Start 回傳 null";
                return false;
            }
            _stdin = _proc.StandardInput;
            _stdout = _proc.StandardOutput;
            _alive = true;

            // 背景讀取執行緒：從 stdout 讀取所有行，區分命令回應與警告推送
            _alertThread = new Thread(AlertPumpLoop) { IsBackground = true, Name = "BS-AlertPump" };
            _alertThread.Start();

            // ping 測試
            var resp = Send("{\"cmd\":\"ping\"}", 3000);
            if (resp is null)
            {
                LastError = "Bridge 啟動但無回應";
                MarkDead();
                return false;
            }
            LastError = "";
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return false;
        }
    }

    // ── 命令發送 ────────────────────────────────────────────────────────────

    private readonly Queue<TaskCompletionSource<string?>> _responseQueue = new();

    public JsonElement? Send(string jsonLine, int timeoutMs)
    {
        if (!_alive) return null;
        var tcs = new TaskCompletionSource<string?>();

        lock (_lock)
        {
            // _alive 要在鎖內複查：先前只在鎖外檢查一次，若這個執行緒剛通過檢查、
            // 而 MarkDead() 已經把佇列清空，這一筆 tcs 就永遠不會被完成 →
            // 呼叫端會卡滿整個 timeout（最長 8 秒）才發現程序早就死了。
            if (!_alive) return null;
            try
            {
                lock (_responseQueue) _responseQueue.Enqueue(tcs);
                _stdin!.WriteLine(jsonLine);
                _stdin.Flush();
            }
            catch
            {
                MarkDead();
                return null;
            }
        }

        if (!tcs.Task.Wait(timeoutMs))
        {
            MarkDead();
            return null;
        }

        string? resp = tcs.Task.Result;
        if (resp is null) return null;

        try
        {
            using var doc = JsonDocument.Parse(resp);
            return doc.RootElement.Clone();
        }
        catch { return null; }
    }

    public JsonElement? SendCmd(string cmd, int timeoutMs)
        => Send($"{{\"cmd\":\"{cmd}\"}}", timeoutMs);

    public JsonElement? SendCmd(string cmd, Dictionary<string, object> extra, int timeoutMs)
    {
        extra["cmd"] = cmd;
        string json = JsonSerializer.Serialize(extra);
        return Send(json, timeoutMs);
    }

    // ── 背景讀取迴圈 ────────────────────────────────────────────────────────

    private void AlertPumpLoop()
    {
        try
        {
            while (_alive && _stdout is not null)
            {
                string? line = _stdout.ReadLine();
                if (line is null) { MarkDead(); break; }
                line = line.Trim();
                if (line.Length == 0 || !line.StartsWith('{')) continue;

                // 區分警告推送與命令回應時必須**解析 JSON 看結構**，不能用字串包含比對：
                // 任何命令回應只要內含 "alert": true 這個欄位（例如 threats 指令回傳的事件本身
                // 就帶 alert 標記），就會被從佇列路徑吸走 → 等它的 Send() 逾時 → MarkDead() 把整個
                // 守護進程殺掉。而且 Contains 只涵蓋兩種空白排版，其他序列化器產生的排版不會命中。
                JsonDocument? doc = null;
                bool isAlert = false;
                try
                {
                    doc = JsonDocument.Parse(line);
                    isAlert = doc.RootElement.ValueKind == JsonValueKind.Object
                           && doc.RootElement.TryGetProperty("alert", out var alertFlag)
                           && alertFlag.ValueKind == JsonValueKind.True;
                }
                catch { /* 解析不了就當成一般回應，交給等它的呼叫端 */ }

                if (isAlert && doc is not null)
                {
                    AlertReceived?.Invoke(doc.RootElement.Clone());
                }
                else
                {
                    // 命令回應——交給等待的 Send() 呼叫
                    TaskCompletionSource<string?>? tcs = null;
                    lock (_responseQueue)
                        if (_responseQueue.Count > 0)
                            tcs = _responseQueue.Dequeue();
                    tcs?.TrySetResult(line);
                }

                doc?.Dispose();
            }
        }
        catch { MarkDead(); }
    }

    // ── 生命週期 ────────────────────────────────────────────────────────────

    private void MarkDead()
    {
        // 取 _lock：否則會出現「Send 已通過 _alive 檢查 → MarkDead 清空佇列 → Send 才入列」
        // 的窗口，那一筆 tcs 永遠不會被完成，呼叫端得枯等整個 timeout。
        // lock 在同一執行緒是可重入的，所以 Send 內部呼叫這裡不會死鎖。
        lock (_lock)
        {
            _alive = false;
            try { _proc?.Kill(); } catch { }
            // 喚醒所有等待中的回應
            lock (_responseQueue)
                while (_responseQueue.Count > 0)
                    _responseQueue.Dequeue().TrySetResult(null);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_alive)
        {
            try
            {
                lock (_lock) _stdin?.WriteLine("{\"cmd\":\"quit\"}");
                _proc?.WaitForExit(3000);
            }
            catch { }
        }
        _alive = false;
        try { if (_proc is { HasExited: false }) _proc.Kill(); } catch { }
        _proc?.Dispose();
    }
}
