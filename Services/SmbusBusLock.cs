using System.Threading;

namespace XinSpect;

/// <summary>
/// SMBus 的<b>軟體層</b>仲裁：業界共用的具名互斥鎖。
/// </summary>
/// <remarks>
/// <para>
/// <c>Global\Access_SMBUS.HTP.Method</c> 是 CPU-Z、AIDA64、HWiNFO、主機板燈光軟體與各家
/// SPD 工具共同遵守的名字。先取這把鎖，同機的衝突大部分根本不會發生——這比事後從壞掉的
/// 讀值去猜「是不是被誰搶了」有效得多。
/// </para>
/// <para>
/// 這一層<b>不能取代</b><see cref="SmbusController"/> 的硬體 INUSE_STS 旗號：BIOS 與 SMM
/// 不理 Windows 的任何鎖，而它們隨時可能為了風扇轉速或溫度去讀同一條匯流排。兩層防的是
/// 不同的東西，所以兩層都要。
/// </para>
/// <para>
/// 建不出這把鎖時（例如缺 SeCreateGlobalPrivilege）不當成致命錯誤：回傳一個空殼並在說明裡
/// 標明「軟體層仲裁停用」，讓硬體那層獨自撐著。直接放棄會讓功能在權限受限的機器上完全消失，
/// 而那些機器並沒有比較危險——它們只是少了一層便利的協調。
/// </para>
/// </remarks>
public sealed class SmbusBusLock : IDisposable
{
    /// <summary>各家硬體監控軟體共用的互斥鎖名稱。</summary>
    public const string WellKnownName = @"Global\Access_SMBUS.HTP.Method";

    private Mutex? _mutex;
    private bool _disposed;

    private SmbusBusLock(Mutex? mutex) => _mutex = mutex;

    /// <summary>用正式名稱取鎖。</summary>
    public static SmbusBusLock? TryAcquire(int timeoutMs, out string note)
        => TryAcquire(WellKnownName, timeoutMs, out note);

    /// <summary>
    /// 取鎖。回 <c>null</c> 代表<b>別人正在用匯流排，不該繼續</b>；
    /// 回非 null 但 <paramref name="note"/> 非空代表可以繼續，但有需要一併告訴使用者的前提。
    /// </summary>
    public static SmbusBusLock? TryAcquire(string name, int timeoutMs, out string note)
    {
        note = "";
        Mutex mutex;
        try
        {
            mutex = new Mutex(false, name);
        }
        catch (Exception ex)
        {
            note = $"無法建立 SMBus 的具名互斥鎖（{ex.GetType().Name}）：軟體層仲裁停用，"
                 + "只剩控制器的 INUSE_STS 這一層。請自行確認沒有其他監控軟體在執行。";
            return new SmbusBusLock(null);
        }

        try
        {
            if (mutex.WaitOne(timeoutMs, exitContext: false)) return new SmbusBusLock(mutex);

            mutex.Dispose();
            note = $"SMBus 的具名互斥鎖在 {timeoutMs} ms 內取不到——CPU-Z、AIDA64、HWiNFO 或主機板"
                 + "燈光軟體正在使用匯流排。請關閉它們後重試；本工具不搶。";
            return null;
        }
        catch (AbandonedMutexException)
        {
            // 鎖已經是我們的了（前一個持有者當掉或被強制結束），但匯流排上可能留著一筆
            // 沒收尾的交易。取得，並且把這件事說出來——靜靜當成一次乾淨的取得會讓後面
            // 讀到的怪值失去解釋。
            note = "前一個持有者未正常釋放 SMBus 互斥鎖（可能當掉或被強制結束），匯流排狀態未知。";
            return new SmbusBusLock(mutex);
        }
        catch (Exception ex)
        {
            mutex.Dispose();
            note = $"等待 SMBus 互斥鎖時失敗（{ex.GetType().Name}：{ex.Message}）。";
            return null;
        }
    }

    /// <summary>釋放並關閉。互斥鎖有執行緒歸屬，跨執行緒釋放會拋例外——這裡一律吞掉，因為
    /// 釋放失敗的後果（鎖被判定為 abandoned）比讓例外往上炸小得多。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var m = _mutex;
        _mutex = null;
        if (m is null) return;
        try { m.ReleaseMutex(); } catch { }
        m.Dispose();
    }
}
