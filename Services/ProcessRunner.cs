using System.Diagnostics;
using System.Text;

namespace XinSpect;

/// <summary>子行程的執行結果。<c>Stdout</c> 為 null 時 <c>Reason</c> 說明原因。</summary>
/// <param name="Stdout">標準輸出全文（<c>ExitCode != 0</c> 時仍會帶回，由呼叫端決定要不要用）。</param>
/// <param name="Stderr">標準錯誤全文。</param>
/// <param name="ExitCode">結束碼；逾時或被殺為 <c>null</c>。</param>
/// <param name="TimedOut">是否逾時（逾時時上面兩個輸出不保證完整）。</param>
/// <param name="Reason">失敗原因（逾時、起不來、拋例外）；成功時為 <c>null</c>。</param>
public sealed record ProcessRunResult(string? Stdout, string? Stderr, int? ExitCode, bool TimedOut, string? Reason)
{
    /// <summary>結束碼 0 且沒有逾時。</summary>
    public bool Succeeded => !TimedOut && ExitCode == 0;
}

/// <summary>
/// 唯讀執行外部主控台程式並收集兩個輸出串流（第 330 輪，修 D4）。
/// </summary>
/// <remarks>
/// <para>
/// <b>為什麼要有這支：</b>先前有兩處各自寫了一份「先 <c>ReadToEnd()</c> 再
/// <c>WaitForExit(timeout)</c>」的程式碼，而那個順序有一個潛伏的死鎖：
/// </para>
/// <list type="number">
/// <item>子行程先把 <b>stderr</b> 寫滿 pipe 緩衝區（Windows 預設約 4 KB）就卡在 write 上；</item>
/// <item>父行程正卡在 <b>stdout</b> 的同步 read 上等 EOF；</item>
/// <item>於是 <c>WaitForExit(timeoutMs)</c> 那一行<b>永遠執行不到</b>——timeout 只是安慰劑。</item>
/// </list>
/// <para>
/// 更糟的一種是「重導 stderr 但一次都不讀」：子行程只要往 stderr 寫超過緩衝區就直接鎖死，
/// 而父行程的 <c>catch</c> 也救不了（例外永遠不會來，因為根本沒拋）。
/// </para>
/// <para>
/// <b>做法：</b>兩個串流都用<b>非同步</b>讀，<b>在 WaitForExit 之前就開始</b>。
/// 逾時就殺整棵樹，並把兩個讀取工作的例外吃掉——否則遲到的例外會在 GC 時變成
/// 未觀察例外，把整個行程帶走（那等於用更糟的方式重現我們正要防的問題）。
/// </para>
/// <para>
/// <b>界線：</b>只執行、不解讀。參數由呼叫端給；這支<b>不接收命令字串</b>
/// （一律走 <c>ArgumentList</c>，避免空白與引號的組字問題）。
/// </para>
/// </remarks>
public static class ProcessRunner
{
    /// <summary>預設上限：健康的系統工具都是毫秒級，秒級就已經不對了。</summary>
    public const int DefaultTimeoutMs = 5000;

    public static ProcessRunResult Run(string fileName, IEnumerable<string> args,
        int timeoutMs = DefaultTimeoutMs)
    {
        Process? process = null;
        try
        {
            var psi = new ProcessStartInfo(fileName)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (string arg in args) psi.ArgumentList.Add(arg);

            process = Process.Start(psi);
            if (process is null)
                return new ProcessRunResult(null, null, null, false, $"{fileName} 沒有啟動起來（行程物件是空的）");

            // **關鍵順序**：兩個串流都在 WaitForExit 之前開始讀。
            // 少讀任何一個，只要子行程往那裡寫滿緩衝區就會死鎖。
            Task<string> outTask = process.StandardOutput.ReadToEndAsync();
            Task<string> errTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* 已經結束就沒什麼好殺的 */ }
                Observe(outTask);
                Observe(errTask);
                return new ProcessRunResult(null, null, null, true,
                    $"{fileName} 在 {timeoutMs} 毫秒內沒有回應，已中止（整個子行程樹）");
            }

            // 行程已結束，兩個讀取會在串流關閉後完成；這裡等它們收乾淨。
            string stdout = outTask.GetAwaiter().GetResult();
            string stderr = errTask.GetAwaiter().GetResult();
            return new ProcessRunResult(stdout, stderr, process.ExitCode, false, null);
        }
        catch (Exception ex)
        {
            return new ProcessRunResult(null, null, null, false, $"執行 {fileName} 失敗：{ex.Message}");
        }
        finally
        {
            process?.Dispose();
        }
    }

    /// <summary>把可能遲到的例外吃掉，避免它變成未觀察例外。</summary>
    private static void Observe(Task task) =>
        _ = task.ContinueWith(static t => _ = t.Exception,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
}
