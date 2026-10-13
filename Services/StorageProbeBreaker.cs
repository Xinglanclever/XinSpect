using System.Collections.Concurrent;

namespace XinSpect;

/// <summary>
/// 儲存裝置協定查詢的斷路器：記住「這顆磁碟的這條查詢不會回來」，冷卻期間不再重問。
/// </summary>
/// <remarks>
/// <para>
/// <b>為什麼需要它（實測）</b>：2026-10-13 在本機量更新路徑，<c>SmartFailingNowFactsService.Collect</c>
/// 一輪要 <b>12.06 秒</b>，佔整條 <c>LoadSoftwareFacts</c>（16.9 秒）的七成。逐顆磁碟逐條查詢量下去，
/// 時間全部落在兩顆 NVMe（PhysicalDrive 2 與 4）的協定查詢上：<c>NVMe 健康紀錄</c> 3006 ms、
/// <c>NVMe Identify Controller</c> 3000 ms，各<b>正好是看門狗的 3 秒上限</b>——不是慢，是根本沒回來。
/// 同一顆碟的匯流排型別、ATA 屬性、門檻表都是毫秒級，所以那是這兩條查詢專屬的現象，不是碟壞了。
/// </para>
/// <para>
/// <b>原本的代價是每輪重付</b>：<see cref="StorageSmartService"/> 的靜態查詢是無狀態的，
/// 而記得「問過不回應」的 <c>_noResponse</c> 是 UI 服務<b>實例</b>上的欄位——
/// 走事實收集的這條路根本不經過它。於是每按一次更新就再等 12 秒，
/// 而且每筆逾時都在核心裡留下一條**殺不掉的執行緒**（見 <see cref="DiskIo"/> 的實測記錄），
/// 每輪多洩兩條。
/// </para>
/// <para>
/// <b>為什麼是冷卻而不是永久放棄</b>：永久放棄等於憑一次的逾時就宣稱這顆碟這輩子不會回應；
/// 驅動重載、外接盒換埠、休眠喚醒都可能讓它回來。冷卻到期就再問一次——代價是每秒級的一次重試，
/// 換到的是「不會因為一次壞運氣就永久少一項事實」。
/// </para>
/// <para>
/// <b>界線</b>：斷路器只省掉<b>重複的等待</b>，不改判定。跳過時回 <c>null</c>，而逾時本來也是回
/// <c>null</c>——呼叫端看到的結果逐字相同（這一輪以「事實鍵、值、三態、原因全等」驗證）。
/// 鍵是<b>（磁碟，查詢）</b>成對的：同一顆碟的 ATA 查詢讀得到時，不會被 NVMe 查詢的逾時連坐。
/// </para>
/// </remarks>
public static class StorageProbeBreaker
{
    /// <summary>已知不回應的（磁碟, 查詢）多久之後再試一次。見類別說明的取捨。</summary>
    public const int CooldownMs = 10 * 60 * 1000;

    /// <summary>診斷閘：<c>XINSPECT_DISK_BREAKER=off</c> 時完全不經斷路器（不記也不跳），供 A/B 對照。</summary>
    public const string OffSwitch = "XINSPECT_DISK_BREAKER";

    private static readonly ConcurrentDictionary<(int Index, string Op), long> Tripped = new();

    private static long _skipped;

    /// <summary>被斷路器省下的查詢次數（診斷用；不是事實，不進任何報告）。</summary>
    public static long SkippedCount => Interlocked.Read(ref _skipped);

    /// <summary>目前掛著的（磁碟, 查詢）筆數。</summary>
    public static int TrippedCount => Tripped.Count;

    private static bool Enabled =>
        !string.Equals(Environment.GetEnvironmentVariable(OffSwitch), "off", StringComparison.OrdinalIgnoreCase);

    /// <summary>診斷閘目前是否啟用；測試用它確保診斷開關不會漏回環境。</summary>
    public static bool IsEnabled => Enabled;

    /// <summary>
    /// 這一輪該不該直接跳過？<paramref name="nowMs"/> 供測試注入時間（預設 <see cref="Environment.TickCount64"/>）。
    /// 冷卻到期會一併把記錄拿掉，讓下一次真的去問。
    /// </summary>
    public static bool ShouldSkip(int index, string op, long? nowMs = null)
    {
        if (!Enabled) return false;
        if (!Tripped.TryGetValue((index, op), out long trippedAt)) return false;
        if ((nowMs ?? Environment.TickCount64) - trippedAt < CooldownMs) return true;
        Tripped.TryRemove((index, op), out _);
        return false;
    }

    /// <summary>記下「這顆磁碟的這條查詢逾時了」。只有逾時才記（見 <see cref="DiskIo.GuardedProbe{T}"/>）。</summary>
    public static void Trip(int index, string op, long? nowMs = null)
    {
        if (!Enabled) return;
        Tripped[(index, op)] = nowMs ?? Environment.TickCount64;
    }

    /// <summary>把「跳過」計一次——只給診斷用量，不影響行為。</summary>
    public static void CountSkip() => Interlocked.Increment(ref _skipped);

    /// <summary>清空所有記錄與計數（測試用）。</summary>
    public static void Reset()
    {
        Tripped.Clear();
        Interlocked.Exchange(ref _skipped, 0);
    }
}
