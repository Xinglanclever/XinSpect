// ⚠ 未編譯——需作者機驗證（dotnet build 命令見 patch-24.md）
//
// PolicyEngine.cs — 完整替換檔（patch-24，P0-12 誠實化：阻擋模式如實申報）
// 覆蓋目標：BlueSquadron/PolicyEngine.cs（原 79 行）
//
// 修復內容（對照原檔）：
//   原版 SetMode 接受任意 mode 字串並存入，status 回 mode，但整個 Bridge 沒有任何
//   執行路徑讀 mode=="block" 去阻止什麼——「阻擋」是假的，卻回 ok:true。
//   本版：
//   1. BlockingImplemented 常數（Phase 1 事實：false）——執行能力由代碼現狀決定，
//      不由使用者設定決定；
//   2. ModeImplemented(line)——個別防線現在設定的模式是否真的有執行路徑
//      （detect/warn＝偵測確實在跑→true；block→false）；
//   3. SetMode 只接受已知防線與已知模式（未知回 false，呼叫端回 Err，不靜默吞）；
//      「block」仍可記錄為意向（保留使用者意願、Phase 2 落地即生效），但所有回應
//      端必須帶 implemented:false（見 patch-24-Program.cs 的 setPolicy／status）。
//   GUI 消費端：本頁沒有「阻擋」選項的按鈕——誠實聲明文字同步更新（patch-24-BlueSquadronView.xaml）。

namespace BlueSquadron;

/// <summary>
/// 防禦策略引擎：管理每條防線的模式（偵測/警告/阻擋）與威脅事件蒐集。
/// Phase 1 僅蒐集威脅事件；Phase 2 加入驅動回呼後才有阻擋能力。
/// </summary>
internal sealed class PolicyEngine
{
    /// <summary>
    /// Phase 1 的代碼事實：沒有任何執行路徑實施「阻擋」（ETW 只讀、驅動回呼只記錄，
    /// IOCTL_BS_SET_POLICY 派遣表裡根本沒有這個 case）。這是編譯期常數，不是設定——
    /// 改成 true 之前，必須先有真的會擋東西的代碼。
    /// </summary>
    public const bool BlockingImplemented = false;

    /// <summary>本引擎認得的防線（與 Bridge 啟動時的預設啟用集一致）。</summary>
    public static readonly string[] KnownLines =
        ["dma", "firmware", "cpu", "storage", "drivers"];

    /// <summary>本引擎認得的模式。block 目前是「已記錄的意向」，不是能力（見 BlockingImplemented）。</summary>
    public static readonly string[] KnownModes = ["detect", "warn", "block"];

    private readonly object _lock = new();
    private readonly List<ThreatEventRecord> _threats = [];
    private readonly Dictionary<string, string> _modes = new()
    {
        ["dma"] = "detect",
        ["firmware"] = "detect",
        ["cpu"] = "detect",
        ["storage"] = "detect",
        ["drivers"] = "detect",
    };
    private readonly HashSet<string> _enabled = ["dma", "firmware", "cpu", "storage", "drivers"];

    /// <summary>
    /// 設定防線模式。line／mode 不在已知集合內回 false（呼叫端應回 Err——
    /// 原版對未知 line 靜默無操作、對未知 mode 照存，兩者都會讓設定端誤以為生效了）。
    /// </summary>
    public bool SetMode(string line, string mode)
    {
        if (!KnownLines.Contains(line)) return false;
        if (!KnownModes.Contains(mode)) return false;
        lock (_lock)
            _modes[line] = mode;
        return true;
    }

    public void Enable(string line)
    {
        lock (_lock) _enabled.Add(line);
    }

    public void Disable(string line)
    {
        lock (_lock) _enabled.Remove(line);
    }

    public bool IsEnabled(string line)
    {
        lock (_lock) return _enabled.Contains(line);
    }

    public string GetMode(string line)
    {
        lock (_lock) return _modes.GetValueOrDefault(line, "detect");
    }

    /// <summary>
    /// 該防線「現在設定的模式」是否真的有執行路徑在跑：
    /// detect／warn 靠 ETW 與回呼記錄，實際在執行；block 沒有任何執行路徑（Phase 1）。
    /// status 指令逐防線回報此值，設定端可據此標示「未實施」。
    /// </summary>
    public bool ModeImplemented(string line)
    {
        if (BlockingImplemented) return true;   // Phase 2 落地後這條改由能力查詢決定
        return GetMode(line) != "block";
    }

    /// <summary>記錄一筆威脅事件。</summary>
    public void RecordThreat(string severity, string defenseLine, string title, string detail)
    {
        lock (_lock)
        {
            _threats.Add(new ThreatEventRecord(
                DateTimeOffset.UtcNow, severity, defenseLine, title, detail));
            // Keep only last 500
            if (_threats.Count > 500)
                _threats.RemoveRange(0, _threats.Count - 500);
        }
    }

    /// <summary>取得最近的威脅事件。</summary>
    public IReadOnlyList<ThreatEventRecord> RecentThreats(int max)
    {
        lock (_lock)
        {
            int start = Math.Max(0, _threats.Count - max);
            return _threats.GetRange(start, _threats.Count - start).AsReadOnly();
        }
    }
}

internal sealed record ThreatEventRecord(
    DateTimeOffset Timestamp,
    string Severity,     // "critical"/"warning"/"advisory"
    string DefenseLine,  // "dma"/"firmware"/"cpu"/"storage"/"drivers"
    string Title,
    string Detail
);
