namespace XinSpect;

/// <summary>
/// 磁碟 I/O 看門狗：把可能卡在核心裡的同步 IOCTL 圈起來，逾時就放棄。
/// </summary>
/// <remarks>
/// <para>
/// <b>為什麼需要它</b>：2026-09-04 在本機傾印測試基準檔時，一支列舉 <c>PhysicalDrive 0–7</c>
/// 的程式卡在某顆碟的 IOCTL 裡沒有回來。那個程序**連提權後的 <c>taskkill /F</c> 與
/// <c>Process.Kill()</c> 都殺不掉**（回「存取被拒」），因為它停在核心模式的不可中斷等待裡；
/// 它一路握著檔案鎖直到重開機。同一組同步 IOCTL 正是儲存頁與 <c>MachineAgeService</c> 在用的，
/// 所以那不是測試專屬的意外：在某些碟上，按一下「讀取」就足以把整個程式凍成殺不掉的程序。
/// </para>
/// <para>
/// <b>做法</b>：卡住的執行緒中止不了（.NET 沒有 <c>Thread.Abort</c>，有也沒用），所以這裡的策略
/// 是「不等它」——逾時就把那條執行緒留在原地自己爛掉，呼叫端拿到 <c>null</c> 當作「讀不到」。
/// 洩掉一條執行緒，換整個程式不被凍住，這筆交換是划算的；而「讀不到」在本專案裡是合法且
/// 誠實的結果，不需要為它編一個值出來。
/// </para>
/// <para>
/// <b>逾時後不重試、不換路徑</b>：卡住的那條 I/O 還在核心裡排隊，再發一次只是多卡一條，
/// 而且會讓「哪一顆碟有問題」變得更難查。
/// </para>
/// </remarks>
public static class DiskIo
{
    /// <summary>
    /// 單顆碟的讀取上限。健康的裝置回應 IOCTL 是毫秒級的；到了秒級就已經不對了，
    /// 3 秒是為了容忍機械碟從待機轉起來的那一下。
    /// </summary>
    public const int DefaultTimeoutMs = 3000;

    /// <summary>
    /// 在背景執行緒上執行 <paramref name="read"/>；逾時、拋例外或讀取本身回 <c>null</c> 時，
    /// 一律回 <c>null</c>（＝讀不到）。呼叫端保證在 <paramref name="timeoutMs"/> 之內拿回控制權。
    /// </summary>
    public static T? Guarded<T>(Func<T?> read, int timeoutMs = DefaultTimeoutMs) where T : class
    {
        var task = Task.Run(read);
        try
        {
            if (task.Wait(timeoutMs)) return task.Result;
        }
        catch
        {
            return null;                       // 讀取自己拋了例外
        }

        // 逾時。把遲到的例外吃掉，否則它會在 GC 時變成未觀察例外，把整個行程帶走——
        // 那等於用一種更糟的方式重現我們正要防的問題。
        _ = task.ContinueWith(static t => _ = t.Exception,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        return null;
    }

    /// <summary>
    /// <see cref="Guarded{T}"/> 的實值型別版本（<c>uint</c>、<c>long</c>、tuple…）。
    /// 語意完全相同：逾時或失敗回 <c>null</c>，呼叫端自行決定「讀不到」要顯示成什麼。
    /// </summary>
    public static T? GuardedValue<T>(Func<T?> read, int timeoutMs = DefaultTimeoutMs) where T : struct
    {
        var task = Task.Run(read);
        try
        {
            if (task.Wait(timeoutMs)) return task.Result;
        }
        catch
        {
            return null;
        }

        _ = task.ContinueWith(static t => _ = t.Exception,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        return null;
    }
}
