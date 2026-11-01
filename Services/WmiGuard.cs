namespace XinSpect;

/// <summary>
/// WMI 讀取的逾時保護。
/// </summary>
/// <remarks>
/// <b>為什麼需要它：</b><see cref="System.Management.ManagementObjectSearcher"/> 的列舉
/// （<c>Get()</c> 之後逐筆 <c>Next_</c>）是同步的 DCOM 呼叫，而且<strong>沒有可用的逾時</strong>——
/// <c>ManagementOptions.Timeout</c> 只管單筆作業（Get／Put／Delete），管不到列舉。
/// 整機 WMI 一卡住，呼叫端就再也回不來。
///
/// 2026-10-31 的實際狀況就是這樣：主視窗的產生路徑上有一次 <c>Win32_PhysicalMemory</c> 查詢，
/// 五份行程全部停在 <c>IEnumWbemClassObject.Next_</c>——行程活著、視窗一個也沒出現，
/// 使用者的感受就是「雙擊沒反應」。
///
/// <b>這裡的做法：</b>把查詢丢到背景執行緒，呼叫端只等到上界為止；逾時就放手，
/// 那條背景執行緒留在原地（不會拖住 UI，行程結束後一併消失），
/// 呼叫端拿回的是「讀不到」，而不是「整個程式沒反應」。
/// 這與 <see cref="DiskIo.GuardedProbe{T}"/> 對 IOCTL 做的事一致，差別只在對象是 WMI。
/// </remarks>
internal static class WmiGuard
{
    /// <summary>
    /// 一次 WMI 讀取的上界。健康的 WMI 查詢是毫秒級，到秒級就已經不對了；
    /// 3 秒是「使用者還等得下去、畫面也不會看起來像死掉」的折衷。
    /// </summary>
    public const int DefaultTimeoutMs = 3000;

    /// <summary>
    /// 執行 <paramref name="read"/>，回傳結果與「這次是不是拖到逾時」。
    /// </summary>
    /// <remarks>
    /// 讀取本身丟例外（權限不足、命名空間不存在、類別不支援）是<b>快速失敗</b>，不算逾時：
    /// 兩者要分開，否則「這台機器沒有這個類別」會被講成「WMI 沒回應」。
    /// </remarks>
    public static (T? Value, bool TimedOut) Probe<T>(Func<T?> read, int timeoutMs = DefaultTimeoutMs)
        where T : class
    {
        var task = Task.Run(read);
        try
        {
            if (task.Wait(timeoutMs)) return (task.Result, false);
        }
        catch
        {
            return (null, false);          // 快速失敗——那不是逾時
        }

        // 逾時。把遲到的例外吃掉，否則它會在 GC 時變成未觀察例外，把整個行程帶走——
        // 那等於用一種更糟的方式重現我們正要防的問題。
        _ = task.ContinueWith(static t => _ = t.Exception,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        return (null, true);
    }

    /// <summary>只要結果、不關心是逾時還是失敗的方便版本；兩者都回 <c>null</c>。</summary>
    public static T? Read<T>(Func<T?> read, int timeoutMs = DefaultTimeoutMs) where T : class
        => Probe(read, timeoutMs).Value;
}
