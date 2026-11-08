using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace XinSpect;

/// <summary>
/// 審計日誌服務（V7 WP36／A47）：追加寫入＋載入，JSON 陣列檔（canonical 選項與鏈雜湊一致）。
/// 每筆 EntryHash 蓋「不含自身雜湊欄位」的 canonical 位元組，PreviousHash 串接前一筆——
/// 任何一筆被改動，該筆雜湊不符且後續鏈全部斷裂（<see cref="AuditVerifier"/> 抓）。
/// 上傳雲端／區塊鏈存證刻意不做（TASK-GAP-6 §A47 明確不做）。
/// </summary>
public static class AuditLogService
{
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "XinSpect", "Audit", "audit.json");

    public static AuditEntry Append(
        IReadOnlyList<AuditEntry> existing,
        string @operator, string machineHash, string action, string scope,
        string resultSummary, string resultHash, DateTimeOffset timestampUtc)
    {
        int sequence = existing.Count == 0 ? 1 : existing[^1].Sequence + 1;
        string previous = existing.Count == 0 ? AuditEntry.Genesis : existing[^1].EntryHash;
        var entry = new AuditEntry
        {
            Sequence = sequence,
            TimestampUtc = timestampUtc,
            Operator = @operator,
            MachineHash = machineHash,
            Action = action,
            Scope = scope,
            ResultSummary = resultSummary,
            ResultHash = string.IsNullOrWhiteSpace(resultHash) ? "—" : resultHash,
            PreviousHash = previous,
            EntryHash = "",
        };
        return entry with { EntryHash = AuditEntry.ComputeHash(entry) };
    }

    /// <summary>讀現有日誌（檔案不存在＝空日誌，不是錯誤）；格式錯誤拋出由呼叫方三態。</summary>
    public static List<AuditEntry> Load(string path)
    {
        if (!File.Exists(path)) return [];
        var entries = JsonSerializer.Deserialize<List<AuditEntry>>(File.ReadAllText(path), AuditEntry.CanonicalOptions);
        return entries ?? [];
    }

    // ── 寫入面防護 ──────────────────────────────────────────────────────
    // Save 是整檔覆寫；兩個寫者交錯（同行程兩執行緒，或雙實例各自 Load→Append→Save）
    // 就會後寫蓋前寫——丟筆、序號斷裂。AuditVerifier 抓得到斷鏈（偵測面在），
    // 但寫入面不該靠偵測兜底：行程內用監視器鎖，跨實例用命名 Mutex
    // （與 EvidenceTimelineService 的 Local\XinSpect-EvidenceTimeline-v1 同一模式）。
    // 鎖順序固定 ProcessGate → FileMutex，單向無死鎖；兩者對同執行緒皆可重入，
    // Save 在 Mutate 鎖內被呼叫不會自鎖。
    private static readonly object ProcessGate = new();
    private static readonly Mutex FileMutex = new(false, @"Local\XinSpect-AuditLog-v1");

    private static void WithFileLock(Action action)
    {
        lock (ProcessGate)
        {
            bool held = false;
            try { FileMutex.WaitOne(); held = true; }
            catch (AbandonedMutexException) { held = true; }   // 前一持有者已死——鎖其實已到手，照樣做
            try { action(); }
            finally { if (held) FileMutex.ReleaseMutex(); }
        }
    }

    /// <summary>
    /// 讀→改→寫一口氣在寫入鎖內完成。所有「Load→Append→Save」的組合都必須走這裡——
    /// 分開呼叫時，兩個寫者各讀到同一份舊狀態、各自加一筆再各自覆寫，只鎖 Save 擋不住這種丟筆。
    /// update 收到鎖內讀到的現況，回傳要落地的完整清單；回傳值為落地後的清單（呼叫端要回報筆數時用）。
    /// </summary>
    public static List<AuditEntry> Mutate(string path, Func<List<AuditEntry>, List<AuditEntry>> update)
    {
        List<AuditEntry> result = [];
        WithFileLock(() =>
        {
            var entries = Load(path);
            result = update(entries);
            SaveCore(path, result);
        });
        return result;
    }

    public static void Save(string path, IReadOnlyList<AuditEntry> entries)
        => WithFileLock(() => SaveCore(path, entries));

    private static void SaveCore(string path, IReadOnlyList<AuditEntry> entries)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(entries, AuditEntry.CanonicalOptions));
    }

    /// <summary>操作者名（誰）：本機執行帳戶。寫進日誌前不去除——審計的「誰」就是它；檔案在本機、不含掃描內容。</summary>
    public static string CurrentOperator() => Environment.UserName;
}

/// <summary>
/// 審計鏈驗證器（V7 WP36／A47）：逐筆重算 EntryHash、檢查 PreviousHash 串接、序號連續。
/// 任何一筆被改動 → 該筆雜湊不符；即使攻擊者重算被改那筆的雜湊，後續筆的 PreviousHash 也對不上——
/// 除非整條鏈全部重寫（那等於偽造整份日誌，超出本機防線，見界線聲明）。
/// </summary>
public static class AuditVerifier
{
    public sealed record AuditVerdict(bool Valid, string? FailureReason, int CheckedCount);

    public static AuditVerdict Verify(IReadOnlyList<AuditEntry> entries)
    {
        string expectedPrevious = AuditEntry.Genesis;
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            if (e.Sequence != i + 1)
                return new(false, $"序號不連續：第 {i + 1} 筆應為 {i + 1}，實際 {e.Sequence}", i);
            if (!string.Equals(e.PreviousHash, expectedPrevious, StringComparison.Ordinal))
                return new(false, $"第 {e.Sequence} 筆的 PreviousHash 與前筆雜湊不符——鏈已斷裂", i);
            var recomputed = AuditEntry.ComputeHash(e);
            if (!string.Equals(recomputed, e.EntryHash, StringComparison.Ordinal))
                return new(false, $"第 {e.Sequence} 筆的內容雜湊不符——該筆被竄改或損毀", i);
            expectedPrevious = e.EntryHash;
        }
        return new(true, null, entries.Count);
    }

    /// <summary>便利載入＋驗證：檔案不存在＝空日誌（有效）；格式損毀＝無效帶原因。</summary>
    public static AuditVerdict VerifyFile(string path)
    {
        if (!File.Exists(path)) return new(true, null, 0);
        List<AuditEntry>? entries;
        try
        {
            entries = JsonSerializer.Deserialize<List<AuditEntry>>(File.ReadAllText(path), AuditEntry.CanonicalOptions);
        }
        catch (Exception ex)
        {
            return new(false, $"日誌檔無法解析——已損毀或被竄改：{ex.GetType().Name}", 0);
        }
        return Verify(entries ?? []);
    }
}

/// <summary>結果雜湊的便利計算（對任意內容位元組取 SHA-256 hex 小寫——把判決對應到可驗證的內容而不含內容本身）。</summary>
public static class AuditResultHash
{
    public static string OfBytes(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));
    public static string OfText(string content) => OfBytes(System.Text.Encoding.UTF8.GetBytes(content));
}
