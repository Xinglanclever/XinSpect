using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace BlueSquadron;

/// <summary>
/// ETW 即時事件監控：訂閱核心事件偵測可疑活動。
/// Phase 1：驅動載入、進程建立。Phase 2 加入驅動回呼後會移除部分 ETW 偵測。
/// </summary>
internal sealed class EtwWatcher : IDisposable
{
    private TraceEventSession? _session;
    private Thread? _thread;
    private volatile bool _running;
    private readonly PolicyEngine _policy;
    private readonly Action<string>? _alertPush;

    // 已知漏洞驅動 SHA256（LOLDrivers 精選）——Phase 2 從檔案載入完整清單
    private static readonly HashSet<string> KnownVulnerableHashes = new(StringComparer.OrdinalIgnoreCase)
    {
        // TrueSight.sys (Adlice/truesight.sys) — BYOVD used by multiple threat actors
        "A7B2BBE5AC4900EE53E4C83B32F6B7E1CC8F1B4FF2F05B1EB0C9B8A4A1B0DCFB",
        // gdrv.sys — GIGABYTE vulnerable driver
        "31F4CFB4C71DA44120752721103A16512444C13C2AC2D857A7E6F13CB679B427",
        // RTCore64.sys — MSI Afterburner vulnerable driver
        "01AA278B07B58DC46C84BD0B1B5C8E9EE4E62EA0BF7A695862444AF32E87F1FD",
        // dbutil_2_3.sys — Dell BIOS Utility
        "0296E2CE999E67C76352613A718E11516FE1B0EFC3FFDB8918FC999DD76A73A5",
        // mhyprot2.sys — miHoYo anti-cheat (abused in BYOVD)
        "0466E90BF0E83B776CA8716E01D35A8A2E5F96D3BD1FC246E8A6AD3CB77C39D3",
    };

    public EtwWatcher(PolicyEngine policy, Action<string>? alertPush = null)
    {
        _policy = policy;
        _alertPush = alertPush;
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _thread = new Thread(Run) { IsBackground = true, Name = "EtwWatcher" };
        _thread.Start();
    }

    private void Run()
    {
        try
        {
            _session = new TraceEventSession("BlueSquadronETW");
            // Microsoft-Windows-Kernel-Process for process create/terminate
            _session.EnableKernelProvider(
                KernelTraceEventParser.Keywords.ImageLoad |
                KernelTraceEventParser.Keywords.Process);

            _session.Source.Kernel.ImageLoad += OnImageLoad;
            _session.Source.Kernel.ProcessStart += OnProcessStart;

            _session.Source.Process();  // blocks until disposed
        }
        catch (UnauthorizedAccessException)
        {
            _policy.RecordThreat("advisory", "drivers",
                "ETW 監控需要系統管理員權限",
                "無法啟動 ETW 核心追蹤。驅動載入與進程監控功能不可用。");
        }
        catch (Exception ex)
        {
            _policy.RecordThreat("advisory", "drivers",
                "ETW 監控啟動失敗",
                $"例外：{ex.Message}");
        }
    }

    private void OnImageLoad(Microsoft.Diagnostics.Tracing.Parsers.Kernel.ImageLoadTraceData data)
    {
        if (!_policy.IsEnabled("drivers")) return;
        // Only care about kernel-mode loads (ProcessID == 0 or 4)
        if (data.ProcessID != 0 && data.ProcessID != 4) return;

        string path = data.FileName ?? "";
        if (string.IsNullOrEmpty(path)) return;

        // Check against known vulnerable driver hashes
        string? hash = ComputeFileHash(path);
        if (hash is not null && KnownVulnerableHashes.Contains(hash))
        {
            string msg = $"偵測到已知漏洞驅動載入：{Path.GetFileName(path)} (SHA256: {hash[..16]}…)";
            _policy.RecordThreat("critical", "drivers",
                "BYOVD 漏洞驅動載入", msg);
            _alertPush?.Invoke(System.Text.Json.JsonSerializer.Serialize(new
            {
                alert = true,
                severity = "critical",
                line = "drivers",
                title = "偵測到已知漏洞驅動載入",
                detail = msg,
                ts = DateTimeOffset.UtcNow,
            }));
        }

        // Log all kernel driver loads as advisory for timeline
        _policy.RecordThreat("advisory", "drivers",
            "核心驅動載入",
            $"{Path.GetFileName(path)} (PID={data.ProcessID})");
    }

    private void OnProcessStart(Microsoft.Diagnostics.Tracing.Parsers.Kernel.ProcessTraceData data)
    {
        if (!_policy.IsEnabled("cpu")) return;
        // Watch for suspicious processes that might indicate an active attack
        string name = data.ImageFileName ?? "";
        string[] suspicious = ["mimikatz", "rubeus", "sharphound", "bloodhound",
                               "procdump", "nanodump", "ppldump", "lsassy"];
        foreach (var s in suspicious)
        {
            if (name.Contains(s, StringComparison.OrdinalIgnoreCase))
            {
                string msg = $"偵測到可疑進程：{name} (PID={data.ProcessID})";
                _policy.RecordThreat("critical", "cpu",
                    "可疑進程建立", msg);
                _alertPush?.Invoke(System.Text.Json.JsonSerializer.Serialize(new
                {
                    alert = true,
                    severity = "critical",
                    line = "cpu",
                    title = "偵測到可疑進程建立",
                    detail = msg,
                    ts = DateTimeOffset.UtcNow,
                }));
                break;
            }
        }
    }

    private static string? ComputeFileHash(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var hash = System.Security.Cryptography.SHA256.HashData(fs);
            return Convert.ToHexString(hash);
        }
        catch { return null; }
    }

    public void Dispose()
    {
        _running = false;
        _session?.Dispose();
        _session = null;
    }
}
