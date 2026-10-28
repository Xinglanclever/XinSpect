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
        try
        {
            var psi = new ProcessStartInfo("bcdedit.exe", "/enum")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process is null) return (null, "bcdedit.exe 沒有啟動起來（行程物件是空的）");

            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();

            if (!process.WaitForExit(TimeoutMs))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* 已經結束就沒什麼好殺的 */ }
                return (null, $"bcdedit /enum 在 {TimeoutMs / 1000} 秒內沒有回應，已中止");
            }

            if (string.IsNullOrWhiteSpace(output))
                return (null, string.IsNullOrWhiteSpace(error)
                    ? "bcdedit /enum 沒有任何輸出"
                    : $"bcdedit /enum 沒有輸出，錯誤訊息：{error.Trim()}");

            return (output, null);
        }
        catch (Exception ex)
        {
            return (null, "呼叫 bcdedit /enum 失敗：" + ex.Message);
        }
    }
}
