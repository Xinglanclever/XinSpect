using System.Diagnostics;

namespace XinSpect;

/// <summary>
/// 唯讀執行 <c>bcdedit /enum</c> 並取回原始文字（U10 第三圈 BD-0001，第 315 輪）。
/// </summary>
/// <remarks>
/// <para>
/// <b>只讀。</b>參數固定是 <c>/enum</c>，沒有任何 <c>/set</c>、<c>/delete</c>、<c>/store</c> 路徑，
/// 也不碰韌體。解析全部交給 <see cref="BootOrderFactsService"/>（純函式），所以測試不需要開行程。
/// </para>
/// <para>
/// <b>輸出編碼不指定。</b>bcdedit 是主控台程式，重導向時寫的是主控台字碼頁（繁中是 CP950）；
/// 硬指定 UTF-8 會把中文描述解成亂碼。GUID 與欄位名是 ASCII，不受影響。
/// </para>
/// </remarks>
public static class BootOrderReader
{
    /// <summary>等 bcdedit 回應的上限：超過就殺掉並如實說「沒回應」。</summary>
    public const int TimeoutMs = 5000;

    /// <summary>讀一次 BCD；讀不到就是 <c>null</c> 加一句理由，不抛例外。</summary>
    public static (string? Output, string? Reason) TryReadEnum()
    {
        // 第 330 輪（修 D4）：先前這裡是先 ReadToEnd 再 WaitForExit——那個順序有死鎖。
        // 現在改走 ProcessRunner：兩個串流在 WaitForExit 之前就開始非同步讀。
        var run = ProcessRunner.Run("bcdedit.exe", ["/enum"], TimeoutMs);
        if (run.TimedOut) return (null, run.Reason);
        if (run.Stdout is null) return (null, run.Reason ?? "bcdedit /enum 沒有任何輸出");

        if (string.IsNullOrWhiteSpace(run.Stdout))
            return (null, string.IsNullOrWhiteSpace(run.Stderr)
                ? "bcdedit /enum 沒有任何輸出"
                : $"bcdedit /enum 沒有輸出，錯誤訊息：{run.Stderr.Trim()}");

        return (run.Stdout, null);
    }
}
