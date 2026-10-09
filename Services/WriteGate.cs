namespace XinSpect;

/// <summary>
/// 硬體寫入稽核閘門（docs/PROGRAM-ULTIMATE-2026-10-10.md §5.3 的最小落地版）：
/// <b>每一次對硬體的寫入（MSR／PCI 設定空間／I/O 埠）都記進這裡</b>——寫前後值、時間、呼叫者、結果。
/// </summary>
/// <remarks>
/// <para>
/// <b>為什麼做在 WinRing0Bridge 底層而不是各服務：</b>全庫 17 個硬體寫入呼叫點
/// （PmuProgramming／Rdt／DramTraffic／TopDown／AmdSmu／ImcSmbus／IoPortAccess／SmbusController）
/// 全部經過橋接的三個寫入 API——在底層收口，閘門不需要知道任何一個服務，也不會因為
/// 新服務忘記接線而漏記（漏接線正是本專案實測過最貴的缺陷類別）。
/// </para>
/// <para>
/// <b>與同意閘門的關係：</b>同意閘門（使用者點頭才跑）在服務層，沒有變；這裡只做
/// <b>稽核</b>——已經被同意的寫入，寫了什麼、成不成功，要有可回答的帳本。
/// </para>
/// <para>
/// <b>界線：</b>帳本在記憶體、隨 Session（收集／測試一輪）重置；不上傳、不落盤——
/// 寫入稽核是「本次執行做了什麼」的申報面，不是持久化日誌（持久化審計另有 AuditLogService）。
/// </para>
/// </remarks>
public static class WriteGate
{
    /// <summary>一筆寫入稽核。</summary>
    public sealed record Entry(string Target, string Caller, string Detail, bool Succeeded, DateTimeOffset AtUtc);

    /// <summary>帳本上限：寫入是罕見事件（同意閘門之後才會發生）；超過代表有服務在瘋狂寫——截斷並在描述裡明說。</summary>
    public const int Cap = 512;

    private static readonly object Lock = new();
    private static readonly List<Entry> SessionLog = [];

    /// <summary>本 session 的寫入稽核（快照）。</summary>
    public static IReadOnlyList<Entry> Session
    {
        get { lock (Lock) { return [.. SessionLog]; } }
    }

    /// <summary>記一筆寫入。target 如「MSR 0x309」「PCI 0:31.5+0xF0」「IO 0x70」；detail 帶寫入值與結果。</summary>
    public static void Record(string target, string caller, string detail, bool succeeded)
    {
        lock (Lock)
        {
            if (SessionLog.Count >= Cap) return;   // 截斷；Describe 會說「已達上限」
            SessionLog.Add(new Entry(target, caller, detail, succeeded, DateTimeOffset.UtcNow));
        }
    }

    /// <summary>新收集輪開始時清帳（與 v2.43 的「資料更新後舊比對失效即清空」同一哲學：舊帳不冒充現況）。</summary>
    public static void Reset()
    {
        lock (Lock)
        {
            SessionLog.Clear();
        }
    }

    /// <summary>給 CLI／UI 的一段描述：幾筆、成功幾筆、截斷了沒有、前幾筆明細。</summary>
    public static string DescribeSession(int detailLimit = 6)
    {
        lock (Lock)
        {
            if (SessionLog.Count == 0)
                return "本次執行沒有對硬體的寫入（唯讀收集）。";
            int truncated = SessionLog.Count >= Cap ? 1 : 0;
            var head = SessionLog.Take(detailLimit)
                .Select(e => $"  {e.AtUtc:HH:mm:ss.fff} {e.Target}（{e.Caller}）→ {(e.Succeeded ? "成功" : "失敗")}：{e.Detail}");
            return $"本次執行對硬體寫入 {SessionLog.Count} 筆" +
                   (truncated == 1 ? "（已達帳本上限，後續未記）" : "") +
                   (SessionLog.Count > detailLimit ? $"，僅列前 {detailLimit} 筆" : "") + "：\n" +
                   string.Join("\n", head);
        }
    }
}
